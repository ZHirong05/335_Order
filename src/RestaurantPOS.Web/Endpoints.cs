using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RestaurantPOS.Core;
using RestaurantPOS.Infrastructure;
namespace RestaurantPOS.Web;
public static class Endpoints
{
    private static readonly SemaphoreSlim SetupLock = new(1);
    public static void MapPosEndpoints(this WebApplication app)
    {
        app.MapPost("/language", async (HttpContext c) =>
        {
            var form = await c.Request.ReadFormAsync();
            var language = form["language"].ToString();
            if (language is not ("en" or "zh")) return Results.BadRequest();
            c.Response.Cookies.Append("335.language", language, new CookieOptions { HttpOnly = true, IsEssential = true,
                SameSite = SameSiteMode.Lax, Secure = c.Request.IsHttps, MaxAge = TimeSpan.FromDays(365), Path = "/" });
            if (c.Request.Headers["X-InPlace-Language"] == "true") return Results.NoContent();
            var target = form["returnUrl"].ToString();
            if (!target.StartsWith('/') || target.StartsWith("//") || target.Contains('\\')) target = "/dashboard";
            return Results.LocalRedirect(target);
        }).RequireAntiforgery();
        app.MapPost("/auth/login", async (HttpContext c, SignInManager<ApplicationUser> signIn) =>
        {
            var f = await c.Request.ReadFormAsync();
            var r = await signIn.PasswordSignInAsync(f["email"].ToString(), f["password"].ToString(), false, true);
            return Results.LocalRedirect(r.Succeeded ? "/dashboard" : "/login?failed=true");
        }).RequireAntiforgery();
        app.MapPost("/auth/logout", async (SignInManager<ApplicationUser> s) => { await s.SignOutAsync(); return Results.LocalRedirect("/login"); }).RequireAuthorization().RequireAntiforgery();
        app.MapPost("/auth/setup", async (HttpContext c, UserManager<ApplicationUser> users, IConfiguration config, PosDb db) =>
        {
            var f = await c.Request.ReadFormAsync(); var token = config["Setup:Token"];
            if (string.IsNullOrEmpty(token) || token.Length < 32 || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(f["token"].ToString()))) return Results.LocalRedirect("/setup?failed=true");
            await SetupLock.WaitAsync();
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                if (await users.Users.AnyAsync()) return Results.Conflict(new { error = "Setup already completed" });
                var email = f["email"].ToString(); var u = new ApplicationUser { UserName = email, Email = email };
                var r = await users.CreateAsync(u, f["password"].ToString());
                if (!r.Succeeded) return Results.LocalRedirect("/setup?failed=true");
                r = await users.AddToRoleAsync(u, "Admin");
                if (!r.Succeeded) { await users.DeleteAsync(u); throw new InvalidOperationException("Admin role assignment failed"); }
                await transaction.CommitAsync();
                return Results.LocalRedirect("/login");
            }
            finally { SetupLock.Release(); }
        }).RequireAntiforgery();
        app.MapHealthChecks("/api/health");
        var api = app.MapGroup("/api").RequireAuthorization();
        api.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                if (context.HttpContext.Request.Method is "POST" or "PATCH" or "PUT" or "DELETE")
                    await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext);
                return await next(context);
            }
            catch (AntiforgeryValidationException) { return Results.Problem("Invalid antiforgery token", statusCode: 400); }
            catch (BusinessException e) { return Results.Problem(e.Message, statusCode: e.StatusCode); }
            catch (DbUpdateConcurrencyException) { return Results.Problem("Record changed. Reload and retry.", statusCode: 409); }
        });
        api.MapGet("/antiforgery", (HttpContext c, IAntiforgery a) => new { token = a.GetAndStoreTokens(c).RequestToken, header = "RequestVerificationToken" });
        var accounting = api.MapGroup("/accounting").RequireAuthorization(p => p.RequireRole("Admin", "Cashier"));
        accounting.MapGet("/day", (DateOnly date, AccountingService s) => s.Day(date));
        accounting.MapPost("/expenses", async (ExpenseInput input, AccountingService s, ClaimsPrincipal user) =>
            Results.Ok(new { id = await s.RecordExpense(input, Actor(user)) }));
        api.MapGet("/statistics", (DateOnly date, ReportPeriod period, ProductGroup? group, AccountingService s) => s.Statistics(date, period, group ?? ProductGroup.All))
            .RequireAuthorization(p => p.RequireRole("Admin", "Cashier"));
        var orders = api.MapGroup("").RequireAuthorization(p => p.RequireRole("Admin", "Waiter", "Cashier"));
        orders.MapGet("/menu", async (OrderService s) => (await s.Menu()).Select(x => new { x.Id, x.Name, x.Description, x.Price, Category = x.Category.Name, Variations = x.Variations.OrderBy(v => v.SortOrder).Select(v => new { v.Id, v.Name, v.AdditionalPrice, Price = x.Price + v.AdditionalPrice }), OptionGroups = x.OptionGroups.OrderBy(g => g.SortOrder).Select(g => new { g.Id, g.Name, g.MaxSelections, Options = g.Options.OrderBy(o => o.SortOrder).Select(o => new { o.Id, o.Name, o.AdditionalPrice, o.IsDefault }) }) }));
        orders.MapGet("/tables", async (OrderService s) => { var a = await s.Active(); return (await s.Tables()).Select(t => new { t.Id, t.Number, t.Capacity, Status = a.Any(o => o.TableId == t.Id) ? "Occupied" : "Available" }); });
        orders.MapGet("/orders/active", async (OrderService s) => (await s.Active()).Select(x => new { x.Id, x.OrderNumber, x.DisplayNumber, x.BusinessDate, x.TableId, x.OrderType, x.Status, x.Version }));
        orders.MapGet("/orders/{id:guid}", (Guid id, OrderService s) => s.Bill(id));
        orders.MapGet("/orders/{id:guid}/bill", (Guid id, OrderService s) => s.Bill(id));
        api.MapPost("/orders/{id:guid}/delete-bill", (Guid id, DeleteBillInput i, OrderService s, ClaimsPrincipal u) => s.DeleteSettledBill(id, i, Actor(u))).RequireAuthorization(p => p.RequireRole("Admin", "Cashier"));
        api.MapPost("/orders/{id:guid}/checkout", (Guid id, CheckoutInput i, OrderService s, ClaimsPrincipal u) => s.Checkout(id, i, Actor(u))).RequireAuthorization(p => p.RequireRole("Admin", "Cashier"));
        api.MapPost("/orders/{id:guid}/split-payments", (Guid id, SplitPaymentInput i, OrderService s, ClaimsPrincipal u) => s.SplitPayment(id, i, Actor(u))).RequireAuthorization(p => p.RequireRole("Admin", "Cashier"));
        api.MapPost("/orders/{id:guid}/cancel-items", (Guid id, CancelItemsInput i, OrderService s, ClaimsPrincipal u) => s.CancelItems(id, i, Actor(u))).RequireAuthorization(p => p.RequireRole("Admin", "Waiter"));
        api.MapPost("/orders", async (CreateOrderInput i, OrderService s, ClaimsPrincipal u) => { var id = await s.Create(i, Actor(u)); return Results.Created($"/api/orders/{id}", new { id }); }).RequireAuthorization(p => p.RequireRole("Admin", "Waiter"));
        api.MapPost("/orders/{id:guid}/items", (Guid id, AddItemsInput i, OrderService s, ClaimsPrincipal u) => s.Add(id, i, Actor(u))).RequireAuthorization(p => p.RequireRole("Admin", "Waiter"));
        api.MapPost("/orders/{id:guid}/items/{itemId:guid}/cancel", (Guid id, Guid itemId, CancelInput i, OrderService s, ClaimsPrincipal u) => s.Cancel(id, itemId, i, Actor(u))).RequireAuthorization(p => p.RequireRole("Admin", "Waiter"));
        api.MapGet("/kitchen/tickets", async (OrderService s) => (await s.Tickets()).Select(t => new { t.Id, t.TicketNumber, t.Status, t.CreatedAtUtc, t.OrderId, t.Order.OrderNumber, t.Order.DisplayNumber, t.Order.BusinessDate, t.Order.Version, t.Order.TableId, Items = t.Items.Select(i => new { i.OrderItem.ItemNameSnapshot, i.Quantity, i.OrderItem.CancelledQuantity, i.Status, i.NotesSnapshot, i.CancellationReason }) })).RequireAuthorization(p => p.RequireRole("Admin", "Kitchen"));
        api.MapPatch("/kitchen/tickets/{id:guid}/status", (Guid id, TicketStatusInput i, OrderService s, ClaimsPrincipal u) => s.SetTicket(id, i, Actor(u))).RequireAuthorization(p => p.RequireRole("Admin", "Kitchen"));
    }
    private static string Actor(ClaimsPrincipal u) => u.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private static RouteHandlerBuilder RequireAntiforgery(this RouteHandlerBuilder endpoint) => endpoint.AddEndpointFilter(async (context, next) =>
    {
        try { await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext); }
        catch (AntiforgeryValidationException) { return Results.Problem("Invalid antiforgery token", statusCode: 400); }
        return await next(context);
    });
}
