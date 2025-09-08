
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace EReceiptOnlineDemo;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var demoMode = builder.Configuration.GetValue<bool>("Demo", true);
        var opts = builder.Configuration.GetSection("Shortener").Get<ShortenerOptions>() ?? new ShortenerOptions();

        var app = builder.Build();

        var receipts = new ConcurrentDictionary<string, Receipt>();
        var tokenToReceipt = new ConcurrentDictionary<string, string>();
        var codes = new ConcurrentDictionary<string, ShortMap>();
        var otpStore = new ConcurrentDictionary<string, OtpEntry>();

        app.MapGet("/", () => Results.Content(Templates.IndexHtml, "text/html"));

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        // ---- ISSUE from "TCRM" page (demo) ----
        app.MapPost("/tcrm/issue", (IssueRequest req) =>
        {
            if (req == null || string.IsNullOrWhiteSpace(req.TxnId) || string.IsNullOrWhiteSpace(req.Msisdn))
                return Results.BadRequest(new { error = "Missing fields" });

            // mint token (256-bit, base64url)
            var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var longUrl = $"{opts.ViewBaseUrl}?token={token}";

            var code = Helpers.GenerateCode(opts.CodeLength <= 0 ? 7 : opts.CodeLength);
            var expiresAt = DateTimeOffset.UtcNow.AddHours(opts.DefaultTtlHours <= 0 ? 48 : opts.DefaultTtlHours);

            var receipt = new Receipt
            {
                ReceiptId = Guid.NewGuid().ToString("N"),
                TxnId = req.TxnId,
                Msisdn = req.Msisdn,
                Amount = req.Amount,
                Currency = string.IsNullOrWhiteSpace(req.Currency) ? "USD" : req.Currency,
                Items = req.Items ?? new List<ReceiptItem>(),
                ExpiresAt = expiresAt,
                MaxUses = opts.DefaultUsageMax <= 0 ? 2 : opts.DefaultUsageMax,
                Uses = 0,
                CreatedAt = DateTimeOffset.UtcNow
            };
            receipts[receipt.ReceiptId] = receipt;
            tokenToReceipt[token] = receipt.ReceiptId;

            codes[code] = new ShortMap
            {
                Code = code,
                Token = token,
                LongUrl = longUrl,
                ExpiresAt = expiresAt,
                Usage = 0,
                UsageMax = receipt.MaxUses,
                CreatedAt = DateTimeOffset.UtcNow
            };

            var shortUrl = $"{opts.ShortBaseUrl.TrimEnd('/')}/s/{code}";
            Console.WriteLine($"[DEMO SMS] to {req.Msisdn}: {shortUrl}");

            return Results.Ok(new
            {
                receiptId = receipt.ReceiptId,
                token,
                longUrl,
                shortUrl,
                expiresAt
            });
        });

        // ---- PUBLIC facade: short link ----
        app.MapGet("/s/{code}", (string code) =>
        {
            if (!codes.TryGetValue(code, out var map))
                return Results.Content(Templates.ErrorPage("Invalid or unknown link code."), "text/html", Encoding.UTF8);

            if (map.ExpiresAt <= DateTimeOffset.UtcNow)
                return Results.Content(Templates.ErrorPage("This link has expired."), "text/html", Encoding.UTF8);

            if (map.Usage >= map.UsageMax)
                return Results.Content(Templates.ErrorPage("Maximum number of allowed views has been reached."), "text/html", Encoding.UTF8);

            return Results.Redirect(map.LongUrl, false);
        });

        // ---- PUBLIC facade: view page ----
        app.MapGet("/view", (HttpRequest http) =>
        {
            var token = http.Query["token"].ToString();
            if (string.IsNullOrWhiteSpace(token)) return Results.Content(Templates.ErrorPage("Missing token"), "text/html");

            if (!tokenToReceipt.TryGetValue(token, out var rid) || !receipts.TryGetValue(rid, out var rec))
                return Results.Content(Templates.ErrorPage("Invalid or unknown token"), "text/html");

            if (rec.ExpiresAt <= DateTimeOffset.UtcNow)
                return Results.Content(Templates.ErrorPage("This link has expired."), "text/html");

            if (rec.Uses >= rec.MaxUses)
                return Results.Content(Templates.ErrorPage("Maximum number of allowed views has been reached."), "text/html");

            // pre-OTP page
            return Results.Content(Templates.ViewHtml(token, rec), "text/html");
        });

        // ---- API: send OTP (demo: return code in JSON + log) ----
        app.MapPost("/api/otp/send", (OtpSendRequest req) =>
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Token))
                return Results.BadRequest(new { error = "Missing token" });

            if (!tokenToReceipt.TryGetValue(req.Token, out var rid) || !receipts.TryGetValue(rid, out var rec))
                return Results.BadRequest(new { error = "Invalid token" });

            var code = new Random().Next(100000, 999999).ToString();
            var entry = new OtpEntry
            {
                Code = code,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                AttemptsLeft = 3
            };
            otpStore[req.Token] = entry;

            Console.WriteLine($"[DEMO OTP] to {rec.Msisdn}: {code}");

            return Results.Ok(new { otpDemo = demoMode ? code : "SENT" });
        });

        // ---- API: verify OTP ----
        app.MapPost("/api/otp/verify", (OtpVerifyRequest req) =>
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Token) || string.IsNullOrWhiteSpace(req.Code))
                return Results.BadRequest(new { error = "Missing fields" });

            if (!otpStore.TryGetValue(req.Token, out var entry))
                return Results.BadRequest(new { error = "OTP not issued" });

            if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
                return Results.BadRequest(new { error = "OTP expired" });

            if (entry.AttemptsLeft <= 0)
                return Results.BadRequest(new { error = "Too many attempts" });

            if (!string.Equals(entry.Code, req.Code))
            {
                entry.AttemptsLeft -= 1;
                return Results.BadRequest(new { error = "Invalid code", attemptsLeft = entry.AttemptsLeft });
            }

            // success
            if (!tokenToReceipt.TryGetValue(req.Token, out var rid) || !receipts.TryGetValue(rid, out var rec))
                return Results.BadRequest(new { error = "Invalid token" });

            // increment usage (simulate atomically)
            if (rec.ExpiresAt <= DateTimeOffset.UtcNow)
                return Results.BadRequest(new { error = "Link expired" });
            if (rec.Uses >= rec.MaxUses)
                return Results.BadRequest(new { error = "Usage limit exceeded" });

            rec.Uses += 1;
            otpStore.TryRemove(req.Token, out _);

            return Results.Ok(new { receiptId = rec.ReceiptId });
        });

        // ---- API: receipt JSON ----
        app.MapGet("/api/receipt/{id}", (string id) =>
        {
            if (!receipts.TryGetValue(id, out var rec))
                return Results.NotFound(new { error = "Not found" });
            return Results.Ok(rec);
        });

        // ---- static assets ----
        app.MapGet("/style.css", () => Results.Content(Templates.Css, "text/css"));
        app.MapGet("/script.js", () => Results.Content(Templates.ScriptJs, "application/javascript"));

        app.Run("http://0.0.0.0:8080");
    }
}

