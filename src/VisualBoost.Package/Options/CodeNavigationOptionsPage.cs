using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using VisualBoost.SemanticNavigation;

namespace VisualBoost.Options;

[Guid("6f0c7d2e-3b1a-4e58-9c47-1d2a8b5e7f30")]
public sealed class CodeNavigationOptionsPage : DialogPage
{
    private int workerCount;

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
    [Description("동시에 색인할 파일 수입니다. 0이면 논리 코어의 절반을 쓰되 메모리가 적으면 줄입니다. 값이 클수록 첫 색인이 빨라지고 메모리를 많이 씁니다.")]
    public int WorkerCount
    {
        get => workerCount;
        set => workerCount = value < 0 ? 0 : value > 64 ? 64 : value;
    }

    [Category("정의·참조 탐색"), DisplayName("사용할 수 없을 때 기본 탐색 실행"), DefaultValue(true)]
    [Description("clangd나 컴파일 명령이 없을 때 Visual Studio 기본 정의 이동·참조 찾기를 대신 실행합니다.")]
    public bool FallbackToVisualStudio { get; set; } = true;

    internal SemanticNavigationSettings CreateSettings() =>
        new(Enabled, StartOnSolutionOpen, ClangdPath?.Trim().Trim('"') ?? string.Empty, WorkerCount, FallbackToVisualStudio);

    protected override void OnApply(PageApplyEventArgs e)
    {
        base.OnApply(e);
        if (e.ApplyBehavior != ApplyKind.Cancel) SemanticNavigationRuntime.Service?.ApplySettings(CreateSettings());
    }
}
