# DocumentCompare: 문서·코드 통합 비교 / 버전관리 / Rust 전환 1차 기술 검토

- 검토일: 2026-10-20
- 기준 저장소: `blodburn/documentcomparison`
- 기준 커밋: `9844154` (`v5.21.11`, `origin/main`)
- 검토 범위: 저장소 소스 및 회귀 테스트의 정적 검토, 공통 모델 초안, 이행·검증 계획
- 범위 제외: Rust 실측 성능 비교, 코드 AST PoC, UI 동작 수동 검증, Word/HWPX 완전 렌더링
- 상태: **1차 아키텍처 리뷰**. 구현 승인이나 Rust 전면 전환 결정이 아님.

## 1. 결론

**권고: 기존 C# 문서 엔진과 Avalonia UI는 유지하면서, 통합 버전 저장 모델과 코드 전용 비교 엔진을 독립적으로 신설한다. Rust 채택 여부는 동일 입력의 정확도·속도·메모리·배포 검증 후 결정한다.**

지금은 버전 관리 기반과 코드 구조 분석을 설계할 시점이지, 기존 문서 비교·Word 변경추적 엔진을 일괄 포팅할 시점이 아니다. 이 원칙을 지키지 않으면 이미 교정한 조/항/호 계보·이동·변경표시/서식 로직의 회귀 위험이 크다.

## 2. 실제 소스에서 확인한 현황

| 구성 | 확인한 구현 | 평가 |
|---|---|---|
| 실행/배포 | `.NET 8`, `Avalonia 12.1.2`, self-contained Windows x64 single-file | 당분간 UI 유지 권장. Rust UI 재작성 효용 불확실 |
| 문서 입력 | `NativeDocumentReader.ReadAsync`: `.docx` / `.txt`, 파일 크기 40MiB 제한 | PDF/HWPX/코드 프로젝트는 새 어댑터 필요 |
| 문서 비교 | `NativeComparisonEngine` 약 2,992줄, 2~3개 파일·조/항/호/일반 단위 비교 | 기존 Golden Reference. 직접 Rust 포팅 보류 |
| 문서 내보내기 | `NativeOfficeExporter` 약 3,473줄, XLSX/Word 변경추적 | C# 유지, 비교 결과 DTO 이후에 어댑터로 연결 |
| 버전 저장 | `VersionHistoryProjectVm` = 프로젝트명 + `List<VersionFileVm>`; 각 파일은 `Path`, `Sha256`, `AddedAtUtc` | **스냅샷 아님**. 원본 변경/이동 후 동일 버전을 재현할 수 없음 |
| 버전 프로젝트 | `VersionHistoryControl`에서 `.dcv.json`으로 경로 목록 직렬화 | 버전 노드/부모/분기/병합, 재현 가능한 내부 저장소 부재 |
| 파일 등록 | `VersionFileImportPolicy`의 경로 중복 검사 + SHA256 해시 | 동일 경로의 다른 시점 버전을 저장하지 못하는 구조 |
| 변경정보 | `ComparisonRowVm`, `MarkerVm`, `VersionHistoryChangeSequencer` 등 | 화면행/마커/표시번호가 엔진 데이터에 강하게 결합 |
| 구조 판정 | 조/항/호용 텍스트 처리, 문자열 상태 메시지 분류 | 코드 AST와 판정 메커니즘을 분리해야 함 |
| 테스트 | `tests/DocumentCompare.Regression/Program.cs`에 스크립트형 회귀 검사, 검사용 OpenXML 3.3.0 의존 | 이미 구축된 검증 기준. 코드/저장소 테스트 별도 추가 필요 |
| CI | 기준 트리에서 GitHub Actions 워크플로 파일 확인되지 않음 | 코드·Rust 병행 작업 전 CI 필요 |

**중요한 구분**: `NativeComparisonEngine.CompareAsync`는 비교 도중 파일 상태가 바뀌는지 검사하고, 내보내기에도 파일 상태 검증이 있다. 그러나 이는 '비교 결과와 당시 파일이 일치하는가'를 확인하는 안전장치이지, 과거 버전의 파일 **내용 자체를 보존하는 기능**은 아니다.

### 2.1 즉시 구조 개선이 필요한 이유

