using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using RestaurantPOS.Core;
namespace RestaurantPOS.Tests;

public sealed class WebTests
{
    [Fact] public async Task DevelopmentPreviewOpensWorkspacesAndCreatesOrdersWithoutLogin()
    {
        using var app = new TestApp(bypass: true);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        foreach (var path in new[] { "/dashboard", "/order", "/kitchen", "/cashier", "/api/orders/active" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        var token = await client.GetFromJsonAsync<Token>("/api/antiforgery");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", token!.token);
        var request = new CreateOrderInput(OrderType.Takeaway, null, "Preview order", [new(1, 1)], Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/orders", request)).StatusCode);
    }
    [Fact] public async Task ProductionIgnoresSkipSignInFlag()
    {
        using var app = new TestApp(bypass: true, environment: "Production");
        using var client = app.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/orders/active")).StatusCode);
    }
    [Fact] public async Task MenuVariationsReachKitchenAndCashierPreviews()
    {
        using var app = new TestApp(bypass: true);
        using var client = app.CreateClient();
        var menu = await client.GetFromJsonAsync<List<MenuView>>("/api/menu");
        var food = menu!.Single(m => m.Name == "Nasi Lemak");
        var large = food.Variations.Single(v => v.Name == "Large");
        Assert.Equal(10.50m, large.Price);
        var csrf = await client.GetFromJsonAsync<Token>("/api/antiforgery");
        client.DefaultRequestHeaders.Add("RequestVerificationToken", csrf!.token);
        var created = await client.PostAsJsonAsync("/api/orders", new CreateOrderInput(OrderType.Takeaway, null, "Variation flow", [new(food.Id, 2, "Less chili", large.Id)], Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var bill = await client.GetFromJsonAsync<Bill>(created.Headers.Location!);
        Assert.Equal(21m, bill!.Total); Assert.Equal("Nasi Lemak (Large)", bill.Items.Single().Name);
        Assert.Contains("Nasi Lemak (Large)", await client.GetStringAsync("/api/kitchen/tickets"));
        var preview = await client.GetStringAsync($"/cashier/{bill.OrderId}");
        Assert.Contains("Nasi Lemak (Large)", preview); Assert.Contains("Less chili", preview);
        Assert.Contains("RM 21.00", await client.GetStringAsync($"/cashier/{bill.OrderId}"));
        var orderPage = await client.GetStringAsync("/order");
        Assert.Contains("打包", orderPage); Assert.Contains("吃", orderPage);
    }
    [Fact] public async Task FirstRunSetupRequiresSecretAndClosesAfterAdminCreation()
    {
        using var app = new TestApp(seed: false);
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var html = await client.GetStringAsync("/setup");
        var csrf = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        Dictionary<string, string> form = new() { ["token"] = "wrong", ["email"] = "owner@demo.local", ["password"] = app.Password, ["__RequestVerificationToken"] = csrf };
        var rejected = await client.PostAsync("/auth/setup", new FormUrlEncodedContent(form));
        Assert.Equal("/setup?failed=true", rejected.Headers.Location!.OriginalString);
        form["token"] = app.SetupToken;
        var created = await client.PostAsync("/auth/setup", new FormUrlEncodedContent(form));
        Assert.Equal("/login", created.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/auth/setup", new FormUrlEncodedContent(form))).StatusCode);
        await Login(client, "owner", app.Password);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/kitchen/tickets")).StatusCode);
    }
    [Fact] public async Task LoginRoleAuthorizationAntiforgeryAndOrderingWorkTogether()
    {
        using var app = new TestApp();
        using var waiter = app.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await waiter.GetAsync("/api/orders/active")).StatusCode);
        await Login(waiter, "waiter", app.Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.GetAsync("/api/kitchen/tickets")).StatusCode);
        var request = new CreateOrderInput(OrderType.Takeaway, null, "Integration order", [new(1, 1)], Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await waiter.PostAsJsonAsync("/api/orders", request)).StatusCode);
        var token = await waiter.GetFromJsonAsync<Token>("/api/antiforgery");
        waiter.DefaultRequestHeaders.Add("RequestVerificationToken", token!.token);
        var created = await waiter.PostAsJsonAsync("/api/orders", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await waiter.GetAsync("/order")).StatusCode);
        using var kitchen = app.CreateClient(new() { AllowAutoRedirect = false });
        await Login(kitchen, "kitchen", app.Password);
        Assert.Equal(HttpStatusCode.OK, (await kitchen.GetAsync("/api/kitchen/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kitchen.GetAsync("/api/orders/active")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await kitchen.PostAsJsonAsync("/api/orders", request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await kitchen.GetAsync("/kitchen")).StatusCode);
        using var cashier = app.CreateClient(new() { AllowAutoRedirect = false });
        await Login(cashier, "cashier", app.Password);
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync(created.Headers.Location!)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/cashier")).StatusCode);
    }
    private static async Task Login(HttpClient c, string role, string password)
    {
        var html = await c.GetStringAsync("/login");
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(token);
        var result = await c.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = role + "@demo.local", ["password"] = password, ["__RequestVerificationToken"] = token }));
        Assert.Equal("/dashboard", result.Headers.Location!.OriginalString);
    }
    [Fact] public async Task OnlyCashierOrAdminCanCheckoutAndTableBecomesAvailable()
    {
        using var app = new TestApp();
        using var waiter = app.CreateClient(new() { AllowAutoRedirect = false });
        await Login(waiter, "waiter", app.Password);
        var waiterToken = await waiter.GetFromJsonAsync<Token>("/api/antiforgery");
        waiter.DefaultRequestHeaders.Add("RequestVerificationToken", waiterToken!.token);
        var request = new CreateOrderInput(OrderType.DineIn, 2, "Checkout integration", [new(1, 1)], Guid.NewGuid().ToString());
        var created = await waiter.PostAsJsonAsync("/api/orders", request); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var bill = (await waiter.GetFromJsonAsync<Bill>(created.Headers.Location!))!;
        bill = (await waiter.GetFromJsonAsync<Bill>(created.Headers.Location!))!;
        var payment = new CheckoutInput(bill.Version, bill.Balance, PaymentMethod.Cash, 20m, "Test counter", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.PostAsJsonAsync($"/api/orders/{bill.OrderId}/checkout", payment)).StatusCode);
        using var cashier = app.CreateClient(new() { AllowAutoRedirect = false }); await Login(cashier, "cashier", app.Password);
        Assert.Equal(HttpStatusCode.BadRequest, (await cashier.PostAsJsonAsync($"/api/orders/{bill.OrderId}/checkout", payment)).StatusCode);
        var cashierToken = await cashier.GetFromJsonAsync<Token>("/api/antiforgery");
        cashier.DefaultRequestHeaders.Add("RequestVerificationToken", cashierToken!.token);
        Assert.Equal(HttpStatusCode.OK, (await cashier.PostAsJsonAsync($"/api/orders/{bill.OrderId}/checkout", payment)).StatusCode);
        var settled = (await cashier.GetFromJsonAsync<Bill>(created.Headers.Location!))!;
        Assert.Equal(OrderStatus.Closed, settled.Status); Assert.Equal(0m, settled.Balance); Assert.Single(settled.Payments!);
        var tables = await cashier.GetFromJsonAsync<List<TableView>>("/api/tables");
        Assert.Equal("Available", tables!.Single(t => t.Id == 2).Status);
        Assert.Contains("CLOSED", await cashier.GetStringAsync($"/cashier/{bill.OrderId}"));
        Assert.Equal(HttpStatusCode.Created, (await waiter.PostAsJsonAsync("/api/orders", request with { IdempotencyKey = Guid.NewGuid().ToString() })).StatusCode);
    }
    private sealed record Token(string token);
    [Fact] public async Task OptionalCancellationAndSettledCashierSectionWorkThroughHttp()
    {
        using var app = new TestApp(bypass: true); using var client = app.CreateClient();
        var token = await client.GetFromJsonAsync<Token>("/api/antiforgery"); client.DefaultRequestHeaders.Add("RequestVerificationToken", token!.token);
        var created = await client.PostAsJsonAsync("/api/orders", new CreateOrderInput(OrderType.Takeaway, null, "Sections", [new(1, 2)], Guid.NewGuid().ToString()));
        var bill = (await client.GetFromJsonAsync<Bill>(created.Headers.Location!))!;
        var cancelled = await client.PostAsJsonAsync($"/api/orders/{bill.OrderId}/cancel-items", new { version = bill.Version, items = new[] { new { itemId = bill.Items.Single().Id, quantity = 1 } }, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        bill = (await client.GetFromJsonAsync<Bill>(created.Headers.Location!))!;
        bill = (await client.GetFromJsonAsync<Bill>(created.Headers.Location!))!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/orders/{bill.OrderId}/checkout", new CheckoutInput(bill.Version, bill.Balance, PaymentMethod.Cash, 20m, "", Guid.NewGuid().ToString()))).StatusCode);
        var cashier = await client.GetStringAsync("/cashier");
        Assert.DoesNotContain("Ongoing bills", cashier); Assert.Contains("Settled bills", cashier);
        Assert.Contains($"/cashier/{bill.OrderId}", cashier); Assert.Contains("View receipt", cashier);
        Assert.DoesNotContain($"/order/{bill.OrderId}", await client.GetStringAsync("/order"));
    }
    [Fact] public async Task PaymentPageBackReturnsToEditableOrder()
    {
        using var app = new TestApp(bypass: true); using var client = app.CreateClient();
        var csrf = await client.GetFromJsonAsync<Token>("/api/antiforgery"); client.DefaultRequestHeaders.Add("RequestVerificationToken", csrf!.token);
        var created = await client.PostAsJsonAsync("/api/orders", new CreateOrderInput(OrderType.Takeaway, null, "Workflow", [new(1, 2)], Guid.NewGuid().ToString()));
        var bill = (await client.GetFromJsonAsync<Bill>(created.Headers.Location!))!;
        var page = await client.GetStringAsync("/order"); Assert.Contains(bill.DisplayNumber, page); Assert.Contains("Nasi Lemak", page); Assert.DoesNotContain(bill.OrderNumber, page);
        page = await client.GetStringAsync($"/order/{bill.OrderId}"); Assert.Contains("Add Item", page); Assert.Contains("Cancel Item", page); Assert.DoesNotContain("Search food or drinks", page);
        Assert.DoesNotContain($"/cashier/{bill.OrderId}", await client.GetStringAsync("/cashier"));
        Assert.DoesNotContain($"/cashier/{bill.OrderId}", await client.GetStringAsync("/cashier"));
        bill = (await client.GetFromJsonAsync<Bill>(created.Headers.Location!))!;
        Assert.DoesNotContain($"/cashier/{bill.OrderId}", await client.GetStringAsync("/cashier"));
        bill = (await client.GetFromJsonAsync<Bill>(created.Headers.Location!))!;
        var paymentPage = await client.GetStringAsync($"/cashier/{bill.OrderId}");
        Assert.Contains($"/order/{bill.OrderId}", paymentPage);
        Assert.DoesNotContain("Undo checkout", paymentPage);
        Assert.Equal(OrderStatus.Open, bill.Status);
        var item = bill.Items.Single();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/orders/{bill.OrderId}/cancel-items", new CancelItemsInput(bill.Version, [new(item.Id, 3)], "Too many", Guid.NewGuid().ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/orders/{bill.OrderId}/cancel-items", new CancelItemsInput(bill.Version, [new(item.Id, 1)], "No longer needed", Guid.NewGuid().ToString()))).StatusCode);
        Assert.Equal(1, (await client.GetFromJsonAsync<Bill>(created.Headers.Location!))!.Items.Single().Quantity);
        page = await client.GetStringAsync("/kitchen"); Assert.Contains("<del>1", page); Assert.Contains("No longer needed", page);
    }
    private sealed record TableView(int Id, string Status);
    private sealed record MenuView(int Id, string Name, decimal Price, List<VariationView> Variations);
    private sealed record VariationView(int Id, string Name, decimal AdditionalPrice, decimal Price);
    private sealed class TestApp(bool seed = true, bool bypass = false, string environment = "Development") : WebApplicationFactory<Program>
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "335-web-" + Guid.NewGuid());
        public string Password { get; } = Guid.NewGuid() + "Aa1!";
        public string SetupToken { get; } = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Development:SkipSignIn", bypass.ToString());
            builder.UseSetting("Data:Directory", folder);
            builder.UseSetting("Seed:Enabled", seed.ToString());
            if (!bypass) builder.UseSetting("Seed:Password", Password);
            builder.UseSetting("Setup:Token", SetupToken);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing); SqliteConnection.ClearAllPools();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
