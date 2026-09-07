using System;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;

namespace VisualBoost.DocumentNavigation;

// VS가 WPF보다 먼저 처리하는 편집 명령을 검색란으로 돌립니다. 닫혀 있으면 체인을 그대로 통과합니다.
internal sealed class DocumentNavigationCommandFilter : IOleCommandTarget
{
    private readonly DocumentNavigationControl control;
    internal IOleCommandTarget? Next { get; set; }
    public DocumentNavigationCommandFilter(DocumentNavigationControl control) => this.control = control;
    private bool Owns(Guid group, uint id) => control.IsOpen &&
        (group == VSConstants.VSStd2K || group == VSConstants.GUID_VSStandardCommandSet97 && StandardCommand(id) is not null);
    private static RoutedCommand? StandardCommand(uint id) => (VSConstants.VSStd97CmdID)id switch
    {
        VSConstants.VSStd97CmdID.Cut => ApplicationCommands.Cut,
        VSConstants.VSStd97CmdID.Copy => ApplicationCommands.Copy,
        VSConstants.VSStd97CmdID.Paste => ApplicationCommands.Paste,
        VSConstants.VSStd97CmdID.Undo or VSConstants.VSStd97CmdID.MultiLevelUndo => ApplicationCommands.Undo,
        VSConstants.VSStd97CmdID.Redo or VSConstants.VSStd97CmdID.MultiLevelRedo => ApplicationCommands.Redo,
        VSConstants.VSStd97CmdID.SelectAll => ApplicationCommands.SelectAll,
        _ => null
    };
    public int QueryStatus(ref Guid group, uint count, OLECMD[] commands, IntPtr text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // 호스트는 여러 명령을 한 번에 물을 수 있으므로 다른 명령 상태를 덮어쓰지 않습니다.
        var result = Next?.QueryStatus(ref group, count, commands, text) ?? unchecked((int)0x80040100);
        var allOwned = count > 0;
        for (var i = 0; i < count; i++)
        {
            if (Owns(group, commands[i].cmdID)) commands[i].cmdf = 3;
            else allOwned = false;
        }
        return allOwned ? 0 : result;
    }
    public int Exec(ref Guid group, uint id, uint options, IntPtr input, IntPtr output)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!Owns(group, id)) return Next?.Exec(ref group, id, options, input, output) ?? unchecked((int)0x80040100);
        var search = control.SearchInput;
        if (group == VSConstants.GUID_VSStandardCommandSet97) Execute(StandardCommand(id), search);
        else switch ((VSConstants.VSStd2KCmdID)id)
        {
            case VSConstants.VSStd2KCmdID.TYPECHAR:
                if (input != IntPtr.Zero)
                {
                    var value = Convert.ToChar(Marshal.GetObjectForNativeVariant(input));
                    if (!char.IsControl(value))
                    {
                        search.Focus();
                        var start = search.SelectionStart;
                        search.SelectedText = value.ToString();
                        search.Select(start + 1, 0);
                    }
                }
                break;
            case VSConstants.VSStd2KCmdID.BACKSPACE: Execute(EditingCommands.Backspace, search); break;
            case VSConstants.VSStd2KCmdID.DELETE: Execute(EditingCommands.Delete, search); break;
            case VSConstants.VSStd2KCmdID.DELETEWORDLEFT: Execute(EditingCommands.DeletePreviousWord, search); break;
            case VSConstants.VSStd2KCmdID.DELETEWORDRIGHT: Execute(EditingCommands.DeleteNextWord, search); break;
            case VSConstants.VSStd2KCmdID.RETURN: control.Accept(); break;
            case VSConstants.VSStd2KCmdID.CANCEL: control.Cancel(); break;
            case VSConstants.VSStd2KCmdID.TAB: case VSConstants.VSStd2KCmdID.BACKTAB: control.ToggleFocus(); break;
            case VSConstants.VSStd2KCmdID.UP: control.MoveSelection(-1); break;
            case VSConstants.VSStd2KCmdID.DOWN: control.MoveSelection(1); break;
            case VSConstants.VSStd2KCmdID.PAGEUP: control.MoveSelection(-12); break;
            case VSConstants.VSStd2KCmdID.PAGEDN: control.MoveSelection(12); break;
            case VSConstants.VSStd2KCmdID.LEFT: if (!control.MoveTree(false)) Execute(EditingCommands.MoveLeftByCharacter, search); break;
            case VSConstants.VSStd2KCmdID.RIGHT: if (!control.MoveTree(true)) Execute(EditingCommands.MoveRightByCharacter, search); break;
            case VSConstants.VSStd2KCmdID.LEFT_EXT: Execute(EditingCommands.SelectLeftByCharacter, search); break;
            case VSConstants.VSStd2KCmdID.RIGHT_EXT: Execute(EditingCommands.SelectRightByCharacter, search); break;
            case VSConstants.VSStd2KCmdID.WORDPREV: Execute(EditingCommands.MoveLeftByWord, search); break;
            case VSConstants.VSStd2KCmdID.WORDNEXT: Execute(EditingCommands.MoveRightByWord, search); break;
            case VSConstants.VSStd2KCmdID.WORDPREV_EXT: Execute(EditingCommands.SelectLeftByWord, search); break;
            case VSConstants.VSStd2KCmdID.WORDNEXT_EXT: Execute(EditingCommands.SelectRightByWord, search); break;
            case VSConstants.VSStd2KCmdID.HOME: case VSConstants.VSStd2KCmdID.BOL: Execute(EditingCommands.MoveToLineStart, search); break;
            case VSConstants.VSStd2KCmdID.END: case VSConstants.VSStd2KCmdID.EOL: Execute(EditingCommands.MoveToLineEnd, search); break;
            case VSConstants.VSStd2KCmdID.HOME_EXT: case VSConstants.VSStd2KCmdID.BOL_EXT: Execute(EditingCommands.SelectToLineStart, search); break;
            case VSConstants.VSStd2KCmdID.END_EXT: case VSConstants.VSStd2KCmdID.EOL_EXT: Execute(EditingCommands.SelectToLineEnd, search); break;
            // 들여쓰기·주석·줄 삭제 등 문서 전용 명령도 검색 중 원본 버퍼로 누출시키지 않습니다.
            default: break;
        }
        return 0;
    }
    private static void Execute(RoutedCommand? command, TextBox search)
    {
        search.Focus();
        if (command?.CanExecute(null, search) == true) command.Execute(null, search);
    }
}
