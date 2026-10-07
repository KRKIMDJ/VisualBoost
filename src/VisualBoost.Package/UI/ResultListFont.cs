using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;

namespace VisualBoost.UI;

/// <summary>
/// 결과 목록 창(참조 창·파일 탐색·심볼 탐색·정의 후보)과 목록 팝업(문서 함수 트리·이름 제안)이 함께 쓰는 글씨 크기입니다.
/// 검색 입력란·머리 줄·결과 행·코드 줄·열 머리글·상태와 안내 줄·우클릭 메뉴까지 모든 글자가 환경 글꼴과 이 크기 하나를 쓰고
/// 강조는 굵기로만 합니다. 크기를 바꿀 때는 이 키의 값만 바꿉니다.
/// </summary>
/// <remarks>
/// 값은 VS 환경 글꼴 크기 × 옵션 비율(기본 100%)이며 애플리케이션 리소스로 게시합니다. 환경 글꼴은 PC의 VS 설정마다 달라
/// 고정 비율(이전 90%)이 PC마다 작게 보였으므로(2026-10-07 사용자 피드백) 비율을 옵션으로 둡니다. 환경 글꼴을 바꾸면 VS가 갱신하는
/// 리소스 값을 따라 다시 계산합니다. 게시 전(VS 밖 테스트 등)에는 키가 없어 컨트롤 기본 크기를 씁니다. UI thread에서만 씁니다.
/// </remarks>
public static class ResultListFont
{
    public const int DefaultPercent = 100;
    public const int MinimumPercent = 80;
    public const int MaximumPercent = 200;

    private static int percent = DefaultPercent;

    // 환경 글꼴 크기 리소스를 따라가는 연결 속성입니다. WPF는 애플리케이션 리소스가 바뀌면 열린 창의 트리에만 알리므로, 트리 밖 요소에 둔
    // 리소스 참조는 VS를 다시 시작할 때까지 갱신되지 않습니다(2026-10-07 검토·실험). 늘 열려 있는 VS 주 창에 값만 담아 따라갑니다.
    private static readonly DependencyProperty EnvironmentSizeProperty = DependencyProperty.RegisterAttached(
        "EnvironmentSize", typeof(object), typeof(ResultListFont), new PropertyMetadata(null, (_, _) => Update()));

    private static FrameworkElement? tracker;

    /// <summary>XAML에서 <c>DynamicResource</c> 키로 씁니다.</summary>
    public static object SizeKey { get; } = new ComponentResourceKey(typeof(ResultListFont), nameof(SizeKey));

    /// <summary>옵션 값을 허용 범위로 맞춥니다.</summary>
    public static int Clamp(int value) => Math.Max(MinimumPercent, Math.Min(MaximumPercent, value));

    /// <summary>환경 글꼴 크기(DIP)에 비율을 곱한 목록 글씨 크기입니다.</summary>
    public static double Size(double environmentSize, int value) => Math.Round(environmentSize * Clamp(value) / 100d, 2);

    /// <summary>옵션 비율을 적용해 목록 글씨 크기를 게시합니다. 패키지 시작과 옵션 적용 때 부릅니다.</summary>
    public static void Publish(int value)
    {
        percent = Clamp(value);
        if (Application.Current is null) return;
        // 패키지 시작 때 주 창이 아직 없으면 임시 요소에 두었다가 다음 게시(옵션 적용) 때 주 창으로 옮깁니다.
        var host = (FrameworkElement?)Application.Current.MainWindow;
        if (tracker is null || (host is not null && !ReferenceEquals(tracker, host)))
        {
            var previous = tracker;
            tracker = host ?? new TextBlock();
            tracker.SetResourceReference(EnvironmentSizeProperty, VsFonts.EnvironmentFontSizeKey);
            previous?.ClearValue(EnvironmentSizeProperty);
        }

        Update();
    }

    /// <summary>
    /// 패키지 시작 때 주 창이 없어 임시 요소로 따라가고 있으면 주 창으로 옮깁니다. 옵션을 적용하지 않아도 옮기도록 결과 창을 만들 때와 목록 팝업을 열 때 부릅니다
    /// (2026-10-07 검토). UI thread에서 부릅니다.
    /// </summary>
    public static void EnsureTracking()
    {
        if (Application.Current?.MainWindow is { } main && !ReferenceEquals(tracker, main)) Publish(percent);
    }

    private static void Update()
    {
        if (tracker is null || Application.Current is null) return;
        var environment = tracker.GetValue(EnvironmentSizeProperty) is double size && size > 0 ? size : SystemFonts.MessageFontSize;
        Application.Current.Resources[SizeKey] = Size(environment, percent);
    }
}
