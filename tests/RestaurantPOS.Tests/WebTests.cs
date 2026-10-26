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
    private sealed record Token(string token);
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
