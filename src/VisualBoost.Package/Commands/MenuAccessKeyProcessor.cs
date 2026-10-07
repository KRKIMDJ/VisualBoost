using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows.Input;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VisualBoost.Core.Input;

namespace VisualBoost.Commands;

/// <summary>
/// 편집기에서 Alt+글자 키가 VS 메뉴 접근 키(예: Git 메뉴의 G)와 겹쳐 VisualBoost 명령 바인딩이 실행되지 않는 문제를 막습니다.
/// </summary>
/// <remarks>
/// VS는 메뉴 접근 키와 같은 글자의 Alt 바인딩을 실행하지 않고 키를 편집기로 넘긴 뒤 메뉴를 엽니다(2026-10-07 사용자 피드백: 편집기 범위
/// Alt+G가 Git 메뉴를 엶, 같은 범위의 Alt+M·Alt+O는 메뉴와 겹치지 않아 동작). 편집기까지 온 Alt+글자 키가 지금 VisualBoost 명령에
/// 할당돼 있으면 그 명령을 실행하고 키를 처리한 것으로 표시해 메뉴가 열리지 않게 합니다. 사용자가 도구 > 옵션 > 키보드에서 바인딩을 바꾸면
/// 바꾼 키를 따르고, 지운 키는 메뉴로 돌려줍니다. VS가 먼저 처리한 키는 여기까지 오지 않습니다.
/// </remarks>
[Export(typeof(IKeyProcessorProvider))]
[Name("VisualBoostMenuAccessKeys")]
[Order(Before = "default")]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Interactive)]
internal sealed class MenuAccessKeyProcessorProvider : IKeyProcessorProvider
{
    public KeyProcessor GetAssociatedProcessor(IWpfTextView wpfTextView) => new MenuAccessKeyProcessor();
}

internal sealed class MenuAccessKeyProcessor : KeyProcessor
{
    // VSCT 기본 바인딩이 있는 명령입니다. 사용자가 다른 명령에 Alt+글자를 줄 수도 있지만 VisualBoost 명령만 대신 실행합니다.
    private static readonly int[] BoundCommands =
    {
        CommandIds.GoToDefinition, CommandIds.FindReferences, CommandIds.OpenDocumentMembers, CommandIds.SwitchHeaderSource,
        CommandIds.OpenFileSearch, CommandIds.OpenSymbolSearch, CommandIds.GenerateFunction, CommandIds.ShowReferencesWindow,
    };

    private const string CommandSet = "{4cce3464-a08f-4e0d-a5fd-a297c7fcd41e}";

    public override void KeyDown(KeyEventArgs args)
    {
        if (args.Key != Key.System || Keyboard.Modifiers != ModifierKeys.Alt || args.SystemKey < Key.A || args.SystemKey > Key.Z) return;
        ThreadHelper.ThrowIfNotOnUIThread();
        var letter = (char)('A' + (args.SystemKey - Key.A));
        if (Package.GetGlobalService(typeof(SDTE)) is not DTE2 dte) return;
        foreach (var id in BoundCommands)
        {
            Command command;
            try
            {
                command = dte.Commands.Item(CommandSet, id);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (command.Bindings is not object[] bindings || !bindings.OfType<string>().Any(binding => KeyBindingText.IsAltLetter(binding, letter))) continue;
            if (!command.IsAvailable) return;
            object? input = null;
            object? output = null;
            dte.Commands.Raise(CommandSet, id, ref input, ref output);
            args.Handled = true;
            return;
        }
    }
}
