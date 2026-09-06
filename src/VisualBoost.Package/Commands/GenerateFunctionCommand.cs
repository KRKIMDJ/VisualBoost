using System;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EnvDTE;
using EnvDTE80;
using Microsoft;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using VisualBoost.CodeGeneration;
using VisualBoost.Core.CodeGeneration;
using VisualBoost.Core.FilePairing;
using VisualBoost.Services;

namespace VisualBoost.Commands;

internal sealed class GenerateFunctionCommand
{
    private readonly VisualBoostPackage package;
    private readonly SolutionFileIndexService index;
    private GenerateFunctionCommand(VisualBoostPackage package, SolutionFileIndexService index) { this.package = package; this.index = index; }
    public static async Task InitializeAsync(VisualBoostPackage package, SolutionFileIndexService index, CancellationToken token)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(token);
        var service = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        Assumes.Present(service);
        var handler = new GenerateFunctionCommand(package, index);
        service.AddCommand(new OleMenuCommand((_, _) => SearchCommandRunner.Run(package, "GenerateFunction", handler.RunAsync),
            new CommandID(new Guid("4cce3464-a08f-4e0d-a5fd-a297c7fcd41e"), CommandIds.GenerateFunction)));
    }
    private async Task RunAsync()
    {
        try { await GenerateAsync(); }
        catch (GenerationNotSupportedException exception)
        {
            await NotifyUnavailableAsync(exception.Message);
        }
    }
    private async Task GenerateAsync()
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (!package.IsCodeGenerationEnabled()) throw new GenerationNotSupportedException("옵션의 코드 생성 기능이 꺼져 있습니다.");
        var dte = await package.GetServiceAsync(typeof(SDTE)) as DTE2;
        var components = await package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
        var manager = await package.GetServiceAsync(typeof(SVsTextManager)) as IVsTextManager;
        Assumes.Present(dte); Assumes.Present(components); Assumes.Present(manager);
        var adapters = components.GetService<IVsEditorAdaptersFactoryService>();
        var sourceView = ActiveView(manager, adapters);
        var sourceSnapshot = sourceView.TextSnapshot;
        var sourcePath = GenerationModelReader.Normalize(dte.ActiveDocument?.FullName ?? "");
        var solutionRoot = Path.GetDirectoryName(GenerationModelReader.Normalize(dte.Solution.FullName))!;
        await Task.Run(() => ValidateFile(solutionRoot, sourcePath));
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (sourceView.IsClosed || sourceView.TextSnapshot != sourceSnapshot || GenerationModelReader.Normalize(dte.ActiveDocument?.FullName ?? "") != sourcePath)
            throw new GenerationNotSupportedException("실행 도중 현재 문서가 바뀌었습니다. 다시 실행하세요.");
        var context = GenerationModelReader.Read(dte, sourceSnapshot);
        if (context.AvailableDirection is null)
        {
            await NotifyUnavailableAsync("대응 선언/정의가 이미 있어 현재 사용 가능한 도구가 없습니다.");
            return;
        }
        var declaration = context.Function.IsDefinition;
        await Task.Run(() => new CppGenerationProvider().ValidateSource(sourceSnapshot.GetText(), context.Function));
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var options = package.GetGeneralOptions().CreateFilePairingOptions();
        var candidates = index.FindByStem(Path.GetFileNameWithoutExtension(sourcePath));
        var paths = await Task.Run(() => new FilePairResolver(options).FindMatches(sourcePath, candidates)
            .Select(m => m.Path).Where(p => IsAllowedFile(solutionRoot, p)).Take(32).ToArray());
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (declaration)
        {
            // 선언 대상은 파일명 유사도보다 언어 모델이 확인한 소속 클래스의 헤더를 우선합니다.
            var headerPath = context.HeaderPath;
            paths = await Task.Run(() => IsAllowedFile(solutionRoot, headerPath) ? new[] { headerPath } : Array.Empty<string>());
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        }
        paths = paths.Where(path => BelongsToProject(dte, path, context.Project)).ToArray();
        if (paths.Length == 0) throw new GenerationNotSupportedException("같은 프로젝트의 안전한 대응 파일을 찾지 못했습니다. 파일을 새로 만들거나 프로젝트를 변경하지 않습니다.");
        var targetPath = GenerationModelReader.Normalize(paths[0]);
        if (sourceView.IsClosed || sourceView.TextSnapshot != sourceSnapshot || GenerationModelReader.Normalize(dte.ActiveDocument?.FullName ?? "") != sourcePath)
            throw new GenerationNotSupportedException("도구 준비 중 현재 문서가 바뀌었습니다. 다시 실행하세요.");
        context.ValidateBeforeApply();
        var actions = GenerationCodeActions.Create(context, Path.GetFileName(targetPath));
        using var menuLifetime = new CancellationTokenSource();
        EventHandler closed = (_, _) => menuLifetime.Cancel();
        EventHandler<Microsoft.VisualStudio.Text.TextContentChangedEventArgs> changed = (_, _) => menuLifetime.Cancel();
        sourceView.Closed += closed; sourceView.TextBuffer.Changed += changed;
        DocumentCodeAction? selected;
        try
        {
            selected = await new DocumentCodeActionMenu().ShowAsync(sourceView.VisualElement,
                new Point(sourceView.Caret.Left - sourceView.ViewportLeft, sourceView.Caret.Bottom - sourceView.ViewportTop), actions, menuLifetime.Token);
        }
        finally { sourceView.Closed -= closed; sourceView.TextBuffer.Changed -= changed; }
        if (selected is null) return;
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (sourceView.IsClosed || sourceView.TextSnapshot != sourceSnapshot || GenerationModelReader.Normalize(dte.ActiveDocument?.FullName ?? "") != sourcePath)
            throw new GenerationNotSupportedException("도구 선택 중 현재 문서가 바뀌었습니다. 다시 실행하세요.");
        await Task.Run(() => ValidateFile(solutionRoot, targetPath));
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        dte.ItemOperations.OpenFile(targetPath);
        var targetView = ActiveView(manager, adapters);
        if (GenerationModelReader.Normalize(dte.ActiveDocument?.FullName ?? "") != targetPath)
            throw new GenerationNotSupportedException("대상 코드 편집기를 열지 못했습니다.");
        var targetSnapshot = targetView.TextSnapshot;
        var targetClass = declaration ? GenerationModelReader.ReadTargetClass(context, targetPath, targetSnapshot) : null;
        var access = declaration ? selected.Id : "private";
        var plan = await Task.Run(() => new CppGenerationProvider().Create(sourceSnapshot.GetText(), context.Function, targetSnapshot.GetText(),
            declaration ? GenerationDirection.Declaration : GenerationDirection.Definition, Path.GetFileName(context.HeaderPath), targetClass, access));
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (sourceView.TextSnapshot != sourceSnapshot || targetView.TextSnapshot != targetSnapshot)
            throw new GenerationNotSupportedException("분석 도중 편집 내용이 바뀌었습니다. 다시 실행하세요.");
        await Task.Run(() => { ValidateFile(solutionRoot, sourcePath); ValidateFile(solutionRoot, targetPath); });
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (!package.IsCodeGenerationEnabled() || !BelongsToProject(dte, sourcePath, context.Project) || !BelongsToProject(dte, targetPath, context.Project) ||
            !string.Equals(Path.GetDirectoryName(GenerationModelReader.Normalize(dte.Solution.FullName)), solutionRoot, StringComparison.OrdinalIgnoreCase))
            throw new GenerationNotSupportedException("솔루션·프로젝트 또는 코드 생성 옵션이 변경되었습니다.");
        context.ValidateBeforeApply();
        GenerationEditApplier.Apply(sourceView, sourceSnapshot, targetView, targetSnapshot, plan,
            components.GetService<ITextUndoHistoryRegistry>(), components.GetService<IEditorOperationsFactoryService>().GetEditorOperations(targetView));
        targetView.Caret.EnsureVisible(); targetView.VisualElement.Focus();
        var status = await package.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
        status?.SetText("VisualBoost: " + Path.GetFileName(targetPath) + "에 " + (declaration ? "선언" : "정의") + " 생성 · Ctrl+Z로 취소 · " + plan.Warning);
    }
    private async Task NotifyUnavailableAsync(string message)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        SystemSounds.Beep.Play();
        var status = await package.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
        status?.SetText("VisualBoost: " + message);
    }
    private static IWpfTextView ActiveView(IVsTextManager manager, IVsEditorAdaptersFactoryService adapters)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (manager.GetActiveView(1, null, out var native) == 0)
        {
            var view = adapters.GetWpfTextView(native);
            if (view is not null && !view.IsClosed && view.TextBuffer.ContentType.IsOfType("C/C++")) return view;
        }
        throw new GenerationNotSupportedException("C++ 코드 편집기에서 실행하세요.");
    }
    private static bool BelongsToProject(DTE2 dte, string path, string project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var item = dte.Solution.FindProjectItem(path);
        return item is not null && item.ContainingProject.UniqueName == project && item.FileCount > 0 &&
            string.Equals(GenerationModelReader.Normalize(item.FileNames[1]), path, StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsAllowedFile(string root, string path)
    { try { ValidateFile(root, path); return true; } catch (GenerationNotSupportedException) { return false; } }
    private static void ValidateFile(string root, string path)
    {
        GenerationPathPolicy.Validate(root, path);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new GenerationNotSupportedException("기존의 쓰기 가능한 파일만 생성 대상으로 사용할 수 있습니다.");
        // 프로젝트 내부 링크를 통해 외부 원본을 바꾸는 일을 막습니다.
        for (var current = path; current is not null && current.Length >= root.Length; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new GenerationNotSupportedException("링크·정션 경로는 생성 대상에서 제외합니다.");
    }
}
