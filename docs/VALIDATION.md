# Validation record

Development machine: Windows x64, .NET SDK 10.0.400, runtime 10.0.11.

Verified with actual SQLite and ASP.NET HTTP test host:
- First-run secret validation, transactional admin creation, setup closure and admin login.
- Cookie login by Waiter/Kitchen/Cashier, API role restrictions and antiforgery rejection/acceptance.
- Waiter-created takeaway order visible to kitchen API and cashier bill API.
- EF migration application, active-table write race exclusion, duplicate submission/reused-key rejection.
- Price snapshots, decimal RM precision, persisted order reads through a new service instance.
- Append creates distinct tickets; stale versions rejected.
- Cancellation after preparation preserves original ticket quantities/reason and reconciles from database.
- SQLite online backup restores a migrated database.

Kestrel smoke check: `/api/health` returned 200 `Healthy`; `/login` returned 200. Smoke server stopped afterward. All 9 automated tests passed. PowerShell files parsed without syntax errors; service installation/update/restore scripts have not been executed against Windows Service Control Manager.

Pending manual acceptance: Android touch layout, sound permissions/background suspension, actual browser reconnect, power/network outage, complete stop/start with live browser sessions, HTTPS trust, service identity/file ACLs, target-PC publish/deploy/restore drill. The automated HTTP flow is not a browser automation test. Payment/refund/split/close/reporting tests are pending with those features.
