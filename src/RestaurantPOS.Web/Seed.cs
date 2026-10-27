using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RestaurantPOS.Core;
using RestaurantPOS.Infrastructure;
namespace RestaurantPOS.Web;
public static class Seed
{
    public static async Task Run(IServiceProvider services, IHostEnvironment environment, IConfiguration config)
    {
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { "Admin", "Waiter", "Kitchen", "Cashier" })
            if (!await roles.RoleExistsAsync(role)) { var r = await roles.CreateAsync(new(role)); if (!r.Succeeded) throw new InvalidOperationException("Role seed failed"); }
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var preview = DevelopmentPreview.Enabled(environment, config);
        if (preview)
        {
            var user = await users.FindByEmailAsync(DevelopmentPreview.Email);
            if (user == null)
            {
                user = new() { UserName = DevelopmentPreview.Email, Email = DevelopmentPreview.Email };
                var result = await users.CreateAsync(user);
                if (!result.Succeeded) throw new InvalidOperationException("Could not create preview identity.");
                result = await users.AddToRoleAsync(user, "Admin");
                if (!result.Succeeded) throw new InvalidOperationException("Could not assign preview role.");
            }
        }
        if (!environment.IsDevelopment() || !config.GetValue<bool>("Seed:Enabled")) return;
        var password = config["Seed:Password"];
        if (!preview && string.IsNullOrEmpty(password)) throw new InvalidOperationException("Set Seed__Password to an explicit development-only password (12+ characters).");
        foreach (var role in string.IsNullOrEmpty(password) ? Array.Empty<string>() : new[] { "Admin", "Waiter", "Kitchen", "Cashier" })
        {
            var email = $"{role.ToLowerInvariant()}@demo.local";
            var user = await users.FindByEmailAsync(email);
            if (user == null)
            {
                user = new() { UserName = email, Email = email };
                var r = await users.CreateAsync(user, password!);
                if (!r.Succeeded) throw new InvalidOperationException(string.Join("; ", r.Errors.Select(x => x.Description)));
                await users.AddToRoleAsync(user, role);
            }
        }
        var db = services.GetRequiredService<PosDb>();
        if (await db.Menu.AnyAsync()) { await SeedVariations(db); return; }
        db.Tables.AddRange(Enumerable.Range(1, 10).Select(n => new DiningTable { Number = n }));
        var mains = new MenuCategory { Name = "Rice & noodles" }; var drinks = new MenuCategory { Name = "Drinks" };
        db.Menu.AddRange(new MenuItem { Category = mains, Name = "Nasi Lemak", Price = 8.50m }, new MenuItem { Category = mains, Name = "Char Kuey Teow", Price = 10.00m }, new MenuItem { Category = mains, Name = "Chicken Rice", Price = 9.50m }, new MenuItem { Category = drinks, Name = "Teh Tarik", Price = 3.20m }, new MenuItem { Category = drinks, Name = "Kopi O", Price = 2.80m });
        await db.SaveChangesAsync();
        await SeedVariations(db);
        var waiter = await users.FindByEmailAsync(preview ? DevelopmentPreview.Email : "waiter@demo.local");
        var s = services.GetRequiredService<OrderService>();
        await s.Create(new(OrderType.DineIn, (await db.Tables.OrderBy(x => x.Number).FirstAsync()).Id, "Demo table", [new((await db.Menu.FirstAsync()).Id, 2, "No chili")], "demo-dine-in"), waiter!.Id);
        await s.Create(new(OrderType.Takeaway, null, "Demo takeaway", [new((await db.Menu.FirstAsync()).Id, 1)], "demo-takeaway"), waiter.Id);
    }
    private static async Task SeedVariations(PosDb db)
    {
        // Extend existing development databases without replacing menu prices or historical orders.
        var demoNames = new[] { "Nasi Lemak", "Char Kuey Teow", "Chicken Rice", "Teh Tarik", "Kopi O" };
        foreach (var item in await db.Menu.Include(x => x.Variations).Where(x => demoNames.Contains(x.Name)).ToListAsync())
        {
            if (item.Variations.Count != 0) continue;
            var drink = item.Name is "Teh Tarik" or "Kopi O";
            item.Variations.Add(new() { Name = drink ? "Hot" : "Regular", AdditionalPrice = 0m, SortOrder = 0 });
            item.Variations.Add(new() { Name = drink ? "Iced" : "Large", AdditionalPrice = drink ? 0.50m : 2.00m, SortOrder = 1 });
        }
        await db.SaveChangesAsync();
    }
}
