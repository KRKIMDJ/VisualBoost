using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>
/// 모르는 이름을 알린 진단의 위치입니다. <see cref="Name"/>이 있으면 위치 대신 그 이름을 씁니다(불완전 타입 진단). 위치가 이름이 아니면
/// <see cref="Fallback"/>(진단의 첫 관련 정보 위치)을 봅니다.
/// </summary>
public sealed class DiagnosticNameSite
{
    public DiagnosticNameSite(string path, int line, int start, int end, string? name = null, DiagnosticNameSite? fallback = null)
    {
        Path = path;
        Line = line;
        Start = start;
        End = end;
        Name = name;
        Fallback = fallback;
    }

    public DiagnosticNameSite? Fallback { get; }

    public string Path { get; }

    /// <summary>줄(0부터)입니다.</summary>
    public int Line { get; }

    /// <summary>줄 안 시작·끝 위치(UTF-16, LSP 기본)입니다.</summary>
    public int Start { get; }

    public int End { get; }

    public string? Name { get; }
}

/// <summary>
/// Unreal 공유 PCH 대신 넣을 헤더를 고릅니다. PCH 없이 분석한 문서·색인 단위의 진단에서 모르는 이름을 꺼내고, 자체 이름 색인에서 그 이름을
/// 정의한 헤더를 찾습니다.
/// </summary>
/// <remarks>
/// 공유 PCH는 엔진 헤더 대부분(UnrealEd 단계 약 1.6 GB)을 넣어 주므로 PCH에 기대는 소스는 PCH 없이 분석하면 실패합니다. 실제 Unreal
/// 프로젝트에서는 PCH가 필요한 문서·색인 단위 대부분이 프로젝트 헤더 몇 곳의 include 누락 때문이었고(TU 82개 중 PCH 단위 30개, 엔진 헤더
/// 2개로 대부분 해결), 그 헤더만 넣으면 문서 하나가 1.75~2.0 GB·11~14초에서 0.8~1.2 GB·4.6~7.3초가 되었습니다(2026-10-09 측정).
/// clangd 진단은 이름을 메시지 대신 위치에서 읽습니다. 다른 파일(include한 헤더)에서 난 오류는 clangd가 첫 관련 정보(relatedInformation)로
/// 실제 위치를 줍니다. 불완전 타입은 위치가 식이라 메시지의 첫 따옴표 안 타입을 씁니다. 색인 단위는 컴파일러 출력에서 읽습니다
/// (<see cref="NamesInCompilerOutput"/>). 찾지 못하면 호출자가 공유 PCH로 돌아가므로 결과는 PCH와 같고 비용만 다릅니다.
/// </remarks>
public static class IncludeSupplements
{
    /// <summary>모듈마다 기억할 보충 헤더 수 상한입니다. 넘으면 그 모듈은 공유 PCH에 크게 기대는 것으로 보고 더 보충하지 않습니다.</summary>
    public const int MaxHeadersPerModule = 32;

    /// <summary>한 번에 찾을 이름 수 상한입니다.</summary>
    public const int MaxNames = 64;

    // 위치의 낱말이 모르는 이름인 진단(clang 진단 이름, clangd가 code로 보냄)입니다. 경고 옵션이 있는 진단은 옵션 이름으로 옵니다.
    private static readonly HashSet<string> NameCodes = new(StringComparer.Ordinal)
    {
        "undeclared_var_use", "undeclared_var_use_suggest", "unknown_typename", "unknown_typename_suggest", "unknown_type_or_class_name_suggest",
        "no_template", "no_template_suggest", "undeclared_use", "-Wundef", "pp_undef_identifier"
    };

    // 타입이 전방 선언만 되어 불완전하다는 진단입니다. 위치는 식이라 메시지의 타입 이름을 씁니다.
    // 앞선언만 된 클래스 템플릿을 값으로 쓴 오류(implicit instantiation of undefined template)도 같은 방식으로 템플릿 이름을 씁니다.
    private static readonly HashSet<string> IncompleteTypeCodes = new(StringComparer.Ordinal)
    {
        "incomplete_member_access", "typecheck_incomplete_tag", "incomplete_type", "typecheck_decl_incomplete_type", "incomplete_base_class",
        "incomplete_nested_name_spec", "call_incomplete_argument", "call_incomplete_return", "call_function_incomplete_return",
        "typecheck_nonviable_condition_incomplete", "template_instantiate_undefined"
    };

    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly Regex QuotedType = new(@"'([^']+)'", RegexOptions.CultureInvariant);