1. `CompareAsync`는 `IReadOnlyList<string> paths`와 2~3개 문서를 전제로 한다. 프로젝트 트리/커밋 단위 비교와 다르다.
2. `VersionFileVm.Path`와 `Sha256`만으로는 그 시점의 콘텐츠 복원이 불가능하다. 경로 이동, 원본 삭제, 파일 덮어쓰기에 취약하다.
3. `MemberVm`은 `Header`, `Body`, `Text`를 기본으로 하고 마커 위치는 문자열 오프셋/화면행에 연결되어 있다. 파일 경로·AST 노드·커밋·문서 노드로 확장하려면 위치 추상화가 필요하다.
4. 버전 선택의 `Before`/`After`는 UI 인덱스(`_beforeIndex`, `_afterIndex`)다. 새 공통 계보 모델에는 안정적인 버전 ID가 필요하다.
5. 구조 변경 메시지 등은 문서 도메인별 문자열 규칙을 사용한다. 코드의 이동/이름 변경/시그니처 변경을 여기에 섞으면 잘못된 유형 판정 가능성이 높아진다.
6. 출력은 화면, Word 변경추적, XLSX마다 표시 요구사항이 다르다. **판정 데이터와 표현 DTO를 분리**해야 기존 동작이 보존된다.

## 3. 목표 아키텍처 (초안)

```text
[Sources]
  DOCX/TXT/HWPX/PDF -- Document Adapters
  Code files/folders  -- Code Adapters (Text, AST)
  Git repositories    -- Git Import Adapter
           |                         |
           +-----------+-------------+
                       v
         [Immutable Version/Blob Store]
           Project / Version DAG / Snapshot Manifest
                       |
                       v
       [Change Evaluation Orchestrator]
           |                       |
    Document Analyzer         Code Analyzer
    (기존 C# 유지)            (Rust PoC 후보)
           |                       |
           +-----------+-----------+
                       v
           [Common ChangeEvent + Location]
                       |
           +-----------+-----------+
           |           |           |
      Avalonia UI   Exports    History/Search
     (type viewer)  (Word/XLSX)  (indexed DB)
```

- **저장과 판정은 별도 계층**: 스냅샷 생성 시점과 비교 시점을 분리하고, 원본 파일을 수정하지 않는다.
- **도메인 파서는 별도 계층**: 문서의 조/항/호와 코드의 AST를 같은 문법 구조로 강제하지 않는다.
- **변경 이벤트는 공통 계층**: 추가·삭제·변경·이동·이름 변경·속성 변경과 위치 정보를 공유한다.
- **표시번호는 파생 데이터**: 화면 `[1]`, `[2]`는 비교 시 선택한 정렬에서 다시 계산하며 영구 ID로 사용하지 않는다.
- **UI는 선택한 버전 한 개를 표시**하되, 전/후 차이 Overlay 및 변경목록으로 이동한다. 코드 모드에서는 해당 파일/함수 위치에 연결한다.

## 4. 공통 버전 저장 모델 (초안)

| 엔티티 | 핵심 필드 | 의미 |
|---|---|---|
| `Project` | `projectId`, `name`, `kind`, `repositoryRef?` | 문서 묶음 또는 코드 프로젝트 |
| `Version` | `versionId`, `parentVersionIds[]`, `snapshotId`, `sourceRef?`, `createdAtUtc` | 순차 버전/브랜치/병합을 나타내는 노드 |
| `Snapshot` | `snapshotId`, `rootManifestId`, `schemaVersion` | 특정 시점에 복원 가능한 파일 트리 |
| `ManifestEntry` | `relativePath`, `blobId`, `entryKind`, `size`, `metadata` | 파일/디렉터리/필요 시 심볼릭 링크 정보 |
| `Blob` | `blobId`, `size`, `data` | SHA-256 기반 불변 콘텐츠 저장; 충돌 시 크기·바이트 검증 |
| `LogicalFile` | `fileId`, `pathAtVersion`, `blobId` | 파일 이동·이름변경 전후의 논리적 파일 계보 |
| `ChangeEvent` | `eventId`, `kind`, `beforeLocation?`, `afterLocation?`, `evidence`, `confidence?` | 비교에서 관찰된 변경 사실 및 근거 |

### 저장 규칙

