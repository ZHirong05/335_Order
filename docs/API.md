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
| GET /api/kitchen/tickets | Admin, Kitchen | authoritative active-order tickets, including cancellations |
| PATCH /api/kitchen/tickets/{id}/status | Admin, Kitchen | New → Preparing → Ready |

Enum JSON values are numeric: OrderType DineIn=0, Takeaway=1; KitchenStatus New=0, Preparing=1, Ready=2, Cancelled=3. GUIDs are strings. Money uses decimal RM values.

Create example:
```json
{"orderType":1,"tableId":null,"notes":"Pack separately","items":[{"menuItemId":1,"quantity":2,"notes":"No chili"}],"idempotencyKey":"client-generated-uuid"}
```
Add example: `{"version":1,"items":[{"menuItemId":1,"quantity":1,"notes":""}],"idempotencyKey":"another-client-uuid"}`.
Cancel: `{"version":2,"quantity":1,"reason":"Customer changed mind"}`.
Kitchen: `{"version":3,"status":1}`. Version is the **order** version; reload after a conflict. The same idempotency key and identical request replays the committed order ID. A changed request requires a new key. Keys are scoped to the actor.

## Real-time
`/hubs/orders` allows only Admin/Waiter/Kitchen/Cashier. The server joins authenticated role groups; clients cannot select arbitrary groups. Implemented event names: OrderCreated, KitchenTicketCreated, KitchenTicketUpdated, ItemCancelled. Payload: eventId, eventType, orderId, version. No mutable EF entities or sensitive metadata. UI uses a singleton server event dispatcher over its existing Blazor circuit, then database reconciliation every five seconds. Initial load and resumed circuits reload authoritative tickets. Hub integrations must reconnect and GET authoritative state themselves. Delivery is best effort after commit, not a durable outbox.

Payment, split-payment, close, reporting and admin CRUD endpoints are **not yet implemented**. PaymentRecorded/OrderClosed/OrderUpdated publication is reserved for those milestones.
