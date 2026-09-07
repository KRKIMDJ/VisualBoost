using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Windows;
using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.TextManager.Interop;
using VisualBoost.Commands;
using VisualBoost.Core.CodeGeneration;
using VisualBoost.Services;

namespace VisualBoost.CodeGeneration;

internal static class QuickIncludeTool
{
    public static async Task<bool> TryRunAsync(VisualBoostPackage package, SolutionFileIndexService index)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await package.GetServiceAsync(typeof(SDTE)) as DTE2;
        var components = await package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
        var manager = await package.GetServiceAsync(typeof(SVsTextManager)) as IVsTextManager;
        Assumes.Present(dte); Assumes.Present(components); Assumes.Present(manager);
        var view = GenerateFunctionCommand.ActiveView(manager, components.GetService<IVsEditorAdaptersFactoryService>());
        var snapshot = view.TextSnapshot;
        var point = view.Caret.Position.BufferPosition;
        if (point.Snapshot != snapshot) return false;
        var line = point.GetContainingLine(); var text = line.GetText(); var pos = point.Position - line.Start.Position;
        bool Part(int p) => p >= 0 && p < text.Length && (char.IsLetterOrDigit(text[p]) || text[p] == '_');
        if (!Part(pos)) pos--;
        if (!Part(pos)) return false;
        var start = pos; var end = pos + 1;
        while (Part(start - 1)) start--; while (Part(end)) end++;
        if (!char.IsLetter(text[start]) && text[start] != '_') return false;
        var symbol = text.Substring(start, end - start);
        var prefix = text.Substring(0, start).TrimEnd();
        if (prefix.EndsWith(".", StringComparison.Ordinal) || prefix.EndsWith("->", StringComparison.Ordinal)) return false;
        var explicitOwner = Regex.Match(prefix, @"(?:\b[A-Za-z_]\w*::)+$").Value.TrimEnd(':');
        var span = new SnapshotSpan(snapshot, line.Start.Position + start, end - start);
        if (!QuickIncludeDiagnostics.Get(view, components.GetService<IViewTagAggregatorFactoryService>()).HasError(span)) return false;
        var classifier = components.GetService<IClassifierAggregatorService>().GetClassifier(view.TextBuffer);
        try
        {
            if (classifier.GetClassificationSpans(span).Any(c => c.ClassificationType.IsOfType("comment") || c.ClassificationType.IsOfType("string"))) return false;
        }
        finally { (classifier as IDisposable)?.Dispose(); }
        if (!package.IsQuickIncludeEnabled()) throw new GenerationNotSupportedException("옵션의 빠른 인클루드 기능이 꺼져 있습니다.");
        var sourcePath = GenerationModelReader.Normalize(dte.ActiveDocument?.FullName ?? "");
        var solutionPath = dte.Solution.FullName;
        var root = Path.GetDirectoryName(GenerationModelReader.Normalize(solutionPath))!;
        var projectRoot = FileSearchContext.GetActiveProjectDirectory(dte);
        var sourceText = snapshot.GetText();
        using var lifetime = new CancellationTokenSource();
        EventHandler closed = (_, _) => lifetime.Cancel();
        EventHandler<TextContentChangedEventArgs> changed = (_, _) => lifetime.Cancel();
        view.Closed += closed; view.TextBuffer.Changed += changed;
        try
        {
            var candidates = await Task.Run(() =>
            {
                GenerateFunctionCommand.ValidateFile(root, sourcePath);
                var paths = QuickIncludeHeaderLookup.Find(sourcePath, projectRoot, symbol, explicitOwner,
                    index.FindSymbol(symbol), index.FindByStem(symbol), ReadHeader, lifetime.Token);
                foreach (var path in paths)
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    // 1순위가 이미 포함되었거나 안전하지 않으면 다른 동명 심볼의 헤더를 대신 넣지 않습니다.
                    if (File.Exists(path)) return new[] { (path, plan: QuickInclude.Create(sourceText, sourcePath, path, lifetime.Token, IsUnrealPublicHeader(path))) };
                }
                return Array.Empty<(string path, GenerationPlan plan)>();
            }, lifetime.Token);
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(lifetime.Token);
            if (candidates.Length == 0)
            {
                var state = index.GetSnapshot();
                throw new GenerationNotSupportedException(state.IsAnalyzing ? "선언 헤더를 아직 찾지 못했습니다. 소스 분석 완료 후 다시 실행하세요." :
                    "심볼 인덱스와 동명 헤더에서 선언을 찾지 못했습니다. 헤더 저장 여부와 소스 분석 설정을 확인하세요.");
            }
            var actions = new[] { QuickIncludeCodeActions.Create(symbol, candidates[0].path, candidates[0].plan.Signature) };
            if (GenerationModelReader.Normalize(dte.ActiveDocument?.FullName ?? "") != sourcePath || dte.Solution.FullName != solutionPath) return true;
            var selected = await new DocumentCodeActionMenu().ShowAsync(view.VisualElement,
                new Point(view.Caret.Left - view.ViewportLeft, view.Caret.Bottom - view.ViewportTop), actions, lifetime.Token);
            if (selected is null) return true;
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(lifetime.Token);
            var candidate = candidates[0];
            await Task.Run(() =>
            {
                GenerateFunctionCommand.ValidateFile(root, sourcePath);
                if (!File.Exists(candidate.path)) throw new GenerationNotSupportedException("선택한 헤더가 사라졌습니다.");
            }, lifetime.Token);
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(lifetime.Token);
            if (!package.IsQuickIncludeEnabled() || dte.Solution.FullName != solutionPath || GenerationModelReader.Normalize(dte.ActiveDocument?.FullName ?? "") != sourcePath)
                throw new GenerationNotSupportedException("문서·솔루션·옵션이 변경되었습니다. 다시 실행하세요.");
            GenerationEditApplier.Apply(view, snapshot, view, snapshot, candidate.plan!, components.GetService<ITextUndoHistoryRegistry>(),
                components.GetService<IEditorOperationsFactoryService>().GetEditorOperations(view), "VisualBoost 빠른 인클루드", preserveCaret: true);
            view.Caret.EnsureVisible(); view.VisualElement.Focus();
            var status = await package.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
            status?.SetText("VisualBoost: #include \"" + candidate.plan!.Signature + "\" 추가 · Ctrl+Z로 취소 · 모듈/빌드 의존성은 변경하지 않음");
            return true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return true; }
        finally { view.Closed -= closed; view.TextBuffer.Changed -= changed; }
    }
    private static bool IsUnrealPublicHeader(string path)
    {
        // 폴더 이름만으로 다른 C++ 프로젝트를 Unreal 모듈로 오인하지 않습니다.
        for (var folder = Path.GetDirectoryName(path); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            var name = Path.GetFileName(folder);
            if (!string.Equals(name, "Public", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "Classes", StringComparison.OrdinalIgnoreCase)) continue;
            var module = Path.GetDirectoryName(folder);
            return module is not null && File.Exists(Path.Combine(module, Path.GetFileName(module) + ".Build.cs"));
        }
        return false;
    }
    private static string? ReadHeader(string path)
    {
        try
        {
            // 성장 중인 파일도 실제로 읽는 문자 수를 제한합니다. 원본 파일에는 쓰지 않습니다.
            if (!File.Exists(path)) return null;
            using var reader = new StreamReader(path, System.Text.Encoding.UTF8, true);
            var buffer = new char[1024 * 1024 + 1]; var count = 0;
            while (count < buffer.Length)
            { var read = reader.Read(buffer, count, buffer.Length - count); if (read == 0) break; count += read; }
            return count <= 1024 * 1024 ? new string(buffer, 0, count) : null;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            ActivityLog.LogWarning("VisualBoost/QuickInclude", path + ": " + exception.Message);
            return null;
        }
    }
}
