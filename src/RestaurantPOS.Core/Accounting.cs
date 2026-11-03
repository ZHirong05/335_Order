namespace RestaurantPOS.Core;

public sealed class Expense
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly PurchasedOn { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string RecordedByUserId { get; set; } = "";
}
public record ExpenseInput(Guid Id, DateOnly PurchasedOn, string Description, decimal Amount);
public enum ReportPeriod { Day, Month, Year }
public enum ProductGroup { All, Food, Beverage }
public record SalesBill(Guid OrderId, string DisplayNumber, string OrderBusinessDate, int? TableNumber, OrderStatus Status,
    DateTime LastPaymentUtc, decimal BillTotal, decimal ReceivedInPeriod, decimal TotalPaid, string[] Methods);
public record SalesBucket(string Label, decimal Sales, int Payments);
public record ProductSales(int MenuItemId, string Name, string Category, bool IsBeverage, int Quantity, decimal Revenue);
public record SalesReport(DateOnly From, DateOnly Through, decimal Revenue, int PaymentCount, int BillCount,
    decimal Expenses, IReadOnlyList<SalesBucket> Buckets, IReadOnlyList<ProductSales> Products)
{
    public decimal AfterExpenses => Revenue - Expenses;
}
public record AccountingDay(DateOnly Date, IReadOnlyList<Expense> Expenses, IReadOnlyList<SalesBill> Bills)
{
    public decimal Sales => Bills.Sum(b => b.ReceivedInPeriod);
    public decimal Spending => Expenses.Sum(e => e.Amount);
}
