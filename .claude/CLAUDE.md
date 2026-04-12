# CLAUDE.md — DS 가상 EAP 서버 개발 지시 명세서

> **작성자**: 수석 아키텍트
> **수신자**: Claude Code
> **버전**: v1.0 (2026-04-12)
> **작업 성격**: C# 가상 EAP Publisher 코드 작성 (MQTTnet 기반)
> **저장소**: ds-eap (가상 EAP 서버 전용)

---

## 0. 프로젝트 컨텍스트

### 0.1 너의 역할
너는 15년 차 제조 IT(MES/스마트 팩토리) 도메인의 수석 개발자로서, DS 주식회사 비전 검사 장비의 **가상 EAP(Mock Publisher) 서버**를 C#으로 구현한다. 이 서버는 실제 장비(Genesem VELOCE-G7) 없이 전체 데이터 파이프라인을 검증하는 테스트 환경의 핵심이다.

### 0.2 프로젝트의 본질
- 망 분리된 반도체 후공정 공장 현장에서, N대의 비전 검사 장비(EAP)를 한 대의 모바일 앱에서 모니터링
- 통신: MQTT v5.0 (Eclipse Mosquitto 2.x) over Local Wi-Fi
- 핵심 가치: **데이터 병목 없는 파이프라인** + 현장 엔지니어 즉시성
- 가상 EAP는 8종 이벤트를 MQTT Broker에 발행하고, CONTROL_CMD를 수신하는 **Mock Publisher**

### 0.3 시스템 내 위치

```
가상 EAP 서버(본 프로젝트) ← 너가 만드는 것
        │
        │  MQTT Publish (8종 이벤트, JSON)
        ▼
   Eclipse Mosquitto Broker (로컬 Wi-Fi)
        │
        ├──→ 모바일 앱 (Flutter) ── 실시간 N:1 타일 모니터링
        ├──→ Historian 서버 (Node.js/TimescaleDB) ── 시계열 적재
        ├──→ Oracle 서버 (Python) ── 1차 Rule-based + 2차 AI 검증
        └──→ MES 서버 (C#) ── 중앙 제어

[Subscribe 경로]
MES / 모바일 → Broker → 가상 EAP (CONTROL_CMD 수신)
```

### 0.4 작업 시작 전 필독 문서

작업을 시작하기 전에 **반드시 아래 문서를 순서대로 읽어서 컨텍스트를 머릿속에 적재**한다. 이걸 건너뛰면 MQTT 정책, Mock 데이터 구조, 이벤트 시퀀스를 잘못 구현할 위험이 있다.

1. **`명세서/eap-spec-v1.md`** — **가상 EAP 서버 작업 명세서 (1차 구현 권위 문서)**
   - 8종 이벤트 발행/수신 명세, CONTROL_CMD 핸들러, N:1 시뮬레이션, 시나리오, Graceful Shutdown
   - Mock 데이터 27종 인덱스, Rule 38개, PASS drop 정책
   - 이 문서가 EAP 구현의 직접적인 설계도

2. **`명세서/DS_EAP_MQTT_API_명세서.md`** — MQTT API 전체 명세 (v3.4 확정)
   - 8종 이벤트 토픽·QoS·Retained 정책, 페이로드 필드 정의
   - Retained Message 정책 (§1.1.1), 진행률 3필드 (§3.1), 알람 ACK (§6.6)
   - Mobile Subscriber 세션 정책 (부록 A.7), 재연결 백오프 (부록 A.6)

3. **`명세서/DS_이벤트정의서.md`** — 5대분류 / 15소분류 / 38 Rule 이벤트 분류 체계
   - Rule R01~R38c 판정 기준 참조 (Oracle 연동 시 필요)

4. **`문서/기획안.md`** — 시스템 아키텍처, 7종 서버 구성, 데이터 흐름
   - 프로젝트 전체 맥락 파악용 (읽기 전용)

> **💡 Claude Code 사용 패턴**: 작업 전에 `명세서/eap-spec-v1.md`, `명세서/DS_EAP_MQTT_API_명세서.md`, `명세서/DS_이벤트정의서.md`를 순서대로 읽고 컨텍스트를 적재하라.

### 0.5 문서 간 충돌 시 우선순위

> **API 명세서 v3.4 > eap-spec-v1 > 이벤트 정의서 v1.0**

---

## 1. 작업 원칙 (모든 Task 공통)

### 1.1 기술 스택 고정

| 항목 | 기술 | 이유 |
|:---|:---|:---|
| 언어 | C# (.NET 8.0+) | GVisionWpf 실제 사용 언어와 동일 |
| MQTT 라이브러리 | MQTTnet 4.x | GVisionWpf 실제 사용 라이브러리 동일 |
| JSON 직렬화 | System.Text.Json | UTF-8 네이티브, 고성능 |
| 설정 관리 | JSON/YAML 외부 설정 파일 | 장비 수·레시피·시나리오 런타임 변경 가능 |
| 로깅 | Microsoft.Extensions.Logging + Serilog | 발행 메시지 타임스탬프·토픽 콘솔 출력 |

