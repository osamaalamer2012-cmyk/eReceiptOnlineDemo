using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using System.IO;

var builder = WebApplication.CreateBuilder(args);

var demoMode = builder.Configuration.GetValue<bool>("Demo", true);
var opts = builder.Configuration.GetSection("Shortener").Get<ShortenerOptions>() ?? new ShortenerOptions();

var app = builder.Build();

// Serve static files from wwwroot
app.UseDefaultFiles();
app.UseStaticFiles();

// ✅ Serve the Agent page at "/" (and handle HEAD)
app.MapMethods("/", new[] { "GET", "HEAD" }, (IWebHostEnvironment env) =>
{
    var indexPath = Path.Combine(env.WebRootPath ?? "", "index.html");
    if (File.Exists(indexPath)) return Results.File(indexPath, "text/html");
    // Fallback inline page if wwwroot/index.html is missing
    return Results.Content("""
<!doctype html><html><head><meta charset="utf-8"><title>e-Receipt</title>
<link rel="stylesheet" href="/style.css"></head><body>
<header><h1>e-Receipt Demo</h1></header>
<main class="card"><p>Index file not found. Ensure <code>wwwroot/index.html</code> is in your repo.</p>
<p>Health: <a href="/health">/health</a></p></main></body></html>
""", "text/html");
});

// ✅ Any unmatched path → index.html (SPA-style)
app.MapFallback((IWebHostEnvironment env) =>
{
    var indexPath = Path.Combine(env.WebRootPath ?? "", "index.html");
    return File.Exists(indexPath)
        ? Results.File(indexPath, "text/html")
        : Results.NotFound();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/tcrm/issue", (IssueRequest req) =>
{
    if (req is null || string.IsNullOrWhiteSpace(req.TxnId) || string.IsNullOrWhiteSpace(req.Msisdn))
        return Results.BadRequest(new { error = "Missing fields" });

    var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    var longUrl = $"{opts.ViewBaseUrl}?token={token}";

    var code = GenerateCode(opts.CodeLength <= 0 ? 7 : opts.CodeLength);
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

// Short link redirect
app.MapGet("/s/{code}", (string code) =>
{
    if (!codes.TryGetValue(code, out var map))
        return Results.Content(Html.Error("Invalid or unknown link code."), "text/html");
    if (map.ExpiresAt <= DateTimeOffset.UtcNow)
        return Results.Content(Html.Error("This link has expired."), "text/html");
    if (map.Usage >= map.UsageMax)
        return Results.Content(Html.Error("Maximum number of allowed views has been reached."), "text/html");
    return Results.Redirect(map.LongUrl, false);
});

// OTP APIs (demo)
app.MapPost("/api/otp/send", (OtpSendRequest req) =>
{
    if (req == null || string.IsNullOrWhiteSpace(req.Token))
        return Results.BadRequest(new { error = "Missing token" });
    if (!tokenToReceipt.TryGetValue(req.Token, out var rid) || !receipts.TryGetValue(rid, out var rec))
        return Results.BadRequest(new { error = "Invalid token" });

    var code = new Random().Next(100000, 999999).ToString();
    otpStore[req.Token] = new OtpEntry { Code = code, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), AttemptsLeft = 3 };

    Console.WriteLine($"[DEMO OTP] to {rec.Msisdn}: {code}");
    return Results.Ok(new { otpDemo = demoMode ? code : "SENT" });
});

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

    if (!tokenToReceipt.TryGetValue(req.Token, out var rid) || !receipts.TryGetValue(rid, out var rec))
        return Results.BadRequest(new { error = "Invalid token" });
    if (rec.ExpiresAt <= DateTimeOffset.UtcNow)
        return Results.BadRequest(new { error = "Link expired" });
    if (rec.Uses >= rec.MaxUses)
        return Results.BadRequest(new { error = "Usage limit exceeded" });

    rec.Uses += 1;
    otpStore.TryRemove(req.Token, out _);
    return Results.Ok(new { receiptId = rec.ReceiptId });
});

// Receipt JSON
app.MapGet("/api/receipt/{id}", (string id) =>
{
    if (!receipts.TryGetValue(id, out var rec)) return Results.NotFound(new { error = "Not found" });
    return Results.Ok(rec);
});

// ✅ Bind to Render’s dynamic port
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");

// ===== in-memory stores =====
static ConcurrentDictionary<string, Receipt> receipts = new();
static ConcurrentDictionary<string, string> tokenToReceipt = new();
static ConcurrentDictionary<string, ShortMap> codes = new();
static ConcurrentDictionary<string, OtpEntry> otpStore = new();

// ===== helpers / models =====
static string GenerateCode(int length)
{
    const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    var bytes = RandomNumberGenerator.GetBytes(length);
    Span<char> chars = stackalloc char[length];
    for (int i = 0; i < length; i++) chars[i] = alphabet[bytes[i] % alphabet.Length];
    return new string(chars);
}

record ShortenerOptions
{
    public string ShortBaseUrl { get; set; } = "http://localhost:8080";
    public string ViewBaseUrl { get; set; } = "http://localhost:8080/view.html";
    public int CodeLength { get; set; } = 7;
    public int DefaultTtlHours { get; set; } = 48;
    public int DefaultUsageMax { get; set; } = 2;
}

record IssueRequest
{
    public string TxnId { get; init; } = default!;
    public string Msisdn { get; init; } = default!;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public List<ReceiptItem>? Items { get; init; }
}
record ReceiptItem { public string Sku { get; init; } = ""; public string Name { get; init; } = ""; public int Qty { get; init; } public decimal Price { get; init; } }
record Receipt
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
record ShortMap
{
    public string Code { get; init; } = default!;
    public string Token { get; init; } = default!;
    public string LongUrl { get; init; } = default!;
    public int Usage { get; set; }
    public int UsageMax { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
record OtpEntry { public string Code { get; init; } = default!; public DateTimeOffset ExpiresAt { get; init; } public int AttemptsLeft { get; set; } = 3; }
record OtpSendRequest { public string Token { get; init; } = default!; }
record OtpVerifyRequest { public string Token { get; init; } = default!; public string Code { get; init; } = default!; }

static class Html
{
    public static string Error(string message) =>
$@"<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'><title>Error</title>
<link rel='stylesheet' href='/style.css'></head><body><header><h1>e-Receipt</h1></header><main><section class='card'>
<h2>Cannot open receipt</h2><p>{System.Net.WebUtility.HtmlEncode(message)}</p><p><a href='/'>Back</a></p></section></main></body></html>";
}

