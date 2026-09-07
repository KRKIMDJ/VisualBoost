using System;
namespace VisualBoost.DocumentNavigation;

internal static class DocumentNavigationSettings
{
    public static bool NameOrder { get; private set; }
    public static bool ShowBar { get; private set; } = true;
    public static event EventHandler? Changed;
    public static void Publish(bool nameOrder, bool showBar = true)
    { NameOrder = nameOrder; ShowBar = showBar; Changed?.Invoke(null, EventArgs.Empty); }
}