### 1.2 MQTT 정책 필수 준수 (코드에 반드시 반영)

#### 1.2.1 QoS 정책

| QoS | 대상 이벤트 | 이유 |
|:---|:---|:---|
| QoS 1 | HEARTBEAT, STATUS_UPDATE, INSPECTION_RESULT | 주기적 발행, 1회 누락 허용 |
| QoS 2 | LOT_END, HW_ALARM, RECIPE_CHANGED, CONTROL_CMD, ORACLE_ANALYSIS | 정확히 1회 전달 보장 필수 |

#### 1.2.2 Retained 플래그 (Publish 시 `Retain = true` 필수)

| Retained ✅ (true) | Retained ❌ (false) |
|:---|:---|
| `ds/{eq}/status` | `ds/{eq}/heartbeat` |
| `ds/{eq}/lot` | `ds/{eq}/result` |
| `ds/{eq}/alarm` | `ds/{eq}/control` |
| `ds/{eq}/recipe` | |
| `ds/{eq}/oracle` | |

#### 1.2.3 Will 메시지

```csharp
// 연결 옵션 빌더에 Will 설정 필수
.WithWillTopic($"ds/{equipmentId}/alarm")
.WithWillPayload(willPayloadJson)         // HW_ALARM(EAP_DISCONNECTED) 페이로드
.WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce) // QoS 2
.WithWillRetain(true)                     // WillRetain = true 필수
```

#### 1.2.4 재연결 백오프

```
1s → 2s → 5s → 15s → 30s, max 60s, jitter ±20%
```

코드에 지수 백오프 + jitter 로직을 반드시 포함할 것. 하드코딩 금지, 설정 파일에서 백오프 단계를 읽을 수 있도록 구현.

#### 1.2.5 Graceful Shutdown 시퀀스

```
[SIGTERM / Ctrl+C 수신]
    │
    ├─ equipment_status == RUN?
    │   ├── Yes → ① LOT_END(ABORTED) 발행 (QoS 2, Retain=true)
    │   │         ② STATUS_UPDATE(IDLE) 발행 (Retain=true)
    │   │         ③ 진행 중 Heartbeat/INSPECTION 타이머 중지
    │   └── No  → ③으로 직행
    │
    ├─ ④ 활성 알람이 있으면 빈 페이로드 + Retain=true로 clear (선택)
    │
    ├─ ⑤ MqttClient.DisconnectAsync() 호출
    │     → Broker가 정상 DISCONNECT 수신 → Will 메시지 발동 안 함
    │
    └─ ⑥ 프로세스 종료 (5초 타임아웃)
```

### 1.3 절대 금지 사항

- ❌ **실로그 기반 Mock(01~17)의 수치 변경 금지** — Carsem 14일 실측값
- ❌ **Rule 38개 번호(R01~R38c) 재배치 금지** — 새 Rule은 R39부터 부여
- ❌ **기존 8개 토픽 패턴(`ds/{eq}/heartbeat` 등) 구조 변경 금지**
- ❌ **PascalCase ↔ snake_case 무단 변환 금지**
  - `inspection_detail` 내부 필드: **PascalCase** (GVisionWpf 원본)
  - 그 외 모든 JSON 필드: **snake_case**
- ❌ **`saw_process` 필드 부활 금지** (README.md 제외 정책 준수)
- ❌ **Mock JSON 발행 시 `_` prefix 메타 필드 포함 금지** — `_source`, `_note`, `_synthetic`, `_metadata` 등은 발행 페이로드에서 반드시 제거

### 1.4 필수 코드 품질 기준

- ✅ 모든 MQTT 발행/구독 코드에 **try-catch + 로깅** 포함
- ✅ **CancellationToken** 전파 — 모든 async 메서드에 token 파라미터 필수
- ✅ **IDisposable / IAsyncDisposable** 패턴으로 리소스 정리
- ✅ 타이밍 정확도: Heartbeat 3s ±500ms / STATUS 6s ±1s / takt 1,620ms ±200ms
- ✅ JSON 직렬화 시 **timestamp는 ISO 8601 UTC 밀리초(`.fffZ`)**, message_id는 **UUID v4**
- ✅ 코드 내 주석에 해당 명세서 절 번호 참조 (예: `// eap-spec §4.1 HEARTBEAT 3단계 판정`)

### 1.5 데이터 컨벤션

| 항목 | 규칙 | 예시 |
|:---|:---|:---|
| Timestamp | ISO 8601 UTC 밀리초 | `2026-01-22T16:41:42.123Z` |
| Message ID | UUID v4 (RFC 4122) | `e7026e09-477c-43c3-8ba5-35b7b7f8a659` |
| Equipment ID | `DS-VIS-NNN` 형식 | `DS-VIS-001` ~ `DS-VIS-004` |
| JSON 필드명 | snake_case (inspection_detail 내부만 PascalCase) | `equipment_status`, `ZAxisNum` |
| JSON 직렬화 | System.Text.Json, PropertyNamingPolicy = null (혼용 지원) | camelCase 변환 금지 |

