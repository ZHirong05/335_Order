using RestaurantPOS.Core;
namespace RestaurantPOS.Tests;
public sealed class ItemDisplayTests
{
    [Theory]
    [InlineData("咖喱面（面类：生面，配料：叉烧）", "咖喱面", "生面|+ 叉烧")]
    [InlineData("咖喱面（面类：生面，+：叉烧）", "咖喱面", "生面|+ 叉烧")]
    [InlineData("虾水饺（4粒）（做法：汤）", "虾水饺（4粒）", "汤")]
    [InlineData("冬菇凤爪面（3份）（面类：面，做法：干）", "冬菇凤爪面（3份）", "面|干")]
    [InlineData("虾汤面（面类：米粉，+：水饺（2粒））", "虾汤面", "米粉|+ 水饺（2粒）")]
    [InlineData("Nasi Lemak (Large)", "Nasi Lemak", "Large")]
    [InlineData("Kopi（Serving：Cold）", "Kopi", "Cold")]
    [InlineData("Kopi (Serving：Cold)", "Kopi", "Cold")]
    [InlineData("招牌豆腐（8粒）", "招牌豆腐（8粒）", "")]
    public void SelectionSuffixDisplaysSeparatelyWithoutChangingTheSnapshot(string snapshot, string name, string options)
    {
        var display = ItemDisplay.Parse(snapshot);
        Assert.Equal(name, display.Name); Assert.Equal(options, string.Join("|", display.Options));
    }
}
