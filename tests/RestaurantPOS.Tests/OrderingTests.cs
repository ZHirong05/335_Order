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
        var id = Guid.NewGuid(); var itemId = Guid.NewGuid(); var ticketId = Guid.NewGuid();
        var oldReference = "D-20261027-preserved-reference";
        await using (var db = oldFactory.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Orders (Id,OrderNumber,OrderType,TableId,Status,CreatedAtUtc,UpdatedAtUtc,CreatedByUserId,Notes,Version) VALUES ({id},{oldReference},1,NULL,0,{"2026-10-27 10:00:00"},{"2026-10-27 10:00:00"},{"waiter"},{""},1)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO OrderItems (Id,OrderId,MenuItemId,ItemNameSnapshot,UnitPriceSnapshot,Quantity,CancelledQuantity,Notes,KitchenStatus,CreatedAtUtc) VALUES ({itemId},{id},1,{"Existing dish"},850,2,0,{"No chili"},0,{"2026-10-27 10:00:00"})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Tickets (Id,OrderId,TicketNumber,Status,CreatedAtUtc,CompletedAtUtc) VALUES ({ticketId},{id},{"K-legacy"},0,{"2026-10-27 10:00:00"},NULL)");
            await db.Database.MigrateAsync();
            Assert.Empty(await db.MenuVariations.ToListAsync());
        }
        var migrated = await oldService.Bill(id);
        Assert.Equal(17m, migrated.Total); Assert.Equal(id, migrated.OrderId); Assert.Equal(oldReference, migrated.OrderNumber);
        Assert.Equal("#0001", migrated.DisplayNumber); Assert.Equal("2026-10-27", migrated.BusinessDate);
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
        var input = new CheckoutInput(await QueueCheckout(id), 17m, PaymentMethod.Cash, 20m, "Counter 1", "checkout-cash");
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
        await service.Checkout(id, new(await QueueCheckout(id), 17m, method, null, "Reference", "noncash-checkout"), "waiter");
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
        var paymentVersion = await QueueCheckout(id);
        await Assert.ThrowsAsync<BusinessException>(() => service.Checkout(id, new(paymentVersion, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), method, decimal.Parse(tendered, System.Globalization.CultureInfo.InvariantCulture), "", "invalid-checkout"), "waiter"));
        var bill = await service.Bill(id); Assert.Equal(OrderStatus.AwaitingPayment, bill.Status); Assert.Equal(17m, bill.Balance); Assert.Equal(paymentVersion, bill.Version); Assert.Empty(bill.Payments!);
        Assert.DoesNotContain(events.Items, e => e.EventType is "PaymentRecorded" or "OrderClosed");
    }
    [Fact] public async Task ConcurrentCheckoutsOnlyRecordOnePayment()
    {
        var id = await service.Create(Request(), "waiter");
        var paymentVersion = await QueueCheckout(id);
        async Task<bool> Try(string key) { try { await service.Checkout(id, new(paymentVersion, 17m, PaymentMethod.Cash, 20m, "", key), "waiter"); return true; } catch (BusinessException) { return false; } }
        var results = await Task.WhenAll(Task.Run(() => Try("checkout-one")), Task.Run(() => Try("checkout-two")));
        Assert.Single(results, success => success); Assert.Single((await service.Bill(id)).Payments!);
    }
    [Fact] public async Task ChangedOrderCannotBeCheckedOutUsingAnOlderBill()
    {
        var id = await service.Create(Request(), "waiter");
        await service.Add(id, new(1, [new(1, 1)], "added-before-payment"), "waiter");
        var paymentVersion = await QueueCheckout(id);
        await Assert.ThrowsAsync<BusinessException>(() => service.Checkout(id, new(1, 17m, PaymentMethod.Cash, 20m, "", "old-bill-checkout"), "waiter"));
        await Assert.ThrowsAsync<BusinessException>(() => service.Checkout(id, new(paymentVersion, 17m, PaymentMethod.Cash, 20m, "", "underpaid-checkout"), "waiter"));
        var bill = await service.Bill(id); Assert.Equal(OrderStatus.AwaitingPayment, bill.Status); Assert.Equal(25.50m, bill.Balance); Assert.Empty(bill.Payments!);
    }
    [Fact] public async Task CheckoutRetainsUnfinishedKitchenWork()
    {
        var id = await service.Create(Request(), "waiter");
        await service.Checkout(id, new(await QueueCheckout(id), 17m, PaymentMethod.QR, null, "", "prepaid-checkout"), "waiter");
        var ticket = Assert.Single(await service.Tickets()); Assert.Equal(OrderStatus.Closed, ticket.Order.Status);
        await service.SetTicket(ticket.Id, new(ticket.Order.Version, KitchenStatus.Preparing), "waiter");
        Assert.Single(await service.Tickets());
        await service.SetTicket(ticket.Id, new((await service.Bill(id)).Version, KitchenStatus.Ready), "waiter");
        Assert.Empty(await service.Tickets()); Assert.Equal(0m, (await service.Bill(id)).Balance);
    }
    [Fact] public async Task FullyCancelledOrderClosesWithoutInventingAPayment()
    {
        var id = await service.Create(Request(dineIn: true), "waiter"); var item = (await service.Bill(id)).Items.Single();
        await service.Cancel(id, item.Id, new(1, 2, "Whole order cancelled"), "waiter");
        await service.Checkout(id, new(await QueueCheckout(id), 0m, PaymentMethod.Cash, null, "", "zero-checkout"), "waiter");
        var bill = await service.Bill(id); Assert.Equal(OrderStatus.Closed, bill.Status); Assert.Empty(bill.Payments!); Assert.Equal(0m, bill.Paid);
        await service.Create(Request("table-reused", true), "waiter");
    }
    private async Task<long> QueueCheckout(Guid id)
    {
        var bill = await service.Bill(id);
        await service.RequestCheckout(id, new(bill.Version, Guid.NewGuid().ToString()), "waiter");
        return (await service.Bill(id)).Version;
    }
    [Fact] public async Task DailyNumbersResetAtMalaysiaMidnightAndKeepInternalReferences()
    {
        var time = new TestClock(DateTimeOffset.Parse("2026-10-26T15:59:00Z"));
        var numbered = new OrderService(factory, events, time);
        var first = await numbered.Create(Request("day-first"), "waiter");
        var second = await numbered.Create(Request("day-second"), "waiter");
        Assert.Equal("#0001", (await numbered.Bill(first)).DisplayNumber); Assert.Equal("#0002", (await numbered.Bill(second)).DisplayNumber);
        Assert.Equal("2026-10-26", (await numbered.Bill(first)).BusinessDate);
        time.Now = DateTimeOffset.Parse("2026-10-26T16:00:00Z");
        var nextDay = await numbered.Create(Request("next-day-first"), "waiter");
        Assert.Equal("#0001", (await numbered.Bill(nextDay)).DisplayNumber); Assert.Equal("2026-10-27", (await numbered.Bill(nextDay)).BusinessDate);
        Assert.Equal(first, await numbered.Create(Request("day-first"), "waiter"));
        Assert.StartsWith("T-", (await numbered.Bill(first)).OrderNumber);
        Assert.Equal(3, (await numbered.Active()).Count);
    }
    [Fact] public async Task ConcurrentOrderCreationAllocatesUniqueDailyNumbers()
    {
        var time = new TestClock(DateTimeOffset.Parse("2026-10-27T10:00:00Z"));
        var numbered = new OrderService(factory, events, time);
        var ids = await Task.WhenAll(Enumerable.Range(1, 6).Select(i => Task.Run(() => numbered.Create(Request($"concurrent-{i}"), "waiter"))));
        await using var db = factory.CreateDbContext();
        Assert.Equal(6, ids.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 6), await db.Orders.OrderBy(o => o.DailyNumber).Select(o => o.DailyNumber).ToListAsync());
    }
    [Fact] public async Task CheckoutHandoffCanBeUndoneUntilPaymentIsRecorded()
    {
        var id = await service.Create(Request(dineIn: true), "waiter");
        var request = new HandoffInput(1, "handoff-request");
        await service.RequestCheckout(id, request, "waiter"); await service.RequestCheckout(id, request, "waiter");
        var bill = await service.Bill(id); Assert.Equal(OrderStatus.AwaitingPayment, bill.Status); Assert.Empty(bill.Payments!);
        await Assert.ThrowsAsync<BusinessException>(() => service.Create(Request("occupied-during-handoff", true), "waiter"));
        await Assert.ThrowsAsync<BusinessException>(() => service.Add(id, new(bill.Version, [new(1, 1)], "locked-addition"), "waiter"));
        await Assert.ThrowsAsync<BusinessException>(() => service.Cancel(id, bill.Items.Single().Id, new(bill.Version, 1, "Wait"), "waiter"));
        await service.UndoCheckout(id, new(bill.Version, "handoff-undo"), "waiter");
        bill = await service.Bill(id); Assert.Equal(OrderStatus.Open, bill.Status);
        await service.Add(id, new(bill.Version, [new(1, 1)], "after-undo-add"), "waiter");
        var editableVersion = (await service.Bill(id)).Version;
        await Assert.ThrowsAsync<BusinessException>(() => service.Checkout(id, new(editableVersion, 25.50m, PaymentMethod.Cash, 30m, "", "not-handed-off"), "waiter"));
        var version = await QueueCheckout(id);
        await service.Checkout(id, new(version, 25.50m, PaymentMethod.Cash, 30m, "", "final-payment"), "waiter");
        bill = await service.Bill(id);
        await Assert.ThrowsAsync<BusinessException>(() => service.UndoCheckout(id, new(bill.Version, "undo-paid-order"), "waiter"));
        Assert.Single(bill.Payments!); Assert.Equal(0m, bill.Balance);
    }
    [Fact] public async Task BatchCancellationIsAtomicQuantityBoundedAndIdempotent()
    {
        var id = await service.Create(Request() with { Items = [new(1, 2), new(1, 3)] }, "waiter");
        var bill = await service.Bill(id); var first = bill.Items.Single(i => i.Quantity == 2); var second = bill.Items.Single(i => i.Quantity == 3);
        await Assert.ThrowsAsync<BusinessException>(() => service.CancelItems(id, new(1, [new(first.Id, 1), new(second.Id, 4)], "Invalid batch", "bad-batch"), "waiter"));
        Assert.Equal(42.50m, (await service.Bill(id)).Total); Assert.Equal(1, (await service.Bill(id)).Version);
        var input = new CancelItemsInput(1, [new(first.Id, 1), new(second.Id, 2)], "Customer changed mind", "valid-batch");
        await service.CancelItems(id, input, "waiter"); await service.CancelItems(id, input, "waiter");
        bill = await service.Bill(id); Assert.Equal(17m, bill.Total); Assert.All(bill.Items, i => Assert.Equal(1, i.Quantity));
        var ticket = Assert.Single(await service.Tickets());
        Assert.All(ticket.Items, i => Assert.Equal("Customer changed mind", i.CancellationReason));
        Assert.Equal(5, ticket.Items.Sum(i => i.Quantity)); Assert.Equal(3, ticket.Items.Sum(i => i.OrderItem.CancelledQuantity));
        await Assert.ThrowsAsync<BusinessException>(() => service.CancelItems(id, new(bill.Version, [new(first.Id, 2)], "Too many", "over-cancel"), "waiter"));
        Assert.Single(events.Items, e => e.EventType == "ItemCancelled");
    }
    [Fact] public async Task SplitPaymentsPersistRemainingQuantitiesAndCompleteReceipt()
    {
        var id = await service.Create(Request(dineIn: true), "waiter");
        var version = await QueueCheckout(id); var item = (await service.Bill(id)).Items.Single();
        var input = new SplitPaymentInput(version, [new(item.Id, 1)], 8.50m, PaymentMethod.Cash, 10m, "person one", "split-first");
        await service.SplitPayment(id, input, "waiter");
        await service.SplitPayment(id, input, "waiter");
        var restarted = new OrderService(factory, events); var bill = await restarted.Bill(id);
        Assert.Equal(OrderStatus.AwaitingPayment, bill.Status); Assert.Equal(8.50m, bill.Balance);
        Assert.Equal(2, bill.Items.Single().Quantity); Assert.Equal(1, bill.Items.Single().RemainingQuantity);
        Assert.Equal(1.50m, Assert.Single(bill.Payments!).Change);
        await Assert.ThrowsAsync<BusinessException>(() => service.Create(Request("still-occupied", true), "waiter"));
        await Assert.ThrowsAsync<BusinessException>(() => service.UndoCheckout(id, new(bill.Version, "undo-partial"), "waiter"));
        await restarted.SplitPayment(id, new(bill.Version, [new(item.Id, 1)], 8.50m, PaymentMethod.QR, null, "person two", "split-last"), "waiter");
        bill = await restarted.Bill(id);
        Assert.Equal(OrderStatus.Closed, bill.Status); Assert.Equal(17m, bill.Total); Assert.Equal(0m, bill.Balance);
        Assert.Equal(2, bill.Payments!.Count); Assert.Equal(0, bill.Items.Single().RemainingQuantity);
        Assert.All(bill.Payments!, p => Assert.Equal(1, Assert.Single(p.Items!).Quantity));
        await service.Create(Request("table-released", true), "waiter");
    }
    [Fact] public async Task InvalidSplitSelectionsDoNotRecordPaymentsOrChangeVersion()
    {
        var id = await service.Create(Request(), "waiter"); var version = await QueueCheckout(id);
        var item = (await service.Bill(id)).Items.Single();
        List<List<PaymentSelection>> invalid = [[], [new(item.Id, 0)], [new(item.Id, 3)], [new(Guid.NewGuid(), 1)], [new(item.Id, 1), new(item.Id, 1)]];
        foreach (var selections in invalid)
            await Assert.ThrowsAsync<BusinessException>(() => service.SplitPayment(id, new(version, selections, 8.50m, PaymentMethod.Cash, 10m, "", Guid.NewGuid().ToString()), "waiter"));
        foreach (var amount in new[] { 8m, 8.501m, 20m })
            await Assert.ThrowsAsync<BusinessException>(() => service.SplitPayment(id, new(version, [new(item.Id, 1)], amount, PaymentMethod.QR, null, "", Guid.NewGuid().ToString()), "waiter"));
        var bill = await service.Bill(id); Assert.Empty(bill.Payments!); Assert.Equal(version, bill.Version);
    }
    [Fact] public async Task PaidQuantitiesCannotBePaidAgainAndFullCheckoutUsesRemainder()
    {
        var id = await service.Create(Request(), "waiter"); var version = await QueueCheckout(id); var item = (await service.Bill(id)).Items.Single();
        await service.SplitPayment(id, new(version, [new(item.Id, 1)], 8.50m, PaymentMethod.QR, null, "", "one-person"), "waiter");
        var bill = await service.Bill(id);
        await Assert.ThrowsAsync<BusinessException>(() => service.SplitPayment(id, new(bill.Version, [new(item.Id, 2)], 17m, PaymentMethod.QR, null, "", "over-remaining"), "waiter"));
        await Assert.ThrowsAsync<BusinessException>(() => service.SplitPayment(id, new(version, [new(item.Id, 1)], 8.50m, PaymentMethod.QR, null, "", "stale-payment"), "waiter"));
        await service.Checkout(id, new(bill.Version, 8.50m, PaymentMethod.Card, null, "", "remaining-full"), "waiter");
        bill = await service.Bill(id); Assert.Equal(0m, bill.Balance); Assert.Equal(2, bill.Items.Single().PaidQuantity);
        Assert.Equal(2, bill.Payments!.SelectMany(p => p.Items!).Sum(a => a.Quantity));
    }
    [Fact] public async Task LegacyFullPaymentMigrationRestoresItemAllocations()
    {
        var id = await service.Create(Request(), "waiter"); var version = await QueueCheckout(id);
        await service.Checkout(id, new(version, 17m, PaymentMethod.Cash, 20m, "legacy", "legacy-payment"), "waiter");
        var before = await service.Bill(id);
        await using (var db = factory.CreateDbContext())
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261027145319_DailyNumbersAndOrderWorkflow");
            await migrator.MigrateAsync();
        }
        var bill = await service.Bill(id);
        Assert.Equal(before.OrderNumber, bill.OrderNumber); Assert.Equal(before.Payments!.Single().Id, bill.Payments!.Single().Id);
        Assert.Equal(2, bill.Items.Single().PaidQuantity); Assert.Equal(0, bill.Items.Single().RemainingQuantity);
        Assert.Equal(17m, Assert.Single(bill.Payments!.Single().Items!).Amount);
    }
    private sealed class TestClock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now; }
    [Fact] public async Task CancellationReasonIsOptionalForSingleAndBatchOperations()
    {
        var id = await service.Create(Request(), "waiter"); var item = (await service.Bill(id)).Items.Single();
        await service.Cancel(id, item.Id, new(1, 1), "waiter");
        await service.CancelItems(id, new(2, [new(item.Id, 1)], IdempotencyKey: "optional-reason"), "waiter");
        var ticket = Assert.Single(await service.Tickets());
        Assert.Equal("", ticket.Items.Single().CancellationReason); Assert.Equal(KitchenStatus.Cancelled, ticket.Status);
        Assert.Equal(0m, (await service.Bill(id)).Total);
        Assert.Equal(2, ticket.Items.Single().Quantity);
    }
    [Fact] public async Task CashierOrdersIncludePendingAndSettledButContinueExcludesSettled()
    {
        var open = await service.Create(Request("open-for-waiter"), "waiter");
        var ongoing = await service.Create(Request("ongoing-for-cashier"), "waiter");
        var settled = await service.Create(Request("settled-for-history"), "waiter");
        await QueueCheckout(ongoing); var version = await QueueCheckout(settled);
        await service.Checkout(settled, new(version, 17m, PaymentMethod.Cash, 20m, "", "settle-history"), "waiter");
        var cashier = await service.CashierOrders();
        Assert.Contains(cashier, o => o.Id == ongoing && o.Status == OrderStatus.AwaitingPayment);
        Assert.Contains(cashier, o => o.Id == settled && o.Status == OrderStatus.Closed);
        Assert.DoesNotContain(cashier, o => o.Id == open);
        var continued = await service.Active();
        Assert.DoesNotContain(continued, o => o.Id == settled);
        Assert.Contains(continued, o => o.Id == open); Assert.Contains(continued, o => o.Id == ongoing);
    }
    [Fact] public async Task PaymentAndUndoCheckoutCannotBothSucceed()
    {
        var id = await service.Create(Request(), "waiter"); var version = await QueueCheckout(id);
        async Task<bool> TryUndo() { try { await service.UndoCheckout(id, new(version, "race-undo"), "waiter"); return true; } catch (BusinessException) { return false; } }
        async Task<bool> TryPay() { try { await service.Checkout(id, new(version, 17m, PaymentMethod.Cash, 20m, "", "race-payment"), "waiter"); return true; } catch (BusinessException) { return false; } }
        var result = await Task.WhenAll(Task.Run(TryUndo), Task.Run(TryPay));
        Assert.Single(result, successful => successful);
        var bill = await service.Bill(id);
        if (bill.Status == OrderStatus.Open) { Assert.Empty(bill.Payments!); Assert.Equal(17m, bill.Balance); }
        else { Assert.Equal(OrderStatus.Closed, bill.Status); Assert.Single(bill.Payments!); Assert.Equal(0m, bill.Balance); }
    }
    private sealed class Events : IBusinessEvents { public List<BusinessEvent> Items { get; } = []; public Task Publish(BusinessEvent e) { lock (Items) Items.Add(e); return Task.CompletedTask; } }
    private sealed class Factory(DbContextOptions<PosDb> options) : IDbContextFactory<PosDb> { public PosDb CreateDbContext() => new(options); }
}
