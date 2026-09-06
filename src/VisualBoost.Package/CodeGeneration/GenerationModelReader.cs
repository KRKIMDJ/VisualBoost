using System;
using System.IO;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.VCCodeModel;
using VisualBoost.Core.CodeGeneration;
using VisualBoost.Core.Analysis;
using VisualBoost.Services;

namespace VisualBoost.CodeGeneration;

internal sealed class GenerationModelContext
{
    public GenerationDirection? AvailableDirection => ExistingPath.Length > 0 ? null :
        Function.IsDefinition ? GenerationDirection.Declaration : GenerationDirection.Definition;
    public GenerationFunction Function = null!;
    public string SourcePath = "", Project = "", HeaderPath = "", ExistingPath = "";
    public object Owner = null!;
    public Action ValidateBeforeApply = null!;
}

internal static class GenerationModelReader
{
    public static GenerationModelContext Read(DTE2 dte, ITextSnapshot snapshot)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var doc = dte.ActiveDocument;
            if (doc?.Selection is not TextSelection selection || doc.ProjectItem?.ContainingProject?.CodeModel is not VCCodeModel model ||
                !model.IsSynchronized || doc.ProjectItem.FileCodeModel is not VCFileCodeModel fileModel)
                throw new GenerationNotSupportedException("현재 C++ 언어 모델이 준비되지 않았습니다. 분석 완료 후 다시 실행하세요. 별도 분석을 강제로 시작하지 않습니다.");
            if (fileModel.CodeElementFromPoint(selection.ActivePoint, vsCMElement.vsCMElementFunction) is not VCCodeFunction function)
                throw new GenerationNotSupportedException("함수 선언 또는 정의 내부에 커서를 두고 실행하세요.");
            if (function.IsTemplate || function.IsInjected || function.IsDefault || function.IsDelete)
                throw new GenerationNotSupportedException("템플릿·매크로 생성·default/delete 함수는 아직 지원하지 않습니다.");
            object owner = function.Parent;
            var ownerName = OwnerName(owner);
            var sourcePath = Normalize(doc.FullName);
            var definitionPath = NormalizeOptional(function.get_Location(vsCMWhere.vsCMWhereDefinition));
            var declarationPath = NormalizeOptional(function.get_Location(vsCMWhere.vsCMWhereDeclaration));
            // 언어 서비스가 위치 조회 자체를 실패하면 '없음'으로 추측해 생성하지 않습니다.
            var isDefinition = string.Equals(definitionPath, sourcePath, StringComparison.OrdinalIgnoreCase);
            var where = isDefinition ? vsCMWhere.vsCMWhereDefinition : vsCMWhere.vsCMWhereDeclaration;
            if (!isDefinition && !string.Equals(declarationPath, sourcePath, StringComparison.OrdinalIgnoreCase)) throw new GenerationNotSupportedException("현재 버퍼와 함수의 위치가 일치하지 않습니다.");
            var start = Offset(snapshot, function.get_StartPointOf(vsCMPart.vsCMPartWhole, where));
            var end = Offset(snapshot, function.get_EndPointOf(vsCMPart.vsCMPartWhole, where));
            var nameStart = Offset(snapshot, function.get_StartPointOf(vsCMPart.vsCMPartName, where));
            var nameEnd = Offset(snapshot, function.get_EndPointOf(vsCMPart.vsCMPartName, where));
            if (nameEnd < nameStart) throw new GenerationNotSupportedException("함수 이름 범위가 변경되었습니다.");
            var nameText = snapshot.GetText(nameStart, nameEnd - nameStart);
            var nameIndex = nameText.LastIndexOf(function.Name, StringComparison.Ordinal);
            if (nameIndex < 0) throw new GenerationNotSupportedException("함수 이름이 현재 편집 내용과 다릅니다. 언어 모델 갱신 후 다시 실행하세요.");
            nameStart += nameIndex;
            if (Offset(snapshot, selection.TopPoint) < start || Offset(snapshot, selection.BottomPoint) > end)
                throw new GenerationNotSupportedException("선택 범위가 하나의 함수를 벗어납니다.");
            var otherWhere = isDefinition ? vsCMWhere.vsCMWhereDeclaration : vsCMWhere.vsCMWhereDefinition;
            var otherPath = isDefinition ? declarationPath : definitionPath;
            var context = new GenerationModelContext
            {
                SourcePath = sourcePath, Project = doc.ProjectItem.ContainingProject.UniqueName, Owner = owner,
                HeaderPath = OwnerPath(owner),
                Function = new GenerationFunction(function.Name, ownerName, start, nameStart, end, isDefinition),
                ExistingPath = otherPath,
                ValidateBeforeApply = () =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    if (!model.IsSynchronized || OwnerName((object)function.Parent) != ownerName || !string.IsNullOrWhiteSpace(function.get_Location(otherWhere)))
                        throw new GenerationNotSupportedException("도구 선택 이후 언어 모델이 변경되었거나 대응 함수가 생겼습니다. 다시 확인하세요.");
                }
            };
            return context;
        }
        catch (COMException exception)
        { throw new GenerationNotSupportedException("C++ 언어 모델에서 함수·대응 위치를 확정하지 못했습니다. 자동 생성하지 않습니다. " + exception.Message); }
        catch (ArgumentException exception)
        { throw new GenerationNotSupportedException("언어 모델의 위치가 현재 버퍼와 일치하지 않습니다. " + exception.Message); }
    }

    public static GenerationClass ReadTargetClass(GenerationModelContext context, string targetPath, ITextSnapshot snapshot)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!string.Equals(context.HeaderPath, targetPath, StringComparison.OrdinalIgnoreCase))
            throw new GenerationNotSupportedException("선택한 헤더가 언어 모델에서 확인한 클래스 선언 파일과 다릅니다.");
        if (context.Owner is VCCodeClass cls)
            return new GenerationClass(cls.FullName, Offset(snapshot, cls.get_StartPointOf(vsCMPart.vsCMPartWhole)),
                Offset(snapshot, cls.get_StartPointOf(vsCMPart.vsCMPartName)), Offset(snapshot, cls.get_EndPointOf(vsCMPart.vsCMPartWhole)));
        if (context.Owner is VCCodeStruct str)
            return new GenerationClass(str.FullName, Offset(snapshot, str.get_StartPointOf(vsCMPart.vsCMPartWhole)),
                Offset(snapshot, str.get_StartPointOf(vsCMPart.vsCMPartName)), Offset(snapshot, str.get_EndPointOf(vsCMPart.vsCMPartWhole)));
        throw new GenerationNotSupportedException("대상 클래스를 확인하지 못했습니다.");
    }
    private static string OwnerName(object owner)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // 중첩 클래스 자체가 비템플릿이어도 바깥 클래스가 템플릿이면 한정 이름만으로 정의할 수 없습니다.
        object? enclosing = owner;
        var depth = 0;
        while (enclosing is VCCodeClass || enclosing is VCCodeStruct)
        {
            if (++depth > 64) throw new GenerationNotSupportedException("클래스 소속의 중첩 범위를 확인하지 못했습니다.");
            if (enclosing is VCCodeClass outer)
            {
                if (outer.IsTemplate || outer.IsInjected) throw new GenerationNotSupportedException("템플릿·매크로 생성 클래스의 내부 멤버는 지원하지 않습니다.");
                enclosing = outer.Parent;
            }
            else
            {
                var outerStruct = (VCCodeStruct)enclosing;
                if (outerStruct.IsTemplate || outerStruct.IsInjected) throw new GenerationNotSupportedException("템플릿·매크로 생성 구조체의 내부 멤버는 지원하지 않습니다.");
                enclosing = outerStruct.Parent;
            }
        }
        if (owner is VCCodeClass cls && !cls.IsTemplate && !cls.IsInjected) return cls.FullName;
        if (owner is VCCodeStruct str && !str.IsTemplate && !str.IsInjected) return str.FullName;
        throw new GenerationNotSupportedException("비템플릿 class/struct의 멤버 함수만 지원합니다.");
    }
    private static string OwnerPath(object owner)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return Normalize(owner is VCCodeClass cls ? cls.get_Location(vsCMWhere.vsCMWhereDeclaration) : ((VCCodeStruct)owner).get_Location(vsCMWhere.vsCMWhereDeclaration));
    }
    private static int Offset(ITextSnapshot snapshot, TextPoint point)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var line = snapshot.GetLineFromLineNumber(point.Line - 1);
        var column = point.LineCharOffset - 1;
        if (column < 0 || column > line.Length) throw new GenerationNotSupportedException("모델의 열 위치가 현재 줄을 벗어납니다.");
        return line.Start.Position + column;
    }
    internal static string Normalize(string path) => SearchPath.TryNormalize(path, out var normalized) ? normalized : throw new GenerationNotSupportedException("로컬 파일 경로를 확인하지 못했습니다.");
    private static string NormalizeOptional(string path) => string.IsNullOrWhiteSpace(path) ? "" : Normalize(path);
}
