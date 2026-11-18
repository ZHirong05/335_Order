using Microsoft.EntityFrameworkCore;
using RestaurantPOS.Core;
using RestaurantPOS.Infrastructure;

namespace RestaurantPOS.Web;

public static class RestaurantTables
{
    public static async Task Apply(PosDb db)
    {
        int[] numbers = [1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 16, 17, 18, 19];
        var existing = await db.Tables.ToListAsync();
        foreach (var table in existing)
        {
            var active = numbers.Contains(table.Number);
            if (table.IsActive == active) continue;
            table.IsActive = active;
            table.Version++;
        }
        foreach (var number in numbers.Where(n => existing.All(t => t.Number != n)))
            db.Tables.Add(new DiningTable { Number = number });
        await db.SaveChangesAsync();
    }
}
