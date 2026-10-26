using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RestaurantPOS.Core;
using RestaurantPOS.Infrastructure;
namespace RestaurantPOS.Web;
[Authorize(Roles = "Admin,Waiter,Kitchen,Cashier")]
public sealed class OrdersHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        foreach (var role in new[] { "Admin", "Waiter", "Kitchen", "Cashier" })
            if (Context.User!.IsInRole(role)) await Groups.AddToGroupAsync(Context.ConnectionId, role);
        await base.OnConnectedAsync();
    }
}
public sealed class BusinessNotifications(IHubContext<OrdersHub> hub, ILogger<BusinessNotifications> logger) : IBusinessEvents
{
    public event Action<BusinessEvent>? Changed;
    public async Task Publish(BusinessEvent notification)
    {
        foreach (Action<BusinessEvent> listener in Changed?.GetInvocationList() ?? [])
            try { listener(notification); } catch (Exception ex) { logger.LogWarning(ex, "UI delivery failed {EventId}", notification.EventId); }
        try { await hub.Clients.Groups("Admin", "Waiter", "Kitchen", "Cashier").SendAsync(notification.EventType, notification); }
        catch (Exception ex) { logger.LogWarning(ex, "Business hub delivery failed after commit {EventId}", notification.EventId); }
    }
}
