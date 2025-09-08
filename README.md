# e-Receipt Online Demo (ASP.NET Core 8, static pages)

Endpoints:
- / (Agent UI)
- /s/{code} (short link redirect)
- /view.html?token=... (customer OTP)
- /receipt.html#<id> (receipt page)
- /api/* (demo OTP + receipt JSON)

Run locally:
  dotnet run --urls=http://0.0.0.0:8080

Render env vars after deploy:
  Shortener__ShortBaseUrl = https://<your-app>.onrender.com
  Shortener__ViewBaseUrl  = https://<your-app>.onrender.com/view.html
