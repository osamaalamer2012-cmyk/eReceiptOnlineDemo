# e-Receipt Online Demo (ASP.NET Core 8 Minimal API)

This demo shows the full journey online: agent issues receipt → short URL → customer OTP → receipt view. SMS/OTP are simulated.

## Local run
```bash
dotnet run --urls=http://0.0.0.0:8080
# Open http://localhost:8080
```

## Docker
```bash
docker build -t ereceipt-demo .
docker run -p 8080:8080 ereceipt-demo
```

## Free deploy (Render)
- Create a new **Web Service** from this folder (Dockerfile is included).
- After deploy, open the public URL.
- Use the Agent page to issue a receipt, then click the short link in **SMS Preview**.
- On the customer page, click **Send OTP** (demo shows the OTP). Enter it to view the receipt.
