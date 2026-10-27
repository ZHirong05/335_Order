# Validation record

Development machine: Windows x64, .NET SDK 10.0.400, runtime 10.0.11.

Previously verified on the original first slice with actual SQLite and ASP.NET HTTP test host:
- First-run secret validation, transactional admin creation, setup closure and admin login.
- Cookie login by Waiter/Kitchen/Cashier, API role restrictions and antiforgery rejection/acceptance.
- Waiter-created takeaway order visible to kitchen API and cashier bill API.
- EF migration application, active-table write race exclusion, duplicate submission/reused-key rejection.
- Price snapshots, decimal RM precision, persisted order reads through a new service instance.
- Append creates distinct tickets; stale versions rejected.
- Cancellation after preparation preserves original ticket quantities/reason and reconciles from database.
- SQLite online backup restores a migrated database.

Previous Kestrel smoke check: `/api/health` returned 200 `Healthy`; `/login` returned 200. The initial 9 automated tests passed, and the subsequent development-preview update passed all 11 tests. PowerShell files parsed without syntax errors; service installation/update/restore scripts have not been executed against Windows Service Control Manager.

## Guided ordering and variations update (27 October 2026)

Build passed with zero warnings and zero errors. `git diff --check` passed. The additive MenuVariations migration was generated and reviewed: it creates only the variation table, FK, nonnegative-price constraint and unique name index; it does not update existing orders.

The guided-ordering update added cases for variation name/price snapshots, unavailable/missing/wrong-item variation rejection, independent append/cancellation, migration of an existing database and menu→order→kitchen→cashier preview HTTP integration. Earlier execution attempts were blocked by Windows Application Control (`0x800711C7`); a later rerun during checkout development completed successfully. No machine security policy was changed.

Demo variations are Regular/Large (+RM 2.00) and Hot/Iced (+RM 0.50), not confirmed production pricing. Restart the app once after updating so migrations apply before using the new screens.

## Newest-first displays and checkout (27 October 2026)

**All 35 automated tests passed** using actual SQLite and the ASP.NET HTTP test host. Checkout tests verify cash/change, all four non-cash methods, exact-balance/precision validation, stale bills, idempotency, concurrent checkout exclusion, closed-order edit rejection, table release/reuse, zero-balance closure without a synthetic payment, role restrictions/CSRF and continued preparation of paid kitchen tickets. Newest-first order/ticket queries are also covered.

Headless Edge verified the actual rendered UI against an isolated Development database on port 5084: Take away→category grid/filter→Iced drink with quantity/notes→send; newest-first cashier and kitchen cards; QR checkout; paid kitchen ticket→Preparing→Ready; Dine-in→10 table cards→Large meal→cash received/change→closed bill→table available; occupied-table append to the existing order; mobile-width layout and Escape dismissal of the variation picker. No browser page errors were observed. Screenshots and the one-off browser runner are in ignored `.local/browser-check/`. The test server was separate from the normal development database.

Pending manual acceptance: real Android touch/audio behavior and background suspension, actual browser reconnect, power/network outage, complete stop/start with live browser sessions, HTTPS trust, service identity/file ACLs, target-PC publish/deploy/restore drill. Partial/split-payment, refund and reporting tests remain pending with those features.

## Daily numbering and reversible waiter checkout

All 41 tests passed after this update. New coverage verifies daily sequences reset at Malaysian midnight, concurrent creation gets unique numbers, idempotent retries preserve numbers, and migration preserves legacy GUID/reference/price data. Batch cancellation tests cover all-or-nothing rollback, remaining-quantity limits, idempotency and preserved ticket quantities/reasons. Handoff/undo tests verify edit locks, no payment during handoff, table occupancy, rejection after payment, and a concurrent payment-versus-undo race. HTTP tests verify item previews, hidden long references, cashier visibility only after handoff, undo and cancellation rendering.

Headless Edge verified the rendered UI with an isolated test database: Back discards new unsent items; submitting returns to Continue an order; cards show saved contents and daily labels; existing orders show Add Item/Cancel Item; Back from Add discards its draft; numeric cancellation input clamps to remaining quantity; the cancellation reason immediately enables confirmation; kitchen receives a live red, struck-through cancellation; waiter Checkout opens the bill without payment; Undo checkout returns to editable order actions; subsequent additions preserve the same internal ID/reference/display number. The aggregate Total outstanding text is absent. Mobile order actions were checked at 390px without horizontal overflow. Screenshots and runner are in ignored `.local/browser-check/`. No page errors were observed. The cancellation reason input binding was corrected after the first browser run exposed a blur-dependent confirmation button.

Restart once after updating: this migration adds BusinessDate/DailyNumber and CancellationReason, then backfills existing daily labels. No Windows security policy was changed for these checks. Production financial reporting and further cashier UI design remain deferred.

## Optional reasons, kitchen colors and bill sections

All 44 automated tests passed. New tests cover single/batch cancellation with no reason, HTTP cancellation with an omitted reason field, cashier retrieval of pending/settled records and exclusion of settled records from the waiter list. Price and payment assertions remain on the detailed bill/receipt; shared preview cards focus on item contents.

Headless Edge verified blank-reason cancellation without blurring the field, distinct new/preparing/cancelled item colors, retained strike-through on cancelled quantities, shared Ongoing bill cards, live movement into Settled bills after payment, automatic removal from an already-open Continue an order list, reopening a settled receipt and mobile layout at 390px. Measured colors: new rgb(154,75,0), preparing rgb(28,95,133), cancelled rgb(162,32,32). No page errors were observed. Screenshots and the isolated runner remain under ignored `.local/browser-check/`.


Item-based split payment validation: 48 automated tests passed, including persistent remaining quantities, idempotency, invalid selection rollback, stale/overpaid quantity rejection, full remaining settlement, table occupancy/release, and migration backfill for existing full payments. Headless Edge passed direct payment form, partial quantity payment, remaining quantities after reload, final consolidated receipt and 390px phone layout against an isolated SQLite database.
