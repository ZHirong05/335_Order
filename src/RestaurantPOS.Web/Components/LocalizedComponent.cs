using Microsoft.AspNetCore.Components;

namespace RestaurantPOS.Web.Components;

public abstract class LocalizedComponent : ComponentBase
{
    [CascadingParameter(Name = "UiLanguage")] public bool UiLanguage { get; set; }
}
