using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace VisualBoost.Options;

[Guid("ed10b75b-8d46-405c-817b-a109d561aa82")]
public sealed class CodeGenerationOptionsPage : DialogPage
{
    [Category("안전한 코드 생성"), DisplayName("선언/정의 생성 사용"), DefaultValue(true)]
    [Description("현재 코드 도구 메뉴에서 C++ 멤버의 선언·정의 틀을 바로 삽입합니다. Undo를 지원하며 자동 저장이나 include 추가는 하지 않습니다.")]
    public bool Enabled { get; set; } = true;
}
