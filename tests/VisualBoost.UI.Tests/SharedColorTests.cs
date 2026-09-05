using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text.Classification;
using VisualBoost.Coloring;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.Coloring;
using VisualBoost.UI;

internal static class SharedColorTests
{
    public static void Run()
    {
        var map = new TestMap();
        var key = SemanticFormatNames.Get(SemanticColorKind.Function, true);
        map.Items[key] = new ResourceDictionary { [EditorFormatDefinition.ForegroundColorId] = Colors.Orange };
        map.Items["CppFunctionSemanticTokenFormat"] = new ResourceDictionary { [EditorFormatDefinition.ForegroundColorId] = Colors.Gray };
        var local = new ColoringSettings(true, new[] { "", "", "#123456", "", "", "", "", "" });
        var shared = new ColoringSettings(true, new string[8], ColoringColorSource.FontsAndColors);
        SharedColorPalette.Attach(map);
        try
        {
            Assert(local.GetColor(SemanticColorKind.Function, true) == 0x123456, "기존 팔레트 설정 유지");
            Assert(shared.GetColor(SemanticColorKind.Function, true) == 0xFFA500, "Fonts and Colors 선택");
            Assert(shared.GetColor(SemanticColorKind.Function, false) == 0x845114, "미등록 밝은 색상 기본값");
            Assert(map.Writes == 0, "Fonts and Colors는 읽기 전용");
            var session = new CppColorFormatSession(map);
            session.Update(shared, true, false);
            Assert(map.Items["CppFunctionSemanticTokenFormat"][EditorFormatDefinition.ForegroundColorId].Equals(Colors.Orange), "편집기와 공유된 색상");
            ColoringSettings.Publish(shared);
            var converter = new SymbolColorConverter();
            var values = new object[] { SourceSymbolKind.Function, false, Brushes.White, Brushes.Black, 1 };
            var result = (SolidColorBrush)converter.Convert(values, typeof(Brush), null!, CultureInfo.InvariantCulture);
            Assert(result.Color == Colors.Orange, "탐색 창과 공유된 색상");
            values[1] = true;
            Assert(ReferenceEquals(Brushes.White, converter.Convert(values, typeof(Brush), null!, CultureInfo.InvariantCulture)), "선택 행 색상 보존");
            values[1] = false;
            values[0] = SourceSymbolKind.Namespace;
            Assert(ReferenceEquals(Brushes.White, converter.Convert(values, typeof(Brush), null!, CultureInfo.InvariantCulture)), "분류하지 않은 심볼 색상 보존");

            map.Items[key] = new ResourceDictionary { [EditorFormatDefinition.ForegroundColorId] = Colors.Aqua };
            var notifications = 0;
            EventHandler handler = (_, _) => notifications++;
            ColoringSettings.Changed += handler;
            try
            {
                for (var i = 0; i < 50; i++) map.Raise(key);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert(notifications == 1, "반복 서식 이벤트 병합");
                Assert(shared.GetColor(SemanticColorKind.Function, true) == 0x00FFFF, "Fonts and Colors 실시간 반영");
            }
            finally { ColoringSettings.Changed -= handler; }
            Assert(local.GetColor(SemanticColorKind.Function, true) == 0x123456, "공유 설정 변경 후에도 기존 팔레트 보존");
            session.Restore();
            Assert(map.Items["CppFunctionSemanticTokenFormat"][EditorFormatDefinition.ForegroundColorId].Equals(Colors.Gray), "편집기 색상 복원");
        }
        finally
        {
            SharedColorPalette.Detach();
            ColoringSettings.Publish(new ColoringSettings(true, new string[8]));
        }
        Assert(map.SubscriberCount == 0, "공유 서식 이벤트 해제");
        Console.WriteLine("PASS: Fonts and Colors 읽기·설정 기준 분리·편집기/팝업 색상 공유·이벤트 50회 병합·해제");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestMap : IEditorFormatMap
    {
        private EventHandler<FormatItemsEventArgs>? changed;
        public event EventHandler<FormatItemsEventArgs> FormatMappingChanged { add => changed += value; remove => changed -= value; }
        public int SubscriberCount => changed?.GetInvocationList().Length ?? 0;
        public Dictionary<string, ResourceDictionary> Items { get; } = new();
        public int Writes { get; private set; }
        public ResourceDictionary GetProperties(string key) => Items.TryGetValue(key, out var properties) ? properties : new();
        public void SetProperties(string key, ResourceDictionary value) { Items[key] = value; Writes++; }
        public void BeginBatchUpdate() { }
        public void EndBatchUpdate() { }
        public void Raise(string key) => changed?.Invoke(this, new FormatItemsEventArgs(new ReadOnlyCollection<string>(new[] { key })));
    }
}
