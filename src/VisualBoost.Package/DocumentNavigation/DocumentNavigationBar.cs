using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;
using VisualBoost.UI;

namespace VisualBoost.DocumentNavigation;

// 팝업 검색란은 이 줄 위에 같은 위치로 열립니다. 대기 상태에서는 캐럿 정보만 표시합니다.
internal sealed class DocumentNavigationBar : Border
{
    private readonly TextBlock caption = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProductIcon icon = new() { Margin = new Thickness(6, 0, 6, 0) };
    public event Action? OpenRequested;
    public DocumentNavigationBar()
    {
        Height = 27;
        BorderThickness = new Thickness(0, 0, 0, 1);
        SetResourceReference(BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
        SetResourceReference(BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
        var content = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left); content.Children.Add(icon);
        var hint = new TextBlock { Text = "Alt+M  ▾", Margin = new Thickness(12, 0, 9, 0), Opacity = .65, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(hint, Dock.Right); content.Children.Add(hint); content.Children.Add(caption);
        var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0), BorderThickness = new Thickness(0),
            ToolTip = "현재 문서의 함수 검색 (Alt+M)", Focusable = false };
        button.SetResourceReference(Control.StyleProperty, VsResourceKeys.ButtonStyleKey);
        System.Windows.Automation.AutomationProperties.SetName(button, "현재 문서 함수 탐색");
        button.Click += (_, _) => OpenRequested?.Invoke();
        Child = button;
    }
    public void SetCurrent(string name, string? filePath, string? functionKind)
    {
        caption.Text = name;
        icon.FilePath = functionKind is null ? filePath : null;
        icon.SymbolKind = functionKind;
    }
}
