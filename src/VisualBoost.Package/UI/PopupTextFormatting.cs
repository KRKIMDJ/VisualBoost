using System;
using System.Windows;
using System.Windows.Media;

namespace VisualBoost.UI;

/// <summary>
/// 편집기에 붙는 팝업(문서 함수 트리·이름 제안)의 글자 렌더링 방식을 정합니다. 부모 없는 Popup은 VS UI의 렌더링 방식을 물려받지 않아
/// 직접 지정해야 하는데, WPF Popup은 기준 요소의 확대 변환(편집기 확대)을 내용에도 적용합니다. VS UI와 같은 Display 방식은 픽셀에 맞춘
/// 글자 폭을 써서 확대·축소하면 자간이 고르지 않으므로, 확대가 없을 때만 Display를 쓰고 확대 중에는 WPF 기본(Ideal)을 씁니다.
/// 팝업을 열 때마다 부릅니다. UI thread에서만 씁니다.
/// </summary>
internal static class PopupTextFormatting
{
    // 배율 비교 허용 오차입니다. 편집기 확대는 정수 백분율이라 이보다 작은 차이는 계산 오차입니다.
    private const double Tolerance = 0.005;

    public static void Apply(DependencyObject surface, Visual? placementTarget)
    {
        if (surface is null) throw new ArgumentNullException(nameof(surface));
        TextOptions.SetTextFormattingMode(surface, IsUnscaled(placementTarget) ? TextFormattingMode.Display : TextFormattingMode.Ideal);
    }

    /// <summary>기준 요소에서 창 루트까지 확대·회전 변환이 없는지 봅니다. 화면에 붙지 않은 요소는 변환을 알 수 없어 확대 없음으로 봅니다.</summary>
    internal static bool IsUnscaled(Visual? target)
    {
        if (target is null || PresentationSource.FromVisual(target)?.RootVisual is not Visual root || ReferenceEquals(root, target)) return true;
        if (target.TransformToAncestor(root) is not Transform transform) return false;
        var matrix = transform.Value;
        return Math.Abs(matrix.M11 - 1) < Tolerance && Math.Abs(matrix.M22 - 1) < Tolerance &&
            Math.Abs(matrix.M12) < Tolerance && Math.Abs(matrix.M21) < Tolerance;
    }
}
