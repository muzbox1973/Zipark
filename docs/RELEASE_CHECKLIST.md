# Zipark 1.0 — 릴리스 체크리스트

표기: ✅ 검증됨(이 개발 환경에서 실제 실행으로 확인) · ⚠️ 부분 검증 · ❌ 미검증 · ⛔ 배포 차단 항목

## A. 수용 기준 (마스터 프롬프트 14장)

| # | 기준 | 상태 | 근거 |
|---|---|---|---|
| 1 | ZIP/7Z 생성·해제 | ✅ | `EngineTests.Roundtrip_PreservesSha256` (ZIP, 7Z, TAR) |
| 2 | 해제 파일 SHA-256 = 원본 | ✅ | 동일 테스트에서 전체 트리 SHA-256 비교 |
| 3 | 분할 압축을 첫 볼륨에서 자동 해제 | ✅ | `SplitVolumes_ExtractFromFirstVolume` (7Z, ZIP) |
| 4 | AES-256 암호화 압축·해제 | ✅ | `SevenZip_Aes256_WithEncryptedNames`, `Zip_Aes256_Roundtrip_AndWrongPasswordDetected` |
| 5 | ISO를 Windows 가상 드라이브로 탑재 | ✅ | `Iso_MountReadUnmount_WithWindowsVirtualDiskApi` (표준 사용자 권한, 드라이브 문자 획득·파일 읽기·해제) |
| 6 | ZIP을 Windows 가상 드라이브로 탑재 | ⚠️ | 파일 시스템 코어·호스트 빌드 완료, 코어 테스트 통과. **WinFsp가 설치되지 않아 실제 드라이브 문자 탑재는 미검증** |
| 7 | 7Z를 Windows 가상 드라이브로 탑재 | ⚠️ | 위와 동일 (솔리드 블록 지연 해제는 코어 테스트로 검증) |
| 8 | 탐색기에서 ZIP/7Z 드라이브 내부 파일 읽기 | ❌ | WinFsp 설치 후 E2E 필요 |
| 9 | ZIP/7Z 드라이브 쓰기 거부 | ⚠️ | 모든 쓰기 콜백이 `STATUS_MEDIA_WRITE_PROTECTED` 반환(코드), 코어 `DenyModification` 테스트. 실제 드라이브 미검증 |
| 10 | 드라이브 사용 중 원본 불변 | ⚠️ | 코어: 원본 쓰기 잠금 + 해제 후 SHA-256 동일 ✅ / 실제 드라이브 미검증 |
| 11 | 암호화 ZIP/7Z 드라이브 탑재 | ⚠️ | 코어: 암호화 ZIP·7Z 읽기, 잘못된 암호 거부, 암호화 디스크 캐시 ✅ / 실제 드라이브 미검증 |
| 12 | WinFsp 관련 오류 안전 처리 | ✅ | 호스트: WinFsp 미설치 시 JSON 오류 + 종료 코드 3 (실행 확인), `DriveService_ReportsMissingWinFspSafely` |
| 13 | 보안·호환성 테스트 | ⚠️ | 보안 보고서 참조. 퍼징·디스크 가득 참·실제 심볼릭 링크 E2E 미검증. RAR 해제 미검증(테스트용 RAR 생성 도구 없음) |
| 14 | Windows 10/11 설치·제거 | ❌ | MSI 빌드 여부는 아래 기록 참조. 이 PC에서 설치/제거는 시스템 변경이므로 **사용자 승인 없이 실행하지 않음**. Windows 10 미검증 |
| 15 | 계정·인터넷 없이 모든 기능 | ✅ | 네트워크 API 사용 없음(코드). 유일한 외부 링크는 사용자가 누르는 WinFsp 공식 사이트 버튼 |
| 16 | 라이선스 고지·오픈소스 공개 준비 | ⚠️ | LICENSE(MIT), THIRD_PARTY_NOTICES, 라이선스 자동 검사 ✅ / 상표·변호사 검토 ⛔ |

## B. 지식재산권 배포 전 검사 (정책 7장)

