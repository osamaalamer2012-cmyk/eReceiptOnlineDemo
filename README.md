# e-Receipt Online Demo (ASP.NET Core 8 Minimal API) — Fixed (no top-level statements)

This version avoids C# CS8803 by using a classic Program.Main entry point and a Templates helper class.

## Local run
```bash
dotnet run --urls=http://0.0.0.0:8080
# Open http://localhost:8080
```

## Docker
```bash
docker build -t ereceipt-demo-fixed .
docker run -p 8080:8080 ereceipt-demo-fixed
```

## Render deploy
- Create a Web Service from this folder (Dockerfile included).
- Set env vars:
  - Shortener__ShortBaseUrl = https://<your-render-app>.onrender.com
  - Shortener__ViewBaseUrl  = https://<your-render-app>.onrender.com/view
- Open the URL, issue a receipt, click the short link, send OTP (demo shows code), verify, view receipt.
