using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
        var meat = curry.OptionGroups.Single(g => g.Name == "配料").Options.Single(o => o.Name == "叉烧");
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
        var extra = prawn.OptionGroups.Single(g => g.Name == "配料");
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
    [Theory]
    [InlineData("面", "米粉", "10.50")]
    [InlineData("米粉", "生面", "11.00")]
    public async Task TwoNoodlesAreOneOrderLineWithStableCombinedNameAndExactSurcharges(string first, string second, string price)
    {
        await Import(); var item = (await service.Menu()).Single(m => m.Name == "咖喱面");
        var noodles = item.OptionGroups.Single(g => g.Name == "面类");
        var meat = item.OptionGroups.Single(g => g.Name == "配料").Options.Single(o => o.Name == "叉烧");
        Assert.Equal(2, noodles.MaxSelections);
        var ids = new List<int> { noodles.Options.Single(o => o.Name == second).Id, meat.Id, noodles.Options.Single(o => o.Name == first).Id };
        var request = new CreateOrderInput(OrderType.Takeaway, null, "", [new(item.Id, 2, OptionIds: ids)], "mixed-noodles");
        var id = await service.Create(request, "admin"); Assert.Equal(id, await service.Create(request, "admin"));
        var bill = await service.Bill(id); var line = Assert.Single(bill.Items);
        Assert.Equal(decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture), line.UnitPrice);
        Assert.Equal(2, line.Quantity); Assert.Equal(line.UnitPrice * 2, bill.Total);
        Assert.Equal(new[] { $"{first}/{second}", "叉烧" }, ItemDisplay.Parse(line.Name).Options);
        Assert.Equal(2, Assert.Single(await service.Tickets()).Items.Single().Quantity);
    }
    [Fact] public async Task ThreeNoodlesAreRejectedAndDishNamesUsePlus()
    {
        await Import(); var menu = await service.Menu(); var item = menu.Single(m => m.Name == "咖喱面");
        var noodles = item.OptionGroups.Single(g => g.Name == "面类"); var meat = item.OptionGroups.Single(g => g.Name == "配料");
        var three = noodles.Options.Take(3).Select(o => o.Id).Append(meat.Options[0].Id).ToList();

        foreach (var ids in new[] { three })
            await Assert.ThrowsAsync<BusinessException>(() => service.Create(new(OrderType.Takeaway, null, "", [new(item.Id, 1, OptionIds: ids)], Guid.NewGuid().ToString()), "admin"));
        Assert.Empty(await service.Active());
        Assert.Contains(menu, m => m.Name == "白斩鸡腿+饭+菜");
        Assert.Contains(menu, m => m.Name == "鸡扒炒饭+荷包蛋");
        Assert.DoesNotContain(menu, m => m.Name.Contains("配菜") || m.Name.Contains("配饭") || m.Name.Contains("配荷包蛋"));
        Assert.All(menu.SelectMany(m => m.OptionGroups), g => Assert.Equal(g.Name == "配料" ? g.Options.Count : g.Name == "面类" ? 2 : 1, g.MaxSelections));
        Assert.DoesNotContain(menu.SelectMany(m => m.OptionGroups), g => g.Name == "+");
    }
    [Theory]
    [InlineData("咖喱面")]
    [InlineData("虾汤面")]
    [InlineData("鱼丸汤粉")]
    [InlineData("炒饭")]
    [InlineData("炒面")]
    public async Task MultipleIngredientsRetainPricesAndKitchenBillSnapshots(string dish)
    {
        await Import();
        var item = (await service.Menu()).Single(m => m.Name == dish);
        var ingredients = item.OptionGroups.Single(g => g.Name == "配料");
        Assert.Equal(ingredients.Options.Count, ingredients.MaxSelections);
        var choices = ingredients.Options.OrderBy(o => o.SortOrder).ToList();
        var other = item.OptionGroups.Where(g => g.Id != ingredients.Id).Select(g => g.Options.OrderBy(o => o.SortOrder).First()).ToList();
        var ids = choices.Concat(other).Select(o => o.Id).Reverse().ToList();
        var input = new CreateOrderInput(OrderType.Takeaway, null, "", [new(item.Id, 2, OptionIds: ids)], "multiple-ingredients");
        var id = await service.Create(input, "admin");
        Assert.Equal(id, await service.Create(input, "admin"));
        var bill = await service.Bill(id);
        var line = Assert.Single(bill.Items);
        Assert.Equal(item.Price + choices.Concat(other).Sum(o => o.AdditionalPrice), line.UnitPrice);
        Assert.Equal(line.UnitPrice * 2, bill.Total);
        Assert.Contains($"配料：{string.Join("/", choices.Select(o => o.Name))}", line.Name);
        Assert.Equal(line.Name, Assert.Single(await service.Tickets()).Items.Single().OrderItem.ItemNameSnapshot);
        await service.Checkout(id, new(bill.Version, bill.Total, PaymentMethod.QR, null, "", "ingredients-paid"), "admin");
        var report = await new AccountingService(factory).Statistics(AccountingService.LocalDate(DateTime.UtcNow), ReportPeriod.Day, ProductGroup.Food);
        Assert.Equal(bill.Total, report.Revenue);
    }
    [Fact] public async Task SelectionLimitMigrationPreservesExistingGroupsAndOptions()
    {
        await using var db = factory.CreateDbContext();
        var group = new MenuOptionGroup { MenuItemId = 1, Name = "面类", Options = [new MenuOption { Name = "面" }, new MenuOption { Name = "米粉" }] };
        db.MenuOptionGroups.Add(group); await db.SaveChangesAsync();
        var id = group.Id; var optionIds = group.Options.Select(o => o.Id).Order().ToList();
        db.ChangeTracker.Clear();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261103073003_MenuOptionDefaults");
        await migrator.MigrateAsync();
        var saved = await db.MenuOptionGroups.AsNoTracking().Include(g => g.Options).SingleAsync(g => g.Id == id);
        Assert.Equal(1, saved.MaxSelections); Assert.Equal(optionIds, saved.Options.Select(o => o.Id).Order());
        await RestaurantMenuCatalog.Apply(db);
        Assert.All((await service.Menu()).SelectMany(m => m.OptionGroups).Where(g => g.Name == "面类"), g => Assert.Equal(2, g.MaxSelections));
    }
    [Fact] public async Task SavedLineEditPreservesOriginalAndSnapshotsExtrasWithExactPrice()
    {
        await Import();
        var menu = await service.Menu(); var curry = menu.Single(m => m.Name == "咖喱面");
        var options = curry.OptionGroups.Select(g => g.Options.First().Id).ToList();
        var original = new ItemInput(curry.Id, 2, "original", OptionIds: options);
        var id = await service.Create(new(OrderType.Takeaway, null, "", [original], "edit-create"), "admin");
        var bill = await service.Bill(id); var old = Assert.Single(bill.Items);
        Assert.Equal(options, (await service.ItemConfiguration(id, old.Id)).OptionIds);
        var changed = original with { Quantity = 3, Notes = "revised", OtherItems = [new("Extra tofu", 1.25m), new("Extra sauce", 0.50m)] };
        var request = new EditItemInput(bill.Version, changed, "edit-save");
        await service.EditItem(id, old.Id, request, "admin");
        await service.EditItem(id, old.Id, request, "admin");
        bill = await service.Bill(id);
        Assert.Equal(0, bill.Items.Single(i => i.Id == old.Id).Quantity);
        var revised = Assert.Single(bill.Items, i => i.Quantity > 0);
        Assert.Equal(old.UnitPrice + 1.75m, revised.UnitPrice);
        Assert.Equal(revised.UnitPrice * 3, bill.Total);
        Assert.Contains("Extra tofu +RM 1.25", revised.Name);
        Assert.Contains("Extra sauce +RM 0.50", revised.Name);
        var config = await service.ItemConfiguration(id, revised.Id);
        Assert.Equal(3, config.Quantity); Assert.Equal(2, config.OtherItems!.Count);
        await using var db = factory.CreateDbContext();
        Assert.True(await db.Events.AnyAsync(e => e.OrderId == id && e.EventType == "ItemEdited"));
        var tickets = await db.Tickets.Include(t => t.Items).ThenInclude(i => i.OrderItem).Where(t => t.OrderId == id).ToListAsync();
        Assert.Equal(2, tickets.Count);
        Assert.Contains(tickets, t => t.Items.Any(i => i.OrderItemId == old.Id && i.Status == KitchenStatus.Cancelled));
        Assert.Contains(tickets, t => t.Items.Any(i => i.OrderItem.ItemNameSnapshot == revised.Name && i.Quantity == 3));
        await service.Checkout(id, new(bill.Version, bill.Total, PaymentMethod.QR, null, "", "edit-paid"), "admin");
        bill = await service.Bill(id);
        await Assert.ThrowsAsync<BusinessException>(() => service.EditItem(id, revised.Id, new(bill.Version, changed, "paid-edit-reject"), "admin"));
    }
    [Theory]
    [InlineData("", "1.00")]
    [InlineData("Extra", "-0.01")]
    [InlineData("Extra", "0.001")]
    public async Task InvalidExtrasRollBackEditWithoutCancellingOriginal(string name, string price)
    {
        var id = await service.Create(new(OrderType.Takeaway, null, "", [new(1, 1)], "invalid-edit-create"), "admin");
        var bill = await service.Bill(id); var old = Assert.Single(bill.Items);
        var item = new ItemInput(1, 1, OtherItems: [new(name, decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture))]);
        await Assert.ThrowsAsync<BusinessException>(() => service.EditItem(id, old.Id, new(bill.Version, item, "invalid-edit-save"), "admin"));
        var unchanged = await service.Bill(id);
        Assert.Equal(bill.Version, unchanged.Version); Assert.Equal(1, Assert.Single(unchanged.Items).Quantity); Assert.Equal(bill.Total, unchanged.Total);
    }
    private sealed class Events : IBusinessEvents { public Task Publish(BusinessEvent e) => Task.CompletedTask; }
    private sealed class Factory(DbContextOptions<PosDb> options) : IDbContextFactory<PosDb> { public PosDb CreateDbContext() => new(options); }
}
