using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Microsoft.VisualStudio.Shell;
using VisualBoost.DocumentNavigation;

internal static class HostControlStyleTests
{
    public static void Run(string root)
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var files = new[] { "UI/FileSearchDialog.xaml", "UI/SymbolSearchDialog.xaml", "UI/SymbolUsagesControl.xaml",
            "DocumentNavigation/DocumentNavigationControl.xaml" };
        foreach (var file in files)
        {
            var xml = XDocument.Load(Path.Combine(root, "src/VisualBoost.Package", file));
            foreach (var button in xml.Descendants(wpf + "Button"))
            {
                Check(((string?)button.Attribute("Style"))?.Contains("VsResourceKeys.ButtonStyleKey") == true, file + " VS 버튼 스타일 누락");
                Check(!button.Elements(wpf + "Button.Template").Any(), "호스트 버튼 템플릿 덮어쓰기 금지");
            }
            foreach (var combo in xml.Descendants(wpf + "ComboBox"))
            {
                Check(((string?)combo.Attribute("Style"))?.Contains("VsResourceKeys.ComboBoxStyleKey") == true, "VS 드롭다운 스타일");
                Check(((string?)combo.Attribute("ItemContainerStyle"))?.Contains("VsResourceKeys.ComboBoxItemStyleKey") == true, "VS 드롭다운 항목 스타일");
            }
        }
        var bar = new DocumentNavigationBar();
        var target = (Button)bar.Child;
        // 실제 VS 테마 대신 리소스를 교체해 상단 버튼이 동적 스타일을 계속 참조하는지 검증합니다.
        foreach (var theme in new[] { "Dark", "Light", "HighContrast" })
        {
            var style = new Style(typeof(Button)); style.Setters.Add(new Setter(FrameworkElement.TagProperty, theme));
            bar.Resources[VsResourceKeys.ButtonStyleKey] = style;
            Check(ReferenceEquals(target.Style, style) && Equals(target.Tag, theme), "테마 리소스 교체 즉시 반영");
        }
        Check(target.ReadLocalValue(Control.TemplateProperty) == DependencyProperty.UnsetValue, "상단 버튼 호스트 템플릿 유지");
        Console.WriteLine("PASS: 버튼·드롭다운·항목의 VS 스타일 등록 및 상단 버튼 동적 테마 교체 (호스트 리소스 대역)");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
