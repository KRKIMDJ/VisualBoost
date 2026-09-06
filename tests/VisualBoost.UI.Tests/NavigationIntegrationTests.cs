using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.VCCodeModel;
using VisualBoost.Services;

internal static class NavigationIntegrationTests
{
    public static void Run()
    {
        var function = new VCCodeFunction();
        var model = new VCFileCodeModel { GetElement = () => function };
        var projectModel = new VCCodeModel { IsSynchronized = true };
        var selection = new TextSelection { TopPoint = new(20, 18), BottomPoint = new(20, 18) };
        var document = new Document
        {
            FullName = "C:/Sample/Widget.cpp", Selection = selection,
            ProjectItem = new() { FileCodeModel = model, ContainingProject = new() { CodeModel = projectModel } },
        };
        var dte = new DTE2 { ActiveDocument = document };
        var provider = new VisualStudioSymbolNavigationProvider();
        var shell = new TestShell();
        Assert(!provider.CanHandle(null) && provider.CanHandle(document.FullName), "문서 존재 판정");
        Check(VSConstants.VSStd97CmdID.GotoDecl);
        Assert(model.Queries == 1, "활성 함수 한 곳만 조회");

        document.FullName = "C:/Sample/Widget.h";
        selection.TopPoint = selection.BottomPoint = new(5, 12);
        Check(VSConstants.VSStd97CmdID.GotoDefn);
        document.FullName = "C:/Sample/Widget.cpp";
        selection.TopPoint = selection.BottomPoint = new(21, 18);
        Check(VSConstants.VSStd97CmdID.GotoDefn);
        selection.TopPoint = new(20, 15);
        selection.BottomPoint = new(21, 18);
        var queries = model.Queries;
        Check(VSConstants.VSStd97CmdID.GotoDefn);
        Assert(model.Queries == queries, "여러 줄 선택은 Code Model 조회 생략");
        selection.TopPoint = selection.BottomPoint = new(20, 18);

        projectModel.IsSynchronized = false;
        Check(VSConstants.VSStd97CmdID.GotoDefn);
        Assert(model.Queries == queries, "분석 중에는 함수 조회나 완료 대기 없이 기본 명령 사용");
        projectModel.IsSynchronized = true;
        document.ProjectItem.ContainingProject.CodeModel = new object();
        Check(VSConstants.VSStd97CmdID.GotoDefn);
        Assert(model.Queries == queries, "다른 언어 모델은 C++ 함수 조회 생략");
        document.ProjectItem.ContainingProject.CodeModel = projectModel;

        model.GetElement = () => null;
        Check(VSConstants.VSStd97CmdID.GotoDefn);
        model.GetElement = () => throw new COMException("Code model unavailable");
        Check(VSConstants.VSStd97CmdID.GotoDefn);
        model.GetElement = () => throw new ArgumentException("Unsupported code element");
        Check(VSConstants.VSStd97CmdID.GotoDefn);
        model.GetElement = () => function;
        function.FailLocation = true;
        Check(VSConstants.VSStd97CmdID.GotoDefn);
        function.FailLocation = false;
        Check(VSConstants.VSStd97CmdID.GotoDecl);

        shell.Commands.Clear();
        shell.FailDeclaration = true;
        Assert(provider.TryNavigateToDefinition(dte, shell), "선언 명령 등록 거절 시 기본 정의 명령 복구");
        Assert(shell.Commands.SequenceEqual(new[] { 936u, 935u }), "복구 명령 순서");
        shell.FailDefinition = true;
        Assert(!provider.TryNavigateToDefinition(dte, shell), "두 명령 모두 거절하면 실패 보고");
        shell.FailDeclaration = shell.FailDefinition = false;
        for (var i = 0; i < 100; i++) Check(VSConstants.VSStd97CmdID.GotoDecl);
        dte.ActiveDocument = null;
        Check(VSConstants.VSStd97CmdID.GotoDefn);
        Console.WriteLine("PASS: 선언·정의 명령 라우팅, 분석 중·비 C++·COM 실패 복구 및 100회 반복 (SDK 대역)");

        void Check(VSConstants.VSStd97CmdID expected)
        {
            shell.Commands.Clear();
            Assert(provider.TryNavigateToDefinition(dte, shell), "탐색 명령 큐 등록");
            Assert(shell.Commands.SequenceEqual(new[] { (uint)expected }), "한 번에 올바른 명령 하나만 등록");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestShell : IVsUIShell
    {
        public List<uint> Commands { get; } = new();
        public bool FailDeclaration { get; set; }
        public bool FailDefinition { get; set; }
        public int PostExecCommand(ref Guid group, uint id, uint options, object? argument)
        {
            Assert(group == VSConstants.GUID_VSStandardCommandSet97 && options == 0 && argument is null,
                "문자열 인수 없이 표준 OLE 명령 등록");
            Commands.Add(id);
            return (id == 936 && FailDeclaration) || (id == 935 && FailDefinition) ? unchecked((int)0x80004005) : 0;
        }
    }
}

// 실제 공급자와 위치 읽기 코드를 연결하고 공개 SDK 경계만 최소 대역으로 바꿉니다.
// 실제 COM 모델의 위치 정확도와 VS 편집기의 명령 실행은 별도 호스트 검증 대상입니다.
namespace EnvDTE
{
    internal enum vsCMElement { vsCMElementFunction }
    internal enum vsCMPart { vsCMPartName, vsCMPartWhole }
    internal sealed class TextPoint
    {
        public TextPoint(int line, int column) { Line = line; LineCharOffset = column; }
        public int Line { get; }
        public int LineCharOffset { get; }
    }
    internal sealed class TextSelection
    {
        public TextPoint TopPoint { get; set; } = new(1, 1);
        public TextPoint BottomPoint { get; set; } = new(1, 1);
        public TextPoint ActivePoint => BottomPoint;
    }
    internal sealed class Document
    {
        public object? Selection { get; set; }
        public ProjectItem ProjectItem { get; set; } = new();
        public string FullName { get; set; } = "";
    }
    internal sealed class ProjectItem
    {
        public object? FileCodeModel { get; set; }
        public Project ContainingProject { get; set; } = new();
    }
    internal sealed class Project { public object? CodeModel { get; set; } public string UniqueName { get; set; } = "Sample"; }
}
namespace EnvDTE80
{
    internal sealed class DTE2 { public Document? ActiveDocument { get; set; } }
}
namespace Microsoft.VisualStudio.VCCodeModel
{
    internal enum vsCMWhere { vsCMWhereDeclaration, vsCMWhereDefinition }
    internal sealed class VCCodeModel { public bool IsSynchronized { get; set; } }
    internal sealed class VCFileCodeModel
    {
        public Func<object?> GetElement { get; set; } = () => null;
        public int Queries { get; private set; }
        public object? CodeElementFromPoint(TextPoint point, vsCMElement scope) { Queries++; return GetElement(); }
    }
    internal sealed class VCCodeFunction
    {
        public string Name { get; set; } = "Reset";
        public object Parent { get; set; } = new VCCodeClass();
        public bool IsTemplate { get; set; }
        public bool IsInjected { get; set; }
        public bool IsDefault { get; set; }
        public bool IsDelete { get; set; }
        public Func<vsCMPart, vsCMWhere, TextPoint>? StartReader { get; set; }
        public Func<vsCMPart, vsCMWhere, TextPoint>? EndReader { get; set; }
        public Func<vsCMWhere, string>? LocationReader { get; set; }
        public bool FailLocation { get; set; }
        public TextPoint get_StartPointOf(vsCMPart part, vsCMWhere where) => StartReader?.Invoke(part, where) ?? (where == vsCMWhere.vsCMWhereDeclaration ? new(5, 10) : new(20, 15));
        public TextPoint get_EndPointOf(vsCMPart part, vsCMWhere where) => EndReader?.Invoke(part, where) ?? (where == vsCMWhere.vsCMWhereDeclaration ? new(5, 16) : new(20, 21));
        public string get_Location(vsCMWhere where) => FailLocation ? throw new COMException("Missing location") :
            LocationReader?.Invoke(where) ?? (where == vsCMWhere.vsCMWhereDeclaration ? "C:/Sample/Widget.h" : "C:/Sample/Widget.cpp");
    }
    internal class VCCodeClass
    {
        public object? Parent { get; set; }
        public string FullName { get; set; } = "Widget";
        public bool IsTemplate { get; set; }
        public bool IsInjected { get; set; }
        public string get_Location(vsCMWhere where) => "C:/Sample/Widget.h";
        public TextPoint get_StartPointOf(vsCMPart part) => part == vsCMPart.vsCMPartName ? new(1, 7) : new(1, 1);
        public TextPoint get_EndPointOf(vsCMPart part) => new(3, 3);
    }
    internal sealed class VCCodeStruct : VCCodeClass { }
}
namespace Microsoft.VisualStudio
{
    internal static class VSConstants
    {
        public static readonly Guid GUID_VSStandardCommandSet97 = new("5efc7975-14bc-11cf-9b2b-00aa00573819");
        internal enum VSStd97CmdID { GotoDefn = 935, GotoDecl = 936 }
    }
    internal static class ErrorHandler { public static bool Succeeded(int result) => result >= 0; }
}
namespace Microsoft.VisualStudio.Shell
{
    internal static class ThreadHelper { public static void ThrowIfNotOnUIThread() { } }
}
namespace Microsoft.VisualStudio.Shell.Interop
{
    internal interface IVsUIShell { int PostExecCommand(ref Guid group, uint id, uint options, object? argument); }
}
