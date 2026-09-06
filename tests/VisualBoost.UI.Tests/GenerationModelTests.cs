using System;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.VCCodeModel;
using VisualBoost.CodeGeneration;
using VisualBoost.Core.CodeGeneration;

internal static class GenerationModelTests
{
    public static void Run()
    {
        var snapshot = new ITextSnapshot("class Widget {\r\n    void Reset();\r\n};");
        var function = new VCCodeFunction
        {
            StartReader = (part, _) => new(2, part == vsCMPart.vsCMPartName ? 10 : 5),
            EndReader = (part, _) => new(2, part == vsCMPart.vsCMPartName ? 15 : 18),
            LocationReader = where => where == vsCMWhere.vsCMWhereDeclaration ? "C:/Sample/Widget.h" : "",
        };
        var model = new VCCodeModel { IsSynchronized = true };
        var file = new VCFileCodeModel { GetElement = () => function };
        var selection = new TextSelection { TopPoint = new(2, 10), BottomPoint = new(2, 10) };
        var dte = new DTE2 { ActiveDocument = new Document { FullName = "C:/Sample/Widget.h", Selection = selection,
            ProjectItem = new() { FileCodeModel = file, ContainingProject = new() { CodeModel = model } } } };
        GenerationModelContext Read() => GenerationModelReader.Read(dte, snapshot);
        var context = Read();
        Check(context.Function.NameStart == snapshot.Text.IndexOf("Reset", StringComparison.Ordinal) && !context.Function.IsDefinition && context.ExistingPath.Length == 0, "CRLF 언어 모델 범위 매핑");
        var cls = GenerationModelReader.ReadTargetClass(context, context.HeaderPath, snapshot);
        Check(cls.NameStart == 6 && cls.End == snapshot.Length, "대상 클래스 범위");
        context.ValidateBeforeApply();
        function.LocationReader = _ => "C:/Sample/Widget.h";
        Reject(context.ValidateBeforeApply, "미리보기 후 새 대응 함수 차단");
        function.LocationReader = where => where == vsCMWhere.vsCMWhereDeclaration ? "C:/Sample/Widget.h" : "";
        model.IsSynchronized = false; var count = file.Queries;
        Reject(() => Read(), "비동기화 모델 거부"); Check(file.Queries == count, "강제 분석·함수 조회 없음");
        model.IsSynchronized = true;
        function.FailLocation = true; Reject(() => Read(), "위치 조회 실패를 부재로 처리하지 않음"); function.FailLocation = false;
        function.IsTemplate = true; Reject(() => Read(), "템플릿 거부"); function.IsTemplate = false;
        function.Parent = new object(); Reject(() => Read(), "소속 불명 거부"); function.Parent = new VCCodeClass();
        function.Parent = new VCCodeClass { Parent = new VCCodeClass { IsTemplate = true } };
        Reject(() => Read(), "바깥 클래스가 템플릿인 중첩 클래스 거부"); function.Parent = new VCCodeClass();
        function.Name = "Other"; Reject(() => Read(), "오래된 이름 거부"); function.Name = "Reset";
        selection.BottomPoint = new(3, 1); Reject(() => Read(), "여러 함수 범위 선택 거부"); selection.BottomPoint = selection.TopPoint;
        file.GetElement = () => throw new COMException("Unavailable"); Reject(() => Read(), "COM 오류 시 무생성");
        Console.WriteLine("PASS: 실제 생성 모델 읽기의 CRLF 범위·소속·선택·동기화·중복·COM 실패 보호 (SDK 경계 대체)");
    }
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static void Reject(Action action, string reason) { try { action(); } catch (GenerationNotSupportedException) { return; } throw new Exception(reason); }
}
