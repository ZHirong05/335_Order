using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RestaurantPOS.Core;

namespace RestaurantPOS.Infrastructure;

public interface IBusinessEvents { Task Publish(BusinessEvent notification); }
public sealed class OrderService(IDbContextFactory<PosDb> factory, IBusinessEvents events, TimeProvider? clock = null)
{
    public async Task<List<MenuItem>> Menu() { await using var db = await factory.CreateDbContextAsync(); return await db.Menu.AsNoTracking().Include(x => x.Category).Include(x => x.Variations.Where(v => v.IsAvailable)).Where(x => x.IsAvailable && x.Category.IsActive).OrderBy(x => x.Category.SortOrder).ThenBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync(); }
    public async Task<List<DiningTable>> Tables() { await using var db = await factory.CreateDbContextAsync(); return await db.Tables.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Number).ToListAsync(); }
    public async Task<List<Order>> Active() { await using var db = await factory.CreateDbContextAsync(); return await db.Orders.AsNoTracking().Include(x => x.Table).Include(x => x.Items).Where(x => x.Status != OrderStatus.Closed).OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).ToListAsync(); }
    public async Task<List<Order>> CashierOrders()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Orders.AsNoTracking().Include(x => x.Table).Include(x => x.Items)
            .Where(x => x.Status == OrderStatus.AwaitingPayment || x.Status == OrderStatus.Closed)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).ToListAsync();
    }
    public async Task<Bill> Bill(Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var o = await db.Orders.AsNoTracking().Include(x => x.Table).Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id) ?? throw new BusinessException("Order not found", 404);
        var recorded = await db.Payments.AsNoTracking().Include(p => p.Allocations).ThenInclude(a => a.OrderItem)
            .Where(x => x.OrderId == id).OrderBy(x => x.ReceivedAtUtc).ThenBy(x => x.Id).ToListAsync();
        var paidQuantities = recorded.SelectMany(p => p.Allocations).GroupBy(a => a.OrderItemId).ToDictionary(g => g.Key, g => g.Sum(a => a.Quantity));
        var payments = recorded.Select(p => new PaymentView(p.Id, p.Amount, p.Method, p.CashTendered, p.Change, p.Reference, p.ReceivedAtUtc,
            p.Allocations.Select(a => new PaymentAllocationView(a.OrderItemId, a.OrderItem.ItemNameSnapshot, a.Quantity, a.Amount)).ToList())).ToList();
        return new(o.Id, o.OrderNumber, o.Table == null ? "Takeaway" : $"Table {o.Table.Number}", o.Version,
            o.Items.Select(x => new BillLine(x.Id, x.ItemNameSnapshot, x.Quantity - x.CancelledQuantity, x.UnitPriceSnapshot, x.Notes, paidQuantities.GetValueOrDefault(x.Id))).ToList(),
            o.Status, payments, o.DisplayNumber, o.BusinessDate);
    }
    // Payment must not remove food that the kitchen still needs to prepare.
    public async Task<List<KitchenTicket>> Tickets()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Tickets.AsNoTracking().Include(x => x.Order).ThenInclude(x => x.Table).Include(x => x.Items).ThenInclude(x => x.OrderItem).Where(x => x.Order.Status != OrderStatus.Closed || x.Status == KitchenStatus.New || x.Status == KitchenStatus.Preparing).OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).ToListAsync();
    }
    public Task<Guid> Create(CreateOrderInput input, string actor) => Write(actor, input.IdempotencyKey, input, async db =>
    {
        if (!Enum.IsDefined(input.OrderType) || (input.OrderType == OrderType.DineIn) != input.TableId.HasValue) throw new BusinessException("Choose a table for dine-in only", 400);
        if ((input.Notes ?? "").Length > 500) throw new BusinessException("Notes too long", 400);
        if (input.TableId.HasValue && (!await db.Tables.AnyAsync(x => x.Id == input.TableId && x.IsActive) || await db.Orders.AnyAsync(x => x.TableId == input.TableId && x.Status != OrderStatus.Closed))) throw new BusinessException("Table is unavailable");
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var businessDate = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuala_Lumpur")).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var number = (await db.Orders.Where(x => x.BusinessDate == businessDate).MaxAsync(x => (int?)x.DailyNumber) ?? 0) + 1;
        var o = new Order { OrderType = input.OrderType, TableId = input.TableId, Notes = input.Notes ?? "", CreatedByUserId = actor, CreatedAtUtc = now, UpdatedAtUtc = now, BusinessDate = businessDate, DailyNumber = number, OrderNumber = $"{(input.TableId.HasValue ? "D" : "T")}-{now:yyyyMMdd}-{Guid.NewGuid():N}" };
        db.Orders.Add(o);
        await Append(db, o, input.Items);
        return (o, "OrderCreated", (object)new { o.OrderNumber });
    });
    public Task<Guid> Add(Guid id, AddItemsInput input, string actor) => Write(actor, input.IdempotencyKey, new { id, input }, async db =>
    {
        var o = await Load(db, id, input.Version);
        RequireEditable(o);
        await Append(db, o, input.Items);
        return (o, "KitchenTicketCreated", (object)new { Count = input.Items.Count });
    });
    public Task<Guid> Cancel(Guid id, Guid itemId, CancelInput input, string actor) => Write(actor, null, input, async db =>
    {
        var o = await Load(db, id, input.Version); RequireEditable(o);
        await ApplyCancellation(db, id, itemId, input.Quantity, input.Reason);
        return (o, "ItemCancelled", (object)new { itemId, input.Quantity, input.Reason });
    });
    public Task<Guid> CancelItems(Guid id, CancelItemsInput input, string actor)
    {
        RequireKey(input.IdempotencyKey);
        return Write(actor, input.IdempotencyKey, new { Operation = "CancelItems", id, input }, async db =>
        {
            var o = await Load(db, id, input.Version); RequireEditable(o);
            if (input.Items == null || input.Items.Count is < 1 or > 100 || input.Items.Any(x => x is null) || input.Items.Select(x => x.ItemId).Distinct().Count() != input.Items.Count)
                throw new BusinessException("Select distinct items to cancel", 400);
            foreach (var line in input.Items) await ApplyCancellation(db, id, line.ItemId, line.Quantity, input.Reason);
            return (o, "ItemCancelled", (object)new { input.Items, input.Reason });
        });
    }
    private static async Task ApplyCancellation(PosDb db, Guid orderId, Guid itemId, int quantity, string reason)
    {
        var item = await db.OrderItems.SingleOrDefaultAsync(x => x.Id == itemId && x.OrderId == orderId) ?? throw new BusinessException("Item not found", 404);
        if (quantity < 1 || quantity > item.Quantity - item.CancelledQuantity || (reason?.Length ?? 0) > 500)
            throw new BusinessException("Provide a valid quantity and an optional reason up to 500 characters", 400);
        item.CancelledQuantity += quantity;
        if (item.CancelledQuantity == item.Quantity) item.KitchenStatus = KitchenStatus.Cancelled;
        var tickets = await db.Tickets.Include(x => x.Items).Where(x => x.Items.Any(i => i.OrderItemId == itemId)).ToListAsync();
        foreach (var ticket in tickets)
        {
            var ticketItem = ticket.Items.Single(x => x.OrderItemId == itemId);
            ticketItem.CancellationReason = reason?.Trim() ?? "";
            if (item.KitchenStatus == KitchenStatus.Cancelled) ticketItem.Status = KitchenStatus.Cancelled;
            if (ticket.Items.All(x => x.Status == KitchenStatus.Cancelled)) ticket.Status = KitchenStatus.Cancelled;
        }
    }
    public Task<Guid> RequestCheckout(Guid id, HandoffInput input, string actor) => Handoff(id, input, actor, undo: false);
    public Task<Guid> UndoCheckout(Guid id, HandoffInput input, string actor) => Handoff(id, input, actor, undo: true);
    private Task<Guid> Handoff(Guid id, HandoffInput input, string actor, bool undo)
    {
        RequireKey(input.IdempotencyKey);
        return Write(actor, input.IdempotencyKey, new { Operation = undo ? "UndoCheckout" : "RequestCheckout", id, input }, async db =>
        {
            var o = await Load(db, id, input.Version);
            if (await db.Payments.AnyAsync(p => p.OrderId == id)) throw new BusinessException("Paid orders cannot be returned for editing");
            if (o.Status != (undo ? OrderStatus.AwaitingPayment : OrderStatus.Open)) throw new BusinessException("Order status changed. Refresh before continuing.");
            o.Status = undo ? OrderStatus.Open : OrderStatus.AwaitingPayment;
            return (o, "OrderUpdated", (object)new { Action = undo ? "CheckoutUndone" : "CheckoutRequested", o.Status });
        });
    }
    private static void RequireKey(string? key) { if (string.IsNullOrWhiteSpace(key)) throw new BusinessException("An idempotency key is required", 400); }
    private static void RequireEditable(Order o) { if (o.Status != OrderStatus.Open) throw new BusinessException("Undo checkout before editing this order"); }
    public Task<Guid> SetTicket(Guid id, TicketStatusInput input, string actor) => Write(actor, null, input, async db =>
    {
        var t = await db.Tickets.Include(x => x.Items).ThenInclude(x => x.OrderItem).SingleOrDefaultAsync(x => x.Id == id) ?? throw new BusinessException("Ticket not found", 404);
        var o = await Load(db, t.OrderId, input.Version, allowClosed: true);
        if (!((t.Status == KitchenStatus.New && input.Status == KitchenStatus.Preparing) || (t.Status == KitchenStatus.Preparing && input.Status == KitchenStatus.Ready))) throw new BusinessException("Invalid kitchen transition");
        t.Status = input.Status;
        foreach (var i in t.Items.Where(x => x.Status != KitchenStatus.Cancelled)) { i.Status = input.Status; i.OrderItem.KitchenStatus = input.Status; }
        if (input.Status == KitchenStatus.Ready) t.CompletedAtUtc = DateTime.UtcNow;
        return (o, "KitchenTicketUpdated", (object)new { TicketId = id, input.Status });
    });
    public Task<Guid> Checkout(Guid id, CheckoutInput input, string actor)
    {
        RequireKey(input.IdempotencyKey);
        return Write(actor, input.IdempotencyKey, new { id, input }, db => RecordPayment(db, id, input.Version, null, input.Amount, input.Method, input.CashTendered, input.Reference, input.IdempotencyKey, actor));
    }
    public Task<Guid> SplitPayment(Guid id, SplitPaymentInput input, string actor)
    {
        RequireKey(input.IdempotencyKey);
        if (input.Items == null) throw new BusinessException("Select items for this payment", 400);
        return Write(actor, input.IdempotencyKey, new { Operation = "SplitPayment", id, input }, db => RecordPayment(db, id, input.Version, input.Items, input.Amount, input.Method, input.CashTendered, input.Reference, input.IdempotencyKey, actor));
    }
    private static async Task<(Order Order, string Event, object Details)> RecordPayment(PosDb db, Guid id, long version, List<PaymentSelection>? selections, decimal amount, PaymentMethod method, decimal? cashTendered, string reference, string key, string actor)
    {
        var order = await Load(db, id, version);
        if (order.Status != OrderStatus.AwaitingPayment) throw new BusinessException("Send the order to cashier before recording payment");
        if (!Enum.IsDefined(method) || (reference?.Length ?? 0) > 100 || !ValidMoney(amount)) throw new BusinessException("Invalid payment method, amount or reference", 400);
        var items = await db.OrderItems.Where(x => x.OrderId == id).ToListAsync();
        var previous = await db.Payments.Include(p => p.Allocations).Where(p => p.OrderId == id).ToListAsync();
        var paidQuantities = previous.SelectMany(p => p.Allocations).GroupBy(a => a.OrderItemId).ToDictionary(g => g.Key, g => g.Sum(a => a.Quantity));
        var balance = items.Sum(x => (x.Quantity - x.CancelledQuantity) * x.UnitPriceSnapshot) - previous.Sum(p => p.Amount);
        if (balance < 0) throw new BusinessException("The recorded payment balance is invalid. Ask the administrator to review the bill.");
        if (selections == null)
        {
            if (amount != balance) throw new BusinessException("Full payment must settle the exact outstanding balance. Refresh the bill and retry.");
            selections = items.Where(i => i.Quantity - i.CancelledQuantity > paidQuantities.GetValueOrDefault(i.Id))
                .Select(i => new PaymentSelection(i.Id, i.Quantity - i.CancelledQuantity - paidQuantities.GetValueOrDefault(i.Id))).ToList();
        }
        else if (selections.Count is < 1 or > 100 || selections.Any(s => s is null) || selections.Select(s => s.OrderItemId).Distinct().Count() != selections.Count)
            throw new BusinessException("Choose 1 to 100 distinct item lines for this payment", 400);
        var allocations = new List<PaymentAllocation>();
        foreach (var selection in selections)
        {
            var item = items.SingleOrDefault(i => i.Id == selection.OrderItemId) ?? throw new BusinessException("Selected item does not belong to this order", 400);
            var remaining = item.Quantity - item.CancelledQuantity - paidQuantities.GetValueOrDefault(item.Id);
            if (selection.Quantity < 1 || selection.Quantity > remaining) throw new BusinessException("Selected quantities exceed the unpaid quantities. Refresh the bill and retry.");
            allocations.Add(new PaymentAllocation { OrderItemId = item.Id, Quantity = selection.Quantity, Amount = selection.Quantity * item.UnitPriceSnapshot });
        }
        if (allocations.Sum(a => a.Amount) != amount || amount > balance) throw new BusinessException("The payment must equal the selected items' subtotal");
        if (amount == 0 && balance > 0) throw new BusinessException("Include a priced item in this payment, or select the full remaining bill", 400);
        var tendered = cashTendered ?? amount;
        if (!ValidMoney(tendered) || tendered < amount || (method != PaymentMethod.Cash && tendered != amount)) throw new BusinessException("Cash received must cover this subtotal. Non-cash payments must equal it.", 400);
        if (amount == 0 && tendered != 0) throw new BusinessException("No payment is due for these items", 400);
        var complete = amount == balance;
        if (amount > 0)
        {
            // Include any remaining free items with the final payer, without charging extra.
            if (complete)
                foreach (var item in items.Where(i => i.UnitPriceSnapshot == 0 && i.Quantity - i.CancelledQuantity > paidQuantities.GetValueOrDefault(i.Id)))
                {
                    var free = allocations.SingleOrDefault(a => a.OrderItemId == item.Id);
                    if (free == null) allocations.Add(new PaymentAllocation { OrderItemId = item.Id, Quantity = item.Quantity - item.CancelledQuantity - paidQuantities.GetValueOrDefault(item.Id), Amount = 0m });
                    else free.Quantity = item.Quantity - item.CancelledQuantity - paidQuantities.GetValueOrDefault(item.Id);
                }
            var payment = new Payment { OrderId = id, Amount = amount, Method = method, CashTendered = tendered, Change = tendered - amount, ReceivedByUserId = actor, Reference = reference?.Trim() ?? "", IdempotencyKey = actor + ":" + key, Allocations = allocations };
            db.Payments.Add(payment);
            var details = new { PaymentId = payment.Id, payment.Amount, payment.Method, payment.CashTendered, payment.Change, Items = allocations.Select(a => new { a.OrderItemId, a.Quantity, a.Amount }).ToList() };
            if (!complete) return (order, "PaymentRecorded", details);
            db.Events.Add(new OrderEvent { OrderId = id, ActorUserId = actor, EventType = "PaymentRecorded", DetailsJson = JsonSerializer.Serialize(details) });
        }
        order.Status = OrderStatus.Closed;
        return (order, "OrderClosed", new { Amount = amount, TableReleased = order.TableId });
    }
    private static bool ValidMoney(decimal amount) => amount >= 0 && amount <= 999999999m && decimal.Round(amount, 2) == amount;
    private static async Task<Order> Load(PosDb db, Guid id, long version, bool allowClosed = false)
    {
        var o = await db.Orders.SingleOrDefaultAsync(x => x.Id == id) ?? throw new BusinessException("Order not found", 404);
        if ((!allowClosed && o.Status == OrderStatus.Closed) || o.Version != version) throw new BusinessException("Order changed or is closed. Reload and retry.");
        o.Version++; o.UpdatedAtUtc = DateTime.UtcNow;
        return o;
    }
    private static async Task Append(PosDb db, Order o, List<ItemInput>? items)
    {
        if (items == null || items.Count is < 1 or > 100) throw new BusinessException("Choose 1–100 items", 400);
        var ticket = new KitchenTicket { OrderId = o.Id, TicketNumber = $"K-{Guid.NewGuid():N}" };
        foreach (var input in items)
        {
            if (input.Quantity is < 1 or > 99 || (input.Notes ?? "").Length > 500) throw new BusinessException("Invalid quantity or notes", 400);
            var menu = await db.Menu.Include(x => x.Category).SingleOrDefaultAsync(x => x.Id == input.MenuItemId && x.IsAvailable && x.Category.IsActive) ?? throw new BusinessException("Menu item unavailable");
            MenuItemVariation? variation = null;
            if (input.VariationId.HasValue)
                variation = await db.MenuVariations.SingleOrDefaultAsync(v => v.Id == input.VariationId && v.MenuItemId == menu.Id && v.IsAvailable)
                    ?? throw new BusinessException("Variation unavailable for this item", 400);
            var item = new OrderItem { OrderId = o.Id, MenuItemId = menu.Id, ItemNameSnapshot = variation == null ? menu.Name : $"{menu.Name} ({variation.Name})", UnitPriceSnapshot = menu.Price + (variation?.AdditionalPrice ?? 0m), Quantity = input.Quantity, Notes = input.Notes ?? "" };
            db.OrderItems.Add(item);
            ticket.Items.Add(new KitchenTicketItem { OrderItemId = item.Id, Quantity = item.Quantity, NotesSnapshot = item.Notes });
        }
        db.Tickets.Add(ticket);
    }
    private async Task<Guid> Write<T>(string actor, string? key, T request, Func<PosDb, Task<(Order Order, string Event, object Details)>> mutation)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new BusinessException("Authentication required", 401);
        if (key != null && (key.Length < 8 || key.Length > 100)) throw new BusinessException("Idempotency key must have 8–100 characters", 400);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
        var scopedKey = key == null ? null : actor + ":" + key;
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.OpenConnectionAsync();
        // Non-deferred transaction obtains SQLite's writer reservation before any read/check.
        using var tx = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: false);
        await db.Database.UseTransactionAsync(tx);
        if (scopedKey != null && await db.Requests.FindAsync(scopedKey) is { } previous)
        {
            if (previous.RequestHash != hash) throw new BusinessException("Idempotency key reused for different request");
            return previous.OrderId;
        }
        var change = await mutation(db);
        var audit = new OrderEvent { OrderId = change.Order.Id, EventType = change.Event, ActorUserId = actor, DetailsJson = JsonSerializer.Serialize(change.Details) };
        db.Events.Add(audit);
        if (scopedKey != null) db.Requests.Add(new RequestRecord { Key = scopedKey, RequestHash = hash, OrderId = change.Order.Id });
        var notifications = db.ChangeTracker.Entries<OrderEvent>().Where(x => x.State == EntityState.Added).Select(x => new BusinessEvent(x.Entity.Id, x.Entity.EventType, x.Entity.OrderId, change.Order.Version)).ToList();
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        // Persistence remains authoritative if delivery fails. Reconciliation repairs missed notifications.
        foreach (var notification in notifications)
            try { await events.Publish(notification); } catch { /* polling reconciles */ }
        return change.Order.Id;
    }
}
