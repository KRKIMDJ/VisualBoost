# VisualBoost

VisualBoost는 Visual Studio에서 일반 C++ 프로젝트의 반복적인 탐색 작업을 단축하는 확장 프로그램입니다.

## 현재 기능

- `Alt+O`: 현재 헤더와 동일한 이름의 구현 파일 또는 현재 구현 파일과 동일한 이름의 헤더로 전환
- `Shift+Alt+O`: 파일명과 경로를 검색하는 키보드 중심 파일 탐색 창 열기
- Solution 로드 시 언어와 관계없이 프로젝트 파일을 백그라운드 인덱싱
- 프로젝트 파일과 C++ include 경로 및 감지된 외부 엔진 소스 인덱싱
- `%LocalAppData%\\VisualBoost\\Cache`의 Solution별 영구 파일 캐시
- C++ include 관계와 주요 심볼 후보 위치의 증분 분석 및 로컬 캐시
- 파일 생성·삭제·이름 변경을 실행 중인 인덱스에 증분 반영
- 실행 중 추가·제거·이름 변경된 프로젝트의 검색 루트 반영
- 동일 디렉터리와 `include`/`src` 같은 대응 디렉터리를 우선하는 후보 점수 계산
- `Tools > Options > VisualBoost > General`에서 C++ 확장자와 대응 디렉터리 설정
- `Tools > VisualBoost 옵션...`에서 설정 페이지 바로 열기
- `Tools > VisualBoost 인덱스 상태...`에서 상태, 파일 수, 검색 루트와 최근 소요 시간 확인
- `.git`, `.vs`, `bin`, `obj`, `packages`, `node_modules` 및 대형 생성물 디렉터리 제외

## 테스트

```powershell
dotnet run --project tests/VisualBoost.Core.Tests/VisualBoost.Core.Tests.csproj
```

## 프로젝트 상태

VisualBoost는 현재 초기 개발 단계입니다. 공개된 기능은 위의 `현재 기능` 목록을 기준으로 하며, 변경 사항은 버전별 릴리스 노트에서 안내합니다.

## 라이선스

VisualBoost는 [MIT License](LICENSE)로 배포됩니다.
