using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using RestaurantPOS.Infrastructure;
namespace RestaurantPOS.Web;
// Cookie validation alone does not invalidate an already connected Blazor circuit.
public sealed class RevalidatingAuthentication(ILoggerFactory logger, IServiceScopeFactory scopes)
    : RevalidatingServerAuthenticationStateProvider(logger)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);
    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var u = await users.GetUserAsync(state.User);
        return u != null && !await users.IsLockedOutAsync(u) && state.User.FindFirstValue("AspNet.Identity.SecurityStamp") == await users.GetSecurityStampAsync(u);
    }
}
