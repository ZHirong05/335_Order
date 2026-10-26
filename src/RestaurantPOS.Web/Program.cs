using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RestaurantPOS.Infrastructure;
using RestaurantPOS.Web;
using RestaurantPOS.Web.Components;

var builder = WebApplication.CreateBuilder(args);
if (args.Length > 0 && args[0] == "verify") { DatabaseMaintenance.Verify(Path.GetFullPath(args[1])); return; }
var data = Path.GetFullPath(builder.Configuration["Data:Directory"] ?? (builder.Environment.IsDevelopment()
    ? Path.Combine(builder.Environment.ContentRootPath, "data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RestaurantPOS", "data")));
Directory.CreateDirectory(data);
var database = Path.Combine(data, "restaurant.db");
if (args.Length > 0 && args[0] == "backup") { DatabaseMaintenance.Backup(database, Path.GetFullPath(args[1])); return; }
builder.Services.AddWindowsService(o => o.ServiceName = "RestaurantPOS");
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
if (OperatingSystem.IsWindows() && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
    builder.Logging.AddEventLog(new Microsoft.Extensions.Logging.EventLog.EventLogSettings { SourceName = "RestaurantPOS" });
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "keys"))).SetApplicationName("335_Order");
builder.Services.AddDbContextFactory<PosDb>(o => o.UseSqlite(new SqliteConnectionStringBuilder { DataSource = database, ForeignKeys = true, DefaultTimeout = 15 }.ToString()));
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(o =>
{
    o.Password.RequiredLength = 12; o.User.RequireUniqueEmail = true;
    o.Lockout.MaxFailedAccessAttempts = 5; o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
}).AddEntityFrameworkStores<PosDb>().AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/login"; o.AccessDeniedPath = "/login";
    o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = true;
    o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, RevalidatingAuthentication>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<BusinessNotifications>();
builder.Services.AddSingleton<IBusinessEvents>(p => p.GetRequiredService<BusinessNotifications>());
builder.Services.AddScoped<OrderService>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealth>("sqlite");
builder.Services.AddHostedService<BackupWorker>();
var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PosDb>();
    await db.Database.MigrateAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await Seed.Run(scope.ServiceProvider, app.Environment, app.Configuration);
}
if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/Error"); app.UseHsts(); app.UseHttpsRedirection(); }
app.UseAuthentication();
if (DevelopmentPreview.Enabled(app.Environment, app.Configuration)) app.Use(DevelopmentPreview.Authenticate);
app.UseAuthorization(); app.UseAntiforgery();
app.MapPosEndpoints();
app.MapHub<OrdersHub>("/hubs/orders");
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
public partial class Program;
