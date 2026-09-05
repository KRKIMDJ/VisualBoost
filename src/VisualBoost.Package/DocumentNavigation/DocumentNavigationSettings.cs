using System;
namespace VisualBoost.DocumentNavigation;

internal static class DocumentNavigationSettings
{
    public static bool NameOrder { get; private set; }
    public static event EventHandler? Changed;
    public static void Publish(bool nameOrder)
    { NameOrder = nameOrder; Changed?.Invoke(null, EventArgs.Empty); }
}
