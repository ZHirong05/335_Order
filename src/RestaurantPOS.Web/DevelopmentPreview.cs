using Microsoft.AspNetCore.Identity;
using RestaurantPOS.Infrastructure;
namespace RestaurantPOS.Web;
public static class DevelopmentPreview
{
    public const string Email = "preview@development.local";
    public static bool Enabled(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment() && configuration.GetValue<bool>("Development:SkipSignIn");
    public static async Task Authenticate(HttpContext context, RequestDelegate next)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            var users = context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByEmailAsync(Email) ?? throw new InvalidOperationException("Development preview user missing.");
            context.User = await context.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
        }
        await next(context);
    }
}
