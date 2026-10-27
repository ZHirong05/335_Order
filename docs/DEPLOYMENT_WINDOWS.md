# Windows deployment (first-slice preparation)

Windows 10/11 x64, 8GB+ RAM. Development does not require restaurant hardware or router access. The application is not yet ready for live financial operations; follow PLAN.md for remaining milestones. Scripts are provided for review and must be tested on the actual PC with an administrator present. Do not execute service installation remotely without approved unattended access and a rollback contact.

## Publish and service

Run `./scripts/publish-windows.ps1 -Output <version-folder>` on the laptop. It publishes self-contained win-x64; `-FrameworkDependent` requires the .NET 10 ASP.NET Core runtime on the target. Restore/publish may need internet; the published application does not. Copy the complete output into a versioned local directory on the restaurant PC. Keep the previous version.

Use a configurable data location, conventionally `C:\ProgramData\RestaurantPOS\data`, outside publish/source. `Data__Directory` overrides the location. Grant Modify permission only to the service identity and administrators for data, keys and backups; publish files need read/execute. Restrict ACLs on configuration/certificate files. Data Protection keys persist in `data/keys`; the first slice relies on NTFS ACLs for key protection at rest. Back those keys up separately with restricted access. For additional protection, configure Windows DPAPI in a later hardening pass. Prefer a dedicated least-privilege service account; New-Service defaults to LocalSystem, so change the Log On identity before production start and validate its permissions.

In elevated PowerShell:

```powershell
./scripts/install-service.ps1 -Executable '<version-folder>\RestaurantPOS.Web.exe' -DataDirectory '<data-folder>'
```

The script configures auto-start and recovery but deliberately leaves it stopped for certificate/identity setup. Environment values live under `HKLM:\SYSTEM\CurrentControlSet\Services\RestaurantPOS`, `Environment` (MultiString). Add certificate variables and `Setup__Token` without dropping existing entries. Service restarts are required after environment changes. Keep all passwords/tokens out of source and publish configuration checked into Git. Remove setup token after initial setup.

## LAN HTTPS and Android trust

Reserve the server's LAN IPv4 address in the restaurant router DHCP settings, with owner approval. Configure a stable internal DNS name or IP matching the certificate SAN. Obtain a server certificate issued by a private CA trusted by every restaurant device, with Server Authentication EKU. A localhost development certificate does not establish Android trust.

Kestrel variables: `ASPNETCORE_URLS=https://0.0.0.0:7443`, `Kestrel__Certificates__Default__Path=<private-pfx-path>`, `Kestrel__Certificates__Default__Password=<secret>`. Restrict the PFX ACL to service identity/admins; set its password through secured deployment configuration. Alternatively terminate TLS at a maintained local reverse proxy and configure trusted forwarded headers/HTTPS behavior explicitly before using it; this repository does not configure a proxy.

On each Android device install only the private CA public root via Settings → Security → Encryption & credentials → Install certificate → CA certificate (labels vary). Protect the CA private key offline. Verify the certificate chain, hostname and expiry in the actual browser; do not bypass certificate warnings. Only then authenticate. Production cookies are Secure. Ensure system time is accurate even during internet outages.

Allow only the needed port on Private networks and the trusted subnet. Example, after confirming the real subnet:

```powershell
New-NetFirewallRule -DisplayName '335 POS trusted LAN' -Direction Inbound -Protocol TCP -LocalPort 7443 -Action Allow -Profile Private -RemoteAddress '<trusted-subnet-CIDR>'
Start-Service RestaurantPOS
```

Do not expose Kestrel publicly or configure router port forwarding. For remote maintenance use a private VPN such as Tailscale or an equivalent approved solution; installation and initial connection can require internet. Restaurant operation remains LAN-only. Arrange unattended PC access, VPN approval, administrator credentials handled privately, backup location and an on-site contact before remote deployment.

## Backups and verification

The hosted job takes one online backup per UTC date, checks integrity, and retains 30 days in `data/backups`. A service outage can skip days; no backup happens while the PC is off. Monitor application logs for failures and periodically copy verified backups to a separate protected disk. A backup on the same disk is insufficient for disk-loss recovery. Monitor available storage. Backup scripts take a snapshot via SQLite Online Backup API, not a bare live database copy:

```powershell
./scripts/backup-database.ps1 -Executable '<published-exe>' -DataDirectory '<data-folder>' -Destination '<new-backup-file.db>'
& '<published-exe>' verify '<backup-file.db>'
```

Destinations must be new files. Backups contain staff and order data; protect their ACLs. `verify` runs SQLite integrity_check, not business acceptance or migration compatibility checks. A restore drill should use a separate test data folder, start the matching version, sign in and verify representative orders/kitchen/bills.

## Update, rollback and restore

1. Stop new ordering and record the last order ID. Publish to a new version directory; do not overwrite the running version.
2. Run `update-service.ps1` with CurrentExecutable, NewExecutable, DataDirectory and BackupDestination. It stops the service, creates a verified online snapshot, changes the service executable and restarts. Startup applies migrations. Configuration/certificate variables must still be valid for the new directory.
3. Check service status, logs, authenticated screens and `/api/health`. Reconcile waiter/kitchen/cashier state before reopening.
4. If verification fails, stop the service, point `sc.exe config RestaurantPOS binPath=` back to the previous quoted executable, and restore the matching pre-upgrade database. Old code must not run blindly against a newer schema.
5. With **all application processes stopped**, run `restore-database.ps1 -Executable <matching-exe> -DataDirectory <data-folder> -Backup <verified-backup>`. It refuses a running named service, verifies integrity, preserves the prior DB/WAL/SHM with timestamp suffixes and copies the backup. Also stop any manually launched process; the script cannot prove none exists. Start service, verify role login and orders, then resume operation. Restoring loses transactions after the backup; reconcile them manually before reopening.

Retention affects only job-generated backups, not preserved rollback files. Review/remove rollback files manually after a successful restore drill. Never copy only a live `.db` file while its WAL may hold committed changes.

## Business time and money

Stored timestamps are UTC, display is Asia/Kuala_Lumpur, currency is RM. Daily display numbers restart at Malaysian midnight and are paired with BusinessDate; original GUIDs and long references remain stored for future reporting. Reports, configurable business-day cutoff and financial adjustment/refund calculations are not implemented. Waiter Checkout only hands an order to cashier and can be undone before payment; it does not release a table. Cashier payment settlement closes the order and produces a settled receipt. Cash tender/change is separate from collected revenue. Future reporting must use local date-boundary conversion to UTC, completed payment transactions and immutable item snapshots; never server-local dates or current menu prices.
