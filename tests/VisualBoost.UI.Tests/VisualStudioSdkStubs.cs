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
        public Project? SubProject { get; set; }
        public ProjectItems? ProjectItems { get; set; }
        public string[] FileNames { get; set; } = new[] { "" };
        public short FileCount => (short)(FileNames.Length - 1);
    }
    internal sealed class Project
    {
        public object? CodeModel { get; set; }
        public string UniqueName { get; set; } = "Sample";
        public string Kind { get; set; } = "Cpp";
        public string FullName { get; set; } = @"C:\Fixture\Sample.vcxproj";
        public string Name { get; set; } = "Sample";
        public ProjectItems? ProjectItems { get; set; }
    }
}
namespace EnvDTE80
{
    internal sealed class DTE2 { public Document? ActiveDocument { get; set; } public Solution Solution { get; set; } = new(); }
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
        public static readonly Guid VSStd2K = new("1496a755-94de-11d0-8c3f-00c04fc2aae2");
        internal enum VSStd97CmdID { Cut = 16, Copy = 15, Paste = 26, Undo = 43, Redo = 29, MultiLevelUndo = 44, MultiLevelRedo = 30, SelectAll = 31, GotoDefn = 935, GotoDecl = 936 }
        // 경계 대역에서는 이름으로 매핑합니다. 실제 SDK의 명령 번호는 패키지 빌드가 결정합니다.
        internal enum VSStd2KCmdID
        {
            TYPECHAR, BACKSPACE, DELETE, DELETEWORDLEFT, DELETEWORDRIGHT, RETURN, CANCEL, TAB, BACKTAB,
            UP, DOWN, PAGEUP, PAGEDN, LEFT, RIGHT, LEFT_EXT, RIGHT_EXT, WORDPREV, WORDNEXT,
            WORDPREV_EXT, WORDNEXT_EXT, HOME, BOL, END, EOL, HOME_EXT, BOL_EXT, END_EXT, EOL_EXT
        }
    }
    internal static class ErrorHandler
    {
        public static bool Succeeded(int result) => result >= 0;
        public static void ThrowOnFailure(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    }
}
namespace Microsoft.VisualStudio.Shell
{
    internal static class ThreadHelper
    {
        public static void ThrowIfNotOnUIThread() { }
        public static TestThreadFactory JoinableTaskFactory { get; } = new();
    }
}
namespace Microsoft.VisualStudio.Shell.Interop
{
    internal interface IVsUIShell { int PostExecCommand(ref Guid group, uint id, uint options, object? argument); }
}