---

## 2. 프로젝트 구조 (권장)

```
ds-eap/
├── .claude/
│   └── CLAUDE.md                          ← 이 파일
├── src/
│   └── DsEap/
│       ├── DsEap.csproj
│       ├── Program.cs                     ← 진입점, Host 구성, Graceful Shutdown
│       ├── Configuration/
│       │   ├── EapSettings.cs             ← Broker 주소, 장비 수, 타이밍 설정
│       │   └── ScenarioConfig.cs          ← multi_equipment_4x.json 매핑 모델
│       ├── Mqtt/
│       │   ├── MqttClientManager.cs       ← 연결/재연결/백오프/Will 설정
│       │   ├── MqttPublisher.cs           ← 발행 공통 래퍼 (QoS/Retained 자동 적용)
│       │   └── MqttSubscriber.cs          ← CONTROL_CMD 구독 핸들러
│       ├── Equipment/
│       │   ├── VirtualEquipment.cs        ← 장비 1대의 상태 머신 (RUN/IDLE/STOP)
│       │   ├── EquipmentManager.cs        ← N대 장비 동시 관리
│       │   └── EquipmentState.cs          ← 장비 상태 enum + 전환 규칙
│       ├── Events/
│       │   ├── Models/                    ← 8종 이벤트 페이로드 DTO
│       │   │   ├── HeartbeatPayload.cs
│       │   │   ├── StatusUpdatePayload.cs
│       │   │   ├── InspectionResultPayload.cs
│       │   │   ├── LotEndPayload.cs
│       │   │   ├── HwAlarmPayload.cs
│       │   │   ├── RecipeChangedPayload.cs
│       │   │   ├── ControlCmdPayload.cs
│       │   │   └── OracleAnalysisPayload.cs
│       │   └── Publishers/               ← 이벤트별 발행 로직
│       │       ├── HeartbeatPublisher.cs  ← 3초 타이머
│       │       ├── StatusPublisher.cs     ← 6초 타이머 + 진행률 3필드 계산
│       │       ├── InspectionPublisher.cs ← takt 1,620ms + PASS/FAIL 분기
│       │       ├── LotEndPublisher.cs     ← LOT 완료/중단
│       │       ├── AlarmPublisher.cs      ← 알람 발행 + ACK 시 retained clear
│       │       ├── RecipePublisher.cs     ← 레시피 변경
│       │       └── OraclePublisher.cs     ← LOT_END 후 비동기 분석 결과
│       ├── MockData/
│       │   ├── MockDataLoader.cs          ← EAP_mock_data/*.json 로딩
│       │   └── MockPayloadTransformer.cs  ← equipment_id 치환 + _ prefix 필드 제거
│       └── Scenarios/
│           ├── ScenarioRunner.cs          ← multi_equipment_4x.json 기반 N:1 시뮬레이션
│           └── SequencePlayer.cs          ← mock_sequence 순서대로 재생
├── config/
│   ├── appsettings.json                   ← Broker 주소, 백오프 단계, 타이밍
│   └── scenarios/
│       └── multi_equipment_4x.json        ← EAP_mock_data/scenarios/ 에서 복사
├── mock-data/                             ← EAP_mock_data/ 에서 복사 (01~27 JSON)
├── tests/
│   └── DsEap.Tests/
│       ├── PayloadSerializationTests.cs   ← JSON 직렬화 검증
│       ├── MqttPolicyTests.cs             ← QoS/Retained 정책 검증
│       └── StateMachineTests.cs           ← 장비 상태 전환 검증
└── README.md
```

---

## 3. Task 실행 순서

의존성에 따라 아래 순서로 진행한다. **각 Task 끝에 검증 체크리스트를 통과해야 다음 Task로.**

| 순서 | Task ID | 제목 | 우선순위 | 예상 |
|:---|:---|:---|:---|:---|
| 1 | E1 | 프로젝트 스캐폴딩 + MQTT 연결 | P0 | 0.5일 |
| 2 | E2 | 8종 이벤트 DTO + JSON 직렬화 | P0 | 1일 |
| 3 | E3 | 단일 장비 이벤트 발행 (Golden Path) | P0 | 1일 |
| 4 | E4 | CONTROL_CMD 수신 핸들러 | P1 | 0.5일 |
| 5 | E5 | N:1 다설비 시뮬레이션 + 시나리오 러너 | P1 | 1일 |
| 6 | E6 | 비정상 시나리오 (알람/ACK/크래시) | P2 | 1일 |
| 7 | E7 | Graceful Shutdown + 통합 테스트 | P2 | 0.5일 |

---

## 4. Task E1 — 프로젝트 스캐폴딩 + MQTT 연결

### 4.1 작업 목표
.NET 프로젝트 생성, MQTTnet 연결 관리자 구현, 재연결 백오프 로직, Will 메시지 설정.

