using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VisualBoost.Core.Analysis;

namespace VisualBoost.Core.SemanticNavigation;

/// <summary>
/// 색인에 없는 정의(엔진 cpp, database 밖 프로젝트 cpp)를 확정할 때 열어 볼 후보 파일을 고릅니다.
/// </summary>
/// <remarks>
/// 후보 하나를 확인하는 데 대형 TU 분석(수 초)이 들므로, 이름 인덱스에서 같은 이름·소속의 함수가 있는 cpp를
/// 먼저 고르고 헤더와 이름이 같은 cpp를 보충합니다. 소속이 다른 같은 이름 함수는 제외합니다.
/// </remarks>
public static class DefinitionCandidates
{
    private static readonly HashSet<string> HeaderExtensions = new(StringComparer.OrdinalIgnoreCase) { ".h", ".hh", ".hpp", ".hxx", ".inl", ".ipp" };
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cpp", ".cc", ".cxx", ".c" };
    private static readonly Regex TypeOrMacroLine = new(
        @"^\s*(template\s*<.*>\s*)?((class|struct|union|enum|namespace|typedef|using)\b|#\s*define\b)",
        RegexOptions.CultureInvariant);
    private static readonly Regex DeletedOrPure = new(@"=\s*(0|delete)\s*;", RegexOptions.CultureInvariant);
    private static readonly Regex GeneratedEvent = new(@"\b(BlueprintImplementableEvent|BlueprintNativeEvent)\b", RegexOptions.CultureInvariant);

    public static bool IsHeader(string path) => HeaderExtensions.Contains(Path.GetExtension(path));

    public static bool IsSource(string path) => SourceExtensions.Contains(Path.GetExtension(path));

    /// <summary>위치의 줄이 타입·별칭·매크로 정의처럼 보이면 헤더가 곧 정의이므로 cpp를 찾지 않습니다.</summary>
    public static bool LooksLikeTypeOrMacro(string lineText) => TypeOrMacroLine.IsMatch(lineText);

    /// <summary>
    /// 선언이 소스 cpp에 본문이 없는 함수(순수 가상 <c>= 0</c>, <c>= delete</c>, 본문을 Unreal 코드 생성기가 만드는
    /// <c>BlueprintImplementableEvent</c>·<c>BlueprintNativeEvent</c>)처럼 보이면 true입니다. 후보 cpp를 열어도 정의가 없어
    /// 요청마다 대형 TU 분석 시간만 쓰므로 요청 시점 확정을 하지 않습니다.
    /// </summary>
    /// <param name="line">0부터 센 선언 줄입니다. 바로 위 <c>UFUNCTION(...)</c>은 앞 선언이 끝난 줄까지 최대 3줄 봅니다.</param>
    public static bool LooksLikeNoSourceBody(string text, int line)
    {
        var lines = text.Split('\n');
        if (line < 0 || line >= lines.Length) return false;
        if (DeletedOrPure.IsMatch(lines[line])) return true;
        for (var i = line - 1; i >= 0 && i >= line - 3; i--)
        {
            var previous = lines[i].Trim();
            if (previous.EndsWith(";", StringComparison.Ordinal) || previous.EndsWith("}", StringComparison.Ordinal) ||
                previous.EndsWith("{", StringComparison.Ordinal))
            {
                break;
            }

            if (GeneratedEvent.IsMatch(previous)) return true;
        }

        return false;
    }

    /// <param name="name">찾는 함수의 이름(소속 제외).</param>
    /// <param name="container">clangd가 알려 준 소속(<c>UPackage</c>, <c>UE::Foo::</c> 등). 없으면 빈 문자열.</param>
    /// <param name="headerPath">선언이 있는 헤더.</param>
    /// <param name="symbols">이름 인덱스에서 <paramref name="name"/>으로 찾은 위치.</param>
    /// <param name="sameStemFiles">헤더와 파일 이름이 같은 파일들.</param>
    public static IReadOnlyList<string> Select(string name, string container, string headerPath, IEnumerable<SourceSymbolLocation> symbols,
        IEnumerable<string> sameStemFiles, int max)
    {
        var owner = LastSegment(container);
        var headerDirectory = Path.GetDirectoryName(Path.GetFullPath(headerPath)) ?? string.Empty;
        var ranked = new List<(string Path, int Rank, int Shared)>();
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != SourceSymbolKind.Function || !IsSource(symbol.Path) || LastSegment(symbol.Name) != name)
            {
                continue;
            }

