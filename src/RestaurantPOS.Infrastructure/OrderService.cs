using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RestaurantPOS.Core;

namespace RestaurantPOS.Infrastructure;

public interface IBusinessEvents { Task Publish(BusinessEvent notification); }
public sealed class OrderService(IDbContextFactory<PosDb> factory, IBusinessEvents events)
{
    public async Task<List<MenuItem>> Menu() { await using var db = await factory.CreateDbContextAsync(); return await db.Menu.AsNoTracking().Include(x => x.Category).Where(x => x.IsAvailable && x.Category.IsActive).OrderBy(x => x.SortOrder).ToListAsync(); }
    public async Task<List<DiningTable>> Tables() { await using var db = await factory.CreateDbContextAsync(); return await db.Tables.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Number).ToListAsync(); }
    public async Task<List<Order>> Active() { await using var db = await factory.CreateDbContextAsync(); return await db.Orders.AsNoTracking().Include(x => x.Table).Include(x => x.Items).Where(x => x.Status != OrderStatus.Closed).OrderBy(x => x.CreatedAtUtc).ToListAsync(); }
    public async Task<Bill> Bill(Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var o = await db.Orders.AsNoTracking().Include(x => x.Table).Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id) ?? throw new BusinessException("Order not found", 404);
        return new(o.Id, o.OrderNumber, o.Table == null ? "Takeaway" : $"Table {o.Table.Number}", o.Version, o.Items.Select(x => new BillLine(x.Id, x.ItemNameSnapshot, x.Quantity - x.CancelledQuantity, x.UnitPriceSnapshot, x.Notes)).ToList());
    }
    // Include cancelled tickets until the order closes so cancellations cannot disappear on reconnect.
    public async Task<List<KitchenTicket>> Tickets()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Tickets.AsNoTracking().Include(x => x.Order).ThenInclude(x => x.Table).Include(x => x.Items).ThenInclude(x => x.OrderItem).Where(x => x.Order.Status != OrderStatus.Closed).OrderBy(x => x.CreatedAtUtc).ToListAsync();
    }
    public Task<Guid> Create(CreateOrderInput input, string actor) => Write(actor, input.IdempotencyKey, input, async db =>
    {
        if (!Enum.IsDefined(input.OrderType) || (input.OrderType == OrderType.DineIn) != input.TableId.HasValue) throw new BusinessException("Choose a table for dine-in only", 400);
        if ((input.Notes ?? "").Length > 500) throw new BusinessException("Notes too long", 400);
        if (input.TableId.HasValue && (!await db.Tables.AnyAsync(x => x.Id == input.TableId && x.IsActive) || await db.Orders.AnyAsync(x => x.TableId == input.TableId && x.Status != OrderStatus.Closed))) throw new BusinessException("Table is unavailable");
        var o = new Order { OrderType = input.OrderType, TableId = input.TableId, Notes = input.Notes ?? "", CreatedByUserId = actor, OrderNumber = $"{(input.TableId.HasValue ? "D" : "T")}-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}" };
        db.Orders.Add(o);
        await Append(db, o, input.Items);
        return (o, "OrderCreated", (object)new { o.OrderNumber });
    });
    public Task<Guid> Add(Guid id, AddItemsInput input, string actor) => Write(actor, input.IdempotencyKey, new { id, input }, async db =>
    {
        var o = await Load(db, id, input.Version);
        await Append(db, o, input.Items);
        return (o, "KitchenTicketCreated", (object)new { Count = input.Items.Count });
    });
    public Task<Guid> Cancel(Guid id, Guid itemId, CancelInput input, string actor) => Write(actor, null, input, async db =>
    {
        var o = await Load(db, id, input.Version);
        var item = await db.OrderItems.SingleOrDefaultAsync(x => x.Id == itemId && x.OrderId == id) ?? throw new BusinessException("Item not found", 404);
        if (input.Quantity < 1 || input.Quantity > item.Quantity - item.CancelledQuantity || string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 500) throw new BusinessException("Provide a valid quantity and cancellation reason", 400);
        item.CancelledQuantity += input.Quantity;
        if (item.CancelledQuantity == item.Quantity) item.KitchenStatus = KitchenStatus.Cancelled;
        // Historical ticket quantity is preserved; remaining and cancelled quantities are rendered separately.
        var tickets = await db.Tickets.Include(x => x.Items).Where(x => x.Items.Any(i => i.OrderItemId == itemId)).ToListAsync();
        foreach (var ticket in tickets)
        {
            if (item.KitchenStatus == KitchenStatus.Cancelled) ticket.Items.Single(x => x.OrderItemId == itemId).Status = KitchenStatus.Cancelled;
            if (ticket.Items.All(x => x.Status == KitchenStatus.Cancelled)) ticket.Status = KitchenStatus.Cancelled;
        }
        return (o, "ItemCancelled", (object)new { itemId, input.Quantity, input.Reason });
    });
    public Task<Guid> SetTicket(Guid id, TicketStatusInput input, string actor) => Write(actor, null, input, async db =>
    {
        var t = await db.Tickets.Include(x => x.Items).ThenInclude(x => x.OrderItem).SingleOrDefaultAsync(x => x.Id == id) ?? throw new BusinessException("Ticket not found", 404);
        var o = await Load(db, t.OrderId, input.Version);
        if (!((t.Status == KitchenStatus.New && input.Status == KitchenStatus.Preparing) || (t.Status == KitchenStatus.Preparing && input.Status == KitchenStatus.Ready))) throw new BusinessException("Invalid kitchen transition");
        t.Status = input.Status;
        foreach (var i in t.Items.Where(x => x.Status != KitchenStatus.Cancelled)) { i.Status = input.Status; i.OrderItem.KitchenStatus = input.Status; }
        if (input.Status == KitchenStatus.Ready) t.CompletedAtUtc = DateTime.UtcNow;
        return (o, "KitchenTicketUpdated", (object)new { TicketId = id, input.Status });
    });
    private static async Task<Order> Load(PosDb db, Guid id, long version)
    {
        var o = await db.Orders.SingleOrDefaultAsync(x => x.Id == id) ?? throw new BusinessException("Order not found", 404);
        if (o.Status == OrderStatus.Closed || o.Version != version) throw new BusinessException("Order changed. Reload and retry.");
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
            var item = new OrderItem { OrderId = o.Id, MenuItemId = menu.Id, ItemNameSnapshot = menu.Name, UnitPriceSnapshot = menu.Price, Quantity = input.Quantity, Notes = input.Notes ?? "" };
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
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        // Persistence remains authoritative if delivery fails. Reconciliation repairs missed notifications.
        try { await events.Publish(new(audit.Id, audit.EventType, change.Order.Id, change.Order.Version)); } catch { /* polling reconciles */ }
        return change.Order.Id;
    }
}