### 4.2 핵심 구현 사항

- `MqttClientManager`: MQTTnet `MqttFactory` + `MqttClientOptionsBuilder`
  - `CleanStart = false` (세션 유지)
  - `SessionExpiryInterval = 3600` (EAP도 세션 보존)
  - `KeepAlivePeriod = 60s`
  - Will 메시지: `ds/{eq}/alarm` 토픽, HW_ALARM(EAP_DISCONNECTED) 페이로드, QoS 2, WillRetain=true
- 재연결 백오프: `1s → 2s → 5s → 15s → 30s, max 60s, jitter ±20%`
- `appsettings.json`: Broker 주소/포트, 장비 ID 목록, 백오프 단계, 타이밍 설정

### 4.3 검증 체크리스트
- [ ] `dotnet build` 성공
- [ ] Mosquitto 로컬 Broker에 연결 성공 로그 출력
- [ ] 의도적 Broker 중단 → 재연결 백오프 로그 확인 (1s→2s→5s...)
- [ ] Will 메시지가 Broker에 등록됨 (`mosquitto_sub`로 확인)
- [ ] `appsettings.json`에서 Broker 주소 변경 시 재빌드 없이 적용

### 4.4 Git 커밋 메시지
```
feat(eap): 프로젝트 스캐폴딩 + MQTT 연결 관리자 (E1)

- .NET 8.0 프로젝트 생성, MQTTnet 4.x 의존성
- MqttClientManager: 연결/재연결/백오프(1s→60s, jitter ±20%)
- Will 메시지: HW_ALARM(EAP_DISCONNECTED), QoS 2, WillRetain=true
- appsettings.json: Broker 주소, 장비 ID, 타이밍 외부 설정
```

---

## 5. Task E2 — 8종 이벤트 DTO + JSON 직렬화

### 5.1 작업 목표
8종 이벤트 페이로드 DTO 클래스를 정의하고, `System.Text.Json` 직렬화/역직렬화를 구현한다.

### 5.2 핵심 구현 사항

- 공통 헤더: `message_id` (UUID v4), `event_type`, `timestamp` (ISO 8601 `.fffZ`), `equipment_id`
- `equipment_status`: HEARTBEAT/CONTROL_CMD/ORACLE_ANALYSIS에서는 **제외** (eap-spec §3.1)
- `inspection_detail` 내부 필드: **PascalCase** 유지 (커스텀 JsonNamingPolicy 또는 `[JsonPropertyName]`)
- 그 외 필드: **snake_case** (`JsonNamingPolicy.SnakeCaseLower` 또는 커스텀)
- STATUS_UPDATE: 진행률 3필드 포함 (`current_unit_count`, `expected_total_units`, `current_yield_pct`)
- CONTROL_CMD: `target_burst_id` 선택 필드 포함 (ALARM_ACK용)

### 5.3 JSON 직렬화 주의사항

```csharp
// PascalCase + snake_case 혼용 전략
// 방법 1: 커스텀 JsonConverter로 inspection_detail 내부만 PascalCase 유지
// 방법 2: [JsonPropertyName("ZAxisNum")] 어트리뷰트로 개별 필드 지정
// 방법 2 권장 — 명시적이고 실수 방지

// null 값 처리: 선택 필드가 null이면 JSON에 포함하지 않음
var options = new JsonSerializerOptions
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false  // 발행 시 압축
};
```

### 5.4 검증 체크리스트
- [ ] 8종 DTO 클래스 모두 생성
- [ ] `HeartbeatPayload` 직렬화 → 4필드만 포함 (equipment_status 없음)
- [ ] `StatusUpdatePayload` 직렬화 → 진행률 3필드 포함, null이면 JSON 미포함
- [ ] `InspectionResultPayload` 직렬화 → `inspection_detail.prs_result[].ZAxisNum` PascalCase 유지
- [ ] `InspectionResultPayload` 직렬화 → `overall_result`, `fail_count` 등 snake_case
- [ ] Mock 01~27 JSON 파일을 역직렬화 → 재직렬화 → 원본과 동등성 검증
- [ ] `_source`, `_note` 등 `_` prefix 필드가 역직렬화 시 무시됨

### 5.5 Git 커밋 메시지
```
feat(eap): 8종 이벤트 DTO + JSON 직렬화 (E2)

- 공통 헤더 (message_id UUID v4, timestamp ISO 8601 .fffZ)
- PascalCase/snake_case 혼용 직렬화 전략
- Mock 01~27 역직렬화 호환성 검증 완료
```

---

## 6. Task E3 — 단일 장비 이벤트 발행 (Golden Path)

### 6.1 작업 목표
DS-VIS-001 단일 장비의 정상 양산 흐름(Golden Path)을 구현한다.

### 6.2 Golden Path 시퀀스 (eap-spec §5.1)

