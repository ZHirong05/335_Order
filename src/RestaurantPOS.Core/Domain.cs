namespace RestaurantPOS.Core;

public enum OrderType { DineIn, Takeaway }
public enum OrderStatus { Open, AwaitingPayment, Closed }
public enum KitchenStatus { New, Preparing, Ready, Cancelled }
public sealed class DiningTable
{
    public int Id { get; set; }
    public int Number { get; set; }
    public int Capacity { get; set; } = 4;
    public bool IsActive { get; set; } = true;
    public long Version { get; set; } = 1;
}
public sealed class MenuCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
public sealed class MenuItem
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public MenuCategory Category { get; set; } = null!;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public bool IsAvailable { get; set; } = true;
    public int SortOrder { get; set; }
}
public sealed class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OrderNumber { get; set; } = "";
    public OrderType OrderType { get; set; }
    public int? TableId { get; set; }
    public DiningTable? Table { get; set; }
    public OrderStatus Status { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string CreatedByUserId { get; set; } = "";
    public string Notes { get; set; } = "";
    public long Version { get; set; } = 1;
    public List<OrderItem> Items { get; set; } = [];
}
public sealed class OrderItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public int MenuItemId { get; set; }
    public string ItemNameSnapshot { get; set; } = "";
    public decimal UnitPriceSnapshot { get; set; }
    public int Quantity { get; set; }
    public int CancelledQuantity { get; set; }
    public string Notes { get; set; } = "";
    public KitchenStatus KitchenStatus { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
public sealed class KitchenTicket
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public string TicketNumber { get; set; } = "";
    public KitchenStatus Status { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public List<KitchenTicketItem> Items { get; set; } = [];
}
public sealed class KitchenTicketItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid KitchenTicketId { get; set; }
    public Guid OrderItemId { get; set; }
    public OrderItem OrderItem { get; set; } = null!;
    public int Quantity { get; set; }
    public KitchenStatus Status { get; set; }
    public string NotesSnapshot { get; set; } = "";
}
public sealed class OrderEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string EventType { get; set; } = "";
    public string ActorUserId { get; set; } = "";
    public string DetailsJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
// Persist response identity and canonical request hash in the same transaction as the mutation.
public sealed class RequestRecord
{
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public Guid OrderId { get; set; }
}
public record ItemInput(int MenuItemId, int Quantity, string Notes = "");
public record CreateOrderInput(OrderType OrderType, int? TableId, string Notes, List<ItemInput> Items, string IdempotencyKey);
public record AddItemsInput(long Version, List<ItemInput> Items, string IdempotencyKey);
public record CancelInput(long Version, int Quantity, string Reason);
public record TicketStatusInput(long Version, KitchenStatus Status);
public record BusinessEvent(Guid EventId, string EventType, Guid OrderId, long Version);
public record BillLine(Guid Id, string Name, int Quantity, decimal UnitPrice, string Notes);
public record Bill(Guid OrderId, string OrderNumber, string Destination, long Version, IReadOnlyList<BillLine> Items)
{
    public decimal Total => Items.Sum(x => x.Quantity * x.UnitPrice);
    public decimal Paid => 0m;
    public decimal Balance => Total;
}
public sealed class BusinessException(string message, int statusCode = 409) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
