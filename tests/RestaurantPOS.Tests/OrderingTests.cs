using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RestaurantPOS.Core;
using RestaurantPOS.Infrastructure;
namespace RestaurantPOS.Tests;

public sealed class OrderingTests : IAsyncLifetime
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "335-tests-" + Guid.NewGuid());
    private Factory factory = null!;
    private OrderService service = null!;
    private readonly Events events = new();
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(folder);
        factory = new(new DbContextOptionsBuilder<PosDb>().UseSqlite($"Data Source={Path.Combine(folder, "test.db")};Foreign Keys=True;Default Timeout=15;Pooling=False").Options);
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        db.Users.Add(new ApplicationUser { Id = "waiter", UserName = "waiter" });
        db.Tables.Add(new DiningTable { Id = 1, Number = 1 });
        db.Menu.Add(new MenuItem { Id = 1, Category = new MenuCategory { Name = "Food" }, Name = "Nasi Lemak", Price = 8.50m });
        await db.SaveChangesAsync(); service = new(factory, events);
    }
    public Task DisposeAsync() { Directory.Delete(folder, true); return Task.CompletedTask; }
    private static CreateOrderInput Request(string key = "request-123", bool dineIn = false) => new(dineIn ? OrderType.DineIn : OrderType.Takeaway, dineIn ? 1 : null, "", [new(1, 2, "No chili")], key);
    [Fact] public async Task CreationIsIdempotentAndTakeawayNeedsNoTable()
    {
        var first = await service.Create(Request(), "waiter");
        Assert.Equal(first, await service.Create(Request(), "waiter"));
        Assert.Single(await service.Active()); Assert.Single(await service.Tickets()); Assert.Single(events.Items);
        await Assert.ThrowsAsync<BusinessException>(() => service.Create(Request() with { Notes = "different" }, "waiter"));
    }
    [Fact] public async Task SnapshotAndDecimalSurviveMenuChangesAndRestart()
    {
        var id = await service.Create(Request(), "waiter");
        await using (var db = factory.CreateDbContext()) { var m = await db.Menu.SingleAsync(); m.Price = 12.99m; m.Name = "Changed"; await db.SaveChangesAsync(); }
        var restarted = new OrderService(factory, events);
        var bill = await restarted.Bill(id); Assert.Equal(17.00m, bill.Total); Assert.Equal("Nasi Lemak", bill.Items.Single().Name);
        Assert.Equal(12.99m, (await restarted.Menu()).Single().Price);
    }
    [Fact] public async Task AppendCreatesNewTicketAndStaleVersionIsRejected()
    {
        var id = await service.Create(Request(), "waiter");
        await service.Add(id, new(1, [new(1, 1)], "append-123"), "waiter");
        Assert.Equal(2, (await service.Tickets()).Count); Assert.Equal(25.50m, (await service.Bill(id)).Total);
        await Assert.ThrowsAsync<BusinessException>(() => service.Add(id, new(1, [new(1, 1)], "append-456"), "waiter"));
    }
    [Fact] public async Task CancellationPreservesTicketAndReconcilesFromDatabase()
    {
        var id = await service.Create(Request(), "waiter"); var ticket = (await service.Tickets()).Single();
        await service.SetTicket(ticket.Id, new(1, KitchenStatus.Preparing), "waiter");
        var item = (await service.Bill(id)).Items.Single();
        await service.Cancel(id, item.Id, new(2, 2, "Customer changed mind"), "waiter");
        var reconnected = new OrderService(factory, events); var restored = (await reconnected.Tickets()).Single();
        Assert.Equal(KitchenStatus.Cancelled, restored.Status); Assert.Equal(2, restored.Items.Single().Quantity);
        Assert.Equal(2, restored.Items.Single().OrderItem.CancelledQuantity); Assert.Equal(0m, (await reconnected.Bill(id)).Total);
        await using var db = factory.CreateDbContext(); Assert.Contains("Customer changed mind", (await db.Events.SingleAsync(x => x.EventType == "ItemCancelled")).DetailsJson);
    }
    [Fact] public async Task ConcurrentOpeningOnlyCreatesOneActiveTableOrder()
    {
        async Task<bool> Try(string key) { try { await service.Create(Request(key, true), "waiter"); return true; } catch (BusinessException) { return false; } }
        var results = await Task.WhenAll(Task.Run(() => Try("table-one")), Task.Run(() => Try("table-two")));
        Assert.Single(results, x => x); Assert.Single(await service.Active());
    }
    [Fact] public async Task InvalidInputRollsBackAndDoesNotBroadcast()
    {
        await Assert.ThrowsAsync<BusinessException>(() => service.Create(Request() with { Items = [new(1, 0)] }, "waiter"));
        Assert.Empty(await service.Active()); Assert.Empty(events.Items);
    }
    [Fact] public async Task OnlineBackupRestoresMigratedDatabase()
    {
        var id = await service.Create(Request(), "waiter"); var destination = Path.Combine(folder, "backup.db");
        DatabaseMaintenance.Backup(Path.Combine(folder, "test.db"), destination);
        await using var restored = new PosDb(new DbContextOptionsBuilder<PosDb>().UseSqlite($"Data Source={destination};Pooling=False").Options);
        Assert.Equal(id, (await restored.Orders.SingleAsync()).Id); Assert.Empty(await restored.Database.GetPendingMigrationsAsync());
    }
    [Fact] public async Task VariationNameAndPriceAreSnapshottedOnTheServer()
    {
        await using (var db = factory.CreateDbContext())
        {
            db.MenuVariations.Add(new() { Id = 100, MenuItemId = 1, Name = "Large", AdditionalPrice = 2.35m });
            await db.SaveChangesAsync();
        }
        var id = await service.Create(Request() with { Items = [new(1, 2, "Less chili", 100)] }, "waiter");
        await using (var db = factory.CreateDbContext())
        {
            var variation = await db.MenuVariations.SingleAsync(); variation.Name = "Extra large"; variation.AdditionalPrice = 9.99m;
            (await db.Menu.SingleAsync()).Price = 20m;
            await db.SaveChangesAsync();
        }
        var bill = await service.Bill(id);
        Assert.Equal("Nasi Lemak (Large)", bill.Items.Single().Name);
        Assert.Equal(10.85m, bill.Items.Single().UnitPrice);
        Assert.Equal(21.70m, bill.Total);
        Assert.Equal("Nasi Lemak (Large)", (await service.Tickets()).Single().Items.Single().OrderItem.ItemNameSnapshot);
        Assert.Equal(9.99m, (await service.Menu()).Single().Variations.Single().AdditionalPrice);
    }
    [Theory]
    [InlineData(999)]
    [InlineData(100)]
    [InlineData(101)]
    public async Task MissingUnavailableAndWrongMenuVariationsAreRejected(int variationId)
    {
        await using (var db = factory.CreateDbContext())
        {
            var category = await db.Categories.SingleAsync();
            db.Menu.Add(new() { Id = 2, CategoryId = category.Id, Name = "Other dish", Price = 5m });
            db.MenuVariations.AddRange(new MenuItemVariation { Id = 100, MenuItemId = 2, Name = "Other item variation" }, new MenuItemVariation { Id = 101, MenuItemId = 1, Name = "Unavailable", IsAvailable = false });
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<BusinessException>(() => service.Create(Request() with { Items = [new(1, 1, VariationId: variationId)] }, "waiter"));
        Assert.Empty(await service.Active()); Assert.Empty(await service.Tickets()); Assert.Empty(events.Items);
        Assert.DoesNotContain((await service.Menu()).Single(x => x.Id == 1).Variations, v => v.Id == 101);
    }
    [Fact] public async Task DifferentVariationsAppendAndCancelIndependently()
    {
        await using (var db = factory.CreateDbContext())
        {
            db.MenuVariations.AddRange(new MenuItemVariation { Id = 100, MenuItemId = 1, Name = "Regular" }, new MenuItemVariation { Id = 101, MenuItemId = 1, Name = "Large", AdditionalPrice = 2m });
            await db.SaveChangesAsync();
        }
        var id = await service.Create(Request() with { Items = [new(1, 1, VariationId: 100)] }, "waiter");
        var addition = new AddItemsInput(1, [new(1, 1, VariationId: 101)], "variation-append");
        await service.Add(id, addition, "waiter");
        await service.Add(id, addition, "waiter");
        Assert.Equal(2, (await service.Tickets()).Count);
        var bill = await service.Bill(id); Assert.Equal(19m, bill.Total);
        await service.Cancel(id, bill.Items.Single(i => i.Name.EndsWith("(Large)")).Id, new(2, 1, "Cancel large only"), "waiter");
        bill = await service.Bill(id);
        Assert.Equal(8.50m, bill.Total); Assert.Equal(1, bill.Items.Single(i => i.Name.EndsWith("(Regular)")).Quantity);
    }
    [Fact] public async Task VariationMigrationPreservesExistingOrders()
    {
        var oldFactory = new Factory(new DbContextOptionsBuilder<PosDb>().UseSqlite($"Data Source={Path.Combine(folder, "old.db")};Foreign Keys=True;Pooling=False").Options);
        await using (var db = oldFactory.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261026141506_InitialOrdering");
            db.Users.Add(new ApplicationUser { Id = "waiter", UserName = "waiter" });
            db.Menu.Add(new() { Id = 1, Category = new() { Name = "Food" }, Name = "Existing dish", Price = 8.50m });
            await db.SaveChangesAsync();
        }
        var oldService = new OrderService(oldFactory, events);
        var id = await oldService.Create(Request(), "waiter");
        await using (var db = oldFactory.CreateDbContext()) { await db.Database.MigrateAsync(); Assert.Empty(await db.MenuVariations.ToListAsync()); }
        Assert.Equal(17m, (await oldService.Bill(id)).Total);
        Assert.Single(await oldService.Tickets());
    }
    [Fact] public async Task NewestOrdersAndKitchenTicketsAreFirst()
    {
        var older = await service.Create(Request("older-order"), "waiter");
        var newer = await service.Create(Request("newer-order"), "waiter");
        await using (var db = factory.CreateDbContext())
        {
            foreach (var o in await db.Orders.ToListAsync()) o.CreatedAtUtc = o.Id == newer ? new DateTime(2026, 10, 27, 5, 0, 0, DateTimeKind.Utc) : new DateTime(2026, 10, 27, 4, 0, 0, DateTimeKind.Utc);
            foreach (var t in await db.Tickets.ToListAsync()) t.CreatedAtUtc = t.OrderId == newer ? new DateTime(2026, 10, 27, 5, 0, 0, DateTimeKind.Utc) : new DateTime(2026, 10, 27, 4, 0, 0, DateTimeKind.Utc);
            await db.SaveChangesAsync();
        }
        Assert.Equal(new[] { newer, older }, (await service.Active()).Select(o => o.Id));
        Assert.Equal(new[] { newer, older }, (await service.Tickets()).Select(t => t.OrderId));
    }
    [Fact] public async Task CashCheckoutRecordsChangeAndReleasesTableAtomically()
    {
        var id = await service.Create(Request(dineIn: true), "waiter");
        var input = new CheckoutInput(1, 17m, PaymentMethod.Cash, 20m, "Counter 1", "checkout-cash");
        await service.Checkout(id, input, "waiter");
        Assert.Equal(id, await service.Checkout(id, input, "waiter"));
        var bill = await service.Bill(id);
        Assert.Equal(OrderStatus.Closed, bill.Status); Assert.Equal(17m, bill.Paid); Assert.Equal(0m, bill.Balance);
        var payment = Assert.Single(bill.Payments!); Assert.Equal(20m, payment.CashTendered); Assert.Equal(3m, payment.Change);
        Assert.Equal("Counter 1", payment.Reference); Assert.Empty(await service.Active());
        await using (var db = factory.CreateDbContext())
        {
            Assert.Single(await db.Payments.ToListAsync());
            Assert.Equal(1, await db.Events.CountAsync(e => e.EventType == "PaymentRecorded"));
            Assert.Equal(1, await db.Events.CountAsync(e => e.EventType == "OrderClosed"));
            Assert.Equal(2, (await db.OrderItems.SingleAsync()).Quantity);
        }
        Assert.Single(events.Items, e => e.EventType == "PaymentRecorded"); Assert.Single(events.Items, e => e.EventType == "OrderClosed");
        await Assert.ThrowsAsync<BusinessException>(() => service.Checkout(id, input with { Reference = "Different" }, "waiter"));
        await Assert.ThrowsAsync<BusinessException>(() => service.Add(id, new(2, [new(1, 1)], "paid-append"), "waiter"));
        await Assert.ThrowsAsync<BusinessException>(() => service.Cancel(id, bill.Items.Single().Id, new(2, 1, "Paid item"), "waiter"));
        var next = await service.Create(Request("next-table-order", true), "waiter");
        Assert.NotEqual(id, next); Assert.Equal(next, (await service.Active()).Single().Id);
    }
    [Theory]
    [InlineData(PaymentMethod.QR)]
    [InlineData(PaymentMethod.Transfer)]
    [InlineData(PaymentMethod.Card)]
    [InlineData(PaymentMethod.Other)]
    public async Task NonCashCheckoutSettlesExactBalance(PaymentMethod method)
    {
        var id = await service.Create(Request(), "waiter");
        await service.Checkout(id, new(1, 17m, method, null, "Reference", "noncash-checkout"), "waiter");
        var bill = await service.Bill(id); var payment = Assert.Single(bill.Payments!);
        Assert.Equal(0m, bill.Balance); Assert.Equal(method, payment.Method); Assert.Equal(0m, payment.Change);
    }
    [Theory]
    [InlineData("16", "20", PaymentMethod.Cash)]
    [InlineData("18", "20", PaymentMethod.Cash)]
    [InlineData("17", "16", PaymentMethod.Cash)]
    [InlineData("17", "20", PaymentMethod.QR)]
    [InlineData("17", "20.001", PaymentMethod.Cash)]
    [InlineData("17.001", "20", PaymentMethod.Cash)]
    public async Task InvalidCheckoutLeavesOrderUnpaidAndOpen(string amount, string tendered, PaymentMethod method)
    {
        var id = await service.Create(Request(), "waiter");
        await Assert.ThrowsAsync<BusinessException>(() => service.Checkout(id, new(1, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), method, decimal.Parse(tendered, System.Globalization.CultureInfo.InvariantCulture), "", "invalid-checkout"), "waiter"));
        var bill = await service.Bill(id); Assert.Equal(OrderStatus.Open, bill.Status); Assert.Equal(17m, bill.Balance); Assert.Equal(1, bill.Version); Assert.Empty(bill.Payments!);
        Assert.DoesNotContain(events.Items, e => e.EventType is "PaymentRecorded" or "OrderClosed");
    }
    [Fact] public async Task ConcurrentCheckoutsOnlyRecordOnePayment()
    {
        var id = await service.Create(Request(), "waiter");
        async Task<bool> Try(string key) { try { await service.Checkout(id, new(1, 17m, PaymentMethod.Cash, 20m, "", key), "waiter"); return true; } catch (BusinessException) { return false; } }
        var results = await Task.WhenAll(Task.Run(() => Try("checkout-one")), Task.Run(() => Try("checkout-two")));
        Assert.Single(results, success => success); Assert.Single((await service.Bill(id)).Payments!);
    }
    [Fact] public async Task ChangedOrderCannotBeCheckedOutUsingAnOlderBill()
    {
        var id = await service.Create(Request(), "waiter");
        await service.Add(id, new(1, [new(1, 1)], "added-before-payment"), "waiter");
        await Assert.ThrowsAsync<BusinessException>(() => service.Checkout(id, new(1, 17m, PaymentMethod.Cash, 20m, "", "old-bill-checkout"), "waiter"));
        await Assert.ThrowsAsync<BusinessException>(() => service.Checkout(id, new(2, 17m, PaymentMethod.Cash, 20m, "", "underpaid-checkout"), "waiter"));
        var bill = await service.Bill(id); Assert.Equal(OrderStatus.Open, bill.Status); Assert.Equal(25.50m, bill.Balance); Assert.Empty(bill.Payments!);
    }
    [Fact] public async Task CheckoutRetainsUnfinishedKitchenWork()
    {
        var id = await service.Create(Request(), "waiter");
        await service.Checkout(id, new(1, 17m, PaymentMethod.QR, null, "", "prepaid-checkout"), "waiter");
        var ticket = Assert.Single(await service.Tickets()); Assert.Equal(OrderStatus.Closed, ticket.Order.Status);
        await service.SetTicket(ticket.Id, new(2, KitchenStatus.Preparing), "waiter");
        Assert.Single(await service.Tickets());
        await service.SetTicket(ticket.Id, new(3, KitchenStatus.Ready), "waiter");
        Assert.Empty(await service.Tickets()); Assert.Equal(0m, (await service.Bill(id)).Balance);
    }
    [Fact] public async Task FullyCancelledOrderClosesWithoutInventingAPayment()
    {
        var id = await service.Create(Request(dineIn: true), "waiter"); var item = (await service.Bill(id)).Items.Single();
        await service.Cancel(id, item.Id, new(1, 2, "Whole order cancelled"), "waiter");
        await service.Checkout(id, new(2, 0m, PaymentMethod.Cash, null, "", "zero-checkout"), "waiter");
        var bill = await service.Bill(id); Assert.Equal(OrderStatus.Closed, bill.Status); Assert.Empty(bill.Payments!); Assert.Equal(0m, bill.Paid);
        await service.Create(Request("table-reused", true), "waiter");
    }
    private sealed class Events : IBusinessEvents { public List<BusinessEvent> Items { get; } = []; public Task Publish(BusinessEvent e) { Items.Add(e); return Task.CompletedTask; } }
    private sealed class Factory(DbContextOptions<PosDb> options) : IDbContextFactory<PosDb> { public PosDb CreateDbContext() => new(options); }
}