- **내용 주소 기반 저장소**(content-addressed store): 버전 간 동일 파일은 중복 저장하지 않는다.
- 파일 경로는 스냅샷 내 상대 경로로 저장한다. 과거 버전 재현에 원본 경로가 필요하지 않아야 한다.
- Git 프로젝트는 Git blob/tree/commit을 직접 참조하는 어댑터로 시작할 수 있다. Git 커밋을 앱의 자체 Git 구현으로 재저장하는 일을 피한다.
- 비-Git 문서는 앱 자체 스냅샷을 저장하고 `v0.1 -> ... -> final_v3` 선형 계보를 기본 지원한다. 내부 구조는 처음부터 다중 부모(DAG)로 설계한다.
- 버전별 콘텐츠의 해시, 출처, 등록 시각을 보존한다. 원본 파일 이동·삭제·내용 변경 후에도 과거 스냅샷이 열려야 한다.
- `dcv.json` 이전에는 **기존 경로 기반 프로젝트를 읽는 마이그레이션**을 제공한다. 원본이 남아 있으면 스냅샷 가져오기를 제안하고, 없으면 유실 경로를 명확하게 표시한다.
- 스냅샷 가져오기 시 파일 크기, 디렉터리 범위, 심볼릭 링크, 비밀 파일, `.gitignore`/앱 자체 제외 규칙을 확인한다. 파일 손상·zip bomb·경로 순회도 방어한다.

### 변경 이벤트 예시 (초안)

```json
{
  "schemaVersion": 1,
  "eventId": "opaque-stable-id",
  "kind": "rename_symbol",
  "sourceFormat": "rust",
  "before": {"versionId": "v2", "path": "src/parser.rs", "nodeId": "symbol:A", "range": {"startByte": 20, "endByte": 35}},
  "after":  {"versionId": "v3", "path": "src/parser.rs", "nodeId": "symbol:A", "range": {"startByte": 20, "endByte": 40}},
  "details": {"oldName": "parse_doc", "newName": "parse_document"},
  "evidence": "same-scope-body-and-callsite",
  "confidence": 0.91
}
```

위 값은 데이터 형식 예시이며 실제 소스에서 관찰한 변경이 아니다. **rename/move 판정은 추론**이므로 `confidence`와 근거를 보존하고, 불확실한 경우 사용자에게 추정이라고 표시해야 한다.

### 위치 표현에서 주의할 사항

- 코드: 경로 + 바이트 범위 + 라인/열 + AST 노드 ID(가능 시) + 심볼 식별자.
- DOCX: 문서 노드/문단/런의 안정적인 주소 + 문자열 범위 + 스타일 식별자.
- 렌더링: 페이지 번호, 화면 좌표/사각형은 별도의 Viewer 위치다. **문서의 영구 식별자로 사용하지 않는다.**
- C# 문자열 오프셋(UTF-16 코드 단위)과 Rust `String`의 UTF-8 바이트 오프셋은 다르다. 한글·이모지·합성문자·줄바꿈 테스트 없이 FFI 데이터 계약을 확정하지 않는다.
- `eventId`는 선택된 diff 실행마다 변하는 `[n]` 표시번호와 독립적이다.

## 5. 코드 비교 기능: 구현/검증 순서

| 단계 | 지원 대상 | 구현 접근 | 검증 기준 |
|---|---|---|---|
| C0 | 단일 텍스트/코드 파일 | 라인·토큰 diff, 인코딩·개행 보존, 언어 자동판별 | 동일·변경·추가·삭제 / 긴 줄 / 한글 / BOM / CRLF |
| C1 | 프로젝트 폴더 전체 | 스냅샷 manifest 비교, 추가·삭제·이동·이름변경 후보 식별 | 파일 경로 바뀜, 동일 내용 이동, 동일 이름 내용 교체 |
| C2 | 주요 언어 AST | tree-sitter 등 언어별 문법 트리로 함수/클래스/선언 단위 추적 | 함수 이동, 이름변경, 블록 분리/병합, 포맷팅-only |
| C3 | Git 리포지토리 | 커밋/브랜치/태그 가져오기; 사용자가 두 커밋을 전/후 지정 | cherry-pick, merge commit, rename, deleted branch |
| C4 | 영향 분석 | 호출/참조 변경 및 API 시그니처 비교 (지원 언어 우선) | 추정과 확정 구분; 실제 동작 동일성 과도한 주장 금지 |

초기 추천 언어: **Rust + C# + Python + TypeScript/JavaScript**. 언어마다 AST grammar·모듈 시스템·심볼 의미가 달라 C2 이후 정밀도는 별도 평가한다. 코드 서식(들여쓰기/공백) 변경은 의미 변경과 반드시 분리한다.

