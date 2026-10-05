using System;
using System.Linq;
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
    }

    private static void Expect(string line, string expected, string message)
    {
        var spans = CppLinePreviewClassifier.Classify(line);
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
