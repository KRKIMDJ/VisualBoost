using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Windows.Forms;
using VisualBoost.Core.Coloring;

namespace VisualBoost.Options;

[AttributeUsage(AttributeTargets.Property)]
public sealed class PaletteDefaultAttribute : Attribute
{
    public PaletteDefaultAttribute(string color) => Color = color;
    public string Color { get; }
}

// 저장 형식은 유지하고 편집 UI만 교체하여 기존 사용자 설정과 기본색 자동 선택을 보존합니다.
public class PaletteColorEditor : UITypeEditor
{
    public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext? context) => UITypeEditorEditStyle.Modal;
    public override bool GetPaintValueSupported(ITypeDescriptorContext? context) => true;

    public override object? EditValue(ITypeDescriptorContext? context, IServiceProvider? provider, object? value)
    {
        var selected = PickColor(ResolveColor(context, value), provider);
        return selected.HasValue ? $"#{selected.Value.R:X2}{selected.Value.G:X2}{selected.Value.B:X2}" : value;
    }

    protected virtual Color? PickColor(Color initial, IServiceProvider? provider)
    {
        using var dialog = new ColorDialog
        {
            Color = initial,
            FullOpen = true,
            AnyColor = true,
            AllowFullOpen = true,
        };
        var owner = provider?.GetService(typeof(IWin32Window)) as IWin32Window;
        var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return result == DialogResult.OK ? dialog.Color : (Color?)null;
    }

    public override void PaintValue(PaintValueEventArgs e)
    {
        using var brush = new SolidBrush(ResolveColor(e.Context, e.Value));
        e.Graphics.FillRectangle(brush, e.Bounds);
    }

    private static Color ResolveColor(ITypeDescriptorContext? context, object? value)
    {
        var fallback = context?.PropertyDescriptor?.Attributes[typeof(PaletteDefaultAttribute)] as PaletteDefaultAttribute;
        if (!SemanticColorPalette.TryParse(value as string, out var rgb))
            SemanticColorPalette.TryParse(fallback?.Color ?? "#808080", out rgb);
        return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
    }
}
