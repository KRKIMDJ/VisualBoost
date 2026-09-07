using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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

internal static class QuickIncludeCodeActions
{
    public static DocumentCodeAction Create(string symbol, string headerPath, string includePath) =>
        new("include", "빠른 인클루드 · " + includePath, symbol + " · " + headerPath);
}

internal interface INativeCodeMenuHost
{
    Task ShowAsync(Point screen, NativeCodeActionMenuTarget target, CancellationToken token);
}

internal sealed class DocumentCodeActionMenu
{
    private readonly INativeCodeMenuHost host;
    public DocumentCodeActionMenu() : this(new VisualStudioCodeMenuHost()) { }
    internal DocumentCodeActionMenu(INativeCodeMenuHost host) { this.host = host; }

    public async Task<DocumentCodeAction?> ShowAsync(FrameworkElement anchor, Point caret, IReadOnlyList<DocumentCodeAction> actions, CancellationToken token)
    {
        if (actions.Count == 0 || token.IsCancellationRequested || !anchor.IsLoaded) return null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var target = new NativeCodeActionMenuTarget(actions, lifetime.Token);
        // WPF DIP를 화면 좌표로 변환하며 음수 좌표의 모니터도 유지합니다.
        var screen = anchor.PointToScreen(new Point(Math.Max(0, Math.Min(caret.X, anchor.ActualWidth)),
            Math.Max(0, Math.Min(caret.Y, anchor.ActualHeight))));
        RoutedEventHandler unloaded = (_, _) => lifetime.Cancel();
        anchor.Unloaded += unloaded;
        try
        {
            await host.ShowAsync(screen, target, lifetime.Token);
            // Shell 메뉴가 닫힌 뒤에만 편집을 재개하며, 선택 직후의 취소도 우선합니다.
            return lifetime.IsCancellationRequested ? null : target.Selected;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return null; }
        finally { anchor.Unloaded -= unloaded; }
    }
}
