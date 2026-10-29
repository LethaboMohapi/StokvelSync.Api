# StokvelSync API (.NET 8 + MySQL)

## Run
1. `docker run -d --name sksql -e MYSQL_ROOT_PASSWORD=PacklyTeabou27@ -e MYSQL_DATABASE=stokvelsync -p 3306:3306 mysql:8`
2. Set `ConnectionStrings:Default`, `Jwt:Key`, `Payments:WebhookSecret`, `Sms:InboundKey` (use `dotnet user-secrets`).
3. `dotnet run` then open `/swagger`. Development uses `EnsureCreated`; for production:
   `dotnet ef migrations add Init && dotnet ef database update`.

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
