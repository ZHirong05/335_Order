using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RestaurantPOS.Core;

namespace RestaurantPOS.Infrastructure;

public sealed class ApplicationUser : IdentityUser;
public sealed class PosDb(DbContextOptions<PosDb> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<DiningTable> Tables => Set<DiningTable>();
    public DbSet<MenuCategory> Categories => Set<MenuCategory>();
    public DbSet<MenuItem> Menu => Set<MenuItem>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<KitchenTicket> Tickets => Set<KitchenTicket>();
    public DbSet<OrderEvent> Events => Set<OrderEvent>();
    public DbSet<RequestRecord> Requests => Set<RequestRecord>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        var money = new ValueConverter<decimal, long>(v => decimal.ToInt64(v * 100m), v => v / 100m);
        b.Entity<MenuItem>().Property(x => x.Price).HasConversion(money);
        b.Entity<OrderItem>().Property(x => x.UnitPriceSnapshot).HasConversion(money);
        b.Entity<DiningTable>().HasIndex(x => x.Number).IsUnique();
        b.Entity<DiningTable>().Property(x => x.Version).IsConcurrencyToken();
        b.Entity<Order>().Property(x => x.Version).IsConcurrencyToken();
        b.Entity<Order>().HasIndex(x => x.OrderNumber).IsUnique();
        b.Entity<Order>().HasIndex(x => x.TableId).IsUnique().HasFilter("TableId IS NOT NULL AND Status <> 2");
        b.Entity<Order>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Order>().HasOne(x => x.Table).WithMany().HasForeignKey(x => x.TableId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Order>().ToTable(t => t.HasCheckConstraint("CK_Order_Type", "(OrderType = 0 AND TableId IS NOT NULL) OR (OrderType = 1 AND TableId IS NULL)"));
        b.Entity<MenuItem>().ToTable(t => t.HasCheckConstraint("CK_Price", "Price >= 0"));
        b.Entity<OrderItem>().HasOne<MenuItem>().WithMany().HasForeignKey(x => x.MenuItemId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<OrderItem>().ToTable(t => t.HasCheckConstraint("CK_Quantity", "Quantity > 0 AND CancelledQuantity >= 0 AND CancelledQuantity <= Quantity"));
        b.Entity<KitchenTicket>().HasIndex(x => x.TicketNumber).IsUnique();
        b.Entity<KitchenTicketItem>().HasOne(x => x.OrderItem).WithMany().HasForeignKey(x => x.OrderItemId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<OrderEvent>().HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<RequestRecord>().HasKey(x => x.Key);
        b.Entity<RequestRecord>().HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}
