using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RestaurantPOS.Core;
using RestaurantPOS.Infrastructure;

namespace RestaurantPOS.Tests;

public sealed class AccountingTests : IAsyncLifetime
{
    private readonly string file = Path.Combine(Path.GetTempPath(), $"335-accounting-{Guid.NewGuid()}.db");
    private Factory factory = null!;
    private AccountingService reporting = null!;
    private OrderService orders = null!;
    public async Task InitializeAsync()
    {
        factory = new(new DbContextOptionsBuilder<PosDb>().UseSqlite($"Data Source={file};Foreign Keys=True;Pooling=False").Options);
        await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        db.Users.Add(new ApplicationUser { Id = "admin", UserName = "admin" });
        db.Menu.AddRange(new MenuItem { Id = 1, Name = "Rice", Price = 8.50m, Category = new MenuCategory { Name = "Food" } },
            new MenuItem { Id = 2, Name = "Tea", Price = 2.50m, Category = new MenuCategory { Name = "Drinks", IsBeverage = true } });
        await db.SaveChangesAsync();
        orders = new(factory, new Events()); reporting = new(factory, new Clock());
    }
    public Task DisposeAsync() { File.Delete(file); return Task.CompletedTask; }
    [Fact] public async Task PurchasesPersistExactlyAndRetriesDoNotDuplicate()
    {
        var date = new DateOnly(2026, 11, 3);
        var input = new ExpenseInput(Guid.NewGuid(), date, "Ice bag", 2.50m);
        Assert.Equal(input.Id, await reporting.RecordExpense(input, "admin"));
        Assert.Equal(input.Id, await reporting.RecordExpense(input, "admin"));
        await reporting.RecordExpense(new(Guid.NewGuid(), date, "Vegetables", 30m), "admin");
        var day = await new AccountingService(factory).Day(date);
        Assert.Equal(32.50m, day.Spending); Assert.Equal(2, day.Expenses.Count);
        await Assert.ThrowsAsync<BusinessException>(() => reporting.RecordExpense(input with { Amount = 3m }, "admin"));
        Assert.Empty((await reporting.Day(date.AddDays(-1))).Expenses);
    }
    [Fact] public async Task ConcurrentExpenseRetriesSaveExactlyOneRecord()
    {
        var input = new ExpenseInput(Guid.NewGuid(), new(2026, 11, 3), "Ice bag", 2.5m);
        var ids = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => reporting.RecordExpense(input, "admin"))));
        Assert.All(ids, id => Assert.Equal(input.Id, id));
        Assert.Single((await reporting.Day(input.PurchasedOn)).Expenses);
    }
    [Fact] public async Task BeverageMigrationClassifiesExistingCategoriesWithoutChangingMenu()
    {
        await using var db = factory.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261027160254_ItemPaymentAllocations");
        await migrator.MigrateAsync();
        var menu = await db.Menu.AsNoTracking().Include(m => m.Category).ToListAsync();
        Assert.True(menu.Single(m => m.Id == 2).Category.IsBeverage);
        Assert.False(menu.Single(m => m.Id == 1).Category.IsBeverage);
        Assert.Equal(2.5m, menu.Single(m => m.Id == 2).Price);
    }
    [Theory]
    [InlineData("0")][InlineData("-1")][InlineData("1.001")][InlineData("1000000000")]
    public async Task InvalidPurchaseAmountsAreRejected(string value)
    {
        await Assert.ThrowsAsync<BusinessException>(() => reporting.RecordExpense(new(Guid.NewGuid(), new(2026, 11, 3), "Ice", decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)), "admin"));
        Assert.Empty((await reporting.Day(new(2026, 11, 3))).Expenses);
    }
    [Fact] public async Task InvalidPurchaseDatesAndDescriptionsAreRejected()
    {
        foreach (var input in new[] { new ExpenseInput(Guid.NewGuid(), new(2026, 11, 4), "Ice", 2.5m), new ExpenseInput(Guid.NewGuid(), new(2026, 11, 3), " ", 2.5m), new ExpenseInput(Guid.Empty, new(2026, 11, 3), "Ice", 2.5m) })
            await Assert.ThrowsAsync<BusinessException>(() => reporting.RecordExpense(input, "admin"));
    }
    [Fact] public async Task SalesUseMalaysiaPaymentDatesSnapshotsAndPaidAllocations()
    {
        var id = await orders.Create(new(OrderType.Takeaway, null, "", [new(1, 3), new(2, 2)], "report-order"), "admin");
        var bill = await orders.Bill(id); var rice = bill.Items.Single(i => i.Name == "Rice");
        await orders.Cancel(id, rice.Id, new(bill.Version, 1), "admin");
        bill = await orders.Bill(id);
        await orders.SplitPayment(id, new(bill.Version, [new(rice.Id, 1)], 8.5m, PaymentMethod.Cash, 10m, "first", "report-first"), "admin");
        bill = await orders.Bill(id);
        await orders.Checkout(id, new(bill.Version, 13.5m, PaymentMethod.Cash, 50m, "last", "report-last"), "admin");
        // The two payers cross Malaysia midnight and the month boundary.
        await using (var db = factory.CreateDbContext())
        {
            var payments = await db.Payments.ToListAsync();
            payments.Single(p => p.Reference == "first").ReceivedAtUtc = DateTime.Parse("2026-10-31T15:59:59Z").ToUniversalTime();
            payments.Single(p => p.Reference == "last").ReceivedAtUtc = DateTime.Parse("2026-10-31T16:00:00Z").ToUniversalTime();
            var menu = await db.Menu.SingleAsync(m => m.Id == 1); menu.Price = 99m; menu.Name = "Renamed";
            await db.SaveChangesAsync();
        }
        await orders.Create(new(OrderType.Takeaway, null, "unpaid", [new(1, 5)], "unpaid-order"), "admin");
        var october = await reporting.Statistics(new(2026, 10, 31), ReportPeriod.Month);
        var november = await reporting.Statistics(new(2026, 11, 1), ReportPeriod.Month);
        var year = await reporting.Statistics(new(2026, 1, 1), ReportPeriod.Year);
        Assert.Equal(8.5m, october.Revenue); Assert.Equal(13.5m, november.Revenue); Assert.Equal(22m, year.Revenue);
        Assert.Equal(2, year.PaymentCount); Assert.Equal(1, year.BillCount); Assert.Equal(12, year.Buckets.Count);
        Assert.Equal(2, year.Products.Single(p => p.Name == "Rice").Quantity); Assert.Equal(17m, year.Products.Single(p => p.Name == "Rice").Revenue);
        var drinks = await reporting.Statistics(new(2026, 11, 1), ReportPeriod.Month, ProductGroup.Beverage);
        Assert.Equal("Tea", Assert.Single(drinks.Products).Name); Assert.Equal(5m, drinks.Products.Single().Revenue);
        var food = await reporting.Statistics(new(2026, 11, 1), ReportPeriod.Month, ProductGroup.Food);
        Assert.Equal("Rice", Assert.Single(food.Products).Name); Assert.Equal(1, food.Products.Single().Quantity);
        var day = await reporting.Day(new(2026, 11, 1)); var row = Assert.Single(day.Bills);
        Assert.Equal(13.5m, row.ReceivedInPeriod); Assert.Equal(22m, row.BillTotal); Assert.Equal(22m, row.TotalPaid);
        Assert.Equal(OrderStatus.Closed, row.Status);
        Assert.Equal(8.5m, (await reporting.Day(new(2026, 10, 31))).Sales);
        Assert.Equal(year.Revenue, year.Buckets.Sum(b => b.Sales));
    }
    [Fact] public async Task EmptyPeriodsHaveZeroFilledBucketsAndNoSales()
    {
        var report = await reporting.Statistics(new(2024, 2, 3), ReportPeriod.Month);
        Assert.Equal(29, report.Buckets.Count); Assert.Equal(0m, report.Revenue); Assert.Empty(report.Products);
        Assert.All(report.Buckets, b => Assert.Equal(0m, b.Sales));
        await Assert.ThrowsAsync<BusinessException>(() => reporting.Statistics(new(2026, 1, 1), (ReportPeriod)99));
    }
    [Fact] public async Task FullyCancelledZeroBillsAppearWithoutInventingRevenue()
    {
        var id = await orders.Create(new(OrderType.Takeaway, null, "", [new(1, 1)], "zero-report"), "admin");
        var bill = await orders.Bill(id);
        await orders.Cancel(id, bill.Items.Single().Id, new(bill.Version, 1), "admin");
        bill = await orders.Bill(id);
        await orders.Checkout(id, new(bill.Version, 0m, PaymentMethod.Cash, 0m, "", "zero-report-close"), "admin");
        var day = await reporting.Day(AccountingService.LocalDate(DateTime.UtcNow));
        Assert.Equal(id, Assert.Single(day.Bills).OrderId); Assert.Equal(0m, day.Sales);
        Assert.Equal(0m, day.Bills.Single().BillTotal);
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 11, 3, 4, 0, 0, TimeSpan.Zero); }
    private sealed class Events : IBusinessEvents { public Task Publish(BusinessEvent e) => Task.CompletedTask; }
    private sealed class Factory(DbContextOptions<PosDb> options) : IDbContextFactory<PosDb> { public PosDb CreateDbContext() => new(options); }
}
