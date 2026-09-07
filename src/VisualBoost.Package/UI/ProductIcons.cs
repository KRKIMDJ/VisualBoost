using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VisualBoost.UI;

// 64×64 좌표계의 독립 벡터 원본입니다. 불변 이미지를 공유해 행 재활용 시 이미지 디코딩을 반복하지 않습니다.
public static class ProductIcons
{
    private static readonly Dictionary<string, DrawingImage> images = Create();
    public static IReadOnlyDictionary<string, DrawingImage> All => images;
    public static DrawingImage Symbol(string? kind) => images.TryGetValue((kind ?? "").ToLowerInvariant(), out var image) ? image : images["unknown"];
    public static DrawingImage File(string? path)
    {
        var key = Path.GetExtension(path ?? "").ToLowerInvariant() switch
        {
            ".h" or ".hpp" or ".hh" or ".hxx" or ".inl" => "header",
            ".cpp" or ".cc" or ".cxx" => "cpp",
            ".cs" => "csharp",
            ".md" or ".markdown" => "markdown",
            ".json" or ".xml" or ".xaml" or ".yaml" or ".yml" => "data",
            ".ini" or ".config" or ".props" or ".targets" or ".vcxproj" or ".csproj" or ".sln" or ".slnx" => "config",
            ".txt" or ".log" => "text",
            ".c" or ".py" or ".js" or ".jsx" or ".ts" or ".tsx" or ".java" or ".kt" or ".rs" or ".go" or ".fs" or ".fsx" or ".ps1" or ".psm1" or ".html" or ".htm" or ".css" => "code",
            _ => "file"
        };
        return images[key];
    }
    private static Dictionary<string, DrawingImage> Create()
    {
        var result = new Dictionary<string, DrawingImage>(StringComparer.Ordinal);
        void Add(string name, string fill, string silhouette, string detail, bool document = false)
        {
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
            {
                // 투명한 전체 경계를 포함해 PNG 출력과 작은 화면 표시가 모두 정확히 64×64 비율을 사용합니다.
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 64, 64));
                var ink = new SolidColorBrush(Color.FromRgb(30, 39, 52));
                var color = (Brush)new BrushConverter().ConvertFromString(fill)!;
                var outline = new Pen(ink, 3) { LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(color, outline, Geometry.Parse(silhouette));
                if (document) dc.DrawGeometry(null, outline, Geometry.Parse("M 39,5 L 39,20 L 54,20"));
                if (detail.Length > 0)
                    dc.DrawGeometry(null, new Pen(ink, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Geometry.Parse(detail));
            }
            drawing.Freeze(); var image = new DrawingImage(drawing); image.Freeze(); result[name] = image;
        }
        const string page = "M 12,5 L 39,5 L 54,20 L 54,58 L 12,58 Z";
        Add("file", "#C8D4E2", page, "M 23,32 L 43,32 M 23,42 L 38,42", true);
        Add("header", "#FFD078", page, "M 23,29 L 23,47 M 41,29 L 41,47 M 23,38 L 41,38", true);
        Add("cpp", "#77DCD1", page, "M 27,30 L 20,37 L 27,44 M 39,30 L 46,37 L 39,44 M 30,50 L 38,50", true);
        Add("csharp", "#C6B0FF", page, "M 28,28 L 25,48 M 39,28 L 36,48 M 22,34 L 44,34 M 21,42 L 43,42", true);
        Add("code", "#91C9FF", page, "M 27,30 L 20,38 L 27,46 M 39,30 L 46,38 L 39,46", true);
        Add("markdown", "#AFC4E7", page, "M 21,45 L 21,30 L 32,40 L 43,30 L 43,45 M 24,51 L 40,51", true);
        Add("data", "#FFA98D", page, "M 27,29 Q 22,29 24,36 L 20,38 L 24,40 Q 22,47 27,47 M 39,29 Q 44,29 42,36 L 46,38 L 42,40 Q 44,47 39,47", true);
        Add("config", "#B5CED9", page, "M 22,32 L 44,32 M 22,43 L 44,43 M 29,28 L 29,36 M 37,39 L 37,47", true);
        Add("text", "#DEE3EC", page, "M 23,29 L 43,29 M 23,38 L 43,38 M 23,47 L 37,47", true);
        Add("namespace", "#9BC8F7", "M 5,12 L 26,12 L 32,20 L 59,20 L 59,53 L 5,53 Z", "M 24,28 L 18,28 L 18,43 L 24,43 M 40,28 L 46,28 L 46,43 L 40,43");
        Add("class", "#C8A5FF", "M 32,5 L 56,19 L 56,46 L 32,59 L 8,46 L 8,19 Z", "M 9,19 L 32,33 L 55,19 M 32,33 L 32,57");
        Add("struct", "#8BC8FF", "M 8,8 L 56,8 L 56,56 L 8,56 Z", "M 8,24 L 56,24 M 8,40 L 56,40 M 25,8 L 25,56");
        Add("union", "#B8ADFA", "M 6,16 L 42,16 L 42,6 L 58,6 L 58,48 L 22,48 L 22,58 L 6,58 Z", "M 22,16 L 42,16 L 42,48 L 22,48 Z");
        Add("enum", "#E9BE7B", "M 9,7 L 55,7 L 55,57 L 9,57 Z", "M 20,19 L 22,19 M 31,19 L 45,19 M 20,32 L 22,32 M 31,32 L 45,32 M 20,45 L 22,45 M 31,45 L 45,45");
        Add("function", "#7BDDB5", "M 17,7 L 47,7 Q 57,7 57,17 L 57,47 Q 57,57 47,57 L 17,57 Q 7,57 7,47 L 7,17 Q 7,7 17,7 Z", "M 41,17 Q 28,13 28,26 L 28,42 Q 28,49 21,48 M 20,30 L 42,30");
        Add("variable", "#FFA782", "M 32,5 A 27,27 0 1 1 31.99,5 Z", "M 19,24 L 31,43 L 45,24");
        Add("macro", "#F1D77B", "M 24,4 L 44,4 L 36,26 L 55,26 L 25,60 L 30,37 L 10,37 Z", "");
        Add("type", "#C8B3EA", "M 32,5 L 59,32 L 32,59 L 5,32 Z", "M 21,24 L 43,24 M 32,24 L 32,45");
        Add("unknown", "#BDC9D8", "M 12,10 L 52,10 L 52,54 L 12,54 Z", "M 22,26 Q 22,17 33,18 Q 46,21 35,32 L 32,36 M 32,45 L 32,46");
        result["scope"] = result["namespace"]; result["constructor"] = result["function"]; result["destructor"] = result["function"];
        return result;
    }
}

public sealed class ProductIcon : Image
{
    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(nameof(FilePath), typeof(string), typeof(ProductIcon), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty SymbolKindProperty = DependencyProperty.Register(nameof(SymbolKind), typeof(string), typeof(ProductIcon), new PropertyMetadata(null, Changed));
    public string? FilePath { get => (string?)GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }
    public string? SymbolKind { get => (string?)GetValue(SymbolKindProperty); set => SetValue(SymbolKindProperty, value); }
    public ProductIcon() { Width = Height = 18; Stretch = Stretch.Uniform; IsHitTestVisible = false; Source = ProductIcons.Symbol("unknown"); }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var icon = (ProductIcon)sender;
        icon.Source = icon.FilePath is not null ? ProductIcons.File(icon.FilePath) : ProductIcons.Symbol(icon.SymbolKind);
    }
}
