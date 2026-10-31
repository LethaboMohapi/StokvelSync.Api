# StokvelSync API (.NET 8 + MySQL)

![version](https://img.shields.io/badge/version-v1.0.0-555555?labelColor=555555&color=0e75b6)
[![Application checks](https://github.com/LethaboMohapi/StokvelSync.Api/actions/workflows/checks.yml/badge.svg)](https://github.com/LethaboMohapi/StokvelSync.Api/actions/workflows/checks.yml)
[![license](https://img.shields.io/badge/license-MIT-007ec6)](LICENSE.txt)
[![.NET](https://img.shields.io/badge/.NET-8.0-512bd4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![MySQL](https://img.shields.io/badge/MySQL-8.0%2B-4479a1?logo=mysql&logoColor=white)](https://www.mysql.com/)
![Code by Lethabo Mohapi](https://img.shields.io/badge/Code_by-Lethabo_Mohapi-blue)

## Run
1. `docker rm -f sksql`
2. `docker run -d --name sksql -e MYSQL_ROOT_PASSWORD=YOUR_PASSWORD -e MYSQL_DATABASE=stokvelsync -p 3306:3306 mysql:8`
3. `dotnet user-secrets set "ConnectionStrings:Default" "Server=localhost;Port=3306;Database=stokvelsync;User=root;Password=YOUR_PASSWORD;"`
4. `dotnet run`
5. Open https://localhost:42161 
For production later, don’t recreate the database this way. Use migrations, so existing data survives schema changes:
`dotnet tool install --global dotnet-ef`
`dotnet ef migrations add Init`
`dotnet ef database update`


## Try the flow
register -> create stokvel (returns JoinCode) -> others join -> `rotation/generate` ->
`POST /contributions` -> `POST /api/payments/webhook` (header `X-Signature`, body `{"providerRef":"SBX-...","status":"success"}`) ->
`GET /ledger?lang=zu`.
SMS: `POST /api/sms/inbound` (header `X-Sms-Key`) body `{"from":"0821234567","text":"BAL ABC234"}`.

## Where each feature lives
- Mobile money: `Services/Payments.cs` (swap `SandboxPaymentProvider`)
- Zulu/Xhosa/Afrikaans: `Services/Localizer.cs` (draft strings, get native-speaker review)
- Rotation: `Services/RotationService.cs`
- Transparent ledger: `Services/LedgerService.cs` (hash-chained, `chainValid` in the API)
- Offline: `POST /api/sync` (idempotent by client `OpId`); SMS: `Services/Sms.cs`

## Before production
Multi-tenant admin/billing (the SaaS layer), OTP phone verification, rate limiting, per-stokvel DB transaction around ledger appends (concurrency), POPIA consent/retention, provider HMAC webhooks, tests.
