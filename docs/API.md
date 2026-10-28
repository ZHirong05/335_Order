# First-slice API

Cookie authentication; no bearer tokens. All except health require login. API authentication failures return 401/403. Errors use Problem Details for domain/antiforgery failures. Unknown IDs return 404; invalid input 400; stale version, occupied table and key reuse 409. Unexpected failures are logged and return 500.

GET `/api/antiforgery` returns `{token,header}`. Send the returned token in `RequestVerificationToken` on every JSON mutation, using the same cookie session. Login/setup/logout forms include a hidden antiforgery token.

| Method/path | Roles | Behavior |
|---|---|---|
| GET /api/health | public | SQLite connectivity, no sensitive data |
| GET /api/menu | Admin, Waiter, Cashier | available menu DTOs |
| GET /api/tables | Admin, Waiter, Cashier | active tables and occupancy |
| GET /api/orders/active | Admin, Waiter, Cashier | active order summaries |
| GET /api/orders/{id} | Admin, Waiter, Cashier | snapshot bill |
| GET /api/orders/{id}/bill | Admin, Waiter, Cashier | snapshot bill |
| POST /api/orders | Admin, Waiter | idempotent creation, 201 + Location |
| POST /api/orders/{id}/items | Admin, Waiter | append new ticket |
| POST /api/orders/{id}/items/{itemId}/cancel | Admin, Waiter | audited cancellation |
| POST /api/orders/{id}/checkout | Admin, Cashier | record full settlement and close order atomically |
| POST /api/orders/{id}/cancel-items | Admin, Waiter | atomic, idempotent multi-item cancellation |
| GET /api/kitchen/tickets | Admin, Kitchen | authoritative active-order tickets, including cancellations |
| PATCH /api/kitchen/tickets/{id}/status | Admin, Kitchen | New → Preparing → Ready |

Enum JSON values are numeric: OrderType DineIn=0, Takeaway=1; KitchenStatus New=0, Preparing=1, Ready=2, Cancelled=3. GUIDs are strings. Money uses decimal RM values.

`GET /api/menu` now includes `variations: [{id,name,additionalPrice,price}]` on each item. Only available variations are returned. `price` is the final unit price; `additionalPrice` is the extra charge over the menu item's base price. Each submitted item may include optional `variationId` in both creation and append requests. The server validates availability and item ownership and calculates the actual price; client-supplied prices are never accepted. Omitting `variationId` keeps the base item for existing integrations. The interactive UI requires choosing one of the offered options. Variation names are appended to the immutable item name snapshot, and their extra price is included in the unit-price snapshot.

Create example:
```json
{"orderType":1,"tableId":null,"notes":"Pack separately","items":[{"menuItemId":1,"quantity":2,"notes":"No chili"}],"idempotencyKey":"client-generated-uuid"}
```
Add example: `{"version":1,"items":[{"menuItemId":1,"quantity":1,"notes":""}],"idempotencyKey":"another-client-uuid"}`.
With a variation: `{"menuItemId":1,"quantity":2,"notes":"No chili","variationId":2}` (use the IDs returned by the menu endpoint).
Cancel: `{"version":2,"quantity":1,"reason":"Customer changed mind"}`.
Kitchen: `{"version":3,"status":1}`. Version is the **order** version; reload after a conflict. The same idempotency key and identical request replays the committed order ID. A changed request requires a new key. Keys are scoped to the actor.

## Full-bill checkout
Checkout is a navigation link to `/cashier/{id}`. Opening payment makes no database changes; Back links to `/order/{id}`. Payment endpoints accept an Open order directly. Partial payment sets AwaitingPayment internally to preserve paid-item locking; this is not a cashier queue. Legacy AwaitingPayment orders without payments remain editable. The handoff and undo-checkout endpoints have been removed. Existing order IDs, versions and payment records remain intact.

