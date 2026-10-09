using System;
using System.IO;
using System.Linq;
using VisualBoost.Services;

/// <summary>
/// C++ 프로젝트 파일에서 검색 루트를 읽을 때 DTD(엔터티 확장)를 처리하지 않는지 VS와 같은 .NET Framework에서 확인합니다.
/// 프로젝트 파일은 받은 저장소의 내용이라 신뢰할 수 없는 입력입니다.
/// </summary>
internal static class ProjectXmlSafetyTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisualBoost.ProjectXml." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Inc"));
            var plain = Write(root, "Plain.vcxproj",
                "<Project><PropertyGroup><IncludePath>$(ProjectDir)Inc;$(IncludePath)</IncludePath></PropertyGroup></Project>");
            Check(Single(root, plain) == Path.Combine(root, "Inc"), "일반 프로젝트의 include 경로 읽기");

            // 엔터티 정의 없이 DOCTYPE만 있는 파일은 전처럼 읽습니다.
            var doctype = Write(root, "Doctype.vcxproj",
                "<!DOCTYPE Project><Project><PropertyGroup><IncludePath>Inc</IncludePath></PropertyGroup></Project>");
            Check(Single(root, doctype) == Path.Combine(root, "Inc"), "DOCTYPE만 있는 프로젝트 읽기");

            // 엔터티를 펼치지 않으므로 정의되지 않은 참조가 되어 이 프로젝트만 건너뜁니다(중첩 확장으로 메모리·시간을 쓰는 입력 차단).
            var entity = Write(root, "Entity.vcxproj",
                "<!DOCTYPE Project [<!ENTITY a \"Inc\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;\">]>" +
                "<Project><PropertyGroup><IncludePath>&a;</IncludePath><AdditionalIncludeDirectories>&b;</AdditionalIncludeDirectories></PropertyGroup></Project>");
            Check(CppProjectSearchRootLocator.Find(root, new[] { entity }).Count == 0, "DTD 엔터티를 펼치지 않음");
            Check(Single(root, plain, entity) == Path.Combine(root, "Inc"), "엔터티 프로젝트가 다른 프로젝트 읽기를 막지 않음");
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string? Single(string solutionDirectory, params string[] projects) =>
        CppProjectSearchRootLocator.Find(solutionDirectory, projects).SingleOrDefault();

    private static string Write(string directory, string name, string text)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
