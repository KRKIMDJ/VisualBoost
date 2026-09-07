using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using VisualBoost.CodeGeneration;
using VisualBoost.Core.CodeGeneration;

internal static class GenerationInteractionTests
{
    public static void Run(string output)
    {
        GenerationModelTests.Run();
        var source = new IWpfTextView(); source.TextBuffer.Set("class Widget { void Reset(); };");
        var target = new IWpfTextView(); target.TextBuffer.Set("#include \"Widget.h\"\n");
        var sourceSnapshot = source.TextSnapshot;
        var snapshot = target.TextSnapshot;
        var undo = new ITextUndoHistoryRegistry();
        var operations = new IEditorOperations();
        var plan = new GenerationPlan(snapshot.Length, "\nvoid Widget::Reset()\n{\n    // TODO: 함수 본문을 구현하세요.\n}\n", "void Widget::Reset()", "자동 저장하지 않습니다. 생성한 본문을 확인하세요.");
        void Apply() => GenerationEditApplier.Apply(source, sourceSnapshot, target, snapshot, plan, undo, operations);
        Apply();
        Check(target.TextSnapshot.Text == snapshot.Text + plan.Text && undo.History.Completed == 1, "단일 Undo 트랜잭션 적용");
        Check(source.TextSnapshot == sourceSnapshot, "출처 버퍼 불변");
        undo.History.Undo(); Check(target.TextSnapshot.Text == snapshot.Text, "Undo 복원");
        undo.History.Redo(); Check(target.TextSnapshot.Text == snapshot.Text + plan.Text, "Redo 재적용");
        Reject(Apply, "오래된 대상 스냅샷 거부");
        target.TextBuffer.Set(snapshot.Text); snapshot = target.TextSnapshot;
        target.TextBuffer.ReadOnly = true; Reject(Apply, "읽기 전용 거부"); target.TextBuffer.ReadOnly = false;
        undo.Available = false; Reject(Apply, "Undo 불가 거부"); undo.Available = true;
        source.TextBuffer.Set(sourceSnapshot.Text); Reject(Apply, "오래된 출처 스냅샷 거부"); sourceSnapshot = source.TextSnapshot;
        source.IsClosed = true; Reject(Apply, "닫힌 출처 거부"); source.IsClosed = false;
        operations.FailAfter = true;
        try { Apply(); throw new Exception("실패 누락"); } catch (InvalidOperationException) { }
        Check(target.TextSnapshot.Text == snapshot.Text && undo.History.Canceled == 1, "삽입 후 실패 시 전체 복원");

        var untouched = target.TextSnapshot;
        var includeView = new IWpfTextView(); includeView.TextBuffer.Set("#pragma once\nclass Foo {};\n");
        var includeSnapshot = includeView.TextSnapshot;
        includeView.Caret.MoveTo(new Microsoft.VisualStudio.Text.SnapshotPoint(includeSnapshot, 20));
        var includePlan = QuickInclude.Create(includeSnapshot.Text, @"C:\Test\Foo.h", @"C:\Test\Bar.h");
        var includeUndo = new ITextUndoHistoryRegistry();
        GenerationEditApplier.Apply(includeView, includeSnapshot, includeView, includeSnapshot, includePlan, includeUndo, new IEditorOperations(), "VisualBoost 빠른 인클루드", true);
        Check(includeView.Caret.Position.BufferPosition.Position == 20 + includePlan.Text.Length, "include 삽입 후 기존 심볼의 캐럿 유지");
        includeUndo.History.Undo(); Check(includeView.TextSnapshot.Text == includeSnapshot.Text, "include 한 번의 Undo");
        includeUndo.History.Redo(); Check(includeView.TextSnapshot.Text == includeSnapshot.Text.Insert(includePlan.Offset, includePlan.Text), "include Redo");
        DocumentCodeActionMenuTests.Run(output);
        Check(target.TextSnapshot == untouched, "도구 메뉴 열기·취소 시 무편집");
        Console.WriteLine("PASS: 실제 편집 적용 코드의 Undo/Redo·버전·읽기 전용·실패 복원 및 문맥 도구 메뉴 (편집기 경계 대체)");
    }
    private static void Reject(Action action, string reason) { try { action(); } catch (GenerationNotSupportedException) { return; } throw new Exception(reason); }
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
}