```
[T+0]     HEARTBEAT (3초 주기 시작, 항상 발행)
[T+1]     RECIPE_CHANGED (Carsem_3X3, IDLE → 시뮬레이션)
[T+2]     STATUS_UPDATE (RUN, lot_id 생성, 6초 주기 시작)
[T+3~N]   INSPECTION_RESULT × 2,792회 (takt 1,620ms)
            ├── PASS (overall_result=PASS, fail_count=0) — 96.2%
            └── FAIL (산발적 ET=11/12/52, fail_count >= 1) — 3.8%
[T+N+1]   LOT_END (COMPLETED, yield 96.2%)
[T+N+2]   STATUS_UPDATE (IDLE)
[T+N+3]   ORACLE_ANALYSIS (NORMAL, 비동기)
```

### 6.3 핵심 구현 사항

- `VirtualEquipment`: 상태 머신 (IDLE → RUN → IDLE, RUN → STOP)
- `HeartbeatPublisher`: 3초 `PeriodicTimer`, 장비 상태 무관 항상 발행
- `StatusPublisher`: 6초 `PeriodicTimer`, 진행률 3필드 매 takt 갱신
  - `current_unit_count`: INSPECTION_RESULT 발행마다 +1
  - `expected_total_units`: 레시피별 직전 LOT 3개 평균 (초기엔 Mock 기준 2,792)
  - `current_yield_pct`: pass_count / current_unit_count × 100
- `InspectionPublisher`: takt 1,620ms 타이머, Mock 04/05/06/07/08 JSON 기반 페이로드
  - 정상 96.2% 확률로 PASS (Mock 04), 3.8%로 FAIL (Mock 05/06/07/08 랜덤)
- `MqttPublisher.PublishAsync()`: 토픽별 QoS + Retained 자동 적용

```csharp
// 토픽별 정책 매핑 (하드코딩 금지, 설정 또는 상수 테이블로 관리)
private static readonly Dictionary<string, (MqttQualityOfServiceLevel Qos, bool Retain)> TopicPolicies = new()
{
    ["heartbeat"] = (QoS.AtLeastOnce, false),
    ["status"]    = (QoS.AtLeastOnce, true),
    ["result"]    = (QoS.AtLeastOnce, false),
    ["lot"]       = (QoS.ExactlyOnce, true),
    ["alarm"]     = (QoS.ExactlyOnce, true),
    ["recipe"]    = (QoS.ExactlyOnce, true),
    ["control"]   = (QoS.ExactlyOnce, false),
    ["oracle"]    = (QoS.ExactlyOnce, true),
};
```

### 6.4 PASS drop 인지 사항

가상 EAP는 PASS/FAIL 무관 **전체 필드를 발행**한다. drop 정책은 수신자(모바일/Historian/Oracle) 책임이다. 단, PASS 시 detail 그룹에 실측 범위 내 정상값을 넣어야 한다 (eap-spec §4.3).

### 6.5 검증 체크리스트
- [ ] `mosquitto_sub -t "ds/DS-VIS-001/#"` 로 8종 이벤트 수신 확인
- [ ] HEARTBEAT: 3초 간격 ±500ms
- [ ] STATUS_UPDATE: 6초 간격 ±1s, RUN 상태에서 진행률 필드 증가 확인
- [ ] INSPECTION_RESULT: takt ~1,620ms ±200ms, PASS:FAIL 비율 약 96:4
- [ ] LOT_END: 2,792 unit 완료 후 COMPLETED 발행
- [ ] STATUS_UPDATE 토픽에 Retained=true 확인 (`mosquitto_sub --retained-only`)
- [ ] LOT_END 토픽에 Retained=true, QoS 2 확인
- [ ] `timestamp` 형식: `2026-01-22T16:41:42.123Z` (.fffZ 밀리초 포함)

### 6.6 Git 커밋 메시지
```
feat(eap): 단일 장비 정상 양산 흐름 구현 (E3)

- VirtualEquipment 상태 머신 (IDLE→RUN→IDLE)
- Heartbeat 3s / STATUS 6s / Inspection takt 1,620ms 타이머
- Mock 기반 PASS/FAIL 페이로드 발행 (96:4 비율)
- 토픽별 QoS/Retained 정책 자동 적용
- LOT_END + ORACLE_ANALYSIS Golden Path 완성
```

---

## 7. Task E4 — CONTROL_CMD 수신 핸들러

### 7.1 작업 목표
`ds/{equipment_id}/control` 토픽 구독, 6종 명령 핸들러 구현.

### 7.2 명령별 동작 (eap-spec §2.2)

| 명령 | 동작 | Mock |
|:---|:---|:---|
| EMERGENCY_STOP | 즉시 STOP 전환 → LOT_END(ABORTED) → STATUS_UPDATE(STOP) | 21번 |
| STATUS_QUERY | 즉시 STATUS_UPDATE 1회 발행 | 22번 |
| ALARM_ACK | `ds/{eq}/alarm` 빈 페이로드 + Retain=true → retained clear | 26, 27번 |
| ALARM_CLEAR | 알람 해제 + 복구 시도 (MES 전용) | Mock 미존재 |
| RECIPE_LOAD | RECIPE_CHANGED 발행 (MES 전용) | Mock 미존재 |
| LOT_ABORT | LOT_END(ABORTED) → STATUS_UPDATE(IDLE) (MES 전용) | Mock 미존재 |

