using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.VisualStudio.Shell;
using VisualBoost.Core.CodeGeneration;

namespace VisualBoost.CodeGeneration;

// 후속 도구도 표시와 선택 수명을 공유하되 실행 로직은 각 명령이 소유합니다.
internal sealed class DocumentCodeAction
{
    public DocumentCodeAction(string id, string title, string description, IReadOnlyList<DocumentCodeAction>? children = null)
    { Id = id; Title = title; Description = description; Children = children ?? Array.Empty<DocumentCodeAction>(); }
    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public IReadOnlyList<DocumentCodeAction> Children { get; }
}

internal static class GenerationCodeActions
{
    public static IReadOnlyList<DocumentCodeAction> Create(GenerationModelContext context, string targetName)
    {
        if (context.AvailableDirection is null) return Array.Empty<DocumentCodeAction>();
        var description = context.Function.Owner + "::" + context.Function.Name + " · " + targetName;
        if (context.AvailableDirection == GenerationDirection.Definition)
            return new[] { new DocumentCodeAction("definition", "정의 생성", "현재 함수 선언 → " + description) };
        return new[] { new DocumentCodeAction("declaration", "선언 생성", "현재 함수 정의 → " + description,
            new[] { new DocumentCodeAction("public", "public 선언 생성", description),
                new DocumentCodeAction("protected", "protected 선언 생성", description),
                new DocumentCodeAction("private", "private 선언 생성", description) }) };
    }
}

internal sealed class DocumentCodeActionMenu
{
    internal ContextMenu Menu { get; } = new() { StaysOpen = false, MinWidth = 210 };
    private readonly ResourceDictionary styles = new()
    {
        Source = new Uri("/" + typeof(DocumentCodeActionMenu).Assembly.GetName().Name + ";component/CodeGeneration/DocumentCodeActionMenuStyles.xaml", UriKind.Relative)
    };

    [SuppressMessage("Usage", "VSTHRD001", Justification = "취소 콜백은 WPF 메뉴의 Dispatcher에 닫기만 비동기로 게시하고 동기 대기를 하지 않습니다.")]
    public Task<DocumentCodeAction?> ShowAsync(FrameworkElement anchor, Point caret, IReadOnlyList<DocumentCodeAction> actions, CancellationToken token)
    {
        if (actions.Count == 0 || token.IsCancellationRequested) return Task.FromResult<DocumentCodeAction?>(null);
        var completion = new TaskCompletionSource<DocumentCodeAction?>(TaskCreationOptions.RunContinuationsAsynchronously);
        DocumentCodeAction? selected = null;
        Menu.Style = (Style)styles["TextOnlyContextMenu"];
        Menu.PlacementTarget = anchor;
        Menu.Placement = PlacementMode.RelativePoint;
        Menu.HorizontalOffset = Math.Max(0, Math.Min(caret.X, anchor.ActualWidth));
        Menu.VerticalOffset = Math.Max(0, Math.Min(caret.Y, anchor.ActualHeight));
        Menu.SetResourceReference(Control.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
        Menu.SetResourceReference(Control.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        foreach (var action in actions)
            Menu.Items.Add(CreateItem(action));
        MenuItem CreateItem(DocumentCodeAction action)
        {
            var item = new MenuItem { Header = action.Title, ToolTip = action.Description, Padding = new Thickness(8, 5, 12, 5) };
            item.Style = (Style)styles["TextOnlyMenuItem"];
            item.SetResourceReference(Control.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            item.SetResourceReference(Control.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            if (action.Children.Count > 0)
                foreach (var child in action.Children) item.Items.Add(CreateItem(child));
            else item.Click += (_, e) => { e.Handled = true; selected = action; Menu.IsOpen = false; };
            return item;
        }
        CancellationTokenRegistration registration = default;
        RoutedEventHandler unloaded = (_, _) => Menu.IsOpen = false;
        anchor.Unloaded += unloaded;
        Menu.Closed += (_, _) =>
        {
            anchor.Unloaded -= unloaded;
            registration.Dispose();
            Menu.Items.Clear();
            Menu.PlacementTarget = null;
            // 메뉴의 입력 표면이 닫힌 후에만 후속 편집을 실행해 포커스·명령 수명이 겹치지 않게 합니다.
            completion.TrySetResult(selected);
        };
        registration = token.Register(() =>
        {
            // 취소는 어느 스레드에서 오더라도 UI를 기다리지 않아 교착을 피합니다.
            if (!Menu.Dispatcher.HasShutdownStarted)
                _ = Menu.Dispatcher.BeginInvoke(new Action(() => Menu.IsOpen = false));
        });
        try { Menu.IsOpen = true; }
        catch
        {
            anchor.Unloaded -= unloaded; registration.Dispose(); Menu.Items.Clear(); Menu.PlacementTarget = null;
            throw;
        }
        return completion.Task;
    }
}