| # | 항목 | 상태 |
|---|---|---|
| 1 | 모든 의존성 라이선스 확인 | ✅ `tools/license-check.ps1` 통과 |
| 2 | 외부 소스코드 출처 확인 | ✅ 외부 코드 복사 없음 (LICENSE_REVIEW §3) |
| 3 | 저작권 표시·소스 제공 의무 | ⚠️ 고지 완료. 릴리스 자산에 7-Zip 소스 아카이브 동봉 권장(미실행) |
| 4 | 이미지·폰트·아이콘 권한 | ✅ 아이콘 자체 제작, 시스템 폰트 참조만 |
| 5 | 제품명·로고 상표 조사 | ⛔ 미실행 — "FreeArc X"(기존 FreeArc와 혼동)를 Zipark로 변경함. Zipark 상표 검색은 아직 안 함 |
| 6 | 특허 위험 검토 | ⚠️ 표준 알고리즘만 사용, 법률 검토 없음 |
| 7 | 설치 프로그램 파일 재배포 권한 | ✅ 7-Zip(LGPL, 원문 동봉), .NET(MIT), WixUI(MS-RL) |
| 8 | 공개 저장소 파일 권리 | ✅ |
| 9 | 지식재산권 변호사 최종 검토 | ⛔ 미실행 |

## C. 검증 기록
(아래는 실제 실행 결과만 기록합니다.)

### 2026-11-02 (Windows 11 Home 10.0.26200, x64, 22 logical CPUs, .NET SDK 10.0.403, 7-Zip 26.05)
- `dotnet test Zipark.sln -c Release` → **77 / 77 통과**, 전체 스위트 5회 연속 통과(병렬 실행 기준).
  - 수정한 결함: 병렬 부하에서 `WorkerProcess_CancelStopsAndCleansUp`가 실패 → 원인은 VFS 디코더 작업이 스레드 풀을 막아
    타이머 기반 취소가 늦게 발화한 것(진단: 5.1 s 경과 시점에도 1.5 s 취소 콜백 미발화). 디코더를 전용 스레드로 이동하고
    테스트는 전용 스레드에서 취소하도록 수정. 기각한 가설: 압축이 너무 빨리 끝남(작업이 4.8 s 동안 실제로 실행됨),
    워커의 취소 처리 결함(단독 실행 시 정상 취소).
- `tools/license-check.ps1` → 15개 패키지 모두 허용 목록 통과.
- `python tools/i18n/strings.py` → 367개 문자열 × 한/영, 누락 없음.
- UI: 실제 WPF 렌더러로 11개 화면 + 알림 바를 라이트/한국어, 다크/영어로 렌더링해 육안 확인
  (`Zipark.exe --screenshot-pages`). 발견·수정: 환경설정 선택 시 왼쪽 메뉴 강조가 남던 문제, 솔리드 7z 폴더의 압축 크기 `0 B` 오표시, 형식 표기 `SEVENZIP`.
- 일반 실행/종료: 창 표시, 워커 격리 활성, 종료 코드 0.
- 릴리스 빌드(`build/build-release.ps1`):
  - `Zipark-1.0.0-x64.msi` (50.8 MB, SHA-256 `66c41c42…3b05c527`) — MSI 테이블 읽기 검사: 파일 285개, 앱·워커·VFS 호스트·7-Zip 엔진+License.txt·고지 문서·자체 포함 런타임 포함, 셸 등록/해제 커스텀 액션 포함.
  - `Zipark-1.0.0-x64-portable.zip` (61.7 MB, SHA-256 `996c2050…0330c89`).
  - 자체 포함 빌드는 .NET 미설치 경로에서 실행 확인(DOTNET_ROOT 없이 12개 화면 렌더 성공). 게시된 워커로 실제 압축+검사 작업 성공.
  - 수정한 결함: 워커/호스트가 UTF-8 BOM으로 시작하는 stdin을 거부 → BOM 제거 처리.
- **MSI 설치/제거는 실행하지 않음** (이 PC의 시스템 변경 — 사용자 승인 필요). Windows 10 미검증.
- **WinFsp 미설치** → ZIP/7Z 드라이브 문자 탑재·탐색기 읽기·쓰기 거부의 실제 드라이브 E2E 미검증.
- 성능: `docs/PERFORMANCE_REPORT.md` (128 MB 데이터셋, 3회 중앙값, 실행 순서 교대).

