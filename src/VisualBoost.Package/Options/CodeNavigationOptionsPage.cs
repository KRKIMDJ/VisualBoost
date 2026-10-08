using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using VisualBoost.Core.SemanticNavigation;
using VisualBoost.SemanticNavigation;

namespace VisualBoost.Options;

[Guid("6f0c7d2e-3b1a-4e58-9c47-1d2a8b5e7f30")]
public sealed class CodeNavigationOptionsPage : DialogPage
{
    private int workerCount;
    private int memoryLimitMegabytes;

    [Category("정의·참조 탐색"), DisplayName("사용"), DefaultValue(true)]
    [Description("C++ 문서에서 정의로 이동과 참조 찾기에 clangd를 사용합니다. 끄면 Visual Studio 기본 탐색을 실행합니다.")]
    public bool Enabled { get; set; } = true;

    [Category("정의·참조 탐색"), DisplayName("Solution을 열 때 색인 시작"), DefaultValue(true)]
    [Description("끄면 첫 탐색 요청 때 시작합니다. 색인은 낮은 우선순위로 실행되며 이전 결과를 캐시에서 다시 씁니다.")]
    public bool StartOnSolutionOpen { get; set; } = true;

    [Category("정의·참조 탐색"), DisplayName("clangd 경로"), DefaultValue("")]
    [Description("비워 두면 현재 Visual Studio, 다른 Visual Studio의 C++ Clang 도구, LLVM 설치 순서로 clangd를 찾습니다.")]
    public string ClangdPath { get; set; } = string.Empty;

    [Category("정의·참조 탐색"), DisplayName("색인 작업 수"), DefaultValue(0)]
    [Description("동시에 색인할 파일 수입니다. 0이면 자동으로 정합니다(논리 코어의 3/8, 최대 8, 메모리가 적으면 더 적게). 값이 클수록 첫 색인이 빨리 끝나지만 CPU와 메모리를 더 씁니다. 0~64 범위로 적용됩니다.")]
    public int WorkerCount
    {
        get => workerCount;
        set => workerCount = value < 0 ? 0 : value > 64 ? 64 : value;
    }

    [Category("정의·참조 탐색"), DisplayName("clangd 메모리 정리 기준(MB)"), DefaultValue(0)]
    [Description("clangd가 이보다 많은 메모리를 쥐고 있으면 색인과 탐색이 멈춘 동안 다시 시작해 메모리를 돌려받습니다. 저장된 색인은 그대로 씁니다. 0이면 자동으로 정합니다(물리 메모리의 1/8, 2~8 GB). 1024~65536 범위로 적용됩니다.")]
    public int MemoryLimitMegabytes
    {
        get => memoryLimitMegabytes;
        set => memoryLimitMegabytes = value <= 0 ? 0 : Math.Max(1024, Math.Min(65536, value));
    }

    [Category("정의·참조 탐색"), DisplayName("Unreal 공유 PCH 포함"), DefaultValue(UnrealPchMode.Auto)]
    [Description("Unreal 프로젝트를 분석할 때 빌드의 공유 PCH 헤더를 넣는 방식입니다. 자동은 넣지 않고 색인한 뒤 분석 오류가 난 파일만 넣어 다시 분석합니다. 항상 넣으면 첫 색인과 메모리가 몇 배 늘고, 넣지 않으면 공유 PCH에 기대는 파일의 정의·참조가 빠질 수 있습니다.")]
    [TypeConverter(typeof(UnrealPchModeConverter))]
    public UnrealPchMode PchMode { get; set; } = UnrealPchMode.Auto;

    [Category("정의·참조 탐색"), DisplayName("사용할 수 없을 때 기본 탐색 실행"), DefaultValue(true)]
    [Description("clangd나 컴파일 명령이 없을 때 Visual Studio 기본 정의 이동·참조 찾기를 대신 실행합니다.")]
    public bool FallbackToVisualStudio { get; set; } = true;

    internal SemanticNavigationSettings CreateSettings() =>
        new(Enabled, StartOnSolutionOpen, ClangdPath?.Trim().Trim('"') ?? string.Empty, WorkerCount, FallbackToVisualStudio, MemoryLimitMegabytes, PchMode);

    protected override void OnApply(PageApplyEventArgs e)
    {
        base.OnApply(e);
        if (e.ApplyBehavior != ApplyKind.Cancel) SemanticNavigationRuntime.Service?.ApplySettings(CreateSettings());
    }
}

/// <summary>
/// 옵션 창에는 한국어 문구로 보이고, 설정 저장(불변 문화권)에는 enum 이름을 씁니다. 표시 문구를 바꿔도 저장된 설정이 그대로 읽힙니다.
/// </summary>
internal sealed class UnrealPchModeConverter : EnumConverter
{
    private static readonly (UnrealPchMode Mode, string Label)[] Labels =
    {
        (UnrealPchMode.Auto, "자동(오류 난 파일만)"),
        (UnrealPchMode.Always, "항상"),
        (UnrealPchMode.Never, "넣지 않음")
    };

    public UnrealPchModeConverter()
        : base(typeof(UnrealPchMode))
    {
    }

    public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
    {
        if (destinationType == typeof(string) && value is UnrealPchMode mode && !Equals(culture, CultureInfo.InvariantCulture))
        {
            foreach (var (candidate, label) in Labels)
            {
                if (candidate == mode) return label;
            }
        }

        return base.ConvertTo(context, culture, value, destinationType);
    }

    public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
    {
        if (value is string text)
        {
            text = text.Trim();
            foreach (var (mode, label) in Labels)
            {
                if (string.Equals(label, text, StringComparison.Ordinal)) return mode;
            }

            // 알 수 없는 저장값(이전 버전에 없던 이름 등)은 기본값으로 읽습니다.
            return Enum.TryParse<UnrealPchMode>(text, true, out var parsed) && Enum.IsDefined(typeof(UnrealPchMode), parsed) ? parsed : UnrealPchMode.Auto;
        }

        return base.ConvertFrom(context, culture, value);
    }
}