            var scope = LastSegment(symbol.Scope.Length > 0 ? symbol.Scope : Qualifier(symbol.Name));
            int rank;
            if (owner.Length == 0)
            {
                rank = scope.Length == 0 ? 0 : 2;
            }
            else if (string.Equals(scope, owner, StringComparison.Ordinal))
            {
                rank = 0;
            }
            else if (scope.Length == 0)
            {
                rank = 1;
            }
            else
            {
                // 다른 클래스의 같은 이름 함수는 확인 비용만 늘립니다.
                continue;
            }

            ranked.Add((symbol.Path, rank, SharedPrefix(headerDirectory, symbol.Path)));
        }

        var stem = Path.GetFileNameWithoutExtension(headerPath);
        foreach (var file in sameStemFiles)
        {
            if (IsSource(file) && string.Equals(Path.GetFileNameWithoutExtension(file), stem, StringComparison.OrdinalIgnoreCase))
            {
                ranked.Add((file, 3, SharedPrefix(headerDirectory, file)));
            }
        }

        return ranked
            .OrderBy(r => r.Rank)
            .ThenByDescending(r => r.Shared)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(r => Path.GetFullPath(r.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToArray();
    }

    /// <summary>
    /// 자체 이름 색인에서 선언과 소속·이름이 같은 함수 정의(소스 파일)가 하나로 정해지면 그 위치입니다. 여럿이면(오버로드, 조건 갈래별 정의)
    /// 선언의 매개변수 수로 좁히고, 그래도 하나가 아니면 null입니다. 색인이 오래되어 그 자리에 이름이 없으면 쓰지 않습니다.
    /// </summary>
    /// <remarks>
    /// 후보 cpp를 clangd로 열어 확정하면 대형 엔진 TU 하나에 2~17초가 들었습니다(2026-10-09 측정, <c>World.cpp</c> 16.7초). 소속까지 같은
    /// 정의가 하나뿐이면 clangd가 확정해도 같은 위치이므로 분석을 건너뜁니다. 이름 색인은 의미 분석이 아니므로 틀린 곳으로 가지 않는 쪽을
    /// 택합니다: 소속은 한쪽이 다른 쪽의 뒷부분(<c>using namespace</c>로 줄여 쓴 정의)일 때만 같다고 보고, 소속 없는 함수는 정의 파일이
    /// 선언 헤더를 직접 include할 때만 씁니다(다른 파일의 같은 이름 <c>static</c>·익명 네임스페이스 함수 배제).
    /// </remarks>
    /// <param name="container">clangd가 알려 준 소속입니다. 없으면 소속 없는 정의만 봅니다.</param>
    /// <param name="headerPath">선언이 있는 파일입니다.</param>
    /// <param name="declaration">선언 위치부터의 글(매개변수 목록 포함)입니다. 모르면 null이며, 그때는 오버로드를 좁히지 않습니다.</param>
    /// <param name="readText">정의 파일의 현재 내용입니다. 읽지 못하면 null을 돌려줍니다.</param>
    public static SourceSymbolLocation? UniqueDefinition(string name, string container, string headerPath, IEnumerable<SourceSymbolLocation> symbols,
        string? declaration, Func<string, string?> readText)
    {
        var owner = container.TrimEnd(':');
        var matches = symbols
            .Where(s => s.Kind == SourceSymbolKind.Function && IsSource(s.Path) && LastSegment(s.Name) == name &&
                        SameOwner(owner, s.Scope.Length > 0 ? s.Scope : Qualifier(s.Name)))
            .GroupBy(s => (Path.GetFullPath(s.Path).ToUpperInvariant(), s.Line))
            .Select(g => g.First())
            .ToList();
        if (matches.Count > 1 && declaration is not null && ParameterCount(declaration) is { } wanted)
        {
            matches = matches.Where(s => ParameterCount(s.Signature) == wanted).ToList();
            // 매개변수 수가 같은 오버로드(FString·FStringView 판 등)는 이름을 뺀 매개변수 형식과 const 멤버 여부로 더 좁힙니다. 선언과 정의를 다르게
            // 적은 형식(형식 별칭, 이름 없는 매개변수)은 맞지 않아 빠지므로 틀린 곳으로 가지 않고 clangd 확정으로 넘어갑니다.
            if (matches.Count > 1 && Shape(declaration) is { } shape)
            {
                matches = matches.Where(s => Shape(s.Signature) is { } other && other.Const == shape.Const &&
                                             other.Types.SequenceEqual(shape.Types, StringComparer.Ordinal)).ToList();
            }
        }

        if (matches.Count != 1) return null;
        var found = matches[0];
        var text = readText(found.Path);
        if (text is null || owner.Length == 0 && !Includes(text, Path.GetFileName(headerPath))) return null;
        var line = SourceLinePreview.LineAt(text, found.Line - 1);
        var start = found.Column - 1;
        return start >= 0 && start + name.Length <= line.Length && string.CompareOrdinal(line, start, name, 0, name.Length) == 0 &&
               (start == 0 || !IsWordChar(line[start - 1])) && (start + name.Length == line.Length || !IsWordChar(line[start + name.Length]))
            ? found
            : null;
    }

    /// <summary>
    /// 소속이 같으면 true입니다. 정의가 <c>using namespace</c> 아래에서 줄여 쓴 소속(<c>FImpl</c>)이나 clangd가 줄여 알린 소속도 받도록
    /// 한쪽이 다른 쪽의 <c>::</c> 경계 뒷부분이면 같다고 봅니다. 마지막 이름만 비교하면 다른 네임스페이스의 같은 이름 클래스가 섞입니다.
    /// </summary>
    public static bool SameOwner(string expected, string actual)
    {
        actual = actual.TrimEnd(':');
        if (expected.Length == 0 || actual.Length == 0) return expected.Length == actual.Length;
        var (longer, shorter) = expected.Length >= actual.Length ? (expected, actual) : (actual, expected);
        return longer.EndsWith(shorter, StringComparison.Ordinal) &&
               (longer.Length == shorter.Length || longer.Substring(0, longer.Length - shorter.Length).EndsWith("::", StringComparison.Ordinal));
    }

    /// <summary>글에 <paramref name="fileName"/>을(경로 앞부분은 무관) include하는 줄이 있으면 true입니다.</summary>
    private static bool Includes(string text, string fileName) =>
        Regex.IsMatch(text, @"^[ \t]*#[ \t]*include[ \t]*[<""](?:[^<>""\r\n]*[/\\])?" + Regex.Escape(fileName) + @"[>""]",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 글에서 처음 나오는 괄호 목록의 매개변수 수입니다. 비었거나 <c>void</c>면 0입니다. 괄호가 닫히지 않으면 null입니다. 템플릿 인수(&lt;…&gt;)와
    /// 안쪽 괄호의 쉼표는 세지 않습니다.
    /// </summary>
    public static int? ParameterCount(string text) => ParameterList(text)?.Parameters.Count;

    /// <summary>
    /// 글에서 처음 나오는 괄호 목록의 매개변수 형식(기본값과 끝의 매개변수 이름을 빼고 공백을 정리한 것)과, 닫는 괄호 뒤가 <c>const</c>인지입니다.
    /// 괄호가 닫히지 않으면 null입니다.
    /// </summary>
    public static (IReadOnlyList<string> Types, bool Const)? Shape(string text) =>
        ParameterList(text) is { } list ? (list.Parameters.Select(TypeOf).ToArray(), ConstSuffix.IsMatch(list.After)) : null;

    private static readonly Regex StringOrComment = new(@"""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])*'|/\*.*?\*/|//[^\n]*",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex Token = new(@"[A-Za-z_]\w*|::|\S", RegexOptions.CultureInvariant);
    private static readonly Regex ConstSuffix = new(@"^\s*const\b", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> TypeKeywords = new(StringComparer.Ordinal)
    {
        "const", "volatile", "signed", "unsigned", "short", "long", "int", "char", "wchar_t", "char8_t", "char16_t", "char32_t", "bool", "float",
        "double", "void", "auto"
    };

    /// <summary>매개변수(원문, 앞뒤 공백 제거)들과 닫는 괄호 뒤의 글입니다. 문자열·문자 상수는 비우고 주석은 공백으로 바꾼 뒤 나눕니다.</summary>
    private static (IReadOnlyList<string> Parameters, string After)? ParameterList(string text)
    {
        text = StringOrComment.Replace(text, m => m.Value[0] is '"' or '\'' ? m.Value.Substring(0, 1) + m.Value.Substring(0, 1) : " ");
        var open = text.IndexOf('(');
        if (open < 0) return null;
        var depth = 0;
        var angle = 0;
        var start = open + 1;
        var parameters = new List<string>();
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (--depth > 0) continue;
                parameters.Add(text.Substring(start, i - start).Trim());
                if (parameters.Count == 1 && (parameters[0].Length == 0 || parameters[0] == "void")) parameters.Clear();
                return (parameters, text.Substring(i + 1));
            }
            else if (depth == 1 && c == '<')
            {
                angle++;
            }
            else if (depth == 1 && c == '>' && angle > 0)
            {
                angle--;
            }
            else if (depth == 1 && angle == 0 && c == ',')
            {
                parameters.Add(text.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }

        return null;
    }

    /// <summary>
    /// 매개변수의 형식입니다. 기본값(맨 바깥 <c>=</c> 뒤)과 끝의 매개변수 이름을 빼고, 이름 사이 공백만 남깁니다. 끝 이름은 앞에 다른 낱말이 있고
    /// 기본 형식 키워드가 아니며 <c>::</c> 뒤가 아닐 때만 이름으로 봅니다(<c>unsigned int</c>, <c>UE::FName</c>).
    /// </summary>
    private static string TypeOf(string parameter)
    {
        var depth = 0;
        for (var i = 0; i < parameter.Length; i++)
        {
            var c = parameter[i];
            if (c is '(' or '[' or '{' or '<') depth++;
            else if (c is ')' or ']' or '}' or '>') depth--;
            else if (c == '=' && depth == 0)
            {
                parameter = parameter.Substring(0, i);
                break;
            }
        }

        var tokens = Token.Matches(parameter).Cast<Match>().Select(m => m.Value).ToList();
        var last = tokens.Count - 1;
        if (tokens.Count >= 2 && IsWordChar(tokens[last][0]) && !char.IsDigit(tokens[last][0]) && !TypeKeywords.Contains(tokens[last]) && tokens[last - 1] != "::")
        {
            tokens.RemoveAt(last);
        }

        var type = new System.Text.StringBuilder();
        for (var i = 0; i < tokens.Count; i++)
        {
            var previous = i > 0 ? tokens[i - 1] : string.Empty;
            if (previous.Length > 0 && IsWordChar(previous[previous.Length - 1]) && IsWordChar(tokens[i][0])) type.Append(' ');
            type.Append(tokens[i]);
        }

        return type.ToString();
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string LastSegment(string name)
    {
        var trimmed = name.TrimEnd(':');
        var index = trimmed.LastIndexOf("::", StringComparison.Ordinal);
        return index < 0 ? trimmed : trimmed.Substring(index + 2);
    }

    private static string Qualifier(string name)
    {
        var index = name.LastIndexOf("::", StringComparison.Ordinal);
        return index < 0 ? string.Empty : name.Substring(0, index);
    }

    /// <summary>같은 모듈 안의 후보를 앞세우기 위한 공통 경로 길이입니다.</summary>
    private static int SharedPrefix(string directory, string path)
    {
        var other = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var length = Math.Min(directory.Length, other.Length);
        var shared = 0;
        for (var i = 0; i < length && char.ToUpperInvariant(directory[i]) == char.ToUpperInvariant(other[i]); i++)
        {
            shared++;
        }

        return shared;
    }
}
