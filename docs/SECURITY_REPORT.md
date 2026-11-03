# Zipark — 보안 테스트 보고서 (1.0)

자동화 테스트(`tests/Zipark.Tests`)로 검증한 위협별 방어와 그 결과입니다. 결과 열은 이 문서 작성 시점의 실제 테스트 실행 결과입니다(아래 "실행 기록" 참고). 테스트로 확인하지 못한 항목은 **미검증**으로 표시합니다.

| 위협 | 방어 | 테스트 | 결과 |
|---|---|---|---|
| Zip Slip / 경로 탐색 (`../`, `..\..\`, `a/../../`) | 목록 단계에서 차단 + 스테이징 해제 후 경로 재검증 + `ResolveUnder` | `EngineTests.ZipSlip_EntriesAreNeverWrittenOutsideDestination`, `SecurityUnitTests.PathSafety_*` | 통과 |
| 절대 경로·UNC·드라이브 문자 | `PathSafety.Check` → Absolute | 동일 | 통과 |
| NTFS 대체 데이터 스트림(`file:stream`) | AlternateStream 차단 | 동일 | 통과 |
| 예약 장치 이름(CON, NUL, COM1…) | ReservedName 차단 | 동일 | 통과 |
| 심볼릭 링크·정션 악용 | 링크 항목 경고·제외, 스테이징 내 재파싱 포인트 삭제(링크만), 대상 경로 체인의 정션 검사 | `BombGuard_FlagsRatioCountAndDuplicates`, `MoveIntoPlace`(코드) | 부분 통과 — 실제 심볼릭 링크를 담은 아카이브로 해제하는 E2E 테스트는 **미검증**(테스트 환경에서 링크 생성 권한 필요) |
| 압축 폭탄 | 해제 전 전체 크기·압축률·항목 수·깊이 검사, 사용자 명시 승인 시에만 진행 | `ArchiveBomb_HighRatioIsBlocked` (32MB 0-바이트 → 차단, 디스크에 아무것도 쓰지 않음) | 통과 |
| 디스크 공간 부족 | 해제 전 여유 공간 검사, 7-Zip "not enough space" → `DiskFull`, 실패 시 부분 결과 삭제 | 사전 검사 코드 경로 | **미검증**(가득 찬 볼륨 재현 안 함) |
| 손상된 압축 파일 | `7z t`/해제 오류를 `CorruptArchive`로 분류, 실패 시 스테이징 삭제 | `Test_DetectsCorruption` | 통과 |
| 누락·손상 볼륨 | 볼륨 세트 탐지, 조각별 SHA-256 | `SplitVolumes_MissingMiddleVolume_IsReported`, `RawMerge_DetectsMissingAndDamagedParts`, `Detector_*` | 통과 |
| 잘못된 암호 | `WrongPassword` / `PasswordRequired` 구분, 부분 결과 남기지 않음 | `Zip_Aes256_Roundtrip_AndWrongPasswordDetected`, `SevenZip_Aes256_WithEncryptedNames`, `OpenEntries_WrongPasswordThrowsAtEnd`, `WrongPassword_IsRejectedAtMount` | 통과 |
| 암호 평문 유출 | 명령줄 대신 stdin, `SecretPassword` 소거, 설정 파일에 암호 필드 없음, 로그 비기록 | `SecretPassword_IsWipedOnDispose`, `Settings_RoundTripWithoutSecrets`, 7z 파일에 암호·파일명 평문 없음 확인 | 통과 |
| 임시 평문 유출(가상 드라이브 캐시) | 디스크 캐시 AES-256-GCM(세션 키, 메모리 전용), 위·변조 시 거부, 종료 시 삭제 | `Memory_LruEvictsAndSpillsToEncryptedDisk`, `TamperedDiskChunk_IsRejected`, `Disk_LimitIsHardCap_AndDisposeRemovesFiles` | 통과 |
| 과도한 메모리 사용 | 캐시 RAM/디스크 상한(하드 캡), 1MB 청크 스트리밍 | `SmallCache_StillServesLargeFile` (2MB 캐시로 6MB 파일 정상 제공) | 통과 |
| 파일명 충돌 | 기본 "둘 다 유지", 건너뛰기/덮어쓰기는 선택 시에만 | `Conflicts_RenameByDefault_SkipAndOverwriteOnRequest` | 통과 |
| 권한 거부 | `UnauthorizedAccessException` → `AccessDenied` 분류 | 코드 경로 | **미검증** |
| 네이티브 엔진 충돌 | 7z.exe·워커·VFS 호스트 별도 프로세스 + Kill-on-close Job Object | `WorkerProcess_*` (워커 격리 동작), 취소 시 정리 | 통과 (엔진 강제 크래시 주입 테스트는 미검증) |
| 가상 드라이브 원본 변경 | 탑재 중 원본에 쓰기 거부 잠금(FileShare.Read), 모든 쓰기 콜백 `STATUS_MEDIA_WRITE_PROTECTED`, 읽기 전용 보안 설명자 | `MountedArchive_FilesReadBackIdentical` (쓰기 열기 실패 + 해제 후 SHA-256 동일) | 코어 통과 / **WinFsp 실제 드라이브 E2E는 미검증**(WinFsp 미설치) |
| ISO 원본 변경 | 읽기 전용 Attach | `Iso_MountReadUnmount_WithWindowsVirtualDiskApi` (쓰기 실패, 해제 후 SHA-256 동일) | 통과 |
| 안전하지 않은 이름이 가상 드라이브에 노출 | 인덱스 생성 시 제외 | `UnsafeEntryNames_AreNotExposed` | 통과 |

## 퍼징
- 목록 파서·경로 검사기·볼륨 탐지기는 단위 테스트로 경계값을 다루지만, **커버리지 기반 퍼징(예: SharpFuzz)은 아직 수행하지 않았습니다(미검증)**. 압축 형식 파싱 자체는 7-Zip 엔진이 별도 프로세스에서 수행하므로, 엔진 크래시는 해당 작업 실패로 격리됩니다.

## 실행 기록
테스트 실행 결과는 `docs/RELEASE_CHECKLIST.md`의 "검증 기록"에 날짜와 함께 남깁니다.
