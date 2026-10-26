using Microsoft.EntityFrameworkCore;
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
    private sealed class Events : IBusinessEvents { public List<BusinessEvent> Items { get; } = []; public Task Publish(BusinessEvent e) { Items.Add(e); return Task.CompletedTask; } }
    private sealed class Factory(DbContextOptions<PosDb> options) : IDbContextFactory<PosDb> { public PosDb CreateDbContext() => new(options); }
}
