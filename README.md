# LogMapping

**오프라인 드라이브 파일 카탈로그** — 외장 하드·SSD·USB의 파일 목록을 로컬에 저장해, **드라이브를 연결하지 않아도** 파일명·경로·확장자로 검색할 수 있는 Windows 단일 실행 도구입니다.

> 현재 버전: **BETA Ver-0.1**

## 주요 기능

- 💽 **드라이브 스캔·카탈로그** — 드라이브를 한 번 스캔하면 오프라인에서도 목록 조회
- 🔎 **전체 검색** — 등록된 모든 드라이브에서 파일명·확장자 검색
- 🗂️ **폴더 트리 탐색** + 색상 태그
- 📑 **중복 파일 탐지** — 여러 드라이브에 걸친 중복(이름+크기) 찾기
- 📤 **내보내기** — CSV / 오프라인 HTML 뷰어
- 🍎 **Mac 드라이브 읽기** — Windows에서 APFS·HFS+ 포맷 드라이브 스캔 지원
- 🔐 **드라이브 식별 = 볼륨 일련번호** — 같은 포트에 하드를 바꿔 꽂아도 서로 섞이지 않음

## 다운로드

[**Releases**](../../releases) 페이지에서 최신 `LogMapping.exe`를 받으세요. 설치 불필요, 단일 실행 파일입니다.

## 시스템 요구사항

| 항목 | 요구사항 |
|---|---|
| 운영체제 | Windows 10 / 11 (64-bit) |
| 웹 엔진 | Microsoft WebView2 런타임 (Windows 11 기본 포함, Windows 10은 Edge 설치 시 자동 포함) |
| 권한 | **관리자 권한 필요** — 물리 디스크(APFS 등) 스캔 때문에 실행 시 UAC 승인 필요 |
| 네트워크 | 불필요 (완전 오프라인 동작) |

## 사용법

1. `LogMapping.exe`를 실행합니다. (첫 실행 시 런타임 초기화로 2–3초 지연될 수 있습니다.)
2. UAC 창이 뜨면 **[예]** 를 선택합니다.
3. **[+ 드라이브 추가]** → 드라이브 선택 → 번호·이름 입력 → **스캔 시작**.
4. 스캔이 끝나면 연결을 해제해도 목록·검색이 동작합니다.
5. 카탈로그는 실행 파일 옆 `data/` 폴더의 `.hcat`·`catalog.db`에 저장됩니다. 다른 PC로 옮길 때는 `LogMapping.exe`와 `data/` 폴더를 함께 복사하세요.

## 빌드 (개발자용)

- **요구**: .NET 8 SDK
- **명령**:
  ```
  dotnet publish LogMapping/LogMapping.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o publish_out
  ```
- 결과: `publish_out/LogMapping.exe` (self-contained 단일 파일, .NET 런타임 번들 포함).

## 기술 스택

C# / .NET 8 (WPF) · WebView2 · SQLite(Microsoft.Data.Sqlite) · DiscUtils(APFS/HFS+ 읽기)

## 제작

**Unknown** · YouTube [@unknown8563](https://www.youtube.com/@unknown8563)
