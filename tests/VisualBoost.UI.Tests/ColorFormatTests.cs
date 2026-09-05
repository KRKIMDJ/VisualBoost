using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using VisualBoost.Coloring;
using VisualBoost.Core.Coloring;

internal static class ColorFormatTests
{
    private const string TypeKey = "CppTypeSemanticTokenFormat";
    private const string Foreground = EditorFormatDefinition.ForegroundColorId;
    private const string BrushKey = EditorFormatDefinition.ForegroundBrushId;

    public static void Run()
    {
        var map = new TestMap();
        map.Items[TypeKey] = new ResourceDictionary { [Foreground] = Colors.Red, ["FontSize"] = 17d };
        var session = new CppColorFormatSession(map);
        var enabled = new ColoringSettings(true, new string[8]);
        session.Update(enabled, true, false);
        Assert(map.Items[TypeKey][Foreground].Equals(Color.FromRgb(0x68, 0xD5, 0xC4)), "어두운 타입 색상");
        Assert(session.AppliedCount == 1 && map.Items.Count == 1, "미지원 분류를 새로 생성하지 않음");
        var writes = map.Writes;
        for (var i = 0; i < 100; i++) session.Update(enabled, true, false);
        Assert(writes == map.Writes && !map.IsInBatchUpdate, "멱등 갱신과 배치 종료");
        map.Items[TypeKey]["FontSize"] = 19d;
        session.Restore();
        Assert(map.Items[TypeKey][Foreground].Equals(Colors.Red) && !map.Items[TypeKey].Contains(BrushKey), "원래 전경 속성 복원");
        Assert(map.Items[TypeKey]["FontSize"].Equals(19d), "외부 글꼴 변경 보존");

        session.Update(enabled, true, false);
        map.Items[TypeKey] = new ResourceDictionary { [Foreground] = Colors.DarkBlue, ["FontSize"] = 15d };
        session.Update(enabled, false, false);
        Assert(map.Items[TypeKey][Foreground].Equals(Color.FromRgb(0, 0x6B, 0x5A)), "밝은 타입 색상");
        session.Update(enabled, false, true);
        Assert(map.Items[TypeKey][Foreground].Equals(Colors.DarkBlue), "고대비에서 새 테마의 원래 색상 복원");
        session.Update(enabled, false, false);
        var overrides = new[] { "#123456", "", "", "", "#654321", "", "", "" };
        session.Update(new ColoringSettings(true, overrides), false, false);
        Assert(map.Items[TypeKey][Foreground].Equals(Color.FromRgb(0x65, 0x43, 0x21)), "사용자 옵션 적용");
        map.Items[TypeKey] = new ResourceDictionary { [Foreground] = Colors.Purple, ["FontSize"] = 21d };
        session.Restore();
        Assert(map.Items[TypeKey][Foreground].Equals(Colors.Purple), "새 외부 색상을 이전 색상으로 덮어쓰지 않음");

        foreach (var (key, kind) in new[] { ("CppLocalVariableSemanticTokenFormat", SemanticColorKind.Variable),
            ("CppMemberFunctionSemanticTokenFormat", SemanticColorKind.Function), ("CppMacroSemanticTokenFormat", SemanticColorKind.Macro) })
        {
            map.Items[key] = new ResourceDictionary { [Foreground] = Colors.Gray };
            session.Update(enabled, true, false);
            var rgb = enabled.GetColor(kind, true);
            Assert(map.Items[key][Foreground].Equals(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)), "분류별 색상 연결");
        }
        session.Restore();
        Console.WriteLine("PASS: C++ 색상 분류, 옵션·테마 전환, 고대비, 외부 변경 보존, 복원 및 100회 멱등 갱신");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestMap : IEditorFormatMap
    {
        public Dictionary<string, ResourceDictionary> Items { get; } = new();
        public int Writes { get; private set; }
        public event EventHandler<FormatItemsEventArgs> FormatMappingChanged { add { } remove { } }
        public bool IsInBatchUpdate { get; private set; }
        public ResourceDictionary GetProperties(string key) => Items.TryGetValue(key, out var value) ? value : new();
        public void SetProperties(string key, ResourceDictionary value) { Items[key] = value; Writes++; }
        public void BeginBatchUpdate() => IsInBatchUpdate = true;
        public void EndBatchUpdate() => IsInBatchUpdate = false;
    }
}

namespace Microsoft.VisualStudio.Text.Classification
{
    // 실제 색상 세션 코드를 실행하되 VS 호스트의 서식 저장소만 대역으로 치환합니다.
    internal interface IEditorFormatMap
    {
        event EventHandler<FormatItemsEventArgs> FormatMappingChanged;
        ResourceDictionary GetProperties(string key);
        void SetProperties(string key, ResourceDictionary value);
        void BeginBatchUpdate();
        void EndBatchUpdate();
    }
    internal sealed class FormatItemsEventArgs : EventArgs
    {
        public FormatItemsEventArgs(System.Collections.ObjectModel.ReadOnlyCollection<string> changedItems) => ChangedItems = changedItems;
        public System.Collections.ObjectModel.ReadOnlyCollection<string> ChangedItems { get; }
    }
    internal static class EditorFormatDefinition
    {
        public const string ForegroundColorId = "ForegroundColor";
        public const string ForegroundBrushId = "Foreground";
    }
}
