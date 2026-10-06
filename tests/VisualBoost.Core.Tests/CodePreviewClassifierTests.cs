using System;
using System.Collections.Generic;
using System.Linq;
using VisualBoost.Core.Analysis;
using VisualBoost.Core.Coloring;

namespace VisualBoost.Core.Tests;

/// <summary>결과 목록 코드 미리보기 한 줄의 색 구간: 구문 종류와 줄 안의 형태 추정만으로 정한 식별자 종류.</summary>
internal static class CodePreviewClassifierTests
{
    public static void Run()
    {
        Expect("    return FMod::Compute(Value);", "return:Keyword Compute:Function", "키워드와 호출 형태");
        Expect("int Count = 0x1F + 1.5e-3f; // 합계", "int:Keyword Count:Variable 0x1F:Number 1.5e-3f:Number // 합계:Comment", "선언 형태·숫자 지수·줄 주석");
        Expect("Log(TEXT(\"a\\\"b\"), L'x', u8\"y\"sv);", "Log:Function \"a\\\"b\":String L'x':String u8\"y\"sv:String", "문자열 이스케이프·접두어·접미사");
        Expect("auto Path = R\"tag(C:\\a\"b)tag\" /* 끝 */;", "auto:Keyword Path:Variable R\"tag(C:\\a\"b)tag\":String /* 끝 */:Comment", "raw 문자열 안의 따옴표와 블록 주석");
        Expect("#include <Game/Widget.h>", "#include:Preprocessor <Game/Widget.h>:String", "꺾쇠 include");
        Expect("  #  define MAX_ITEMS 10", "#  define:Preprocessor MAX_ITEMS:Macro 10:Number", "전처리 지시문과 매크로 이름");
        Expect("#include \"Widget.h\"", "#include:Preprocessor \"Widget.h\":String", "따옴표 include");
        Expect("class GAME_API UWidget : public UObject", "class:Keyword UWidget:Type public:Keyword", "내보내기 표식 뒤 타입 선언");
        Expect("namespace Game::Ui {", "namespace:Keyword Game:Namespace Ui:Namespace", "중첩 네임스페이스");
        Expect("Name = \"unterminated", "\"unterminated:String", "닫히지 않은 문자열은 줄 끝까지");
        Expect("Value /* open", "/* open:Comment", "닫히지 않은 블록 주석은 줄 끝까지");
        Expect("CHECK_SLOW(Url);", string.Empty, "대문자 호출은 매크로일 수 있어 함수로 칠하지 않음");
        Expect("…Compute(1);", "Compute:Function 1:Number", "앞을 자른 줄임표");
        Check(CppLinePreviewClassifier.Classify(null).Count == 0 && CppLinePreviewClassifier.Classify(string.Empty).Count == 0, "빈 입력");
        Check(CppLinePreviewClassifier.Classify(new string('a', CppLinePreviewClassifier.MaximumLength + 1)).Count == 0, "비정상적으로 긴 줄은 칠하지 않음");

        // 템플릿 인수 목록 뒤 여는 괄호는 호출 형태입니다. 비교식과 대문자 이름은 거릅니다.
        Expect("return Cast<UserSettingClass>(GetUserSettings());", "return:Keyword Cast:Function GetUserSettings:Function", "템플릿 호출");
        Expect("auto Ptr = MakeShared<TPair<int32, FString>>(Key, Value);", "auto:Keyword Ptr:Variable MakeShared:Function", "중첩 템플릿 인수와 >> 닫기");
        Expect("Forward<T&&>(Arg);", "Forward:Function", "인수 끝의 &&");
        Expect("if (Index < Count && Limit > (Max)) {}", "if:Keyword", "비교식은 템플릿 호출로 보지 않음");
        Expect("TArray<FString> Names;", string.Empty, "괄호가 없는 템플릿 타입은 호출이 아님");
        Expect("CHECK_T<int>(Value);", "int:Keyword", "대문자 이름은 템플릿 호출 형태여도 함수로 칠하지 않음");

        // 이름 판정은 형태 추정이 칠하지 못한 이름을 채우고, 호출 형태로 추정한 함수보다 앞섭니다. 선언 형태로 확정한 종류는 그대로입니다.
        var names = new Dictionary<string, CodePreviewKind>(StringComparer.Ordinal)
        {
            ["UE_API"] = CodePreviewKind.Macro,
            ["UEnhancedInputUserSettings"] = CodePreviewKind.Type,
            ["FVector"] = CodePreviewKind.Type,
            ["TArray"] = CodePreviewKind.Type,
            ["check"] = CodePreviewKind.Macro,
            ["Count"] = CodePreviewKind.Function,
        };
        CodePreviewKind? Resolve(string name) => names.TryGetValue(name, out var kind) ? kind : null;
        Expect("UE_API virtual UEnhancedInputUserSettings* GetUserSettings() const;",
            "UE_API:Macro virtual:Keyword UEnhancedInputUserSettings:Type GetUserSettings:Function const:Keyword", "이름 판정으로 매크로·타입 보강", Resolve);
        Expect("FVector Offset = FVector(0, 0, 1);", "FVector:Type FVector:Type 0:Number 0:Number 1:Number", "생성자 호출은 이름 판정의 타입", Resolve);
        Expect("check(Value);", "check:Macro", "함수형 매크로", Resolve);
        Expect("TArray<int32>(Source);", "TArray:Type", "템플릿 호출 형태보다 이름 판정", Resolve);
        Expect("int Count = 0;", "int:Keyword Count:Variable 0:Number", "선언 형태로 확정한 변수는 이름 판정보다 앞섬", Resolve);

        // 같은 이름의 심볼 종류들은 확실한 경우만 한 종류로 줄입니다.
        Kinds("Class Function", CodePreviewKind.Type, "타입과 생성자");
        Kinds("Struct Union Enum Type", CodePreviewKind.Type, "타입 종류끼리");
        Kinds("Macro Macro", CodePreviewKind.Macro, "헤더마다 다시 정의한 매크로");
        Kinds("Namespace", CodePreviewKind.Namespace, "네임스페이스");
        Kinds("Function Function", CodePreviewKind.Function, "오버로드");
        Kinds("Function Variable", null, "변수가 섞이면 모름");
        Kinds("Class Variable", null, "타입과 변수");
        Kinds("Macro Function", null, "매크로와 함수");
        Kinds("Namespace Class", null, "네임스페이스와 타입");
        Kinds("Unknown", null, "모르는 종류");
        Kinds(string.Empty, null, "찾지 못함");
    }

    private static void Kinds(string kinds, CodePreviewKind? expected, string message)
    {
        var values = kinds.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(name => (SourceSymbolKind)Enum.Parse(typeof(SourceSymbolKind), name));
        var actual = CppLinePreviewClassifier.KindOfSymbols(values);
        Check(actual == expected, $"{message}: {actual}");
    }

    private static void Expect(string line, string expected, string message, Func<string, CodePreviewKind?>? resolveName = null)
    {
        var spans = CppLinePreviewClassifier.Classify(line, resolveName);
        for (var index = 1; index < spans.Count; index++)
            Check(spans[index - 1].Start + spans[index - 1].Length <= spans[index].Start, message + ": 구간 겹침");
        var actual = string.Join(" ", spans.Select(span => line.Substring(span.Start, span.Length) + ":" + span.Kind));
        Check(actual == expected, $"{message}: {actual}");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