기본 UI는 기존 3열 구성(버전 트리/선택 버전 한 화면/변경 목록)을 재사용하되, 코드 프로젝트는 **폴더 트리와 선택된 파일의 코드 뷰어**를 제공한다. 변경번호 선택은 `eventId -> 위치`로 이동하고, 화면 표시번호는 문서/파일 순서에 따라 재계산한다.

## 6. Rust 채택 옵션과 기술 판정

| 옵션 | 이익 | 위험/비용 | 1차 판단 |
|---|---|---|---|
| A. C#만 확장 | 기존 배포·테스트 재사용, 개발 중단 최소 | AST·Git·대용량 처리를 C#에 새로 구현 | 실험 대조군 |
| B. C# UI+문서 엔진 유지, Rust에 **신규 코드 엔진** | 위험 격리, 언어/AST/메모리 최적화 가능, 향후 서버 재사용 | FFI 또는 IPC, 다중 플랫폼 바이너리, 디버깅 복잡도 | **PoC 1순위** |
| C. 문서 비교까지 Rust 이행 | 단일 Native Core 가능 | 조항 판정 회귀, Word 내보내기 연결, 긴 이전 기간 | Golden Test 충분한 이후 재평가 |
| D. UI까지 Rust 재작성 | .NET 의존성 제거 가능 | 기존 Avalonia 기능 대량 재구현, 레이아웃 엔진 정확도 개선과 별개 | 현 단계 보류 |

- 비교 연산 속도만으로 선택하지 않는다. **프로젝트 총 실행시간, 최대 메모리, 콜드 스타트, 크로스 플랫폼, 크래시/오류 보고, 작업량, 파일 배포 복잡도**를 함께 측정한다.
- Native Rust DLL을 사용하면 현재의 `EXE 1개` 배포 요구에 추가 설계가 필요하다. P/Invoke용 DLL을 별도 배포하거나 EXE에 포함·실행 시 추출하거나 정적 연결하는 방법의 트레이드오프를 검증한다.
- PoC 단계에는 Rust 독립 CLI + JSON Lines로 기능과 정확도를 우선 검증하고, 결과가 긍정적이면 프로덕션 경계(버전이 명시된 C ABI 또는 프로세스 IPC) 선택을 확정하는 방식을 권장한다.
- Rust panic이나 FFI 메모리 소유권을 C# UI로 노출하지 않도록 오류/버퍼 수명 계약을 반드시 정의한다.
- 실제 서버의 `rustc`, `cargo`는 현재 확인되지 않았다. **Rust 기반 속도 비교 결과는 아직 없으며 우월하다고 판정하지 않았다.**

## 7. PoC 실행 계획과 합격 기준

### P0: 기준선·데이터셋 고정

- 현행 `tests/DocumentCompare.Regression` 전체 테스트를 정리하고, 법률/일반/표/서식/이동/3문서 사례를 버전별 JSON Golden 출력으로 별도 저장한다.
- 실제 문서 및 코드는 민감정보·개인정보를 제거한 테스트 fixture만 사용한다. 프로젝트의 공개 저장소에는 원문 비공개 자료를 넣지 않는다.
- Golden 결과에는 문서 노드, 변경 유형, 전/후 위치, 판정 근거와 출력별 표현 차이를 분리한다.
- 성능 기준은 **동일 데이터, 동일 환경, 워밍업 후 5회 이상 실행**하여 중앙값·최대 RSS(가능한 플랫폼)·처리량을 기록한다.

### P1: 불변 스냅샷 PoC

| 테스트 | 기대 결과 |
|---|---|
| `v0.1` 가져온 후 원본 파일 덮어쓰기 | 저장된 `v0.1` 내용/해시 유지 |
| 원본 경로 삭제/이동 | 프로젝트에서 과거 버전 정상 열기 |
| 같은 경로에서 다른 시점 재등록 | 별개의 버전으로 저장 |
| 동일 콘텐츠를 여러 버전에서 공유 | blob 중복 저장 없음 |
| 다수 파일·빈 폴더·유니코드 경로 | manifest 재현 정상 |
| 브랜치/merge parent 2개 | DAG 무결성 정상 |
| 같은 이름의 서로 다른 파일/파일 리네임 | 파일 계보와 path history를 구분 |
| 스냅샷 중단/손상·경로 순회 | 일관성 유지, 읽기 오류 명확히 보고 |