### 7.3 ALARM_ACK 상세 (eap-spec §4.5, API §6.6)

```csharp
// ALARM_ACK 수신 시
async Task HandleAlarmAck(ControlCmdPayload cmd)
{
    // 빈 페이로드 + Retain=true → Broker가 retained 메시지 삭제
    var clearMsg = new MqttApplicationMessageBuilder()
        .WithTopic($"ds/{equipmentId}/alarm")
        .WithPayload(Array.Empty<byte>())  // 빈 페이로드
        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce)
        .WithRetainFlag(true)              // Retain=true → clear
        .Build();
    await mqttClient.PublishAsync(clearMsg);
}
```

### 7.4 검증 체크리스트
- [ ] EMERGENCY_STOP 발행 → 장비 즉시 STOP, LOT_END(ABORTED) 발행 확인
- [ ] STATUS_QUERY 발행 → 즉시 STATUS_UPDATE 1회 수신
- [ ] ALARM_ACK 발행 → `ds/{eq}/alarm` retained 메시지 clear 확인
- [ ] burst ALARM_ACK (target_burst_id 지정) → 해당 burst 그룹 clear
- [ ] Mock 미존재 3종(ALARM_CLEAR/RECIPE_LOAD/LOT_ABORT) → 로그만 출력, 에러 아님

### 7.5 Git 커밋 메시지
```
feat(eap): CONTROL_CMD 수신 핸들러 6종 (E4)

- EMERGENCY_STOP: 즉시 STOP + LOT_END(ABORTED)
- STATUS_QUERY: 즉시 STATUS_UPDATE 1회
- ALARM_ACK: retained alarm clear (단독 + burst 그룹)
- ALARM_CLEAR/RECIPE_LOAD/LOT_ABORT: 스텁 핸들러 (로그만)
```

---

## 8. Task E5 — N:1 다설비 시뮬레이션 + 시나리오 러너

### 8.1 작업 목표
`scenarios/multi_equipment_4x.json` 기반 4대 장비 동시 시뮬레이션.

### 8.2 핵심 구현 사항

- `ScenarioRunner`: 시나리오 JSON 로딩 → 장비별 `VirtualEquipment` 인스턴스 생성
- `MockDataLoader`: `EAP_mock_data/*.json` 로딩, `_` prefix 메타 필드 제거
- `MockPayloadTransformer`: `equipment_id` 필드만 시나리오의 장비 ID로 치환
- `SequencePlayer`: `mock_sequence[]` 배열 순서대로 재생, Heartbeat는 별도 3초 타이머 병행

### 8.3 4대 장비 시나리오

| 장비 | 상태 | 시나리오 | 타일 색상 |
|:---|:---|:---|:---|
| DS-VIS-001 | RUN | 정상 양산 (Carsem_3X3, 수율 96.2%) | GREEN |
| DS-VIS-002 | RUN | Teaching 미완성 (Carsem_4X6, ET=52 폭주) | YELLOW |
| DS-VIS-003 | IDLE | 대기 (직전 LOT 완료, 다음 대기) | GRAY |
| DS-VIS-004 | STOP | CAM_TIMEOUT_ERR 알람 + Will 시뮬레이션 | RED |

### 8.4 검증 체크리스트
- [ ] 4개 토픽 트리 동시 발행 확인: `ds/DS-VIS-001/#`, `ds/DS-VIS-002/#`, ...
- [ ] 각 장비 독립 상태: RUN 2대, IDLE 1대, STOP 1대
- [ ] DS-VIS-002: RECIPE_CHANGED → INSPECTION_RESULT(ET=52) → HW_ALARM 시퀀스
- [ ] DS-VIS-004: HW_ALARM(CAM_TIMEOUT_ERR) retained 메시지 확인
- [ ] 장비별 Heartbeat 독립 3초 주기

### 8.5 Git 커밋 메시지
```
feat(eap): N:1 다설비 시뮬레이션 (E5)

- ScenarioRunner: multi_equipment_4x.json 기반 4대 동시 구동
- MockDataLoader: JSON 로딩 + _ prefix 메타 필드 제거
- MockPayloadTransformer: equipment_id 치환
- 4대 장비(RUN/RUN/IDLE/STOP) 독립 상태 관리
```

---

## 9. Task E6 — 비정상 시나리오

### 9.1 작업 목표
eap-spec §5.2의 비정상 시나리오 10종 중 주요 6종 구현.

### 9.2 구현 대상

