# VisualBoost

VisualBoost는 Visual Studio에서 일반 C++ 프로젝트의 반복적인 탐색 작업을 단축하는 확장 프로그램입니다.

## 현재 기능

- `Alt+O`: 현재 헤더와 동일한 이름의 구현 파일 또는 현재 구현 파일과 동일한 이름의 헤더로 전환
- Solution 로드 시 언어와 관계없이 프로젝트 파일을 백그라운드 인덱싱
- 파일 생성·삭제·이름 변경을 실행 중인 인덱스에 증분 반영
- 동일 디렉터리와 `include`/`src` 같은 대응 디렉터리를 우선하는 후보 점수 계산
- `Tools > Options > VisualBoost > General`에서 C++ 확장자와 대응 디렉터리 설정
- `Tools > VisualBoost 옵션...`에서 설정 페이지 바로 열기
- `.git`, `.vs`, `bin`, `obj`, `packages`, `node_modules` 및 대형 생성물 디렉터리 제외

## 개발 환경

- Visual Studio 2026 또는 Visual Studio 2022 17.14 이상
- `Visual Studio extension development` 워크로드
- .NET SDK 10

## 실행

1. `VisualBoost.sln`을 엽니다.
2. `VisualBoost.Package`를 시작 프로젝트로 선택합니다.
3. `F5`를 눌러 Experimental Instance를 시작합니다.
4. C++ Solution에서 헤더 또는 구현 파일을 연 뒤 `Alt+O`를 누릅니다.

Debug 구성에서는 빌드된 확장이 `Exp` 인스턴스에 자동으로 배포됩니다. 코드를 수정한 뒤 디버깅을 중지하고 다시 `F5`를 누르면 별도로 VSIX를 설치하지 않아도 새 빌드가 반영됩니다.

주 Visual Studio에 설치한 VSIX는 실행 중인 확장 DLL을 안전하게 교체할 수 없으므로 개발 반복 작업에 사용하지 않습니다. 주 인스턴스에서는 Release 후보를 확인할 때만 새 버전의 VSIX를 설치합니다.

단축키가 동작하지 않으면 다음 순서로 확인합니다.

1. `Tools` 메뉴에 `헤더/구현 파일 전환` 명령이 표시되는지 확인합니다.
2. `Tools > Options > Environment > Keyboard`에서 `VisualBoost.SwitchHeaderSource`를 검색합니다.
3. `Alt+O`에 다른 명령이 먼저 할당되어 있는지 확인합니다.

## 테스트

```powershell
dotnet run --project tests/VisualBoost.Core.Tests/VisualBoost.Core.Tests.csproj
```

## 프로젝트 상태

VisualBoost는 현재 초기 개발 단계입니다. 공개된 기능은 위의 `현재 기능` 목록을 기준으로 하며, 변경 사항은 버전별 릴리스 노트에서 안내합니다.

## 라이선스

VisualBoost는 [MIT License](LICENSE)로 배포됩니다.
