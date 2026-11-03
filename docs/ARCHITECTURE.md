# Zipark 아키텍처

## 프로세스 구성 (장애 격리)

```
Zipark.exe (WPF UI, MVVM)
 ├─ Zipark.Worker.exe     작업 1개당 1프로세스. JSON 줄 프로토콜(stdin/stdout). 암호는 stdin 두 번째 줄.
 │    └─ 7z.exe             실제 압축/해제 엔진(7-Zip). 암호는 stdin, 명령줄에 절대 노출 안 함.
 ├─ Zipark.VfsHost.exe    ZIP/7Z 드라이브 1개당 1프로세스 (WinFsp 사용자 모드 파일 시스템)
 │    └─ 7z.exe -so         필요한 파일/솔리드 블록만 스트리밍 디코드
 └─ virtdisk.dll            ISO 탑재 (Windows 기본 API, 별도 프로세스 없음)
```

모든 자식 프로세스는 **Kill-on-close Job Object**(`ProcessJob`)에 등록되어, UI가 비정상 종료되어도 엔진이 남아 압축 파일을 잡고 있지 않습니다. 엔진이 크래시하면 해당 작업만 `EngineCrashed`로 실패합니다.

## 모듈

| 프로젝트 | 역할 |
|---|---|
| Zipark.Core | 형식 판별(시그니처 스니핑), 경로 안전성(Zip Slip), 압축 폭탄 가드, 충돌 해결, 디스크 공간, 체크섬(CRC32/SHA-256), 진행률·ETA, 로깅, Job Object |
| Zipark.Crypto | `SecretPassword`(고정·소거되는 char 버퍼), 암호 강도, 암호화 권고, AES-256-GCM 청크 암호(`ChunkCipher`) |
| Zipark.Compression | 7-Zip 엔진 어댑터: 목록(-slt 파서), 압축(임시 파일→원자적 이름 변경), 안전 해제(스테이징→검증→이동), 검사, `x -so` 스트림 |
| Zipark.ArchiveExplorer | 압축 내부 트리, 검색(와일드카드), 선택 → 추출 목록 |
| Zipark.SplitVolumes | 볼륨 세트 탐지(.001 / .z01+.zip / .partN.rar / .r00), 누락 탐지, 일반 파일 분할·병합(SHA-256 매니페스트) |
| Zipark.Cache | RAM LRU + 디스크 LRU 하이브리드 청크 캐시. 디스크는 세션 키 AES-GCM 암호화, 크기 상한, 비정상 종료 세션 정리 |
| Zipark.VirtualFileSystem | WinFsp와 무관한 읽기 전용 파일 시스템 코어: `ArchiveIndex`(경로 인덱스), `ArchiveContentProvider`(디코드 그룹·지연 해제), `ReadOnlyArchiveFileSystem`(원본 쓰기 잠금, 상태 코드) |
| Zipark.VirtualFileSystem.Host | WinFsp `FileSystemBase` 어댑터(쓰기 콜백 전부 `STATUS_MEDIA_WRITE_PROTECTED`, 읽기 전용 보안 설명자) + 호스트 프로세스 |
| Zipark.VirtualDisk.ISO | `OpenVirtualDisk`/`AttachVirtualDisk`(읽기 전용, 영구 수명)/`DetachVirtualDisk`, 드라이브 문자 매핑 |
| Zipark.ShellIntegration | HKCU 우클릭 메뉴(계단식), OpenWithProgids, 명령줄 파서 |
| Zipark.Settings | JSON 설정(원자적 저장, 손상 시 기본값) — 암호 필드 없음 |
| Zipark.Application | 작업 요청/결과 모델, `JobRunner`, 작업 큐, 워커 프로세스 실행기, 가상 드라이브 서비스 |
| Zipark.Worker | 작업 실행 프로세스 |
| Zipark.Desktop | WPF UI(11개 화면), 다크/라이트, 한/영, 단일 인스턴스 + 탐색기 다중 선택 일괄 처리 |
| Zipark.Tests | xUnit 테스트 |

## ZIP/7Z 가상 드라이브의 지연 해제

임시 폴더에 전체를 푸는 방식은 쓰지 않습니다.

1. 탑재 시 `7z l -slt`로 목록만 읽어 `ArchiveIndex`를 만듭니다(7z는 `Block` 번호 포함).
2. 엔트리를 **디코드 그룹**으로 묶습니다. 7z는 솔리드 블록 단위, ZIP·비솔리드는 파일 단위.
3. 어떤 파일의 바이트가 처음 읽히면 그 그룹 하나만 `7z x -so -i@목록`으로 스트리밍 디코드합니다. 출력은 압축 파일 순서대로 이어지므로 목록의 크기로 파일 경계를 나눠 1MB 청크로 캐시에 넣습니다.
4. 같은 블록의 다른 파일은 부산물로 캐시되어 다시 디코드하지 않습니다(테스트: 5개 파일 읽기에 디코드 1회).
5. 요청된 마지막 파일 뒤로 64MB 선행 읽기를 넘으면 디코드를 중단해, 거대한 블록 앞쪽의 작은 파일은 싸게 열립니다.
6. 암호화되지 않은 Deflate/Store ZIP 엔트리는 프로세스 생성 없이 `System.IO.Compression`으로 바로 읽습니다.
7. 대기 중인 리더에게는 캐시와 별도로 직접 전달(handoff)하므로 캐시가 아주 작아도 진행이 보장됩니다.

## 보안 경계 요약

- 암호: `SecretPassword`(pinned, 소거) → 엔진 stdin. 설정·로그·명령줄·디스크에 기록하지 않음.
- 디스크 캐시: 세션마다 무작위 AES-256 키(메모리에만 존재), 청크마다 GCM 인증. 프로세스 종료 시 폴더 삭제, 다음 실행 시 고아 세션 정리.
- 해제: 목록 사전 검사 → 스테이징 폴더에 해제 → 재파싱 포인트 제거·경로 재검증 → 대상 경로 체인에 정션이 없는지 확인 → 충돌 정책에 따라 이동.
- 압축 생성: 원본은 읽기만. 결과는 임시 이름으로 쓰고 성공 시 이름 변경. 취소·실패 시 임시 파일만 삭제.
