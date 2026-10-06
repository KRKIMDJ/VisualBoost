using Microsoft.VisualStudio.Shell;

namespace VisualBoost.UI;

/// <summary>
/// 결과 목록 창(참조 창·파일 탐색·심볼 탐색·정의 후보)과 목록 팝업(문서 함수 트리·이름 제안)이 함께 쓰는 글씨 크기입니다.
/// 검색 입력란·머리 줄·결과 행·코드 줄·열 머리글·상태와 안내 줄·우클릭 메뉴까지 모든 글자가 환경 글꼴과 이 크기 하나를 쓰고
/// 강조는 굵기로만 합니다. 크기를 바꿀 때는 이 키만 바꿉니다.
/// </summary>
public static class ResultListFont
{
    /// <summary>환경 글꼴 크기의 90%입니다. 사용자가 바꾼 환경 글꼴을 따르도록 VS 비율 키를 씁니다. XAML에서 <c>DynamicResource</c> 키로 씁니다.</summary>
    public static object SizeKey => VsFonts.Environment90PercentFontSizeKey;
}