| 시나리오 | 트리거 | 이벤트 시퀀스 |
|:---|:---|:---|
| Teaching 미완성 | Carsem_4X6 투입 | RECIPE → INSPECTION(ET=52 전수) → ALARM(VISION_SCORE_ERR) |
| 카메라 타임아웃 | ET=30 연속 3회 | INSPECTION(ET=30)×3 → ALARM(CAM_TIMEOUT_ERR, STOP) |
| EAP 크래시 | AggregateException | ALARM(burst) → Heartbeat 중단 → Will(EAP_DISCONNECTED) |
| 긴급 정지 | CONTROL_CMD | CONTROL_CMD 수신 → STATUS(STOP) |
| 단독 알람 ACK | ALARM_ACK | CONTROL_CMD → 빈 페이로드 retained clear |
| burst 그룹 ACK | ALARM_ACK + burst_id | 동일 burst 전체 retained clear |

### 9.3 자동 ACK 시나리오 (eap-spec §4.5)

| 자동 clear 트리거 | 대상 |
|:---|:---|
| `auto_recovery_attempted=true` 알람 복구 성공 | LIGHT_PWR_LOW 등 |
| 동일 hw_error_code 정상 STATUS 6회 연속(36초) | CAM_TIMEOUT_ERR 복구 후 |
| 새 RECIPE_CHANGED 발생 | 이전 레시피 VISION_SCORE_ERR |

### 9.4 검증 체크리스트
- [ ] Teaching 미완성: ET=52 FAIL rate > 50% 시 VISION_SCORE_ERR 알람 발행
- [ ] 카메라 타임아웃: ET=30 연속 3회 → CAM_TIMEOUT_ERR + STOP
- [ ] EAP 크래시 시뮬레이션: Heartbeat 중단 후 Will 메시지 자동 발행
- [ ] ALARM_ACK 후 retained alarm 완전 clear

### 9.5 Git 커밋 메시지
```
feat(eap): 비정상 시나리오 6종 구현 (E6)

- Teaching 미완성 / 카메라 타임아웃 / EAP 크래시
- 긴급 정지 / 단독 ACK / burst 그룹 ACK
- 자동 ACK 3종 트리거 (auto_recovery, 연속 정상, 레시피 변경)
```

---

## 10. Task E7 — Graceful Shutdown + 통합 테스트

### 10.1 작업 목표
SIGTERM/Ctrl+C Graceful Shutdown 구현, 전체 통합 검증.

### 10.2 Shutdown 구현 (eap-spec §10.1)

```csharp
// Program.cs
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Shutdown 시퀀스 (5초 타임아웃)
await host.Services.GetRequiredService<EquipmentManager>()
    .GracefulShutdownAsync(cts.Token)
    .WaitAsync(TimeSpan.FromSeconds(5));
```

### 10.3 통합 테스트 시나리오

1. **Golden Path**: 단일 장비 정상 LOT 완료 → LOT_END(COMPLETED) → ORACLE_ANALYSIS(NORMAL)
2. **N:1 동시**: 4대 장비 동시 구동 10분 → 각 장비 독립 상태 유지
3. **알람 + ACK**: HW_ALARM 발행 → ALARM_ACK → retained clear
4. **Graceful Shutdown**: RUN 중 Ctrl+C → LOT_END(ABORTED) → STATUS(IDLE) → Disconnect
5. **비정상 종료**: 프로세스 Kill → Will(EAP_DISCONNECTED) 자동 발행

### 10.4 검증 체크리스트
- [ ] Graceful: RUN 중 Ctrl+C → LOT_END(ABORTED) + STATUS(IDLE) 발행 후 종료
- [ ] Graceful: Will 메시지 발동 안 함
- [ ] 비정상: `kill -9` → Will(EAP_DISCONNECTED) Broker 자동 발행
- [ ] 5초 타임아웃: Shutdown 5초 초과 시 강제 종료
- [ ] 전체 JSON 파싱 검증: 발행된 모든 메시지 `System.Text.Json` 파싱 성공

### 10.5 Git 커밋 메시지
```
feat(eap): Graceful Shutdown + 통합 테스트 (E7)

- SIGTERM/Ctrl+C: LOT_END(ABORTED) → STATUS(IDLE) → Disconnect
- 5초 타임아웃 강제 종료
- 통합 테스트 5개 시나리오 검증
```

---

## 11. Mock 데이터 참조

### 11.1 Mock 인덱스 (27종)

