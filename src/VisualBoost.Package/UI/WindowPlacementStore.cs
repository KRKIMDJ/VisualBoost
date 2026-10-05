using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;

namespace VisualBoost.UI;

/// <summary>
/// 옵션 페이지가 없는 창(심볼 탐색 등)의 크기·위치를 VS 사용자 설정 저장소에 둡니다.
/// 옵션 화면에 보일 필요가 없는 화면 상태라서 옵션 페이지 숨김 속성 대신 이 저장소를 씁니다.
/// 창 위치는 부가 상태이므로 저장소를 쓸 수 없으면 기본 크기·위치로 열고 탐색 자체는 막지 않습니다.
/// </summary>
internal static class WindowPlacementStore
{
    private const string Collection = @"VisualBoost\WindowPlacement";

    /// <summary>저장한 경계입니다. 없거나 형식이 잘못되었으면 null을 돌려줍니다.</summary>
    public static Rect? Load(IServiceProvider provider, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string text;
        try
        {
            var store = Open(provider);
            if (!store.PropertyExists(Collection, name)) return null;
            text = store.GetString(Collection, name, string.Empty);
        }
        catch (Exception exception) when (exception is ArgumentException or COMException)
        {
            ActivityLog.LogWarning("VisualBoost/WindowPlacement", exception.Message);
            return null;
        }

        var parts = text.Split(',');
        if (parts.Length != 4) return null;
        var values = new double[4];
        for (var index = 0; index < 4; index++)
        {
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out values[index])) return null;
        }

        // 손상된 음수 크기는 Rect가 받지 않으므로 저장값이 없는 것으로 봅니다.
        return values[2] >= 0 && values[3] >= 0 ? new Rect(values[0], values[1], values[2], values[3]) : null;
    }

    public static void Save(IServiceProvider provider, string name, Rect? bounds)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (bounds is not { } value) return;
        var text = string.Join(",",
            new[] { value.Left, value.Top, value.Width, value.Height }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        try
        {
            var store = Open(provider);
            if (!store.CollectionExists(Collection)) store.CreateCollection(Collection);
            store.SetString(Collection, name, text);
        }
        catch (Exception exception) when (exception is ArgumentException or COMException)
        {
            ActivityLog.LogWarning("VisualBoost/WindowPlacement", exception.Message);
        }
    }

    private static WritableSettingsStore Open(IServiceProvider provider) =>
        new ShellSettingsManager(provider).GetWritableSettingsStore(SettingsScope.UserSettings);
}
