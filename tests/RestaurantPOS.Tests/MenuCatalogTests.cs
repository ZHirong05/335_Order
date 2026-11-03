using Microsoft.EntityFrameworkCore;
using RestaurantPOS.Core;
using RestaurantPOS.Infrastructure;

namespace RestaurantPOS.Tests;

public sealed class MenuCatalogTests : IAsyncLifetime
{
    private readonly string file = Path.Combine(Path.GetTempPath(), $"335-menu-{Guid.NewGuid()}.db");
    private Factory factory = null!;
    private OrderService service = null!;
    public async Task InitializeAsync()
    {
        factory = new(new DbContextOptionsBuilder<PosDb>().UseSqlite($"Data Source={file};Foreign Keys=True;Pooling=False").Options);
        await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
        db.Users.Add(new ApplicationUser { Id = "admin", UserName = "admin" });
        db.Menu.Add(new MenuItem { Id = 1, Category = new MenuCategory { Name = "Old food" }, Name = "Old dish", Price = 8.5m });
        await db.SaveChangesAsync(); service = new(factory, new Events());
    }
    public Task DisposeAsync() { File.Delete(file); return Task.CompletedTask; }
    private async Task Import() { await using var db = factory.CreateDbContext(); await RestaurantMenuCatalog.Apply(db); }
    [Fact] public async Task ImportIsIdempotentPreservesHistoryAndUsesRequestedLanguages()
    {
        var old = await service.Create(new(OrderType.Takeaway, null, "", [new(1, 1)], "old-order"), "admin");
        await service.Checkout(old, new(1, 8.5m, PaymentMethod.Cash, 10m, "", "old-payment"), "admin");
        await Import(); var first = await service.Menu(); await Import(); var second = await service.Menu();
        Assert.Equal(74, second.Count); Assert.Equal(12, second.Select(m => m.CategoryId).Distinct().Count());
        Assert.Equal(first.Select(m => m.Id), second.Select(m => m.Id));
        Assert.Equal(37, second.Count(m => !m.Category.IsBeverage)); Assert.Equal(37, second.Count(m => m.Category.IsBeverage));
        Assert.All(second.Where(m => !m.Category.IsBeverage), m => Assert.DoesNotMatch("[A-Za-z]", m.Name));
        Assert.All(second.Where(m => m.Category.IsBeverage), m => Assert.DoesNotMatch("[\\u4e00-\\u9fff]", m.Name));
        var bill = await service.Bill(old); Assert.Equal(8.5m, bill.Total); Assert.Equal("Old dish", bill.Items.Single().Name); Assert.Equal(0m, bill.Balance);
        await Assert.ThrowsAsync<BusinessException>(() => service.Create(new(OrderType.Takeaway, null, "", [new(1, 1)], "disabled-old"), "admin"));
        await using var db = factory.CreateDbContext(); Assert.Single(await db.MenuCatalogImports.ToListAsync());
    }
    [Fact] public async Task EveryWorkbookPriceCaseProducesTheExactOrderPrice()
    {
        await Import(); var menu = (await service.Menu()).ToDictionary(m => m.Name);
        var checks = RestaurantMenuCatalog.Read().PriceChecks; Assert.Equal(211, checks.Count);
        for (var index = 0; index < checks.Count; index++)
        {
            var check = checks[index]; var item = menu[check.Item];
            var groups = item.OptionGroups.OrderBy(g => g.SortOrder).ToList();
            var ids = groups.Select((g, i) => g.Options.Single(o => o.Name == check.Options[i]).Id).ToList();
            var order = await service.Create(new(OrderType.Takeaway, null, "", [new(item.Id, 1, OptionIds: ids)], $"price-case-{index}"), "admin");
            var bill = await service.Bill(order); Assert.Equal(check.Price, bill.Total);
            Assert.All(check.Options, option => Assert.Contains(option, bill.Items.Single().Name));
        }
    }
    [Fact] public async Task MissingDuplicateForeignAndUnavailableChoicesAreRejectedAtomically()
    {
        await Import(); var menu = await service.Menu(); var curry = menu.Single(m => m.Name == "咖喱面");
        var groups = curry.OptionGroups.OrderBy(g => g.SortOrder).ToList();
        var a = groups[0].Options[0].Id; var b = groups[1].Options[0].Id;
        var foreign = menu.Single(m => m.Name == "Kopi").OptionGroups[0].Options[0].Id;
        List<List<int>> invalid = [[], [a], [a, a], [a, foreign], [a, groups[0].Options[1].Id], [a, b, -1]];
        foreach (var ids in invalid)
            await Assert.ThrowsAsync<BusinessException>(() => service.Create(new(OrderType.Takeaway, null, "", [new(curry.Id, 1, OptionIds: ids)], Guid.NewGuid().ToString()), "admin"));
        await using (var db = factory.CreateDbContext()) { (await db.MenuOptions.SingleAsync(o => o.Id == b)).IsAvailable = false; await db.SaveChangesAsync(); }
        await Assert.ThrowsAsync<BusinessException>(() => service.Create(new(OrderType.Takeaway, null, "", [new(curry.Id, 1, OptionIds: [a,b])], "unavailable"), "admin"));
        var plain = menu.Single(m => m.Name == "咖喱鸡饭");
        await Assert.ThrowsAsync<BusinessException>(() => service.Create(new(OrderType.Takeaway, null, "", [new(plain.Id, 1, OptionIds: [a])], "extra-options"), "admin"));
        Assert.Empty(await service.Active()); Assert.Empty(await service.Tickets());
    }
    [Fact] public async Task GroupSelectionsAreIdempotentSnapshottedAndRetainedForKitchenAndSales()
    {
        await Import(); var curry = (await service.Menu()).Single(m => m.Name == "咖喱面");
        var noodles = curry.OptionGroups.Single(g => g.Name == "面类").Options.Single(o => o.Name == "生面");
        var meat = curry.OptionGroups.Single(g => g.Name == "+").Options.Single(o => o.Name == "叉烧");
        var input = new CreateOrderInput(OrderType.Takeaway, null, "", [new(curry.Id, 2, OptionIds: [noodles.Id, meat.Id])], "curry-snapshot");
        var id = await service.Create(input, "admin"); Assert.Equal(id, await service.Create(input, "admin"));
        await using (var db = factory.CreateDbContext()) { var option = await db.MenuOptions.SingleAsync(o => o.Id == meat.Id); option.Name = "changed"; option.AdditionalPrice = 99m; await db.SaveChangesAsync(); }
        await Import(); // Restart/import must not reset deliberate catalog changes.
        var bill = await service.Bill(id); Assert.Equal(22m, bill.Total); Assert.Contains("叉烧", bill.Items.Single().Name); Assert.Contains("生面", bill.Items.Single().Name);
        Assert.Contains("叉烧", Assert.Single(await service.Tickets()).Items.Single().OrderItem.ItemNameSnapshot);
        await service.Checkout(id, new(bill.Version, bill.Total, PaymentMethod.QR, null, "", "curry-paid"), "admin");
        var report = await new AccountingService(factory).Statistics(AccountingService.LocalDate(DateTime.UtcNow), ReportPeriod.Day, ProductGroup.Food);
        Assert.Equal(22m, report.Revenue); Assert.Equal(2, report.Products.Single().Quantity); Assert.Contains("叉烧", report.Products.Single().Name);
    }
    [Fact] public async Task RequestedDefaultsChickenChoicesAndPortionsArePresent()
    {
        await Import(); var menu = await service.Menu();
        var prawn = menu.Single(m => m.Name == "虾汤面");
        var extra = prawn.OptionGroups.Single(g => g.Name == "+");
        Assert.Equal("虾", Assert.Single(extra.Options, o => o.IsDefault).Name);
        Assert.DoesNotContain(extra.Options, o => o.Name == "原味");
        var chicken = menu.Single(m => m.Name == "鸡排／白斩鸡／炸鸡腿");
        Assert.Equal(8m, chicken.Price);
        Assert.Equal(new[] { "炸鸡排肉", "白斩鸡", "炸鸡腿" }, chicken.OptionGroups.Single().Options.OrderBy(o => o.SortOrder).Select(o => o.Name));
        var curry = menu.Single(m => m.Name == "咖喱鸡肉");
        Assert.Equal(6m, curry.Price);
        Assert.Equal(new[] { "小份", "大份" }, curry.OptionGroups.Single().Options.OrderBy(o => o.SortOrder).Select(o => o.Name));
        Assert.Equal(6m, curry.OptionGroups.Single().Options.Single(o => o.Name == "大份").AdditionalPrice);
    }
    private sealed class Events : IBusinessEvents { public Task Publish(BusinessEvent e) => Task.CompletedTask; }
    private sealed class Factory(DbContextOptions<PosDb> options) : IDbContextFactory<PosDb> { public PosDb CreateDbContext() => new(options); }
}
