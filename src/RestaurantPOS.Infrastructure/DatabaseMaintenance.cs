using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace RestaurantPOS.Infrastructure;
public sealed class DesignFactory : IDesignTimeDbContextFactory<PosDb>
{
    public PosDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<PosDb>().UseSqlite("Data Source=design.db").Options);
}
public static class DatabaseMaintenance
{
    public static void Verify(string file)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA integrity_check";
        if (!Equals(command.ExecuteScalar(), "ok")) throw new IOException("Database integrity check failed");
    }
    public static void Backup(string source, string destination)
    {
        if (File.Exists(destination)) throw new IOException("Backup destination already exists");
        using var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        using var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        from.Open(); to.Open(); from.BackupDatabase(to);
        using var check = to.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
        if (!Equals(check.ExecuteScalar(), "ok")) throw new IOException("Backup failed integrity check");
    }
}
