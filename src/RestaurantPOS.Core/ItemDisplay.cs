using System.Text.RegularExpressions;

namespace RestaurantPOS.Core;

public sealed record ItemDisplay(string Name, IReadOnlyList<string> Options)
{
    public static ItemDisplay Parse(string snapshot)
    {
        // Only remove the option suffix, retaining names such as 水饺（4粒）.
        var match = Regex.Match(snapshot, @"^(?<name>.+?)[（(](?<options>[^：:()（）]*[：:].*)[）)]$");
        if (!match.Success) match = Regex.Match(snapshot, @"^(?<name>.+) \((?<options>[^()]*)\)$");
        if (!match.Success) return new(snapshot, []);
        var options = match.Groups["options"].Value.Split('，', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Option).ToList();
        return new(match.Groups["name"].Value.TrimEnd(), options);
    }
    private static string Option(string text)
    {
        var colon = text.IndexOfAny(['：', ':']);
        if (colon < 0) return text;
        return text[(colon + 1)..].Trim();
    }
}
