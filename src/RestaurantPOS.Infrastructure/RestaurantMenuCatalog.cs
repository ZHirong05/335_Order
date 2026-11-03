using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RestaurantPOS.Core;

namespace RestaurantPOS.Infrastructure;

public sealed record CatalogOption(string Name, decimal AdditionalPrice, bool IsDefault = false);
public sealed record CatalogGroup(string Name, List<CatalogOption> Options);
public sealed record CatalogItem(string Name, decimal Price, List<string> SourceRows, List<CatalogGroup> Groups);
public sealed record CatalogCategory(string Name, bool IsBeverage, List<CatalogItem> Items);
public sealed record CatalogPriceCheck(string Item, string Source, decimal Price, List<string> Options);
public sealed record RestaurantCatalog(string Version, string SourceSha256, List<CatalogCategory> Categories, List<CatalogPriceCheck> PriceChecks);

public static class RestaurantMenuCatalog
{
    public static RestaurantCatalog Read()
    {
        using var stream = typeof(RestaurantMenuCatalog).Assembly.GetManifestResourceStream("RestaurantPOS.Infrastructure.Catalog.book1-menu.json")
            ?? throw new InvalidOperationException("Restaurant menu resource missing");
        var catalog = JsonSerializer.Deserialize<RestaurantCatalog>(stream) ?? throw new InvalidOperationException("Restaurant menu is empty");
        var items = catalog.Categories.SelectMany(c => c.Items).ToList();
        static bool Money(decimal amount) => amount >= 0 && amount <= 999999999m && decimal.Round(amount, 2) == amount;
        if (string.IsNullOrWhiteSpace(catalog.Version) || catalog.SourceSha256.Length != 64 || catalog.Categories.Count == 0 ||
            catalog.Categories.Select(c => c.Name).Distinct().Count() != catalog.Categories.Count ||
            items.Select(i => i.Name).Distinct().Count() != items.Count ||
            items.Any(i => string.IsNullOrWhiteSpace(i.Name) || !Money(i.Price) || i.SourceRows.Count == 0 ||
                i.Groups.Select(g => g.Name).Distinct().Count() != i.Groups.Count ||
                i.Groups.Any(g => g.Options.Count == 0 || g.Options.Count(o => o.IsDefault) > 1 || g.Options.Select(o => o.Name).Distinct().Count() != g.Options.Count || g.Options.Any(o => !Money(o.AdditionalPrice)))))
            throw new InvalidOperationException("Restaurant menu validation failed");
        foreach (var check in catalog.PriceChecks)
        {
            var item = items.Single(i => i.Name == check.Item);
            if (check.Options.Count != item.Groups.Count || item.Price + item.Groups.Select((g, index) => g.Options.Single(o => o.Name == check.Options[index]).AdditionalPrice).Sum() != check.Price)
                throw new InvalidOperationException($"Workbook price check failed: {check.Source}");
        }
        return catalog;
    }

    // Add the real menu once; never rewrite sold item snapshots or delete referenced menu IDs.
    public static async Task Apply(PosDb db)
    {
        var catalog = Read();
        await db.Database.OpenConnectionAsync();
        using var tx = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: false);
        await db.Database.UseTransactionAsync(tx);
        var previous = await db.MenuCatalogImports.FindAsync(catalog.Version);
        if (previous != null)
        {
            if (previous.SourceSha256 != catalog.SourceSha256) throw new InvalidOperationException("Menu version already used for a different workbook. Use a new catalog version.");
            return;
        }
        foreach (var existing in await db.Menu.ToListAsync()) existing.IsAvailable = false;
        foreach (var existing in await db.Categories.ToListAsync()) existing.IsActive = false;
        for (var c = 0; c < catalog.Categories.Count; c++)
        {
            var source = catalog.Categories[c];
            var category = new MenuCategory { Name = source.Name, IsBeverage = source.IsBeverage, SortOrder = c };
            db.Categories.Add(category);
            for (var index = 0; index < source.Items.Count; index++)
            {
                var entry = source.Items[index];
                var item = new MenuItem { Category = category, Name = entry.Name, Price = entry.Price, SortOrder = index };
                for (var g = 0; g < entry.Groups.Count; g++)
                {
                    var options = entry.Groups[g];
                    item.OptionGroups.Add(new MenuOptionGroup { Name = options.Name, SortOrder = g, Options = options.Options.Select((o, sort) => new MenuOption { Name = o.Name, AdditionalPrice = o.AdditionalPrice, IsDefault = o.IsDefault, SortOrder = sort }).ToList() });
                }
                db.Menu.Add(item);
            }
        }
        db.MenuCatalogImports.Add(new MenuCatalogImport { Key = catalog.Version, SourceSha256 = catalog.SourceSha256 });
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
}