// ========= Models =========
public record ShortenerOptions
{
    public string ShortBaseUrl { get; set; } = "http://localhost:8080";
    public string ViewBaseUrl { get; set; } = "http://localhost:8080/view";
    public int CodeLength { get; set; } = 7;
    public int DefaultTtlHours { get; set; } = 48;
    public int DefaultUsageMax { get; set; } = 2;
}

public record IssueRequest
{
    public string TxnId { get; init; } = default!;
    public string Msisdn { get; init; } = default!;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public List<ReceiptItem>? Items { get; init; }
}

public record ReceiptItem
{
    public string Sku { get; init; } = "";
    public string Name { get; init; } = "";
    public int Qty { get; init; }
    public decimal Price { get; init; }
}

public record Receipt
{
    public string ReceiptId { get; init; } = default!;
    public string TxnId { get; init; } = default!;
    public string Msisdn { get; init; } = default!;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public List<ReceiptItem> Items { get; init; } = new();
    public DateTimeOffset ExpiresAt { get; init; }
    public int MaxUses { get; init; } = 2;
    public int Uses { get; set; } = 0;
    public DateTimeOffset CreatedAt { get; init; }
}

public record ShortMap
{
    public string Code { get; init; } = default!;
    public string Token { get; init; } = default!;
    public string LongUrl { get; init; } = default!;
    public int Usage { get; set; }
    public int UsageMax { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public record OtpEntry
{
    public string Code { get; init; } = default!;
    public DateTimeOffset ExpiresAt { get; init; }
    public int AttemptsLeft { get; set; } = 3;
}

public record OtpSendRequest { public string Token { get; init; } = default!; }
public record OtpVerifyRequest { public string Token { get; init; } = default!; public string Code { get; init; } = default!; }

// ========= Helpers & Templates =========
public static class Helpers
{
    public static string GenerateCode(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        Span<char> chars = stackalloc char[length];
        for (int i = 0; i < length; i++) chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
    }
}

public static class Templates
{
    public static string IndexHtml => """
<!doctype html>
<html>
<head>
  <meta charset='utf-8'>
  <meta name='viewport' content='width=device-width, initial-scale=1'>
  <title>e-Receipt Demo (TCRM Agent)</title>
  <link rel='stylesheet' href='/style.css'>
</head>
<body>
  <header><h1>e-Receipt Demo — TCRM Agent</h1></header>
  <main>
    <section class='card'>
      <h2>Issue e-Receipt</h2>
      <div class='grid'>
        <label>Txn ID <input id='txnId' value='TX-1001'/></label>
        <label>MSISDN <input id='msisdn' value='+15551234567'/></label>
        <label>Amount <input id='amount' type='number' step='0.01' value='49.90'/></label>
        <label>Currency <input id='currency' value='USD'/></label>
      </div>
      <button id='btnIssue'>Generate e-Receipt</button>
      <div id='issueResult'></div>
    </section>

    <section class='card'>
      <h2>Customer SMS Preview (demo)</h2>
      <div id='smsPreview'>—</div>
    </section>
  </main>
  <script src='/script.js'></script>
</body>
</html>
""";

    public static string ViewHtml(string token, Receipt r)
    {
        var last4 = System.Net.WebUtility.HtmlEncode(r.Msisdn.Length >= 4 ? r.Msisdn[^4..] : r.Msisdn);
        var tokenJson = JsonSerializer.Serialize(token);
        return $@"
<!doctype html>
<html>
<head>
  <meta charset='utf-8'>
  <meta name='viewport' content='width=device-width, initial-scale=1'>
  <title>View Receipt</title>
  <link rel='stylesheet' href='/style.css'>
</head>
<body>
  <header><h1>Secure e-Receipt</h1></header>
  <main>
    <section class='card'>
      <h2>Verify Access</h2>
      <p>For security, we sent a one-time code to your phone ending with <strong>{last4}</strong>.</p>
      <button id='sendOtp'>Send OTP</button>
      <div id='otpDemo'></div>
      <div class='grid'>
        <label>Enter Code <input id='otpCode' maxlength='6' /></label>
      </div>
      <button id='verifyOtp'>Verify & View Receipt</button>
      <div id='status'></div>
    </section>
  </main>
<script>
const token = {tokenJson};
document.getElementById('sendOtp').onclick = async () => {{
  const res = await fetch('/api/otp/send', {{ method:'POST', headers:{{'Content-Type':'application/json'}}, body: JSON.stringify({{ token }}) }});
  const json = await res.json();
  document.getElementById('otpDemo').innerHTML = json.otpDemo ? '<small>DEMO OTP: <b>'+json.otpDemo+'</b></small>' : '';
}};
document.getElementById('verifyOtp').onclick = async () => {{
  const code = document.getElementById('otpCode').value;
  const res = await fetch('/api/otp/verify', {{ method:'POST', headers:{{'Content-Type':'application/json'}}, body: JSON.stringify({{ token, code }}) }});
  const json = await res.json();
  const status = document.getElementById('status');
  if(res.ok) {{
    status.innerHTML = '✅ Verified. Opening receipt...';
    window.location.href = '/receipt.html#'+json.receiptId;
  }} else {{
    status.innerHTML = '❌ '+(json.error || 'Failed');
  }}
}};
</script>
</body>
</html>
";
    }

    public static string Css => """
body{font-family:system-ui,-apple-system,Segoe UI,Roboto,Arial,Helvetica,sans-serif;background:#f6f8fb;margin:0;color:#111}
header{background:#0f172a;color:#fff;padding:14px 20px}
main{max-width:920px;margin:24px auto;padding:0 16px}
.card{background:#fff;border-radius:14px;box-shadow:0 10px 20px rgba(0,0,0,.04);padding:18px 20px;margin-bottom:18px}
h1{margin:0;font-size:20px} h2{margin:0 0 10px 0}
.grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:10px;margin:12px 0}
label{display:flex;flex-direction:column;font-size:12px;color:#374151}
input{padding:10px;border:1px solid #d1d5db;border-radius:10px}
button{background:#2563eb;color:#fff;padding:10px 14px;border:0;border-radius:10px;cursor:pointer}
button:hover{background:#1d4ed8}
#issueResult,#smsPreview,#status{margin-top:10px;font-size:14px}
small{color:#6b7280}
""";

    public static string ScriptJs => """
const el = (id)=>document.getElementById(id);

if (document.getElementById('btnIssue')){
  el('btnIssue').onclick = async ()=>{
    const payload = {
      txnId: el('txnId').value,
      msisdn: el('msisdn').value,
      amount: parseFloat(el('amount').value),
      currency: el('currency').value,
      items: [{ sku:'SKU1', name:'Sample Product', qty:1, price: parseFloat(el('amount').value)}]
    };
    const res = await fetch('/tcrm/issue', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(payload) });
    const json = await res.json();
    if(res.ok){
      el('issueResult').innerHTML = '✅ Issued — receiptId: <b>'+json.receiptId+'</b>';
      el('smsPreview').innerHTML = 'SMS to customer: <a href=\"'+json.shortUrl+'\" target=\"_blank\">'+json.shortUrl+'</a>';
    }else{
      el('issueResult').innerText = '❌ '+(json.error || 'Failed');
    }
  };
}

// Receipt viewer
if (location.pathname.endsWith('/receipt.html')){
  const rid = location.hash.substring(1);
  (async ()=>{
    const res = await fetch('/api/receipt/'+rid);
    const json = await res.json();
    const container = document.createElement('div');
    if(!res.ok){ container.innerHTML = '<p>Not found.</p>'; document.body.appendChild(container); return; }
    container.className='card';
    container.innerHTML = `
      <h2>Receipt #${json.receiptId}</h2>
      <p><b>Txn:</b> ${json.txnId} &nbsp; <b>MSISDN:</b> ${json.msisdn}</p>
      <p><b>Amount:</b> ${json.amount} ${json.currency}</p>
      <table border="0" cellpadding="6"><thead><tr><th align="left">Item</th><th>Qty</th><th>Price</th></tr></thead>
      <tbody>${(json.items||[]).map(i=>`<tr><td>${i.name}</td><td align="center">${i.qty}</td><td align="right">${i.price.toFixed(2)}</td></tr>`).join('')}</tbody></table>
      <p><small>Valid until: ${new Date(json.expiresAt).toLocaleString()}</small></p>
      <button id="downloadPdf">Download PDF (client-side)</button>
    `;
    document.body.appendChild(container);
    const btn = document.getElementById('downloadPdf');
    btn.onclick = ()=>{ window.print(); };
  })();
}
""";
}

// Receipt static shell
