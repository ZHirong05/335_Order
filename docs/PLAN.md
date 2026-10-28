# Implementation plan

Repository inspected: original main contains README only. .NET SDK 10.0.400 installed.

## Scope and assumptions
First delivery is the ordering/kitchen vertical slice permitted by the request. One server, local SQLite, no internet at runtime. Money is stored as integer sen via decimal converters; UTC timestamps; Kuala Lumpur display. No production claims until acceptance testing on target hardware.

## Architecture
Core holds entities and DTOs; Infrastructure owns Identity, SQLite and transactional services; Web owns authorization, Blazor pages, REST and SignalR. One context per operation. SQLite immediate write transactions and unique active-table index protect concurrent writes. Events publish after commit; clients reconcile from SQLite every five seconds and after reconnect. No second SignalR connection for Blazor clients.

## Checklist
- [x] Identity login, first-run setup and explicit development seed
- [x] EF migration, snapshots, idempotent creation and active-table exclusion
- [x] Waiter ordering, append items, kitchen transitions/cancellation, cashier bill
- [x] Role-authorized API and business hub
- [x] Guided order-type/table/food selection, priced variations, category grids and cashier bill preview grid
- [x] Newest-first kitchen/cashier displays and atomic full-bill checkout with cash tender/change, receipt and table release
- [x] Daily display numbering with preserved internal references, saved-item previews, Add/Cancel order management, atomic batch cancellation and direct payment navigation and Back to order editing before payment
- [x] Optional cancellation reasons, distinct kitchen state colors, shared ongoing/settled bill cards and live removal of settled waiter orders
- [x] SQLite tests, backup smoke test, HTTP integration launch verification
- [x] Documentation and Windows scripts (scripts need target-PC validation)

## Follow-up milestones (not part of first slice)
- Refunds and amount-based splits (transactional item-based splits, allocations, full-bill payment/close and tender/change implemented)
- Daily/monthly reports, CSV and business cutoff boundaries
- Full admin CRUD UI/API (including variation editing), staff lifecycle and settings
- Durable outbox/event replay and kitchen cancellation acknowledgement (gesture-enabled sound implemented)
- Localization resources for English/Chinese/Malay (current page language is English)
- Production hardening, full requested test matrix, browser/Android acceptance and service deployment validation

Sending food returns to Continue an order. Checkout opens payment without a handoff or order state change. Back returns to editing before any payment is recorded. Partial payments lock items and remain accessible through Continue payment. Cashier lists settled bills only. Final settlement closes the order and releases its table; unfinished kitchen work stays visible. Production acceptance and refunds remain pending.
