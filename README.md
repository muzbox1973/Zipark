# Zipark (가칭)

Windows 10/11용 **영구 무료·오픈소스 압축 프로그램**. 계정·광고·구독·인터넷 연결이 필요 없고, 파일을 외부로 보내지 않습니다.

> 제품명 **Zipark(집파크)**는 상표 검색 전 단계의 **가칭**입니다. 기존 압축기 FreeArc와의 혼동을 피하려고 초기 가칭 "FreeArc X"에서 변경했습니다 — [docs/LICENSE_REVIEW.md](docs/LICENSE_REVIEW.md).

## 주요 기능
- ZIP · 7Z · TAR · GZIP · BZIP2 · XZ 압축/해제, RAR/RAR5 · ISO · CAB · WIM 등 읽기/해제 (검증된 7-Zip 엔진)
- 압축률 선택, 멀티코어, 진행률·속도·남은 시간, 안전한 취소, 작업 큐, 일괄 해제, 폴더별 개별 압축
- 분할 압축(100MB/700MB/1GB/2GB/4GB/사용자 지정), 첫 볼륨 자동 인식·누락 볼륨 탐지, 일반 파일 분할·병합(SHA-256 검증)
- AES-256 암호화(7Z 파일명 암호화 포함, ZIP AES), 암호 강도 표시, 취약 방식 경고, 암호 비저장
- 압축 내부 탐색·검색·선택 추출, 무결성 검사, CRC32/SHA-256 체크섬
- **가상 드라이브**: ISO(Windows 가상 디스크 API), ZIP/7Z(WinFsp, 읽기 전용, 파일/솔리드 블록 단위 지연 해제, 암호화 캐시)
- 탐색기 우클릭 메뉴, 한국어/영어, 다크/라이트, 고DPI, 초보자/전문가 모드
- 보안: Zip Slip·경로 탐색·심볼릭 링크 차단, 압축 폭탄·디스크 공간 사전 차단, 원본 비변경, 엔진·드라이브 호스트 프로세스 격리

## 빌드
요구 사항: .NET 10 SDK, 7-Zip(엔진 — 테스트 및 동봉용), Windows 10/11 x64.

```bash
dotnet build Zipark.sln -c Release
```

```bash
dotnet test Zipark.sln -c Release
```

릴리스(자체 포함 앱 + 포터블 ZIP + MSI + SBOM):

```bash
pwsh build/build-release.ps1
```

UI 문자열은 `tools/i18n/strings.py` 한 곳에서 관리합니다(실행하면 ko/en XAML 생성 및 누락 검사).

## 문서
- [사용자 매뉴얼](docs/USER_MANUAL.md)
- [아키텍처](docs/ARCHITECTURE.md)
- [성능 테스트 보고서](docs/PERFORMANCE_REPORT.md)
- [보안 테스트 보고서](docs/SECURITY_REPORT.md)
- [릴리스 체크리스트 (검증/미검증 현황)](docs/RELEASE_CHECKLIST.md)
- [라이선스·지식재산권 검토](docs/LICENSE_REVIEW.md)
- [제3자 고지](THIRD_PARTY_NOTICES.md)

## 라이선스
**MIT License** ([LICENSE](LICENSE)) — Copyright (c) 2026 GPT PARK.
동봉된 제3자 구성요소는 각자의 라이선스를 따릅니다: 7-Zip(LGPL-2.1-or-later + unRAR 제한), winfsp.net(GPLv3 + FLOSS 예외), .NET(MIT) — [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

---

### English
Zipark (working title) is a free, open-source archiver for Windows 10/11: ZIP/7Z/TAR/GZ/XZ create & extract,
RAR/ISO extract, AES-256, split volumes, checksums, a job queue, Explorer integration, and ISO/ZIP/7Z virtual drives
(ZIP/7Z via WinFsp, read-only, lazy per-file / per-solid-block decoding). No account, ads, or network access.
See the documents above (Korean) for details.
