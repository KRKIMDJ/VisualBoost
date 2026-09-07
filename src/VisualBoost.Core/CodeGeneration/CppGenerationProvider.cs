using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.DocumentNavigation;

namespace VisualBoost.Core.CodeGeneration;

// 탐색용 인덱스가 아니라 언어 모델이 확인한 원문 범위만 변환합니다.
// 지원하지 않는 구문은 추측해서 편집하지 않고 명시적으로 거부합니다.
public sealed partial class CppGenerationProvider
{
    public void ValidateSource(string source, GenerationFunction function, CancellationToken token = default)
    {
        var code = CheckDocument(source, token);
        Require(QualifiedName.IsMatch(function.Owner), "비템플릿 클래스 멤버만 생성할 수 있습니다.");
        ReadHeader(source, code, function);
    }
    private static readonly Regex QualifiedName = new(@"^[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*$", RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> PrimitiveWords = new(new[] { "void", "bool", "char", "wchar_t", "char8_t", "char16_t", "char32_t", "short", "int", "long", "signed", "unsigned", "float", "double" }, StringComparer.Ordinal);

    public GenerationPlan Create(string source, GenerationFunction function, string target, GenerationDirection direction,
        string headerName, GenerationClass? targetClass = null, string access = "private", CancellationToken token = default)
    {
        var sourceCode = CheckDocument(source, token);
        var targetCode = CheckDocument(target, token);
        Require(QualifiedName.IsMatch(function.Owner), "비템플릿 클래스 멤버만 생성할 수 있습니다.");
        Require(direction == (function.IsDefinition ? GenerationDirection.Declaration : GenerationDirection.Definition), "현재 함수에 적용할 생성 방향이 달라졌습니다.");
        var header = ReadHeader(source, sourceCode, function);
        var newline = target.Contains("\r\n") ? "\r\n" : "\n";
        var unit = Regex.IsMatch(target, @"(?m)^\t+\S") ? "\t" : "    ";
        var warning = "원본은 자동 저장하지 않습니다. 생성 후 본문·접근 수준·include 의존성을 검토하세요.";
        if (direction == GenerationDirection.Definition)
        {
            Require(!Regex.IsMatch(targetCode, @"(?m)^\s*#\s*if"), "조건부 컴파일로 감싼 구현 파일에는 정의를 추가하지 않습니다.");
            // PCH·간접 include·언어 모델의 헤더 표기와 무관하게 코드 틀을 생성합니다.
            // 의존성을 추측해 include를 추가하지 않고 생성 후 상태 안내로 검토 필요성을 알립니다.
            warning = "include 여부는 검사하지 않습니다. 필요한 헤더 의존성은 직접 추가하세요. " + warning;
            CheckDuplicate(target, targetCode, function, header, 0, target.Length, token);
            var name = function.Owner + "::" + function.Name;
            // 한정 함수의 trailing return 영역은 클래스의 중첩 반환형을 올바른 범위에서 찾습니다.
            var signature = header.ReturnType.Length == 0 ? name + header.Parameters + header.Qualifiers
                : header.ReturnType == "void" ? "void " + name + header.Parameters + header.Qualifiers
                : "auto " + name + header.Parameters + header.Qualifiers + " -> " + header.ReturnType;
            var body = unit + "// TODO: 함수 본문을 구현하세요.";
            if (header.ReturnType.Length > 0 && header.ReturnType != "void")
            {
                body += newline + unit + "static_assert(false, \"VisualBoost: implement return value\");";
                warning = "반환값을 임의로 만들지 않기 위해 static_assert(false)를 넣습니다. 본문을 구현하고 제거하기 전까지 컴파일되지 않습니다. " + warning;
            }
            var neighbor = FindNeighbor(source, sourceCode, function, target, targetCode, 0, target.Length, null, token);
            var offset = neighbor?.Offset ?? target.Length;
            return new GenerationPlan(offset, (offset == 0 ? "" : newline + newline) + signature + newline + "{" + newline + body + newline + "}" + newline,
                signature, warning);
        }

        Require(access is "public" or "protected" or "private", "접근 수준을 선택하세요.");
        Require(targetClass is not null && targetClass.FullName == function.Owner, "선택한 헤더에서 정확한 대상 클래스를 확인할 수 없습니다.");
        var cls = targetClass!;
        Require(cls.Start >= 0 && cls.NameStart >= cls.Start && cls.End <= target.Length && cls.End > cls.NameStart, "대상 클래스의 범위가 변경되었습니다.");
        var className = cls.FullName.Split(new[] { "::" }, StringSplitOptions.None).Last();
        Require(targetCode.Substring(cls.NameStart).StartsWith(className, StringComparison.Ordinal) &&
            (cls.NameStart + className.Length == targetCode.Length || !Regex.IsMatch(targetCode[cls.NameStart + className.Length].ToString(), @"\w")), "클래스 이름 위치가 원문과 다릅니다.");
        var classPrefix = targetCode.Substring(cls.Start, cls.NameStart - cls.Start);
        Require(Regex.IsMatch(classPrefix.Trim(), @"^(class|struct)(\s+[A-Za-z_]\w*_API)?$"), "템플릿·속성·매크로로 구성된 클래스는 아직 생성 대상으로 지원하지 않습니다.");
        var open = targetCode.IndexOf('{', cls.NameStart);
        Require(open >= 0 && open < cls.End, "대상 클래스의 본문을 찾지 못했습니다.");
        var close = Match(targetCode, open, '{', '}');
        Require(close < cls.End, "대상 클래스 본문과 언어 모델의 범위가 다릅니다.");
        CheckDuplicate(target, targetCode, function, header, open + 1, close, token);
        var closingLine = target.LastIndexOf('\n', close) + 1;
        var prefix = target.Substring(closingLine, close - closingLine);
        var ownLine = string.IsNullOrWhiteSpace(prefix);
        var baseIndent = ownLine ? prefix : Regex.Match(target.Substring(target.LastIndexOf('\n', cls.Start) + 1), @"^[ \t]*").Value;
        var declaration = (header.ReturnType.Length == 0 ? "" : header.ReturnType + " ") + function.Name + header.Parameters + header.Qualifiers + ";";
        var nearby = FindNeighbor(source, sourceCode, function, target, targetCode, open + 1, close,
            position => AccessAt(targetCode, open, position, classPrefix.TrimStart().StartsWith("struct", StringComparison.Ordinal) ? "public" : "private") == access, token);
        if (nearby is not null)
        {
            var insertion = nearby.Value;
            return new GenerationPlan(insertion.Offset, (insertion.Offset > 0 && target[insertion.Offset - 1] != '\n' ? newline : "") + insertion.Indent + declaration + newline,
                declaration, "선택한 접근 수준에서 주변 함수 순서에 맞춰 선언을 추가합니다. virtual/static/기본 인수는 구현만으로 복원하지 않습니다. " + warning);
        }
        var text = newline + baseIndent + access + ":" + newline + baseIndent + unit + declaration + newline + (ownLine ? "" : baseIndent);
        return new GenerationPlan(ownLine ? closingLine : close, text, declaration,
            "virtual/static/기본 인수는 구현만으로 복원하지 않습니다. 선택한 접근 수준의 새 구역에 선언을 추가합니다. " + warning);
    }

    private sealed class Header
    {
        public string ReturnType = "", Parameters = "", Qualifiers = "";
        public string[] Types = Array.Empty<string>();
    }

    private static Header ReadHeader(string source, string code, GenerationFunction function)
    {
        Require(function.Start >= 0 && function.NameStart >= function.Start && function.End <= source.Length && function.End > function.NameStart, "함수 범위가 원문과 일치하지 않습니다.");
        Require(Regex.IsMatch(function.Name, @"^~?[A-Za-z_]\w*$"), "연산자·특수 함수 생성은 아직 지원하지 않습니다.");
        Require(code.Substring(function.NameStart).StartsWith(function.Name, StringComparison.Ordinal), "함수 이름 위치가 원문과 다릅니다.");
        var before = code.Substring(function.Start, function.NameStart - function.Start).Trim();
        var contextStart = Math.Max(code.LastIndexOf(';', Math.Max(0, function.Start - 1)), code.LastIndexOf('}', Math.Max(0, function.Start - 1))) + 1;
        Require(!Regex.IsMatch(code.Substring(contextStart, function.NameStart - contextStart), @"\b(UFUNCTION|GENERATED_BODY|GENERATED_UCLASS_BODY)\s*\("), "메타데이터를 가진 함수는 그룹 10의 생성 규칙이 필요합니다.");
        Require(!Regex.IsMatch(before, @"\b(template|constexpr|consteval|inline|friend|operator|typedef|extern)\b") && !before.Contains("#"), "템플릿·inline·constexpr·friend 또는 전처리 함수는 아직 지원하지 않습니다.");
        if (function.IsDefinition)
        {
            // 원문의 소속 한정이 실제 클래스 이름과 일치해야 역변환할 수 있습니다.
            var owner = Regex.Match(before, @"([A-Za-z_]\w*(?:::[A-Za-z_]\w*)*)::$");
            Require(owner.Success && (function.Owner == owner.Groups[1].Value || function.Owner.EndsWith("::" + owner.Groups[1].Value, StringComparison.Ordinal)), "구현부의 클래스 한정을 확인하지 못했습니다.");
            before = before.Substring(0, owner.Index).Trim();
        }
        else before = Regex.Replace(before, @"\b(virtual|static|explicit)\b\s*", "").Trim();
        Require(before.Length == 0 || Regex.IsMatch(before, @"^[A-Za-z_][\w\s:*&,<>]*$"), "반환형의 속성·함수 포인터 구문은 아직 지원하지 않습니다.");
        Require(!Regex.IsMatch(before, @"\b(?:[A-Za-z_]\w*_API|__cdecl|__stdcall|__fastcall|__vectorcall|WINAPI|CALLBACK|STDMETHODCALLTYPE)\b"),
            "함수의 내보내기 매크로·명시적 호출 규약은 아직 생성 대상으로 지원하지 않습니다.");
        Require(before != "auto" && !before.Contains("decltype"), "추론 반환형은 아직 지원하지 않습니다.");
        var ownerName = function.Owner.Split(new[] { "::" }, StringSplitOptions.None).Last();
        Require(before.Length > 0 || function.Name.TrimStart('~') == ownerName, "반환형을 확인하지 못했습니다.");
        var open = function.NameStart + function.Name.Length;
        while (open < function.End && char.IsWhiteSpace(code[open])) open++;
        Require(open < function.End && code[open] == '(', "함수 매개변수의 시작을 확인하지 못했습니다.");
        var close = Match(code, open, '(', ')');
        Require(close < function.End, "함수 매개변수 범위가 변경되었습니다.");
        var suffixEnd = close + 1;
        while (suffixEnd < function.End && code[suffixEnd] is not ('{' or ';' or ':')) suffixEnd++;
        var suffix = code.Substring(close + 1, suffixEnd - close - 1).Trim();
        Require(suffixEnd < function.End && (function.IsDefinition ? code[suffixEnd] is '{' or ':' : code[suffixEnd] == ';'), "선언·정의 형태를 확인하지 못했습니다.");
        Require(Regex.IsMatch(suffix, @"^(?:(?:const|volatile|override|final|noexcept(?:\s*\(\s*(?:true|false)\s*\))?)\s*|&&?\s*)*$"), "순수 가상·default/delete·requires·복잡한 noexcept 구문은 아직 지원하지 않습니다.");
        suffix = Regex.Replace(suffix, @"\b(override|final)\b", "");
        suffix = Spaces.Replace(suffix.Trim(), " ");
        var parameters = new List<string>();
        var types = new List<string>();
        foreach (var range in ParameterRanges(code, open + 1, close))
        {
            var formalCode = code.Substring(range.Item1, range.Item2 - range.Item1).Trim();
            if (formalCode.Length == 0) continue;
            Require(Regex.IsMatch(formalCode, @"^[A-Za-z_][\w\s:*&,<>]*$"), "배열·함수 포인터·매개변수 속성·가변 인수는 아직 지원하지 않습니다.");
            // 생성되는 한 줄 선언에 줄 주석이 남아 뒤 매개변수를 삼키지 않도록 표시 주석은 제외합니다.
            parameters.Add(Spaces.Replace(formalCode, " "));
            var lastName = Regex.Match(formalCode, @"\b([A-Za-z_]\w*)$");
            if (lastName.Success && !PrimitiveWords.Contains(lastName.Value))
            {
                var typePrefix = formalCode.Substring(0, lastName.Index).Trim();
                if (typePrefix.Length > 0 && typePrefix is not ("const" or "volatile") && !typePrefix.EndsWith("::", StringComparison.Ordinal)) formalCode = typePrefix;
            }
            if (!formalCode.Contains("&") && !formalCode.Contains("*")) formalCode = Regex.Replace(formalCode, @"\b(const|volatile)\b", "");
            var type = Spaces.Replace(formalCode, "");
            types.Add(type switch { "unsigned" => "unsignedint", "signed" or "signedint" => "int", "shortint" or "signedshort" or "signedshortint" => "short", "longint" or "signedlong" or "signedlongint" => "long", "longlongint" or "signedlonglong" or "signedlonglongint" => "longlong", _ => type });
        }
        if (parameters.Count == 1 && parameters[0] == "void") { parameters.Clear(); types.Clear(); }
        return new Header { ReturnType = Spaces.Replace(before, " "), Parameters = "(" + string.Join(", ", parameters) + ")", Types = types.ToArray(), Qualifiers = suffix.Length == 0 ? "" : " " + suffix };
    }

    private static IEnumerable<Tuple<int, int>> ParameterRanges(string code, int start, int end)
    {
        var begin = start; var formalEnd = -1; var depth = new Stack<char>();
        for (var i = start; i <= end; i++)
        {
            var c = i == end ? ',' : code[i];
            if (depth.Count == 0 && c == '=') { formalEnd = i; continue; }
            if (depth.Count == 0 && c == ',')
            { yield return Tuple.Create(begin, formalEnd >= 0 ? formalEnd : i); begin = i + 1; formalEnd = -1; continue; }
            if (c is '(' or '[' or '{' or '<') depth.Push(c);
            else if (c is ')' or ']' or '}' or '>')
            { Require(depth.Count > 0 && Pair(depth.Pop(), c), "매개변수 기본값의 중첩 구문을 확인하지 못했습니다."); }
        }
        Require(depth.Count == 0, "매개변수 기본값의 비교식·템플릿 구문이 모호합니다.");
    }

    private static void CheckDuplicate(string target, string code, GenerationFunction function, Header header, int start, int end, CancellationToken token)
    {
        // 탐색 후보는 추가 생성의 근거가 아니라 중복/모호성 차단에만 사용합니다.
        foreach (var member in new CppDocumentMemberProvider().Analyze(target, token).Members.Where(m => m.Name == function.Name && m.Start >= start && m.End <= end))
        {
            Header candidate;
            try { candidate = ReadHeader(target, code, new GenerationFunction(function.Name, function.Owner, member.Start, member.NameOffset, member.End, code[member.End - 1] == '}')); }
            catch (GenerationNotSupportedException) { throw new GenerationNotSupportedException("대상에 같은 이름의 구문이 있어 중복 여부를 확정할 수 없습니다."); }
            if (candidate.Types.Length != header.Types.Length) continue;
            // 별칭·사용자 타입은 서로 다르게 보이더라도 동일한 타입일 수 있으므로 보수적으로 차단합니다.
            var distinct = candidate.Types.Zip(header.Types, (a, b) => a != b && IsPrimitive(a) && IsPrimitive(b)).Any(v => v);
            Require(distinct, "대상에 같거나 모호한 함수 시그니처가 이미 있습니다. 기존 코드를 확인하세요.");
        }
    }
    private static bool IsPrimitive(string type) => Regex.IsMatch(type, @"^(void|bool|char|wchar_t|float|double|short|int|long|longlong|unsigned|unsignedint|unsignedlong|unsignedshort|signedint)$");

    private static string CheckDocument(string source, CancellationToken token)
    {
        Require(source.Length <= 2 * 1024 * 1024, "생성 대상은 2,097,152 UTF-16 문자 이하 문서로 제한합니다.");
        ValidateLiterals(source, token);
        var code = CppSymbolDetails.Mask(source, token);
        var conditionals = Regex.Matches(code, @"(?m)^\s*#\s*(if|ifdef|ifndef|elif|else|endif)\b[^\r\n]*").Cast<Match>().ToArray();
        if (conditionals.Length > 0)
        {
            Require(conditionals.Length == 2 && conditionals[0].Groups[1].Value == "ifndef" && conditionals[1].Groups[1].Value == "endif", "조건부 컴파일 분기를 가진 문서는 안전한 자동 생성을 지원하지 않습니다.");
            var guard = Regex.Match(conditionals[0].Value, @"ifndef\s+([A-Za-z_]\w*)\s*$");
            Require(guard.Success && Regex.IsMatch(code.Substring(conditionals[0].Index + conditionals[0].Length), @"^\s*#\s*define\s+" + guard.Groups[1].Value + @"\s*(?:\r?\n|$)"), "단순 헤더 가드만 지원합니다.");
        }
        var stack = new Stack<char>();
        foreach (var c in Regex.Replace(code, @"(?m)^\s*#.*$", ""))
        {
            token.ThrowIfCancellationRequested();
            if (c is '(' or '[' or '{') stack.Push(c);
            else if (c is ')' or ']' or '}') Require(stack.Count > 0 && Pair(stack.Pop(), c), "문서에 닫히지 않거나 불일치하는 괄호가 있습니다.");
        }
        Require(stack.Count == 0, "문서에 닫히지 않은 괄호가 있습니다.");
        return code;
    }

    private static void ValidateLiterals(string source, CancellationToken token)
    {
        for (var i = 0; i < source.Length; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/') { var end = source.IndexOf('\n', i); if (end < 0) return; i = end; }
            else if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            { var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal); Require(end >= 0, "닫히지 않은 주석이 있습니다."); i = end + 1; }
            else if (source[i] == 'R' && i + 1 < source.Length && source[i + 1] == '"')
            {
                var open = source.IndexOf('(', i + 2); Require(open >= 0 && open - i <= 18, "raw 문자열 구문을 확인하지 못했습니다.");
                var ending = ")" + source.Substring(i + 2, open - i - 2) + "\"";
                var end = source.IndexOf(ending, open + 1, StringComparison.Ordinal); Require(end >= 0, "닫히지 않은 raw 문자열이 있습니다."); i = end + ending.Length - 1;
            }
            else if (source[i] is '\'' or '"')
            {
                var quote = source[i++]; var closed = false;
                for (; i < source.Length; i++) { if (source[i] == '\\') { i++; continue; } if (source[i] == quote) { closed = true; break; } Require(source[i] is not ('\r' or '\n'), "문자열 구문을 확인하지 못했습니다."); }
                Require(closed, "닫히지 않은 문자열이 있습니다.");
            }
        }
    }
    private static int Match(string code, int start, char open, char close)
    {
        var depth = 0;
        for (var i = start; i < code.Length; i++) { if (code[i] == open) depth++; else if (code[i] == close && --depth == 0) return i; }
        throw new GenerationNotSupportedException("구문 범위가 완성되지 않았습니다.");
    }
    private static bool Pair(char open, char close) => open + close.ToString() is "()" or "[]" or "{}" or "<>";
    private static void Require(bool value, string message) { if (!value) throw new GenerationNotSupportedException(message); }
}
