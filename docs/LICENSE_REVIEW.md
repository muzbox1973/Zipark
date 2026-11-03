# 라이선스·지식재산권 검토 (Zipark 1.0)

> 이 문서는 개발팀의 기술적 검토 결과입니다. **법률 자문이 아니며**, 정식 배포 전 지식재산권 전문 변호사의 최종 검토를 권장합니다(체크리스트 9번).

## 1. 프로젝트 라이선스: MIT (Copyright (c) 2026 GPT PARK)

초기 후보였던 GPL-3.0-or-later 대신 개발자 결정에 따라 **MIT**로 확정했습니다. MIT는 Zipark가 직접 작성한 코드에만 적용되며, 동봉하는 제3자 구성요소는 각자의 라이선스를 그대로 유지합니다.

| 구성요소 | 라이선스 | MIT 프로젝트와의 관계 | 결과 |
|---|---|---|---|
| Zipark 소스 (이 저장소) | MIT | — | 독자 작성 코드 |
| 7-Zip 26.05 (`7z.exe`, `7z.dll`) | LGPL-2.1-or-later + BSD + unRAR 제한 | 별도 프로세스로 실행(링크 없음, 단순 동봉). 수정 없이 라이선스 원문과 함께 배포 | ✅ |
| winfsp.net (`winfsp-msil.dll`) | GPLv3 + FLOSS 예외 | FLOSS 예외가 MIT 등 승인된 오픈소스 라이선스 소프트웨어와의 결합·배포를 허용. DLL 자체는 GPLv3+예외로 유지, 수정 없음 | ✅ (배포 전 WinFsp `License.txt`의 예외 목록 원문 재확인 권장) |
| WinFsp 드라이버 | GPLv3 + FLOSS 예외 | **동봉하지 않음** — 사용자가 공식 사이트에서 설치 | ✅ |
| .NET 런타임, System.IO.Hashing | MIT | 동일 | ✅ |
| WiX Toolset v5 (빌드 도구, WixUI) | MS-RL | 빌드 도구. MSI에 WixUI 리소스 포함 | ✅ (고지 포함) |
| xUnit 등 테스트 도구 | Apache-2.0 / MIT | 배포 안 함 | ✅ |

**주의**: MIT로 배포하더라도 설치 패키지에는 LGPL(7-Zip)과 GPLv3+예외(winfsp.net) 구성요소가 포함되므로, 해당 라이선스 원문·소스 위치 고지(`THIRD_PARTY_NOTICES.md`, `engine\License.txt`)를 계속 함께 배포해야 합니다.

## 2. 압축 엔진 관련 의무

- 7-Zip 바이너리는 **수정 없이** `engine\` 폴더에 넣고 `License.txt` 원문을 함께 배포합니다(`build/build-release.ps1`).
- 소스 제공: 7-Zip 공식 소스 위치를 `THIRD_PARTY_NOTICES.md`에 명시. 재배포 시 같은 버전의 소스 아카이브를 릴리스 자산으로 함께 올리는 것을 권장합니다(체크리스트 항목).
- 재링크(교체) 가능성: 사용자는 환경설정에서 다른 7-Zip 빌드를 지정할 수 있습니다.
- **unRAR 제한**: Zipark는 RAR **생성 기능이 없고** RAR 압축 알고리즘을 재구현하지 않습니다. RAR은 7-Zip을 통한 읽기/해제만 지원합니다.

## 3. 소스코드 출처

- 모든 C#/XAML/스크립트는 이 프로젝트를 위해 새로 작성되었습니다. 타사 비공개 코드나 출처 불명 코드 조각을 복사하지 않았습니다.
- 외부 API 사용 방식(WinFsp .NET `FileSystemBase` 콜백 시그니처, Windows `virtdisk.dll` API, Job Object API)은 공개 문서·공개 패키지의 메타데이터를 기준으로 작성했습니다.
- AI 보조로 작성된 코드도 기존 프로젝트와의 실질적 유사성을 피하도록 독자 구조(예: 솔리드 블록 디코드 그룹, 세션 키 암호화 청크 캐시)로 설계했습니다. 외부 코드를 추가할 때는 `THIRD_PARTY_NOTICES.md`에 출처·버전·라이선스·수정 여부를 기록해야 합니다.

## 4. UI·디자인

- 반디집·7-Zip·WinRAR 화면을 복제하지 않았습니다. 좌측 내비게이션 + 카드형 레이아웃은 범용 Windows 11 UX 패턴입니다.
- 앱 아이콘/로고는 `tools/branding/make-icon.ps1`로 생성한 고유 그래픽입니다(청록 타일 + 흰색 X 밴드 + 호박색 고리).
- 글꼴·아이콘 글리프는 Windows 시스템 글꼴을 참조만 하며 재배포하지 않습니다.

## 5. 제품명·상표 (⚠ 상표 검색 전 — 배포 차단 항목)

- 초기 가칭 "FreeArc X"는 Bulat Ziganshin의 기존 오픈소스 압축 프로그램 **FreeArc**와 혼동될 수 있어 폐기하고, 제품명을 **Zipark(집파크)**로 바꿨습니다. "Zip + Park"(파일을 세워 두는 곳)와 "Zip + Ark"(파일을 싣는 방주)를 겹친 조어입니다.
- 조어라 식별력은 높은 편이지만 **상표·동일 명칭 검색은 아직 하지 않았습니다**. 정식 배포 전:
  1. 한국(KIPRIS), 미국(USPTO), EU(EUIPO) 상표 검색 — 지정상품 제9류(소프트웨어)
  2. 기존 소프트웨어·서비스(주차 서비스 포함)와 동일·유사 명칭 여부 확인
  3. 도메인, GitHub 조직/저장소 이름 확보
- 문제가 생기면 이름은 `Directory.Build.props`(Product), `tools/i18n/strings.py`, 설치 프로그램, 문서에만 모여 있어 다시 바꾸기 쉽습니다.

## 6. 특허

- AES-256(공개 표준), LZMA/LZMA2·Deflate(7-Zip 구현 사용), ZIP AES(WinZip AE-2 공개 사양)만 사용합니다. 자체 압축·암호 알고리즘은 없습니다.
- 알려진 특허 위험은 발견되지 않았으나, 이는 법률적 결론이 아닙니다.

## 7. 자동 라이선스 검사

- `tools/license-check.ps1`이 `dotnet list package --include-transitive` 결과를 `tools/license-allowlist.json`과 대조합니다. 허용 목록에 없는 패키지가 추가되면 CI가 실패하여 병합이 차단됩니다.
