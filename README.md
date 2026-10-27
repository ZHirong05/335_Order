# 335_Order

Local-first .NET 10 Blazor Interactive Server restaurant ordering and kitchen display. SQLite and ASP.NET Core Identity run on one PC. No external services are needed at runtime.

**Status: development POS, not a complete production release.** Ordering, kitchen tickets, audited cancellations, role login, bill previews/printing, full-bill and item-based split payments, migration and online backup are implemented. Refunds, reports and admin CRUD remain pending. Complete restaurant acceptance testing before live payment collection. See [implementation plan](docs/PLAN.md).

## Start on a Windows development laptop

For a quick preview without signing in, run `dotnet run --project src/RestaurantPOS.Web --launch-profile http` and open **http://localhost:5083/dashboard**. Development automatically enables sample data and a preview Admin identity. Production always requires authentication. No preview password is created. To test actual staff logins, disable preview as shown below. Existing demo passwords remain unchanged.

For automatic rebuilds during development, use `dotnet watch --no-hot-reload --project src/RestaurantPOS.Web --launch-profile http`. This fully restarts the app after code changes, which also rebuilds EF's cached model and applies migrations at startup. After adding entities or migrations, stop any existing hot-reload session with Ctrl+C before starting this command. Do not run two instances against the same development output directory.

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

1. Waiter: `/order` starts with **Take away** and **Dine-in** cards. Take away goes directly to food; dine-in opens a grid of tables. Food is grouped by category. Select a variation/quantity and **Add to order**, then **Send to kitchen**. Sending returns to **Continue an order**, whose cards show the actual saved items. **Back clears unsent cart items** without deleting anything already sent. Opening an existing order (or an occupied table) shows **Add Item**, **Cancel Item**, and **Checkout**. Cancellation supports selecting multiple items, bounded quantities and an optional reason; it preserves original tickets and notifies kitchen in red with cancelled quantities struck through.
2. Kitchen: `/kitchen` shows the newest tickets first. Enable sound with the button, see committed tickets appear, acknowledge and mark ready. Initial historical tickets do not beep. Keep the browser foregrounded; mobile policies may suspend audio/background work. Checkout does not hide unfinished food: paid New/Preparing tickets stay visible and can still be marked ready.
3. **Checkout** in the waiter order workspace sends the order to cashier and opens its bill. It does not record money or release the table. The cashier page has **Ongoing bills** (orders awaiting cashier) and **Settled bills** (closed orders). Both use the same saved-item preview cards as Continue an order. Settled orders leave Continue an order automatically, without a manual refresh. **Undo checkout & edit order** returns the unpaid order to the waiter for Add/Cancel edits; it cannot undo a completed payment. The payment form opens directly for Admin/Cashier. Select items and quantities for each person; the subtotal updates automatically. **Confirm payment & next person** saves that payment and shows only unpaid quantities. The final person uses **Confirm payment & close order**. The table stays occupied until the full balance is settled. The final receipt retains every original bill item and all payments with their item allocations. Undo checkout is available only before the first payment. Existing cash/change handling remains available; broader cashier design and statistics are deferred. The aggregate Total outstanding display is hidden, while underlying order/payment amounts remain stored.
4. Stop with Ctrl+C, start again, and verify orders remain. The default development database is `src/RestaurantPOS.Web/data/restaurant.db`. Data, key files and backups are ignored by Git. Do not delete this directory if you want to keep data.

Kitchen item colors distinguish **New (green)**, **Preparing (black)**, **Ready (black, on a slightly greyed card)** and **Cancelled (red with strike-through)**. Blank cancellation reasons are allowed; quantities remain validated.

Kitchen notifications use the existing Blazor connection and database reconciliation every five seconds. The built-in reconnect dialog covers lost connections. A separate authorized business hub is available for future integrations; SQLite remains authoritative.

Orders display daily numbers such as **#0001**, **#0002**, restarting at midnight in **Asia/Kuala_Lumpur**. Dine-in and takeaway share the same daily sequence. BusinessDate plus DailyNumber identifies the display number; the original GUID `Order.Id` and long `Order.OrderNumber` reference remain unchanged in SQLite/API for future sales statistics. The migration backfills existing orders chronologically without changing identifiers, items, payments or historical prices. Demo seed orders count in the sequence too. Financial statistics can be computed later from these records; hiding an aggregate does not remove its source data.

Development seed adds **Regular / Large (+RM 2.00)** for the demo rice/noodle dishes and **Hot / Iced (+RM 0.50)** for Teh Tarik and Kopi O. These are sample options, not confirmed restaurant pricing. Restart applies the additive `MenuVariations` migration and fills options on existing demo items that have none; existing orders and menu prices remain unchanged. Items without configured variations offer **Standard**. Server-side validation checks that a selected variation belongs to the item and is available; the selected name and final price are snapshotted for kitchen and bills. Variation administration is a later milestone.

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
