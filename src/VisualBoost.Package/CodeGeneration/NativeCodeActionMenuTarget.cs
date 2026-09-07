using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.OLE.Interop;

namespace VisualBoost.CodeGeneration;

// 전역 명령 서비스에 임시 항목을 남기지 않고 메뉴 호출마다 독립된 명령 대상을 제공합니다.
internal sealed class NativeCodeActionMenuTarget : IOleCommandTarget, IDisposable
{
    internal static readonly Guid CommandSet = new("4cce3464-a08f-4e0d-a5fd-a297c7fcd41e");
    internal const int MenuId = 0x1030;
    internal const int DeclarationMenuId = 0x1032;
    internal const uint ItemStart = 0x3000;
    internal const uint ChildStart = 0x4000;
    internal const int RangeSize = 0x1000;
    private const int NotSupported = unchecked((int)0x80040100);
    private const int Disabled = unchecked((int)0x80040101);
    private const int UnknownGroup = unchecked((int)0x80040104);
    private readonly CancellationToken token;
    private DocumentCodeAction[] items;
    private DocumentCodeAction? declaration;
    private bool disposed;
    internal DocumentCodeAction? Selected { get; private set; }
    internal NativeCodeActionMenuTarget(IReadOnlyList<DocumentCodeAction> actions, CancellationToken token)
    {
        this.token = token;
        items = actions.Where(a => a.Children.Count == 0).ToArray();
        var parents = actions.Where(a => a.Children.Count > 0).ToArray();
        // 현재 하위 도구는 선언 접근 수준뿐입니다. 신규 중첩 도구는 VSCT 등록도 함께 확장해야 합니다.
        if (items.Length >= RangeSize || parents.Length > 1 || parents.Any(a => a.Id != "declaration" || a.Children.Count >= RangeSize || a.Children.Any(c => c.Children.Count > 0)))
            throw new InvalidOperationException("등록된 컨텍스트 메뉴 구조로 표시할 수 없는 도구입니다.");
        declaration = parents.SingleOrDefault();
    }
    private DocumentCodeAction? Find(uint id)
    {
        if (disposed) return null;
        if (id == DeclarationMenuId) return declaration;
        if (id >= ItemStart && id - ItemStart < items.Length) return items[id - ItemStart];
        if (id >= ChildStart && declaration is not null && id - ChildStart < declaration.Children.Count) return declaration.Children[(int)(id - ChildStart)];
        return null;
    }
    public int QueryStatus(ref Guid group, uint count, OLECMD[] commands, IntPtr commandText)
    {
        if (group != CommandSet) return UnknownGroup;
        if (count > commands.Length) return unchecked((int)0x80070057);
        var supported = false;
        for (var i = 0; i < count; i++)
        {
            var action = Find(commands[i].cmdID);
            if (action is null)
            {
                // 시작 자리표시자는 숨기고, 동적 범위 끝은 미지원으로 알려 Shell의 열거를 끝냅니다.
                var placeholder = commands[i].cmdID == ItemStart || commands[i].cmdID == ChildStart || commands[i].cmdID == DeclarationMenuId;
                commands[i].cmdf = placeholder ? 1u | 16u : 0;
                supported |= placeholder;
                continue;
            }
            supported = true;
            commands[i].cmdf = 1u | (token.IsCancellationRequested || Selected is not null ? 0u : 2u);
            if (i == 0 && commandText != IntPtr.Zero) WriteText(commandText, action);
        }
        return supported ? 0 : NotSupported;
    }
    public int Exec(ref Guid group, uint command, uint options, IntPtr input, IntPtr output)
    {
        if (group != CommandSet) return UnknownGroup;
        var action = Find(command);
        if (action is null || action.Children.Count > 0) return NotSupported;
        if (token.IsCancellationRequested || Selected is not null) return Disabled;
        Selected = action;
        return 0;
    }
    private static void WriteText(IntPtr buffer, DocumentCodeAction action)
    {
        // OLECMDTEXT는 DWORD 3개 뒤 가변 길이 UTF-16 버퍼입니다. Shell이 제공한 용량을 넘지 않습니다.
        var flags = Marshal.ReadInt32(buffer, 0);
        var capacity = Marshal.ReadInt32(buffer, 8);
        if (capacity <= 0) return;
        string text;
        if ((flags & 1) != 0) text = action.Title.Replace("&", "&&");
        else if ((flags & 2) != 0) text = action.Description;
        else return;
        var length = Math.Min(text.Length, capacity - 1);
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        Marshal.Copy(text.ToCharArray(), 0, IntPtr.Add(buffer, 12), length);
        Marshal.WriteInt16(buffer, 12 + length * 2, 0);
        Marshal.WriteInt32(buffer, 4, length + 1);
    }
    public void Dispose() { disposed = true; items = Array.Empty<DocumentCodeAction>(); declaration = null; Selected = null; }
}
