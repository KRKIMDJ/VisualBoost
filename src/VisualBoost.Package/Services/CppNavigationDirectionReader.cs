using System;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.VCCodeModel;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Services;

internal static class CppNavigationDirectionReader
{
    public static SymbolNavigationDirection Read(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var document = dte.ActiveDocument;
            if (document?.Selection is not TextSelection selection ||
                selection.TopPoint.Line != selection.BottomPoint.Line ||
                document.ProjectItem?.ContainingProject?.CodeModel is not VCCodeModel projectModel ||
                !projectModel.IsSynchronized ||
                document.ProjectItem?.FileCodeModel is not VCFileCodeModel model)
            {
                return SymbolNavigationDirection.Definition;
            }

            // 이미 동기화된 모델의 활성 위치 한 곳만 조회합니다.
            // 프로젝트 트리 순회나 Synchronize 호출로 분석 완료를 기다리지 않습니다.
            if (model.CodeElementFromPoint(selection.ActivePoint, vsCMElement.vsCMElementFunction)
                is not VCCodeFunction function)
            {
                return SymbolNavigationDirection.Definition;
            }

            var declaration = ReadNameRange(function, vsCMWhere.vsCMWhereDeclaration);
            var definition = ReadNameRange(function, vsCMWhere.vsCMWhereDefinition);
            return FunctionNavigationPolicy.SelectDirection(document.FullName,
                selection.TopPoint.Line, selection.TopPoint.LineCharOffset, selection.BottomPoint.LineCharOffset,
                declaration, definition);
        }
        catch (COMException)
        {
            // 커서에 함수가 없거나 C++ 데이터베이스가 준비되지 않은 정상적인 실패입니다.
            return SymbolNavigationDirection.Definition;
        }
        catch (ArgumentException)
        {
            // 일부 언어 서비스는 지원하지 않는 코드 범위를 인수 오류로 반환합니다.
            return SymbolNavigationDirection.Definition;
        }
    }

    private static FunctionNameRange? ReadNameRange(VCCodeFunction function, vsCMWhere where)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var start = function.get_StartPointOf(vsCMPart.vsCMPartName, where);
        var end = function.get_EndPointOf(vsCMPart.vsCMPartName, where);
        if (start is null || end is null || start.Line != end.Line)
            return null;

        return new FunctionNameRange(function.get_Location(where), start.Line,
            start.LineCharOffset, end.LineCharOffset);
    }
}
