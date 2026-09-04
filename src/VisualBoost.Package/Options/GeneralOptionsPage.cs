using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using VisualBoost.Core.FilePairing;

namespace VisualBoost.Options;

[Guid("302ea02e-d82f-41ac-a785-cbf1d5fb1d9f")]
public sealed class GeneralOptionsPage : DialogPage
{
    [Category("C++ 파일 전환")]
    [DisplayName("헤더 확장자")]
    [Description("세미콜론으로 구분합니다. 점은 생략할 수 있습니다.")]
    [DefaultValue(".h;.hpp;.hh;.hxx;.inl")]
    public string HeaderExtensions { get; set; } = ".h;.hpp;.hh;.hxx;.inl";

    [Category("C++ 파일 전환")]
    [DisplayName("구현 확장자")]
    [Description("세미콜론으로 구분합니다. 점은 생략할 수 있습니다.")]
    [DefaultValue(".cpp;.cc;.cxx;.c")]
    public string SourceExtensions { get; set; } = ".cpp;.cc;.cxx;.c";

    [Category("C++ 파일 전환")]
    [DisplayName("대응 디렉터리")]
    [Description("헤더=구현 형식을 세미콜론으로 구분합니다. 예: include=src;inc=source")]
    [DefaultValue("include=src;include=source;inc=src;headers=source")]
    public string DirectoryPairs { get; set; } = "include=src;include=source;inc=src;headers=source";

    [Category("진단")]
    [DisplayName("탐색 실패 시 인덱스 크기 표시")]
    [Description("대응 파일을 찾지 못했을 때 상태 표시줄에 인덱싱된 파일 개수를 표시합니다.")]
    [DefaultValue(true)]
    public bool ShowIndexCountOnFailure { get; set; } = true;

    internal FilePairingOptions CreateFilePairingOptions() =>
        new(ParseList(HeaderExtensions), ParseList(SourceExtensions), ParseDirectoryPairs(DirectoryPairs));

    private static IReadOnlyList<string> ParseList(string value) =>
        (value ?? string.Empty)
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToArray();

    private static IReadOnlyList<(string HeaderDirectory, string SourceDirectory)> ParseDirectoryPairs(string value)
    {
        var results = new List<(string HeaderDirectory, string SourceDirectory)>();
        foreach (var item in ParseList(value))
        {
            var separator = item.IndexOf('=');
            if (separator <= 0 || separator >= item.Length - 1)
            {
                continue;
            }

            results.Add((item.Substring(0, separator).Trim(), item.Substring(separator + 1).Trim()));
        }

        return results;
    }
}