`POST /api/orders/{id}/checkout` accepts:
```json
{"version":3,"amount":17.00,"method":0,"cashTendered":20.00,"reference":"Counter 1","idempotencyKey":"checkout-client-uuid"}
```
PaymentMethod: Cash=0, QR=1, Transfer=2, Card=3, Other=4. Non-cash clients send `cashTendered:null`. The amount must equal the current outstanding balance, and the version must match. Cash tender may exceed the balance; excess is tracked as change, never revenue. Non-cash tender must equal the balance. Money inputs must be nonnegative, have at most two decimal places and not exceed RM 999,999,999. A zero-balance order can close with amount/tender zero and no payment row. References are at most 100 characters. Returns 200 with the order ID; repeating the same key/request does not create another payment. Changed/stale requests return 409 and invalid tender 400.

Payment, audit events and order closure use one SQLite immediate transaction. Closing releases the table through the existing active-order unique index. Closed orders reject item additions/cancellations. Bill DTOs include status, payment details, actual paid amount and balance. Unfinished New/Preparing kitchen tickets remain visible and updateable after checkout until ready, even when the table has been reused; distinguish tickets by order number. Cashier active bills and kitchen tickets are sorted newest-first. This endpoint records completed payments only; no payment gateway or refunds are implemented. Item-based split settlement uses the endpoint below.

## Daily display numbers and cancellations
Order/bill/ticket DTOs now include `displayNumber` (e.g. `#0001`) and `businessDate` (`yyyy-MM-dd`, Malaysian date). The daily sequence is shared by dine-in/takeaway and allocated under the same SQLite writer transaction as creation. Idempotent creation does not consume another number. `id` and the original long `orderNumber` remain the internal references; do not use the daily display number alone as a globally unique key. UI hides long references and the aggregate outstanding total; stored amounts and IDs remain available for future statistics.

`cancel-items` example: `{"version":3,"items":[{"itemId":"order-item-guid","quantity":1}],"reason":"Customer changed mind","idempotencyKey":"cancel-client-uuid"}`. Items must be distinct and belong to this order. Each quantity must be positive and no greater than its uncancelled quantity; the entire batch rolls back if any line is invalid. The original kitchen quantities remain unchanged; linked cancelled quantities and latest cancellation reasons are returned in kitchen DTOs. ItemCancelled is emitted after commit. Cancellation reasons may be omitted or blank (maximum 500 characters when supplied). New items are green, preparing and ready items black, and cancelled quantities red and struck through. Ready tickets use a light grey background while retaining readable text. Previous reasons remain in the immutable order event audit.

## Real-time
`/hubs/orders` allows only Admin/Waiter/Kitchen/Cashier. The server joins authenticated role groups; clients cannot select arbitrary groups. Implemented event names: OrderCreated, KitchenTicketCreated, KitchenTicketUpdated, ItemCancelled. Payload: eventId, eventType, orderId, version. No mutable EF entities or sensitive metadata. UI uses a singleton server event dispatcher over its existing Blazor circuit, then database reconciliation every five seconds. Initial load and resumed circuits reload authoritative tickets. Hub integrations must reconnect and GET authoritative state themselves. Delivery is best effort after commit, not a durable outbox.

Payment settlement also publishes PaymentRecorded and OrderClosed, only after commit. A zero-balance close emits OrderClosed without PaymentRecorded. Refund, reporting and admin CRUD endpoints are **not yet implemented**.

The cashier UI and CashierOrders query list Closed orders only. Unpaid and partially paid orders remain in Continue an order; settled orders disappear through notifications and five-second reconciliation.


## Item-based split payments
`POST /api/orders/{id}/split-payments` (Admin/Cashier, antiforgery required) accepts:
```json
{"version":3,"items":[{"orderItemId":"<guid>","quantity":1}],"amount":8.50,"method":0,"cashTendered":10.00,"reference":"Person 1","idempotencyKey":"unique-client-key"}
```
The amount must exactly match the selected unpaid quantities at saved prices. Each successful payment stores item allocations, increments the order version and emits PaymentRecorded after commit. Repeating an identical request with the same actor/key returns the original result; changed payloads are rejected. Partial payment keeps AwaitingPayment and table occupancy. The final payment also closes the order and releases its table atomically. Item editing is unavailable after any payment. Bill lines expose PaidQuantity and RemainingQuantity, while preserving original net quantities; Payments include Items allocations. Full checkout can settle the remainder after earlier split payments. Free items are included with the final payer; free-only partial payments are rejected.