    // clang-cl 출력의 오류 줄(파일(줄,열): error: 메시지, clang 형식 파일:줄:열: 도 받음)입니다.
    private static readonly Regex CompilerError = new(@"^.*?(?:\(\d+,\d+\)|:\d+:\d+)\s*:\s*(?:fatal\s+)?error\s*:\s*(.*?)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    // 메시지의 이름 자리입니다. NameCodes 진단의 clang 메시지 문구이며 clang 여러 판에서 같습니다. 클래스 멤버(템플릿)가 없다는 오류는
    // 헤더 누락이 아니므로 네임스페이스 멤버만 받습니다(클래스는 " in 'UClass'", 네임스페이스는 " in namespace 'N'").
    private static readonly Regex CompilerName = new(
        @"^(?:use of undeclared identifier|unknown type name) '(?<name>[A-Za-z_]\w*)'|^no template named '(?<name>[A-Za-z_]\w*)'(?! in ')|" +
        @"^no member named '(?<name>[A-Za-z_]\w*)' in namespace '|^'(?<name>[A-Za-z_]\w*)' is not defined, evaluates to 0",
        RegexOptions.CultureInvariant);

    // 불완전 타입 문구(IncompleteTypeCodes): "… incomplete type 'X'", "incomplete definition of type 'X'", "instantiation of undefined template 'X<…>'".
    private static readonly Regex CompilerIncompleteType = new(
        @"incomplete (?:\w+ )?type ('[^']+')|incomplete definition of type ('[^']+')|instantiation of undefined template ('[^']+')", RegexOptions.CultureInvariant);

    /// <summary>
    /// JSON 진단 배열에서 모르는 이름의 위치를 꺼냅니다. 진단 위치가 이 문서의 낱말이 아니면(include 줄에 붙은 다른 파일의 오류) 첫 관련 정보의
    /// 위치를 씁니다.
    /// </summary>
    /// <param name="documentPath">진단을 받은 문서 경로입니다.</param>
    /// <param name="toPath">URI를 경로로 바꿉니다. 바꿀 수 없으면 null입니다.</param>
    public static IReadOnlyList<DiagnosticNameSite> Sites(IReadOnlyList<JsonValue> diagnostics, string documentPath, Func<string, string?> toPath)
    {
        var sites = new List<DiagnosticNameSite>();
        foreach (var diagnostic in diagnostics)
        {
            var code = diagnostic["code"].AsString();
            if (code is null) continue;
            var incomplete = IncompleteTypeCodes.Contains(code);
            if (!incomplete && !NameCodes.Contains(code)) continue;
            if (incomplete)
            {
                if (TypeNameIn(diagnostic["message"].AsString()) is { } type) sites.Add(new DiagnosticNameSite(documentPath, 0, 0, 0, type));
                continue;
            }

            var related = diagnostic["relatedInformation"].Items.FirstOrDefault();
            var fallback = related is not null && related["location"]["uri"].AsString() is string uri && toPath(uri) is string path
                ? SiteOf(path, related["location"]["range"], null)
                : null;
            sites.Add(SiteOf(documentPath, diagnostic["range"], fallback));
        }

        return sites;
    }

    /// <summary>
    /// 위치에서 이름을 읽습니다. 진단 위치가 이름이 아니면(include 줄) 첫 관련 정보 위치를 씁니다. 문서 위치가 이름이면 관련 정보(추천 이름의
    /// 선언 위치 등)는 보지 않습니다. 파일은 한 번씩만 읽고, 위치가 낱말 전체일 때만 이름으로 봅니다.
    /// </summary>
    /// <param name="readText">파일 내용을 읽습니다. 진단을 받은 문서는 clangd에 보낸 내용을 줘야 합니다(저장하지 않은 편집). 읽지 못하면 null입니다.</param>
    public static IReadOnlyList<string> Names(IEnumerable<DiagnosticNameSite> sites, Func<string, string?> readText)
    {
        var texts = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        string? Read(DiagnosticNameSite site)
        {
            if (site.Name is not null) return site.Name;
            if (!texts.TryGetValue(site.Path, out var lines))
            {
                lines = readText(site.Path)?.Split('\n');
                texts[site.Path] = lines;
            }

            if (lines is null || site.Line < 0 || site.Line >= lines.Length) return null;
            var line = lines[site.Line];
            if (site.Start < 0 || site.End <= site.Start || site.End > line.Length) return null;
            // 낱말 전체여야 합니다. 내용이 진단과 어긋나면(진단 뒤 편집) 긴 이름의 일부가 이름처럼 보일 수 있습니다.
            if (site.Start > 0 && IsWordChar(line[site.Start - 1]) || site.End < line.Length && IsWordChar(line[site.End])) return null;
            return line.Substring(site.Start, site.End - site.Start);
        }

        foreach (var site in sites)
        {
            var name = Read(site);
            if ((name is null || !Identifier.IsMatch(name)) && site.Fallback is { } fallback) name = Read(fallback);
            if (name is not null && Identifier.IsMatch(name) && !names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
                if (names.Count >= MaxNames) break;
            }
        }

        return names;
    }

    /// <summary>
    /// clang-cl 출력의 오류 줄에서 모르는 이름을 꺼냅니다(나온 순서, 중복 제외, <see cref="MaxNames"/>까지). 선언되지 않은 이름·모르는 타입·
    /// 템플릿·네임스페이스 멤버, 정의되지 않은 조건 매크로(<c>-Wundef</c> 오류)는 메시지의 첫 따옴표 안 이름을, 불완전 타입은 그 타입 이름을
    /// 씁니다. 번진 오류(변환 실패 등)와 경고·참고 줄은 보지 않습니다.
    /// </summary>
    public static IReadOnlyList<string> NamesInCompilerOutput(string output)
    {
        var names = new List<string>();
        foreach (Match line in CompilerError.Matches(output))
        {
            var message = line.Groups[1].Value;
            var name = CompilerName.Match(message) is { Success: true } named ? named.Groups["name"].Value
                : CompilerIncompleteType.Match(message) is { Success: true } incomplete
                    ? TypeNameIn(incomplete.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value)
                    : null;
            if (name is null || names.Contains(name, StringComparer.Ordinal)) continue;
            names.Add(name);
            if (names.Count >= MaxNames) break;
        }

        return names;
    }

    /// <summary>
    /// 이름을 정의한 헤더를 자체 이름 색인에서 고릅니다. 명령의 include 폴더 안에 있는 헤더만 후보로 보며, 한 이름에 후보가 둘 이상이면 고르지
    /// 않습니다(다른 모듈의 같은 이름 타입을 넣으면 오히려 오류가 날 수 있음). 처음 나온 순서로 돌려줍니다.
    /// </summary>
    /// <remarks>
    /// 타입 후보가 있으면 타입만 봅니다. 이름 색인은 어휘 수준이라 멤버 선언의 정교한 타입 지정자(<c>TEnumAsByte&lt;enum EPhysicalSurface&gt;</c>)를
    /// 열거형으로, 다른 모듈의 같은 이름 별칭(<c>using FTimerHandle = …</c>)을 변수로 기록하므로(UE 5.8 확인), 타입 후보는 그 줄이 실제 정의의
    /// 머리(줄 앞의 class·struct·union·enum 뒤 이름, 같은 줄에서 <c>;</c>로 끝나지 않음)인지 파일에서 확인합니다.
    /// </remarks>
    /// <param name="readText">후보 헤더의 내용을 읽습니다. 읽지 못하면 null입니다.</param>
    public static IReadOnlyList<string> Resolve(IEnumerable<string> names, Func<string, IReadOnlyList<SourceSymbolLocation>> find,
        IReadOnlyCollection<string> includeDirectories, Func<string, string?> readText)
    {
        var texts = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);
        bool DefinesType(SourceSymbolLocation location)
        {
            if (!texts.TryGetValue(location.Path, out var lines))
            {
                lines = readText(location.Path)?.Split('\n');
                texts[location.Path] = lines;
            }

            return lines is not null && location.Line >= 1 && location.Line <= lines.Length && TypeHead(location.Name).IsMatch(lines[location.Line - 1]);
        }

        var headers = new List<string>();
        foreach (var name in names)
        {
            var visible = find(name)
                .Where(l => string.Equals(l.Name, name, StringComparison.Ordinal) && Supplies(l.Kind) && DefinitionCandidates.IsHeader(l.Path) &&
                            includeDirectories.Any(d => Normalize(l.Path).StartsWith(d, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var types = visible.Where(l => IsType(l.Kind)).ToArray();
            var pool = types.Length > 0 ? types.Where(DefinesType) : visible;
            var candidates = pool.Select(l => Normalize(l.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            if (candidates.Length == 1 && !headers.Contains(candidates[0], StringComparer.OrdinalIgnoreCase)) headers.Add(candidates[0]);
        }

        return headers;
    }

    // 타입 정의 머리: 줄 앞의 키워드, API 매크로·alignas 같은 낱말, 이름, 그 뒤 final·기반 목록(:)만 오고 {나 줄 끝(주석 허용)으로 끝남.
    // 전방 선언(;), 반환·매개변수 타입의 정교한 지정자(class UWorld* GetWorld()), 다른 클래스의 멤버 정의(Outer::)는 받지 않습니다.
    private static Regex TypeHead(string name) => new(
        @"^\s*(?:template\s*<[^>]*>\s*)?(?:class|struct|union|enum(?:\s+(?:class|struct))?)\s+(?:[A-Za-z_]\w*(?:\([^)]*\))?\s+)*" + Regex.Escape(name) +
        @"\s*(?:final\s*)?(?::(?!:)[^;{]*)?(?:\{.*|//.*)?$", RegexOptions.CultureInvariant);

    private static bool IsType(SourceSymbolKind kind) =>
        kind is SourceSymbolKind.Class or SourceSymbolKind.Struct or SourceSymbolKind.Union or SourceSymbolKind.Enum or SourceSymbolKind.Type;

    /// <summary>명령의 include 폴더입니다(절대 경로, 슬래시, 끝에 슬래시). 상대 경로는 명령 폴더 기준입니다.</summary>
    public static IReadOnlyList<string> IncludeDirectories(CompileCommand command)
    {
        var result = new List<string>();
        var arguments = command.Arguments;
        void Add(string value)
        {
            if (value.Length == 0) return;
            try
            {
                var full = Normalize(Path.IsPathRooted(value) ? value : Path.Combine(command.Directory, value)).TrimEnd('/') + "/";
                if (!result.Contains(full, StringComparer.OrdinalIgnoreCase)) result.Add(full);
            }
            catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
            {
                // 경로로 바꿀 수 없는 인자는 include 폴더가 아닙니다.
            }
        }

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument is "/I" or "-I" or "/external:I" or "-isystem" or "/imsvc" or "-imsvc")
            {
                if (i + 1 < arguments.Count) Add(arguments[++i]);
            }
            else if ((argument.StartsWith("/I", StringComparison.Ordinal) || argument.StartsWith("-I", StringComparison.Ordinal)) && argument.Length > 2)
            {
                Add(argument.Substring(2));
            }
        }

        return result;
    }

    /// <summary>
    /// 명령 인자의 마지막 강제 include(모듈 정의 헤더) 뒤에 헤더를 강제 include로 넣습니다. 엔진 헤더가 모듈 API 매크로 정의를 먼저 봐야
    /// 하기 때문입니다. 강제 include가 없으면 끝에 붙입니다. 이미 있는 헤더는 넣지 않습니다.
    /// </summary>
    /// <param name="head">파일 경로를 뺀 명령 인자입니다.</param>
    public static IReadOnlyList<string> WithForcedIncludes(IReadOnlyList<string> head, IEnumerable<string> headers)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var at = head.Count;
        for (var i = 0; i + 3 < head.Count; i++)
        {
            if (head[i] == "-Xclang" && head[i + 1] == "-include" && head[i + 2] == "-Xclang")
            {
                existing.Add(Normalize(head[i + 3]));
                at = i + 4;
            }
        }

        var extra = new List<string>();
        foreach (var header in headers)
        {
            if (existing.Add(Normalize(header))) extra.AddRange(new[] { "-Xclang", "-include", "-Xclang", header });
        }

        if (extra.Count == 0) return head;
        return head.Take(at).Concat(extra).Concat(head.Skip(at)).ToArray();
    }

    private static DiagnosticNameSite SiteOf(string path, JsonValue range, DiagnosticNameSite? fallback) =>
        new(path, range["start"]["line"].AsInt32() ?? -1, range["start"]["character"].AsInt32() ?? -1,
            range["end"]["line"].AsInt32() == range["start"]["line"].AsInt32() ? range["end"]["character"].AsInt32() ?? -1 : -1, fallback: fallback);

    /// <summary>불완전 타입 진단 메시지의 첫 따옴표 안 타입에서 이름(한정자·템플릿 인수·포인터 제외)을 꺼냅니다.</summary>
    public static string? TypeNameIn(string? message)
    {
        if (message is null || QuotedType.Match(message) is not { Success: true } match) return null;
        var type = match.Groups[1].Value;
        var angle = type.IndexOf('<');
        if (angle >= 0) type = type.Substring(0, angle);
        type = type.Replace("const ", string.Empty).Replace("volatile ", string.Empty).Replace("struct ", string.Empty).Replace("class ", string.Empty)
            .Replace("enum ", string.Empty).Replace("union ", string.Empty).TrimEnd('*', '&', ' ').Trim();
        var scope = type.LastIndexOf("::", StringComparison.Ordinal);
        if (scope >= 0) type = type.Substring(scope + 2);
        return Identifier.IsMatch(type) ? type : null;
    }

    private static bool IsWordChar(char c) => c == '_' || char.IsLetterOrDigit(c);

    private static bool Supplies(SourceSymbolKind kind) => kind is SourceSymbolKind.Class or SourceSymbolKind.Struct or SourceSymbolKind.Union or
        SourceSymbolKind.Enum or SourceSymbolKind.Type or SourceSymbolKind.Function or SourceSymbolKind.Variable or SourceSymbolKind.Macro;

    private static string Normalize(string path) => Path.GetFullPath(path).Replace('\\', '/');
}
