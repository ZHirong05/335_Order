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
    public DbSet<MenuItemVariation> MenuVariations => Set<MenuItemVariation>();
    public DbSet<MenuOptionGroup> MenuOptionGroups => Set<MenuOptionGroup>();
    public DbSet<MenuOption> MenuOptions => Set<MenuOption>();
    public DbSet<MenuCatalogImport> MenuCatalogImports => Set<MenuCatalogImport>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<KitchenTicket> Tickets => Set<KitchenTicket>();
    public DbSet<OrderEvent> Events => Set<OrderEvent>();
    public DbSet<RequestRecord> Requests => Set<RequestRecord>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<Expense> Expenses => Set<Expense>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        var money = new ValueConverter<decimal, long>(v => decimal.ToInt64(v * 100m), v => v / 100m);
        b.Entity<Expense>().Property(x => x.Amount).HasConversion(money);
        b.Entity<Expense>().Property(x => x.Description).HasMaxLength(200);
        b.Entity<Expense>().HasIndex(x => x.PurchasedOn);
        b.Entity<Expense>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.RecordedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Expense>().ToTable(t => t.HasCheckConstraint("CK_Expense", "Amount > 0 AND length(Description) BETWEEN 1 AND 200"));
        b.Entity<MenuItem>().Property(x => x.Price).HasConversion(money);
        b.Entity<MenuItemVariation>().Property(x => x.AdditionalPrice).HasConversion(money);
        b.Entity<MenuOption>().Property(x => x.AdditionalPrice).HasConversion(money);
        b.Entity<MenuOption>().ToTable(t => t.HasCheckConstraint("CK_OptionPrice", "AdditionalPrice >= 0"));
        b.Entity<MenuItem>().HasMany(x => x.OptionGroups).WithOne().HasForeignKey(x => x.MenuItemId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MenuOptionGroup>().HasMany(x => x.Options).WithOne().HasForeignKey(x => x.MenuOptionGroupId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MenuOptionGroup>().HasIndex(x => new { x.MenuItemId, x.Name }).IsUnique();
        b.Entity<MenuOptionGroup>().ToTable(t => t.HasCheckConstraint("CK_OptionGroupSelections", "MaxSelections BETWEEN 1 AND 20"));
        b.Entity<MenuOption>().HasIndex(x => new { x.MenuOptionGroupId, x.Name }).IsUnique();
        b.Entity<MenuOption>().HasIndex(x => x.MenuOptionGroupId).IsUnique().HasFilter("IsDefault = 1");
        b.Entity<MenuCatalogImport>().HasKey(x => x.Key);
        b.Entity<MenuItemVariation>().HasIndex(x => new { x.MenuItemId, x.Name }).IsUnique();
        b.Entity<MenuItem>().HasMany(x => x.Variations).WithOne().HasForeignKey(x => x.MenuItemId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MenuItemVariation>().ToTable(t => t.HasCheckConstraint("CK_VariationPrice", "AdditionalPrice >= 0"));
        b.Entity<OrderItem>().Property(x => x.UnitPriceSnapshot).HasConversion(money);
        b.Entity<Payment>().Property(x => x.Amount).HasConversion(money);
        b.Entity<Payment>().Property(x => x.CashTendered).HasConversion(money);
        b.Entity<Payment>().Property(x => x.Change).HasConversion(money);
        b.Entity<Payment>().HasIndex(x => x.IdempotencyKey).IsUnique();
        b.Entity<PaymentAllocation>().Property(x => x.Amount).HasConversion(money);
        b.Entity<PaymentAllocation>().HasIndex(x => new { x.PaymentId, x.OrderItemId }).IsUnique();
        b.Entity<Payment>().HasMany(x => x.Allocations).WithOne().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PaymentAllocation>().HasOne(x => x.OrderItem).WithMany().HasForeignKey(x => x.OrderItemId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PaymentAllocation>().ToTable(t => t.HasCheckConstraint("CK_PaymentAllocation", "Quantity > 0 AND Amount >= 0"));
        b.Entity<Payment>().HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Payment>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ReceivedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Payment>().ToTable(t => t.HasCheckConstraint("CK_Payment", "Amount > 0 AND CashTendered >= Amount AND \"Change\" = CashTendered - Amount AND Method >= 0 AND Method <= 4 AND (Method = 0 OR \"Change\" = 0)"));
        b.Entity<DiningTable>().HasIndex(x => x.Number).IsUnique();
        b.Entity<DiningTable>().Property(x => x.Version).IsConcurrencyToken();
        b.Entity<Order>().Property(x => x.Version).IsConcurrencyToken();
        b.Entity<Order>().HasIndex(x => x.OrderNumber).IsUnique();
        b.Entity<Order>().Ignore(x => x.DisplayNumber);
        b.Entity<Order>().HasIndex(x => new { x.BusinessDate, x.DailyNumber }).IsUnique().HasFilter("DailyNumber > 0");
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
