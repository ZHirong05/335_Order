using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RestaurantPOS.Infrastructure;
namespace RestaurantPOS.Web;
public sealed class DatabaseHealth(IDbContextFactory<PosDb> factory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try { await using var db = await factory.CreateDbContextAsync(cancellationToken); return await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Database unavailable"); }
        catch { return HealthCheckResult.Unhealthy("Database unavailable"); }
    }
}
public sealed class BackupWorker(IDbContextFactory<PosDb> factory, ILogger<BackupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                var source = db.Database.GetDbConnection().DataSource;
                var folder = Path.Combine(Path.GetDirectoryName(source)!, "backups"); Directory.CreateDirectory(folder);
                var today = Path.Combine(folder, $"restaurant-{DateTime.UtcNow:yyyyMMdd}.db");
                if (!File.Exists(today)) DatabaseMaintenance.Backup(source, today);
                foreach (var old in Directory.GetFiles(folder, "restaurant-*.db").Where(f => File.GetCreationTimeUtc(f) < DateTime.UtcNow.AddDays(-30))) File.Delete(old);
            }
            catch (Exception ex) { logger.LogError(ex, "Daily backup failed"); }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