### P2: 코드 비교 PoC

- 저장소 폴더 `A → B`를 비교하여 **파일 단위 추가/삭제/이동/이름변경**을 먼저 판정한다.
- Rust, C#, Python, TypeScript를 대상으로 함수 선언 추가/삭제, 함수 이름변경, 같은 함수의 파일 간 이동, 인자 목록 변화, 본문 변경을 검증한다.
- 공백·코드 포맷만 바꾼 사례에서 의미 변경으로 단정하지 않는지 측정한다.
- tree-sitter 기반 AST 추출 여부/버전, 파서 오류 복구, 문법 불완전 코드 처리 등을 기록한다.
- 원본 파일의 UTF-8 바이트 범위를 Rust 측에서 내보내고 C# UTF-16/텍스트 UI 범위와 비교한다.

### P3: Rust vs C# 성능·운용 PoC

- **기능 동일성 먼저**: 공통 모델의 파일/문단/변경 이벤트를 동일 JSON 정규형으로 출력해 비교한다.
- 다음 4개 규모에서 결과 측정: ① 10파일/100KB ② 100파일/5MB ③ 1,000파일/50MB ④ 10,000파일/500MB (총 입력 규모, PoC 샘플 가정).
- 검사 지표: 정확도/누락·오탐, 파일 I/O 시간, AST 파싱, match/diff 시간, 직렬화 시간, 전체 wall time, peak RSS, 중단·취소, 오류율.
- 네이티브 연동, Windows 단일 EXE, macOS 빌드 결과를 기록한다. 실행 환경이 갖춰지기 전에는 수치 개선을 주장하지 않는다.

### Go / No-Go 게이트

1. **정확도 게이트 (필수)**: 기존 문서 Golden Test 100% 통과, 신규 코드 대표 사례 모두 기대 결과와 일치. 코드 함수 이동·rename 오탐은 별도 유형으로 집계.
2. **재현성 게이트 (필수)**: 저장된 과거 Snapshot은 원본이 없어도 동일 해시·바이트로 복원.
3. **UI/내보내기 게이트 (필수)**: 기존 빠른비교·버전관리·Excel·Word 변경추적 실행/서식 회귀 없음.
4. **배포 게이트 (필수)**: 단일 실행파일 요구와 Windows 실행/오류 처리에 대한 검증 완료. macOS는 목표 플랫폼 빌드 경로 정의.
5. **성능 게이트 (판단)**: Rust가 실질적 속도·메모리·안정성·재사용 이점을 보여야 함. 일정만 늘어나면 C# 코드 분석기 또는 혼합 엔진 유지.

## 8. 구현 우선순위 및 예상 작업 묶음

| 우선 | 묶음 | 산출물 | 선행 조건 |
|---|---|---|---|
| P0 | 공통 이벤트 위치/ID 계약 | `ChangeEvent`, `Location`, 스키마 버전 초안 | 현재 판정 모델·Golden 분석 |
| P0 | 불변 Version/Snapshot/Blob 저장소 | 폴더/문서 스냅샷 + 기존 `.dcv.json` 읽기 마이그레이션 | 경로/해시/저장 원자성 설계 |
| P0 | 테스트 기반·CI | 기존 회귀 + 신규 코드/스냅샷 사례 자동 실행 | 비공개 fixture 처리 규칙 |
| P1 | 코드 파일 텍스트 비교 | 한 파일의 diff + 중앙 코드 Viewer | 경로/문자 오프셋 명세 |
| P1 | 폴더 및 파일 이동 탐지 | 파일트리 비교·rename 근거 | 불변 manifest |
| P1 | Rust 신규 코드 AST PoC | 4개 언어의 구조 비교 + C# DTO 연동 | Rust 툴체인/CI 준비 |
| P2 | Git 어댑터 | 커밋/브랜치/태그 UI + 전/후 비교 | 파일트리/snapshot 공통 모델 |
| P2 | 심볼 계보/의미 분석 | 함수 추적, 참조/시그니처 영향 표시 | AST 기반 신뢰도 평가 |
| 별도 | Word/HWPX Layout Engine | 공통 페이지 좌표 모델과 렌더러 | 문서 분석 데이터/서식 규칙 안정화 |