| # | 파일 | 이벤트 | 대표 수치 |
|:---|:---|:---|:---|
| 01 | heartbeat | HEARTBEAT | 3초 주기 |
| 02 | status_run | STATUS_UPDATE | RUN / 1,247/2,792 unit / 수율 95.8% |
| 03 | status_idle | STATUS_UPDATE | IDLE / 2,792/2,792 unit / 수율 96.2% |
| 04 | inspection_pass | INSPECTION_RESULT | PASS / ET=1 전체 |
| 05 | inspection_fail_side_et52 | INSPECTION_RESULT | FAIL / ET=52 8/8 |
| 06 | inspection_fail_side_et12 | INSPECTION_RESULT | FAIL / ET=12 8/8 |
| 07 | inspection_fail_prs_offset | INSPECTION_RESULT | FAIL / ET=11 3/8 |
| 08 | inspection_fail_side_mixed | INSPECTION_RESULT | FAIL / ET=52+12 혼재 |
| 09 | lot_end_normal | LOT_END | COMPLETED / 96.2% / 2,792 units |
| 10 | lot_end_aborted | LOT_END | ABORTED / 94.2% / 656 units |
| 11~17 | alarm_* | HW_ALARM | 7종 알람 (CRITICAL/WARNING) |
| 18~20 | recipe_changed_* | RECIPE_CHANGED | 3종 레시피 전환 |
| 21~22 | control_* | CONTROL_CMD | EMERGENCY_STOP / STATUS_QUERY |
| 23~25 | oracle_* | ORACLE_ANALYSIS | NORMAL / WARNING / DANGER |
| 26~27 | control_alarm_ack_* | CONTROL_CMD | ALARM_ACK (단독/burst) |

### 11.2 실측 기준값 (Carsem 현장)

| 지표 | 실측값 |
|:---|:---|
| Heartbeat 주기 | 3초 |
| STATUS 주기 | 6초 |
| takt_time | ~1,620ms (MAP+PRS+SIDE 합산) |
| total_units/Lot | 2,792 (349 Strip × 8슬롯) |
| 정상 수율 | 96.2% (28 LOT 학습 기반) |
| Lot 소요시간 | 82분 (정상 40~180분, 최대 370분) |

---

## 12. 작업 시 주의사항 (실수 방지)

### 12.1 자주 하는 실수
- ❌ `System.Text.Json`에서 `JsonNamingPolicy.CamelCase` 사용 → `equipment_id`가 `equipmentId`로 변환됨. snake_case 유지 필수.
- ❌ `inspection_detail` 내부를 snake_case로 변환 → `ZAxisNum`이 `z_axis_num`이 됨. PascalCase 유지 필수.
- ❌ Heartbeat에 `equipment_status` 필드 포함 → 명세서 위반. HEARTBEAT/CONTROL_CMD/ORACLE에서는 제외.
- ❌ PASS 시 `fail_count`를 null로 설정 → 0이어야 함.
- ❌ Retained 플래그를 heartbeat에 설정 → stale 메시지 위험. heartbeat/result/control은 false.
- ❌ `DisconnectAsync()` 호출 안 하고 프로세스 종료 → Will 메시지 불필요 발동.
- ❌ Mock JSON의 `_source` 필드를 Broker에 발행 → `_` prefix 필드는 반드시 제거.

### 12.2 도움이 되는 작업 패턴
- ✅ Task 시작 전에 관련 명세서 절을 `view`로 읽어 현재 상태 확인
- ✅ `mosquitto_sub -v -t "ds/#"` 로 모든 발행 메시지 실시간 모니터링
- ✅ JSON 직렬화 결과를 `python3 -m json.tool`로 포맷 확인
- ✅ 각 Task 끝에 검증 체크리스트 모든 항목 점검 후 다음 Task로
- ✅ Git 커밋은 Task 단위로 7번 분리. 한 커밋에 여러 Task 섞지 말 것

### 12.3 막혔을 때
- MQTT 정책이 모호하면 `명세서/DS_EAP_MQTT_API_명세서.md`를 다시 읽는다
- 이벤트 시퀀스가 불확실하면 `명세서/eap-spec-v1.md` §5 시나리오를 확인한다
- Mock 데이터 구조가 기억나지 않으면 `EAP_mock_data/README.md`를 참조한다
- 필드명/값 컨벤션이 모호하면 기존 Mock 01~27의 패턴을 따른다
- 두 가지 해석이 가능한 경우, 이 CLAUDE.md의 §0~§1 원칙으로 돌아간다

---

## 13. 최종 확인

이 명세서를 받았다면, 작업을 시작하기 전에 아래 5가지를 너 자신에게 확인한다.

1. ✅ 7개 Task의 우선순위와 의존성을 이해했는가? (E1 → E2 → E3 → E4 → E5 → E6 → E7)
2. ✅ **C# 코드를 작성**하는 것이 이번 작업의 목표라는 점을 이해했는가?
3. ✅ `명세서/eap-spec-v1.md`가 구현의 1차 권위 문서라는 점을 기억하는가?
4. ✅ 실로그 기반 Mock 01~17의 수치는 절대 변경하지 않는다는 원칙을 기억하는가?
5. ✅ 각 Task 끝에 검증 체크리스트를 모두 통과해야 다음 Task로 넘어간다는 규칙을 따를 것인가?

모두 ✅이면 **§0.4 필독 문서 3개를 먼저 read한 후**, Task E1부터 시작한다.

작업 진행 중 §0~§12 중 어느 절이라도 모순되거나 막막한 부분이 있다면, 추측으로 진행하지 말고 멈춰서 사용자에게 질문한다.

---

**End of CLAUDE.md**