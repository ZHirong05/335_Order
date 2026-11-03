using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RestaurantPOS.Core;

namespace RestaurantPOS.Infrastructure;

public sealed class AccountingService(IDbContextFactory<PosDb> factory, TimeProvider? clock = null)
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuala_Lumpur");
    public DateOnly Today => LocalDate((clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);
    public IReadOnlyList<int> ReportYears => Enumerable.Range(2026, Math.Max(1, Today.Year - 2026 + 1)).ToArray();
    public static DateOnly LocalDate(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone));
    private static DateTime Utc(DateOnly date) => TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), Zone);
    public async Task<Guid> RecordExpense(ExpenseInput input, string actor)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new BusinessException("Authentication required", 401);
        var description = input.Description?.Trim() ?? "";
        if (input.Id == Guid.Empty || description.Length is < 1 or > 200 || input.PurchasedOn.Year < 2000 || input.PurchasedOn > Today ||
            input.Amount <= 0 || input.Amount > 999999999m || decimal.Round(input.Amount, 2) != input.Amount)
            throw new BusinessException("Enter a purchase date up to today, a description and a positive amount with at most two decimal places", 400);
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.OpenConnectionAsync();
        using var tx = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: false);
        await db.Database.UseTransactionAsync(tx);
        var previous = await db.Expenses.FindAsync(input.Id);
        if (previous != null)
        {
            if (previous.RecordedByUserId != actor || previous.PurchasedOn != input.PurchasedOn || previous.Description != description || previous.Amount != input.Amount)
                throw new BusinessException("Expense reference already used for different details");
            return previous.Id;
        }
        db.Expenses.Add(new Expense { Id = input.Id, PurchasedOn = input.PurchasedOn, Description = description, Amount = input.Amount,
            RecordedByUserId = actor, CreatedAtUtc = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(); await tx.CommitAsync(); return input.Id;
    }
    public async Task<AccountingDay> Day(DateOnly date)
    {
        ValidateDate(date);
        var from = Utc(date); var until = Utc(date.AddDays(1));
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var expenses = await db.Expenses.AsNoTracking().Where(e => e.PurchasedOn == date).OrderByDescending(e => e.CreatedAtUtc).ThenByDescending(e => e.Id).ToListAsync();
        var payments = await db.Payments.AsNoTracking().Where(p => p.ReceivedAtUtc >= from && p.ReceivedAtUtc < until).ToListAsync();
        // Fully cancelled/free bills close without a synthetic payment row.
        var zeroCloses = await db.Events.AsNoTracking().Where(e => e.EventType == "OrderClosed" && e.CreatedAtUtc >= from && e.CreatedAtUtc < until && !db.Payments.Any(p => p.OrderId == e.OrderId)).ToListAsync();
        var ids = payments.Select(p => p.OrderId).Concat(zeroCloses.Select(e => e.OrderId)).Distinct().ToList();
        var orders = await db.Orders.AsNoTracking().Include(o => o.Items).Include(o => o.Table).Where(o => ids.Contains(o.Id)).ToListAsync();
        var allPayments = await db.Payments.AsNoTracking().Where(p => ids.Contains(p.OrderId)).ToListAsync();
        var bills = orders.Select(o =>
        {
            var received = payments.Where(p => p.OrderId == o.Id).ToList();
            return new SalesBill(o.Id, o.DisplayNumber, o.BusinessDate, o.Table?.Number, o.Status, received.Count > 0 ? received.Max(p => p.ReceivedAtUtc) : zeroCloses.Where(e => e.OrderId == o.Id).Max(e => e.CreatedAtUtc),
                o.Items.Sum(i => (i.Quantity - i.CancelledQuantity) * i.UnitPriceSnapshot), received.Sum(p => p.Amount),
                allPayments.Where(p => p.OrderId == o.Id).Sum(p => p.Amount), received.Select(p => p.Method.ToString()).Distinct().ToArray());
        }).OrderByDescending(b => b.LastPaymentUtc).ThenByDescending(b => b.OrderId).ToList();
        await tx.CommitAsync(); return new(date, expenses, bills);
    }
    public async Task<SalesReport> Statistics(DateOnly selected, ReportPeriod period, ProductGroup group = ProductGroup.All)
    {
        ValidateDate(selected);
        if (!Enum.IsDefined(period) || !Enum.IsDefined(group)) throw new BusinessException("Invalid reporting filter", 400);
        var start = period switch { ReportPeriod.Month => new DateOnly(selected.Year, selected.Month, 1), ReportPeriod.Year => new DateOnly(selected.Year, 1, 1), _ => selected };
        var end = period switch { ReportPeriod.Month => start.AddMonths(1), ReportPeriod.Year => start.AddYears(1), _ => start.AddDays(1) };
        var from = Utc(start); var until = Utc(end);
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var payments = await db.Payments.AsNoTracking().Include(p => p.Allocations).ThenInclude(a => a.OrderItem)
            .Where(p => p.ReceivedAtUtc >= from && p.ReceivedAtUtc < until).ToListAsync();
        var expenses = await db.Expenses.AsNoTracking().Where(e => e.PurchasedOn >= start && e.PurchasedOn < end).ToListAsync();
        var menu = await db.Menu.AsNoTracking().Include(m => m.Category).ToDictionaryAsync(m => m.Id);
        var products = payments.SelectMany(p => p.Allocations).GroupBy(a => new { a.OrderItem.MenuItemId, a.OrderItem.ItemNameSnapshot })
            .Select(g => new ProductSales(g.Key.MenuItemId, g.Key.ItemNameSnapshot, menu[g.Key.MenuItemId].Category.Name,
                menu[g.Key.MenuItemId].Category.IsBeverage, g.Sum(a => a.Quantity), g.Sum(a => a.Amount)))
            .Where(p => group == ProductGroup.All || p.IsBeverage == (group == ProductGroup.Beverage))
            .OrderByDescending(p => p.Revenue).ThenByDescending(p => p.Quantity).ThenBy(p => p.Name).ToList();
        var buckets = new List<SalesBucket>();
        for (var d = start; d < end; d = period == ReportPeriod.Year ? d.AddMonths(1) : d.AddDays(1))
        {
            var next = period == ReportPeriod.Year ? d.AddMonths(1) : d.AddDays(1);
            var batch = payments.Where(p => LocalDate(p.ReceivedAtUtc) >= d && LocalDate(p.ReceivedAtUtc) < next).ToList();
            buckets.Add(new(period == ReportPeriod.Year ? d.ToString("yyyy-MM") : d.ToString("yyyy-MM-dd"), batch.Sum(p => p.Amount), batch.Count));
        }
        await tx.CommitAsync();
        return new(start, end.AddDays(-1), payments.Sum(p => p.Amount), payments.Count, payments.Select(p => p.OrderId).Distinct().Count(), expenses.Sum(e => e.Amount), buckets, products);
    }
    private static void ValidateDate(DateOnly date)
    {
        if (date.Year is < 2000 or > 9998) throw new BusinessException("Choose a date from 2000 to 9998", 400);
    }
}
