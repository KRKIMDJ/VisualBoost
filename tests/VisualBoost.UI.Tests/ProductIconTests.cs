using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VisualBoost.UI;

internal static class ProductIconTests
{
    public static void Run(string root)
    {
        Check(!ReferenceEquals(ProductIcons.File("Foo.h"), ProductIcons.File("Foo.cpp")), "헤더와 CPP 구분");
        Check(!ReferenceEquals(ProductIcons.Symbol("class"), ProductIcons.Symbol("variable")), "클래스와 변수 구분");
        Check(ReferenceEquals(ProductIcons.Symbol("constructor"), ProductIcons.Symbol("function")), "생성자 함수 계열 공유");
        Check(ReferenceEquals(ProductIcons.File("Foo.HPP"), ProductIcons.File("Bar.h")), "확장자 대소문자 및 이미지 캐시");
        var icon = new ProductIcon { FilePath = "file.cpp" };
        Check(ReferenceEquals(icon.Source, ProductIcons.File("file.cpp")), "파일 탐색 매핑");
        icon.FilePath = null; icon.SymbolKind = "enum";
        Check(ReferenceEquals(icon.Source, ProductIcons.Symbol("enum")), "재활용된 행 아이콘 갱신");
        var output = Path.Combine(root, "artifacts", "icons-64"); Directory.CreateDirectory(output);
        var list = ProductIcons.All.ToArray();
        var sheet = new DrawingVisual();
        using (var dc = sheet.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 36, 44)), null, new Rect(0, 0, 720, 80 + 110 * ((list.Length + 5) / 6)));
            dc.DrawText(Text("VisualBoost · 64px / 18px", 22), new Point(20, 20));
            for (var i = 0; i < list.Length; i++)
            {
                var image = list[i].Value;
                Check(image.IsFrozen && image.Width == 64 && image.Height == 64, "64×64 불변 벡터 원본: " + list[i].Key);
                var single = new DrawingVisual(); using (var item = single.RenderOpen()) item.DrawImage(image, new Rect(0, 0, 64, 64));
                Save(single, 64, 64, Path.Combine(output, list[i].Key + ".png"));
                var x = 20 + i % 6 * 116; var y = 70 + i / 6 * 110;
                dc.DrawImage(image, new Rect(x, y, 64, 64));
                dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(x + 69, y + 5, 24, 24));
                dc.DrawImage(image, new Rect(x + 72, y + 8, 18, 18));
                dc.DrawImage(image, new Rect(x + 72, y + 38, 18, 18));
                dc.DrawText(Text(list[i].Key, 12), new Point(x, y + 70));
            }
        }
        Save(sheet, 720, 80 + 110 * ((list.Length + 5) / 6), Path.Combine(root, "artifacts", "ui-validation", "ProductIcons.png"));
        foreach (var name in new[] { "FileSearchDialog", "SymbolSearchDialog" })
        {
            var xaml = File.ReadAllText(Path.Combine(root, "src", "VisualBoost.Package", "UI", name + ".xaml"));
            Check(xaml.Contains("ui:ProductIcon") && !xaml.Contains("CrispImage") && !xaml.Contains("IconMoniker"), "탐색 UI 자체 아이콘 적용: " + name);
        }
        Console.WriteLine("PASS: 자체 아이콘 64×64 원본·PNG 출력·매핑·재활용·공유 및 밝은/어두운 18px 견본");
    }
    private static FormattedText Text(string text, int size) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.WhiteSmoke, 1);
    private static void Save(Visual visual, int width, int height, string path)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