### 권장 검토 일정 (10영업일 계획의 실행 상태)

| 구간 | 작업 | 상태 |
|---|---|---|
| D1~D2 | 현행 소스/제약/회귀 목록 분석 | **1차 완료** (본 문서) |
| D3 | 공통 버전·위치·이벤트 모델 작성 | **초안 작성**, 스키마 확정/구현 미진행 |
| D4~D5 | 코드 AST/파일 이동 PoC | 미진행 — 개발/테스트 데이터 준비 필요 |
| D6 | Rust/C# 경계·배포 설계 | 옵션 초안 작성, 실증 미진행 |
| D7 | UI 대안 비교/기술 검증 | 개념 검토만 진행 |
| D8 | 성능 벤치마크 | 미진행 — Rust 툴체인 없음 |
| D9 | DOCX/HWPX/Layout 확장성·회귀 | 위험 목록만 작성 |
| D10 | Go/No-Go 및 최종 공수 | PoC 결과 확보 후 판단 |

## 9. 리스크와 보완 방법

- **R1 원본 파일 손실 / 과거 버전 재현 실패 (P0)**: 파일 경로만 저장하지 말고 blob 기반 Snapshot 도입. 기존 `.dcv.json` 호환/마이그레이션.
- **R2 기존 문서 비교 판정 후퇴 (P0)**: C# 엔진 읽기 전용 참조, Golden 비교, feature flag, 병렬 shadow-run 이후 점진적 교체.
- **R3 변경번호/위치 불일치 (P0)**: 영구 `eventId`와 표시 `reviewNumber` 분리, 문서·코드 위치를 독립 타입으로 표현.
- **R4 언어별 AST 의미 오탐 (P0)**: 구문적 사실과 행동 영향 추론을 분리. 근거·신뢰도·미지원 규칙 표시.
- **R5 Rust/C# 인코딩·FFI 문제 (P1)**: 버전 계약, UTF-8 byte/UTF-16 offset 변환, 메모리 소유·예외/취소/타임아웃 규칙.
- **R6 파일시스템 보안/저장공간 (P1)**: 심볼릭 링크·경로 traversal·비밀정보/대형 파일 제외, size quota, crash-safe write.
- **R7 출시 패키징 회귀 (P1)**: Native DLL과 단일 EXE 정책 충돌 실증, 플랫폼별 배포 매트릭스.
- **R8 라이선스 (P1)**: 공개 저장소가 비오픈소스임을 전제로 신규 parser/UI/Git 의존성 라이선스·상업적 사용 조건을 실제 패키지 도입 전 확인.

## 10. 이번 점검에서 직접 확인한 검증 결과

- 원격 기준 `origin/main` = `9844154`, 태그 = `v5.21.11`.
- 원격 SDK `.NET 8.0.425` 확인; `rustc`/`cargo` 실행 경로 확인되지 않음.
- 추적된 저장소 소스는 두 개의 `.csproj` (Avalonia 앱 + 회귀 실행 프로그램)로 구성; Rust 소스/패키지는 현행 `main`에 없음.
- **회귀 기준선 실제 실행**: `dotnet restore` / `dotnet build -c Release` 성공 (**0 warnings, 0 errors**), `dotnet run --no-build`의 마지막 결과 `ALL REGRESSIONS PASSED`.
- 검토에서는 보고서/README 안내만 추가하며 현행 C# 기능의 동작 코드를 변경하지 않는다.
- 별도 Windows GUI 실기기 테스트, Git/HWPX/Rust PoC 및 정량 벤치마크는 수행하지 않았다.

## 11. 1차 결정안과 다음 실행 목표

- **채택 권고**: 통합 버전/파일 Snapshot/변경 이벤트 모델의 신규 설계부터 진행.
- **유지 권고**: 현재 Avalonia UI와 `NativeComparisonEngine`/`NativeOfficeExporter`는 Golden Reference로 유지.
- **검증 조건부 권고**: Rust는 신규 코드 AST/폴더 비교 PoC에서 장점 확인 시 채택.
- **보류 권고**: 전면 Rust 재작성, UI 재작성, 기존 Word Layout Engine 동시 개발.

**다음 실제 작업은 P0의 스냅샷 복원 테스트와 변경 위치/이벤트 DTO 스키마부터다.** 스냅샷 무결성이 확보되어야 Git/폴더/문서 버전을 안정적으로 비교할 수 있다.
