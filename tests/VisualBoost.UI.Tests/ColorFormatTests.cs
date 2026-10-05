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
            ("CppMemberFunctionSemanticTokenFormat", SemanticColorKind.Function), ("CppMacroSemanticTokenFormat", SemanticColorKind.Macro),
            ("CppEnumSemanticTokenFormat", SemanticColorKind.EnumMember), ("CppNamespaceSemanticTokenFormat", SemanticColorKind.Namespace),
            ("VisualBoost.Fast.Type", SemanticColorKind.Type), ("VisualBoost.Fast.Variable", SemanticColorKind.Variable),
            ("VisualBoost.Fast.Function", SemanticColorKind.Function), ("VisualBoost.Fast.Macro", SemanticColorKind.Macro),
            ("VisualBoost.Fast.EnumMember", SemanticColorKind.EnumMember), ("VisualBoost.Fast.Namespace", SemanticColorKind.Namespace) })
        {
            map.Items[key] = new ResourceDictionary { [Foreground] = Colors.Gray };
            session.Update(enabled, true, false);
            var rgb = enabled.GetColor(kind, true);
            Assert(map.Items[key][Foreground].Equals(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)), "분류별 색상 연결");
        }
        session.Restore();
        Assert(CppColorFormatSession.IsRelevant("CppEnumSemanticTokenFormat") &&
            !CppColorFormatSession.IsRelevant("UnrelatedExtension.Format"), "관련 서식 이벤트만 재적용");
        var legacy = new ColoringSettings(true, new[] { "#010101", "#020202", "#030303", "#040404", "#050505", "#060606", "#070707", "#080808" });
        for (var i = 0; i < 4; i++)
        {
            Assert(legacy.GetColor((SemanticColorKind)i, true) == (i + 1) * 0x010101, "기존 어두운 색상 슬롯 유지");
            Assert(legacy.GetColor((SemanticColorKind)i, false) == (i + 5) * 0x010101, "기존 밝은 색상 슬롯 이동");
        }
        var values = new string[12];
        for (var i = 0; i < values.Length; i++) values[i] = "#" + ((i + 1) * 0x010101).ToString("X6");
        var expanded = new ColoringSettings(true, values, ColoringColorSource.Palette, false);
        values[0] = "#FFFFFF";
        for (var i = 0; i < 6; i++)
        {
            Assert(expanded.GetColor((SemanticColorKind)i, true) == (i + 1) * 0x010101, "6개 어두운 그룹 독립 저장 및 복사");
            Assert(expanded.GetColor((SemanticColorKind)i, false) == (i + 7) * 0x010101, "6개 밝은 그룹 독립 저장");
        }
        Assert(legacy.QuickColoring && !expanded.QuickColoring, "빠른 색상 기본값 및 비활성 설정");
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
