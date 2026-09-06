using System;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using VisualBoost.Core.CodeGeneration;

namespace VisualBoost.CodeGeneration;

internal static class GenerationEditApplier
{
    public static void Apply(IWpfTextView sourceView, ITextSnapshot sourceSnapshot, IWpfTextView targetView,
        ITextSnapshot targetSnapshot, GenerationPlan plan, ITextUndoHistoryRegistry undo, IEditorOperations operations)
    {
        if (sourceView.IsClosed || targetView.IsClosed || sourceView.TextSnapshot != sourceSnapshot || targetView.TextSnapshot != targetSnapshot)
            throw new GenerationNotSupportedException("도구 선택 이후 문서가 바뀌었거나 닫혔습니다. 다시 생성하세요.");
        if (plan.Offset < 0 || plan.Offset > targetSnapshot.Length)
            throw new GenerationNotSupportedException("대상 위치가 문서 범위를 벗어났습니다.");
        var span = new Span(plan.Offset, 0);
        if (targetView.TextBuffer.IsReadOnly(span))
            throw new GenerationNotSupportedException("대상 위치가 변경되었거나 읽기 전용입니다. 체크아웃은 강제하지 않습니다.");
        if (!undo.TryGetHistory(targetView.TextBuffer, out var history))
            throw new GenerationNotSupportedException("이 편집기의 Undo 기록을 사용할 수 없어 생성하지 않습니다.");
        using var transaction = history.CreateTransaction("VisualBoost 선언/정의 생성");
        try
        {
            operations.AddBeforeTextBufferChangePrimitive();
            using var edit = targetView.TextBuffer.CreateEdit();
            if (!edit.Replace(span, plan.Text)) throw new GenerationNotSupportedException("대상에 코드를 삽입할 수 없습니다.");
            edit.Apply();
            if (edit.Canceled) throw new GenerationNotSupportedException("편집이 취소되었습니다.");
            targetView.Caret.MoveTo(new SnapshotPoint(targetView.TextSnapshot, plan.Offset));
            operations.AddAfterTextBufferChangePrimitive();
            transaction.Complete();
        }
        catch
        {
            // 삽입 후 후처리에 실패해도 단일 트랜잭션 전체를 되돌립니다.
            transaction.Cancel();
            throw;
        }
    }
}
