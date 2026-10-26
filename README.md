# 335_Order

Local-first .NET 10 Blazor Interactive Server restaurant ordering and kitchen display. SQLite and ASP.NET Core Identity run on one PC. No external services are needed at runtime.

**Status: tested first vertical slice, not a complete production POS.** Ordering, append-only kitchen tickets, preparation states, audited cancellations, role login, bill viewing/printing, migration and online backup are implemented. Payments, split bills, refunds, close, reports and admin CRUD remain pending. Do not use it for live payment collection. See [implementation plan](docs/PLAN.md).

## Start on a Windows development laptop

For a quick preview without signing in, run `dotnet run --project src/RestaurantPOS.Web --launch-profile http` and open **http://localhost:5083/dashboard**. Development automatically enables sample data and a preview Admin identity. Production always requires authentication. No preview password is created. To test actual staff logins, disable preview as shown below. Existing demo passwords remain unchanged.

Install .NET 10 SDK. From this repository directory, in PowerShell:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-restore
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Development__SkipSignIn = 'false'
$env:Seed__Enabled = 'true'
# Enter a development-only password, 12+ chars with upper/lowercase, digit and symbol.
$credential = Get-Credential -UserName 'demo' -Message 'Enter the password for local demo accounts'
$env:Seed__Password = $credential.GetNetworkCredential().Password
dotnet run --project src/RestaurantPOS.Web --no-launch-profile --urls http://localhost:5083
```

Open **http://localhost:5083/login**. Accounts: `admin@demo.local`, `waiter@demo.local`, `kitchen@demo.local`, `cashier@demo.local`, with the password you entered. Seed is disabled unless explicitly enabled **and** environment is Development. It creates 10 tables, Malaysian dishes, one dine-in and one takeaway order. Re-running preserves existing data and passwords. No credentials are stored in Git. Restore requires internet the first time; normal operation uses only local assets and SQLite.

Use three separate browser profiles/browsers (e.g. Edge profile, Chrome profile and Firefox) to keep separate waiter, kitchen and cashier sessions. Multiple ordinary tabs share a login cookie; multiple incognito windows may also share a cookie jar.

1. Waiter: `/order`, select an unused table or takeaway, add dishes/notes and send.
2. Kitchen: `/kitchen`, enable sound with the button, see the committed ticket appear, acknowledge and mark ready. Initial historical tickets do not beep. Keep the browser foregrounded; mobile policies may suspend audio/background work.
3. Cashier: `/cashier`, select the order and view/print its snapshot bill. Waiters may view their bills and cancel items with a reason. Use `/order/{id}` to append items; each submission creates another ticket.
4. Stop with Ctrl+C, start again, and verify orders remain. The default development database is `src/RestaurantPOS.Web/data/restaurant.db`. Data, key files and backups are ignored by Git. Do not delete this directory if you want to keep data.

Kitchen notifications use the existing Blazor connection and database reconciliation every five seconds. The built-in reconnect dialog covers lost connections. A separate authorized business hub is available for future integrations; SQLite remains authoritative.

## First-run administrator without demo seed

Disable `Seed__Enabled`. Configure a random setup token and a fresh data directory, then start:

```powershell
$env:Seed__Enabled = 'false'
$env:Development__SkipSignIn = 'false'
$env:Setup__Token = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
# View this only on the server console; use it in /setup. Never commit/share it.
$env:Setup__Token
dotnet run --project src/RestaurantPOS.Web --no-launch-profile --urls http://localhost:5083
```

Open `/setup`, enter the token, admin email and strong password. Setup closes after the first user exists. Remove `Setup__Token` and restart after setup. Production requires HTTPS and secure cookies. Full menu/staff administration is not yet implemented, so production operational setup is a later milestone.

## Test an Android device on the same Wi-Fi

For temporary development only, bind `--urls http://0.0.0.0:5083`, find the laptop LAN address with `ipconfig`, and browse `http://<laptop-ip>:5083`. If needed, an administrator can allow TCP 5083 only on the Private firewall profile and local subnet. Use demo accounts only on trusted Wi-Fi; remove the temporary firewall rule afterward. This does not require restaurant-router access. Production must use the HTTPS procedure below.

## Maintenance and references

- [API and real-time contract](docs/API.md)
- [ERD, financial invariants and planned entities](docs/ERD.md)
- [Windows deployment, HTTPS, restore and rollback](docs/DEPLOYMENT_WINDOWS.md)

Migrations apply at startup. To add a future migration: `dotnet tool restore`, then `dotnet ef migrations add <Name> --project src/RestaurantPOS.Infrastructure`. Review generated SQL before deployment. Never use `EnsureCreated` for this application.

Automated tests use actual temporary SQLite files and the ASP.NET HTTP test host. They cover idempotency, snapshots/decimal storage, table races, append tickets, cancellation/reconciliation, persistence through a new service instance, migration/backup restore, role restrictions and CSRF. Android rendering, audio, network disconnects, full browser interaction and Windows Service scripts still need manual acceptance testing.