### 2026-11-02 — 제품명 변경(FreeArc X → Zipark), 기본 형식 ZIP
- 프로젝트·네임스페이스·실행 파일(`Zipark.exe`, `Zipark.Worker.exe`, `Zipark.VfsHost.exe`)·설치 프로그램·문서를 Zipark로 변경. MSI UpgradeCode 새로 발급.
- 기본 압축 형식을 ZIP으로 변경(설정 기본값, 형식 목록 첫 항목, 분할 압축 화면). 테스트 `Settings_DefaultFormatIsZip` 추가.
- `dotnet test Zipark.sln -c Release` → **78 / 78 통과**. UI 렌더 확인: 제목·사이드바 "Zipark", 새 압축 화면 형식 기본값 "ZIP".
- `Zipark-1.0.0-x64.msi` (50.6 MB, SHA-256 `e1973b77…f13ecd0e`): 파일 285개, ProductName "Zipark", 이전 이름 파일 0개.
- `Zipark-1.0.0-x64-portable.zip` (61.7 MB, SHA-256 `812cec56…d35669b6`).
- 이전 기록의 FreeArcX 산출물 해시는 더 이상 유효하지 않음(재빌드로 대체).

### 2026-11-03 — MIT 라이선스, 개발자 정보, 사용 설명서·버전 기록
- 라이선스를 MIT(Copyright (c) 2026 GPT PARK)로 변경. 제3자 구성요소(7-Zip LGPL, winfsp.net GPLv3+FLOSS 예외)는 각자 라이선스 유지 — `LICENSE_REVIEW.md` §1 갱신.
- 개발자 GPT PARK, 유튜브·공식 사이트 링크를 앱(홈, 환경설정 → 정보), MSI(제조사·지원 링크), README, 문서에 추가.
- 상세 사용 설명서(`docs/USER_MANUAL.md`, 20개 장)와 버전 기록(`CHANGELOG.md`)을 추가하고, 오프라인 HTML(`Zipark-Manual.html`, `Zipark-VersionHistory.html`)로 변환해 앱 폴더에 포함. 브라우저 렌더링으로 표·중첩 목록·강조 표시 확인.
- `dotnet test Zipark.sln -c Release` → **78 / 78 통과**. UI 렌더 확인(홈 하단 개발자 표시, 환경설정 정보 섹션).
- `Zipark-1.0.0-x64.msi` (50.5 MB, SHA-256 `caee4d35…d713ff86`): Manufacturer "GPT PARK", ARPURLINFOABOUT https://gptparkai.com/, ARPHELPLINK 유튜브.
- `Zipark-1.0.0-x64-portable.zip` (61.7 MB, SHA-256 `69ca0f83…f85f0f2`).

### 2026-11-03 — 1.0.1: 고정 창 크기, 스크롤 없는 레이아웃
- 창 고정(클라이언트 1184×700, 크기 조절 불가·최소화 가능). 작업 영역이 작으면 전체 레이아웃을 비율 축소.
- 홈·새 압축·압축 해제를 다시 배치하고, 분할·암호·환경설정(탭)의 페이지 스크롤을 없앰.
- 자동 레이아웃 검사(`Zipark.exe --screenshot-pages`의 `layout-report.txt`): 각 화면의 자연 높이가 창 높이 안에 들어가는지와 스크롤바 표시 여부를 측정.
  - 한국어/라이트, 영어/다크(전문가 모드), 최악 조건(새 압축의 분할·암호·경고를 모두 펼침, 최근 파일 15개)에서 **19개 화면 모두 넘침 0, 스크롤바 0**. 릴리스 빌드에서도 동일.
  - 발견·수정: 스냅샷 도구가 VisualBrush 늘림 때문에 오른쪽 여백을 실제와 다르게 그리던 문제(1:1 매핑으로 수정).
- 버전 1.0.1 (`Directory.Build.props`에서 단일 관리): MSI ProductVersion 1.0.1.0, Zipark.exe 1.0.1 — 설치된 1.0.0을 업그레이드하도록.
- `dotnet test Zipark.sln -c Release` → **78 / 78 통과**.
- `Zipark-1.0.1-x64.msi` (50.5 MB, SHA-256 `b58f8253…eda95afa`), `Zipark-1.0.1-x64-portable.zip` (61.7 MB, SHA-256 `2d909d75…855b4d9a`).
- 사용자가 1.0.0 MSI를 직접 설치한 것을 확인(설치된 앱: Zipark 1.0.0.0, 게시자 GPT PARK). 설치 시 우클릭 메뉴 등록 커스텀 액션이 동작했음을 레지스트리로 확인 — 기존 "MSI 설치 미검증" 항목 중 설치·셸 등록 부분은 확인됨(제거는 미검증).
