# VisualBoost

VisualBoost는 Visual Studio에서 일반 C++ 프로젝트의 반복적인 탐색 작업을 단축하는 확장 프로그램입니다.

## 현재 기능

- `Alt+O`: 현재 헤더와 동일한 이름의 구현 파일 또는 현재 구현 파일과 동일한 이름의 헤더로 전환
- 헤더·구현 전환 후보가 여러 개면 탐색 창에서 원하는 파일 선택
- `Shift+Alt+O`: 파일명과 경로를 검색하는 키보드 중심 파일 탐색 창 열기
- 최근에 연 파일과 현재 프로젝트의 파일을 검색 결과에서 우선 표시
- 검색어가 없을 때 최근 파일과 현재 프로젝트 파일을 바로 표시
- `전체`, `현재 프로젝트`, `열린 파일`, `외부 소스` 검색 범위 제공
- 파일 형식, 상대 경로, 결과 출처, 검색어 일치 구간 및 인덱싱 상태 표시
- 검색 결과에서 열기, 전체 경로 복사, 탐색기에서 보기를 제공하는 컨텍스트 메뉴
- 검색 중 `Esc` 또는 취소 버튼으로 대규모 검색 중단
- Solution 로드 시 언어와 관계없이 프로젝트 파일을 백그라운드 인덱싱
- 프로젝트 파일과 C++ include 경로 및 감지된 외부 엔진 소스 인덱싱
- `%LocalAppData%\\VisualBoost\\Cache`의 Solution별 영구 파일 캐시
- C++ include 관계와 주요 심볼 후보 위치의 증분 분석 및 로컬 캐시
- 파일 생성·삭제·이름 변경을 실행 중인 인덱스에 증분 반영
- 실행 중 추가·제거·이름 변경된 프로젝트의 검색 루트 반영
- 동일 디렉터리와 `include`/`src` 같은 대응 디렉터리를 우선하는 후보 점수 계산
- `Tools > Options > VisualBoost > General`에서 C++ 확장자와 대응 디렉터리 설정
- `Tools > Options > VisualBoost > 파일 탐색`에서 결과 수, 입력 지연과 빈 검색 추천 설정
- `Tools > Options > VisualBoost > 인덱싱`에서 영구 캐시, 소스 분석과 진단 설정
- `Tools > Visual Boost` 하위 메뉴에서 탐색 명령, 옵션과 인덱스 상태에 접근
- `Tools > Options > VisualBoost > Coloring`에서 색상 견본과 팔레트 선택 창으로 C++ 의미 색상 설정
- `Coloring`의 색상 설정 기준을 `FontsAndColors`로 선택하면 `환경 > 글꼴 및 색 > 텍스트 편집기`의 테마별 `VisualBoost` 전경색 사용
- C++ 편집기와 심볼 탐색 창에서 의미 색상을 공유하며, 선택 행과 고대비 모드의 기본 강조색 유지
- `.git`, `.vs`, `bin`, `obj`, `packages`, `node_modules` 및 대형 생성물 디렉터리 제외

## 테스트

```powershell
dotnet run --project tests/VisualBoost.Core.Tests/VisualBoost.Core.Tests.csproj
```

## 프로젝트 상태

VisualBoost는 현재 초기 개발 단계입니다. 공개된 기능은 위의 `현재 기능` 목록을 기준으로 하며, 변경 사항은 버전별 릴리스 노트에서 안내합니다.

## 라이선스

VisualBoost는 [MIT License](LICENSE)로 배포됩니다.
