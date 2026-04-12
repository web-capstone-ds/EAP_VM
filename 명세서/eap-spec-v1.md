# DS 주식회사 가상 EAP 서버 작업 명세서

**문서번호:** DS-EAP-VM-SPEC-001 v1.0
**작성일:** 2026-04-12
**프로젝트:** 반도체 후공정 비전 검사 장비 — 가상 EAP (Mock Publisher) 서버
**대외비**

| 항목 | 내용 |
| :--- | :--- |
| 장비 모델 | Genesem VELOCE-G7 Saw Singulation |
| 비전 소프트웨어 | GVisionWpf (C# WPF, HALCON + MQTTnet) |
| 현장 실측 기준 | Carsem Inc. / 2026-01-16~29 (14일) |
| 개발 언어 | C# |
| 통신 프로토콜 | MQTT v5.0 (Eclipse Mosquitto 2.x) |
| 네트워크 환경 | 망 분리 공장 현장 로컬 Wi-Fi / BLE |
| Mock 데이터 | 27종 (실제 로그 기반 20종 + 제어/Oracle 7종) |

---

## 1. 개요

### 1.1 목적

가상 EAP 서버는 실제 비전 검사 장비(Genesem VELOCE-G7)를 대체하는 Mock Publisher로, 검사 결과 데이터를 JSON 포맷으로 직렬화하여 MQTT Broker에 발행한다. 실제 장비 없이 전체 데이터 파이프라인(모바일 앱, Oracle 서버, Historian 서버, MES 서버)을 검증할 수 있는 테스트 환경을 제공하는 것이 핵심 목표이다.

### 1.2 시스템 내 위치

```
가상 EAP 서버(본 프로젝트)
        │
        │  MQTT Publish (8종 이벤트)
        ▼
   Eclipse Mosquitto Broker
        │
        ├──→ 모바일 앱 (Flutter) ── 실시간 모니터링
        ├──→ Historian 서버 (Node.js/TimescaleDB) ── 시계열 적재
        ├──→ Oracle 서버 (Python) ── 1차 Rule-based + 2차 AI 검증
        └──→ MES 서버 (C#) ── 중앙 제어
```

### 1.3 데이터 흐름

```
[Publish 경로]
가상 EAP → Broker → 모바일 / Historian / Oracle / MES

[Subscribe 경로]
MES / 모바일 → Broker → 가상 EAP (CONTROL_CMD 수신)
```

---

## 2. 기능 요구사항

### 2.1 이벤트 발행 (Publish) — 8종

가상 EAP 서버는 아래 8종의 이벤트를 MQTT Broker에 발행해야 한다. 모든 이벤트는 DS_EAP_MQTT_API_명세서 v3.4를 준수한다.

| # | 이벤트 타입 | 토픽 패턴 | QoS | Retained | 방향 | 주기/조건 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| 1 | HEARTBEAT | ds/{eq}/heartbeat | 1 | No | Pub | 3초 주기 |
| 2 | STATUS_UPDATE | ds/{eq}/status | 1 | Yes | Pub | 6초 주기 |
| 3 | INSPECTION_RESULT | ds/{eq}/result | 1 | No | Pub | takt ~1,620ms |
| 4 | LOT_END | ds/{eq}/lot | 2 | Yes | Pub | Lot 완료 1회 |
| 5 | HW_ALARM | ds/{eq}/alarm | 2 | Yes | Pub | 이벤트 즉시 |
| 6 | RECIPE_CHANGED | ds/{eq}/recipe | 2 | Yes | Pub | 변경 즉시 |
| 7 | CONTROL_CMD | ds/{eq}/control | 2 | No | Sub | 명령 수신 |
| 8 | ORACLE_ANALYSIS | ds/{eq}/oracle | 2 | Yes | Pub | LOT_END 후 비동기 |

### 2.2 이벤트 수신 (Subscribe) — CONTROL_CMD

가상 EAP는 `ds/{equipment_id}/control` 토픽을 구독하여 다음 명령에 응답해야 한다.

| 명령 코드 | 발행 주체 | 동작 |
| :--- | :--- | :--- |
| EMERGENCY_STOP | MES / 모바일 | 즉시 장비 정지. 진행 중 Lot 중단 → LOT_END(ABORTED) 발행 |
| STATUS_QUERY | MES / 모바일 | 즉시 STATUS_UPDATE 1회 발행 |
| ALARM_ACK | MES / 모바일 | 해당 알람의 retained 메시지 clear (빈 페이로드 + Retain=true) |
| ALARM_CLEAR | MES | 알람 해제 및 복구 시도 |
| RECIPE_LOAD | MES | 지정 Recipe 로드 → RECIPE_CHANGED 발행 |
| LOT_ABORT | MES | 현재 Lot 강제 종료 → LOT_END(ABORTED) 발행 |

### 2.3 N대 장비 시뮬레이션

단일 가상 EAP 프로세스에서 N대의 장비를 동시 시뮬레이션할 수 있어야 한다.

| 요구사항 | 상세 |
| :--- | :--- |
| 장비 ID 라우팅 | DS-VIS-001 ~ DS-VIS-00N, 각 장비별 독립 토픽 트리 |
| 독립 상태 관리 | 장비별 equipment_status(RUN/IDLE/STOP) 독립 유지 |
| 혼합 시나리오 | RUN+IDLE+STOP 혼재 상태 동시 재현 |
| 독립 LOT 진행 | 장비별 서로 다른 레시피/LOT 동시 진행 가능 |

---

## 3. 공통 메시지 규격

### 3.1 공통 헤더 필드

모든 이벤트 메시지에 포함되는 필수 헤더 필드이다.

| 필드명 | 타입 | 예시 | 필수 | 설명 |
| :--- | :--- | :--- | :--- | :--- |
| message_id | string (UUID v4) | e7026e09-477c-43c3-8ba5-35b7b7f8a659 | Y | RFC 4122 UUID v4 |
| event_type | string (enum) | STATUS_UPDATE | Y | 8종 이벤트 코드 |
| timestamp | string (ISO 8601) | 2026-01-22T16:41:42.123Z | Y | UTC 밀리초(.fffZ) 필수 |
| equipment_id | string | DS-VIS-001 | Y | 장비 고유 식별자 |
| equipment_status | string (enum) | RUN | 조건부 | RUN/IDLE/STOP. HEARTBEAT/CONTROL/ORACLE에서는 제외 |

### 3.2 Retained Message 정책

| 토픽 | Retained | 이유 |
| :--- | :--- | :--- |
| ds/{eq}/heartbeat | No | 3초 주기 발행, retained 시 stale 위험 |
| ds/{eq}/status | Yes | 앱 시작 시 즉시 상태 복원 |
| ds/{eq}/result | No | takt마다 갱신, retained 무의미 |
| ds/{eq}/lot | Yes | 마지막 LOT 결과 즉시 복원 |
| ds/{eq}/alarm | Yes | 미해결 알람 즉시 복원. ACK 시 빈 페이로드로 clear |
| ds/{eq}/recipe | Yes | 현재 활성 레시피 즉시 복원 |
| ds/{eq}/control | No | 1회성 명령, retained 금지 |
| ds/{eq}/oracle | Yes | 마지막 LOT 분석 결과 복원 |

### 3.3 Will 메시지 (EAP_DISCONNECTED)

EAP 프로세스 비정상 종료 시 Broker가 자동 발행하는 Will 메시지. 반드시 `WillRetain = true`로 설정해야 한다.

```json
{
  "message_id": "will-{equipment_id}-{uuid}",
  "event_type": "HW_ALARM",
  "timestamp": "{broker_timestamp}",
  "equipment_id": "{equipment_id}",
  "equipment_status": "STOP",
  "alarm_level": "CRITICAL",
  "hw_error_code": "EAP_DISCONNECTED",
  "hw_error_source": "PROCESS",
  "hw_error_detail": "EAP process terminated unexpectedly.",
  "auto_recovery_attempted": false,
  "requires_manual_intervention": true
}
```

---

## 4. 이벤트별 상세 명세

### 4.1 HEARTBEAT — 온라인 감지 경량 신호

**토픽:** `ds/{equipment_id}/heartbeat` | **QoS:** 1 | **주기:** 3초

4필드만 포함하는 경량 생존 신호. 장비 RUN/IDLE/STOP 무관 항상 발행.

| 필드명 | 타입 | 설명 |
| :--- | :--- | :--- |
| message_id | UUID | 메시지 고유 ID |
| event_type | string | "HEARTBEAT" 고정 |
| timestamp | ISO 8601 | UTC 밀리초 |
| equipment_id | string | 장비 ID |

**판정 기준 (Rule R01):**
- ONLINE: 최근 9초 이내 수신
- WARNING: 9~30초 미수신
- OFFLINE: 30초 초과 미수신

```json
{
  "message_id": "e7026e09-477c-43c3-8ba5-35b7b7f8a659",
  "event_type": "HEARTBEAT",
  "timestamp": "2026-01-22T16:17:10.921Z",
  "equipment_id": "DS-VIS-001"
}
```

---

### 4.2 STATUS_UPDATE — 장비 상태 보고

**토픽:** `ds/{equipment_id}/status` | **QoS:** 1 | **Retained:** Yes | **주기:** 6초

| 필드명 | 타입 | 필수 | 설명 |
| :--- | :--- | :--- | :--- |
| message_id | UUID | Y | 공통 헤더 |
| event_type | string | Y | "STATUS_UPDATE" 고정 |
| timestamp | ISO 8601 | Y | 발행 시각 |
| equipment_id | string | Y | 장비 ID |
| equipment_status | enum | Y | RUN / IDLE / STOP |
| lot_id | string | Y | 현재(또는 마지막) Lot ID |
| recipe_id | string | Y | 활성(또는 마지막) Recipe ID |
| recipe_version | string | Y | Recipe 버전 |
| operator_id | string | Y | 운영 엔지니어 ID |
| uptime_sec | integer | Y | 기동 후 경과(초) |
| current_unit_count | integer | N | 현재 LOT 검사 완료 unit 누적 수 |
| expected_total_units | integer | N | 레시피 이력 기반 예상 총 unit 수 (이력 없으면 null) |
| current_yield_pct | float | N | 현재 LOT 실시간 수율(%) |

**상태 전환 규칙:**

| 이전 | 이후 | 트리거 | 정상 여부 |
| :--- | :--- | :--- | :--- |
| IDLE | RUN | LOT 시작 | 정상 |
| RUN | IDLE | LOT_END (COMPLETED) | 정상 |
| RUN | IDLE | LOT_END (ABORTED) | 비정상 |
| RUN | STOP | HW_ALARM (CRITICAL) | 비정상 |
| STOP | STOP | HW_ALARM 후 미복구 | 비정상 |

**구현 시 유의사항:**
- `current_unit_count`: LOT 시작 시 0 초기화, INSPECTION_RESULT 발행 시마다 +1
- `expected_total_units`: 직전 동일 레시피 LOT 3개의 total_units 평균. 이력 없으면 null
- `current_yield_pct`: INSPECTION_RESULT 발행 시마다 pass_count / current_unit_count * 100 갱신

---

### 4.3 INSPECTION_RESULT — 단위 검사 결과

**토픽:** `ds/{equipment_id}/result` | **QoS:** 1 | **주기:** takt ~1,620ms

8슬롯(ZAxisNum 0~7) 기준 단위 부품 검사 결과. 페이로드 약 2.1KB.

#### 4.3.1 최상위 필드

| 필드명 | 타입 | 필수 | 설명 |
| :--- | :--- | :--- | :--- |
| message_id | UUID | Y | 공통 헤더 |
| event_type | string | Y | "INSPECTION_RESULT" 고정 |
| timestamp | ISO 8601 | Y | 밀리초 필수 |
| equipment_id | string | Y | 장비 ID |
| equipment_status | string | Y | 항상 "RUN" |
| lot_id | string | Y | 소속 Lot ID |
| unit_id | string | Y | 단위 부품 ID |
| strip_id | string | Y | 소속 Strip ID |
| recipe_id | string | Y | 적용 Recipe ID |
| recipe_version | string | Y | Recipe 버전 |
| operator_id | string | Y | 운영자 ID |
| overall_result | enum | Y | PASS / FAIL |
| fail_reason_code | string | N | FAIL 시 코드. PASS이면 null |
| fail_count | integer | Y | 불량 슬롯 수. PASS이면 0 |
| total_inspected_count | integer | Y | 슬롯 수 고정 = 8 |
| inspection_detail | object | Y | prs_result[] + side_result[] |
| geometric | object | N | 외형 치수 |
| bga | object | N | BGA 볼 품질 |
| surface | object | N | 표면 결함 |
| singulation | object | N | 절삭 품질 |
| process | object | Y | 검사 성능 메타 |

#### 4.3.2 inspection_detail 서브오브젝트

GVisionWpf 로그 원본 PascalCase 키명 그대로 사용.

**prs_result[] — PRS 픽앤소트 (CameraId=1, InspectionType=10)**

| 필드명 | 타입 | 설명 |
| :--- | :--- | :--- |
| ZAxisNum | int (0~7) | 슬롯 번호 |
| InspectionResult | int (0/1) | 1=PASS / 0=FAIL |
| ErrorType | int | 오류 코드. 1=정상 |
| XOffset | int | X축 오프셋. 정상 범위 ±300 |
| YOffset | int | Y축 오프셋. 정상 범위 ±300 |
| TOffset | int | 회전 오프셋. 정상 범위 ±10,000 |

**side_result[] — SIDE 측면 (CameraId=5, InspectionType=50)**

| 필드명 | 타입 | 설명 |
| :--- | :--- | :--- |
| ZAxisNum | int (0~7) | 슬롯 번호 |
| InspectionResult | int (0/1) | 1=PASS / 0=FAIL |
| ErrorType | int | 오류 코드. 1=정상 |
| XOffset | int | 항상 0 (외관 판정만, 위치보정 없음) |
| YOffset | int | 항상 0 |
| TOffset | int | 항상 0 |

#### 4.3.3 detail 그룹 서브오브젝트

**geometric — 외형 치수**

| 필드명 | 타입 | PASS 예시 | FAIL 예시 | 설명 |
| :--- | :--- | :--- | :--- | :--- |
| dimension_w_mm | float | 10.02 | 10.01 | 패키지 가로 (mm) |
| dimension_l_mm | float | 10.01 | 9.99 | 패키지 세로 (mm) |
| dimension_h_mm | float | 1.45 | 1.46 | 패키지 두께 (mm) |
| x_offset_um | float | 121.0 | 102.0 | X축 오프셋 (um) |
| y_offset_um | float | -94.0 | -130.0 | Y축 오프셋 (um) |
| theta_deg | float | -0.12 | -0.55 | 회전 편차 (deg). 허용 ±0.5 |
| kerf_width_um | float | 52.3 | 51.5 | 절삭 폭 (um) |

**bga — BGA 볼 품질**

| 필드명 | 타입 | PASS 예시 | 설명 |
| :--- | :--- | :--- | :--- |
| available | bool | true | BGA 검사 활성 여부 |
| ball_count_nominal | int | 256 | 설계 볼 개수 |
| ball_count_actual | int | 256 | 실측 볼 개수 |
| ball_diameter_avg_mm | float | 0.498 | 볼 평균 지름 (mm) |
| coplanarity_mm | float | 0.065 | 볼 높이 편차 최댓값 (mm). 기준 ≤ 0.1 |
| pitch_deviation_um | float | 4.2 | 볼 간격 편차 (um) |
| max_ball_offset_um | float | 18.7 | 최대 볼 위치 편차 (um) |
| avg_ball_offset_um | float | 6.1 | 평균 볼 위치 편차 (um) |

**surface — 표면 결함**

| 필드명 | 타입 | PASS 예시 | 설명 |
| :--- | :--- | :--- | :--- |
| foreign_material_size_um | float | 0 | 이물질 최대 크기. 0이면 없음 |
| scratch_area_mm2 | float | 0.003 | 스크래치 면적 (mm2) |
| marking_quality_grade | string | "A" | 마킹 품질 등급 A~F |

**singulation — 절삭 품질**

| 필드명 | 타입 | PASS 예시 | FAIL 예시 | 설명 |
| :--- | :--- | :--- | :--- | :--- |
| chipping_top_um | float | 28.0 | 65.2 | 상면 칩핑 (um). 정상<40 / WARNING 40~50 / CRITICAL>50 |
| chipping_bottom_um | float | 22.5 | 58.1 | 하면 칩핑 (um). 정상<35 / WARNING 35~45 / CRITICAL>45 |
| burr_height_um | float | 5.2 | 9.8 | 버 높이 (um). 정상<5 / WARNING 5~8 / CRITICAL>8 |

**process — 검사 성능 메타**

| 필드명 | 타입 | 예시 | 설명 |
| :--- | :--- | :--- | :--- |
| inspection_duration_ms | int | 1200 | 순수 검사 시간. 정상<1,500 / WARNING 1,500~2,000 |
| takt_time_ms | int | 1620 | 투입~배출 전체. 실측 ~1,620ms |
| algorithm_version | string | "v4.3.1" | 비전 알고리즘 버전 |

#### 4.3.4 fail_reason_code 코드 목록

| 코드 | 관련 슬롯 | ErrorType | 설명 |
| :--- | :--- | :--- | :--- |
| SIDE_VISION_FAIL | side_result | ET=52 | SIDE 알고리즘 종합 실패 (Teaching 미완성) |
| CHIPPING_EXCEED | side_result | ET=12 | 측면 치수/칩핑 기준 미달 |
| DIMENSION_OUT_OF_SPEC | prs_result | ET=11 | PRS 픽업 오프셋 허용치 초과 |
| PRS_X_AXIS_EXCEED | prs_result | ET=15 | X축 과편차 |
| PRS_MULTI_AXIS_EXCEED | prs_result | ET=17 | 복합 편차 |
| IMAGE_ACQUISITION_FAIL | - | ET=30 | 이미지 미취득 (CAM_TIMEOUT 전조) |
| FOREIGN_MATERIAL | surface | ET=50 | 이물질 크기 기준 초과 |
| BGA_BALL_MISSING | bga | - | 볼 개수 설계값 미달 |

#### 4.3.5 ErrorType 코드북 (실측 기반)

| ET | 모듈 | 의미 | Offset 패턴 |
| :--- | :--- | :--- | :--- |
| 1 | MAP/PRS/SIDE | 정상 PASS | PRS: 실측값 / SIDE: 0 |
| 3 | MAP/SIDE | 패키지 미감지 | 모두 0 |
| 11 | PRS | 픽업 오프셋 ±300 초과 | 실측값 있음 |
| 12 | SIDE | 치수/칩핑 기준 미달 | 모두 0 |
| 15 | PRS | X축 편차 과도 | XOffset 큰 값 |
| 17 | PRS | 복합 편차 | 다양한 값 |
| 30 | MAP/PRS/SIDE | 이미지 미취득/타임아웃 | 모두 0 |
| 50 | SIDE | 이물질/오염 | 모두 0 |
| 52 | SIDE | 알고리즘 종합 실패 | 모두 0 |
| 55 | SIDE | Burr 높이 초과 | 모두 0 |
| 62 | PRS | Carsem_4X6 연관 (미확인) | XO±200/YO±100 |

---

### 4.4 LOT_END — Lot 완료 이벤트

**토픽:** `ds/{equipment_id}/lot` | **QoS:** 2 | **Retained:** Yes

Lot 완료 직후 1회 발행. LOT_END 수신 시 Oracle 2차 검증 트리거.

| 필드명 | 타입 | 필수 | 설명 |
| :--- | :--- | :--- | :--- |
| message_id | UUID | Y | 공통 헤더 |
| event_type | string | Y | "LOT_END" 고정 |
| timestamp | ISO 8601 | Y | 완료 시각 |
| equipment_id | string | Y | 장비 ID |
| equipment_status | string | Y | 항상 "IDLE" |
| lot_id | string | Y | 완료된 Lot ID |
| lot_status | enum | Y | COMPLETED / ABORTED / ERROR |
| total_units | int | Y | 전체 단위 수. 실측: 2,792 |
| pass_count | int | Y | 합격 수 |
| fail_count | int | Y | 불합격 수 |
| yield_pct | float | Y | 수율(%). 실측: 96.2% |
| lot_duration_sec | int | Y | 소요시간(초). 실측: 4,923s (82분) |

**수율 판정 기준 (Rule R23):**

| 구간 | 판정 | 조치 |
| :--- | :--- | :--- |
| >= 98% | EXCELLENT | 정상 양산 |
| 95~98% | NORMAL | 정상. Carsem_3X3 실측 96.2% |
| 90~95% | WARNING | Oracle 1차 검증 트리거 |
| 80~90% | MARGINAL | 생산 중단 검토 |
| < 80% | CRITICAL | 즉시 생산 중단 |

**Lot 소요시간 판정 (Rule R24):**
- < 24,000s (400분): NORMAL
- >= 24,000s: LOT_END 미수신 의심 → CRITICAL

---

### 4.5 HW_ALARM — 하드웨어/소프트웨어 알람

**토픽:** `ds/{equipment_id}/alarm` | **QoS:** 2 | **Retained:** Yes

| 필드명 | 타입 | 필수 | 설명 |
| :--- | :--- | :--- | :--- |
| message_id | UUID | Y | 공통 헤더 |
| event_type | string | Y | "HW_ALARM" 고정 |
| timestamp | ISO 8601 | Y | 알람 발생 시각 |
| equipment_id | string | Y | 장비 ID |
| equipment_status | enum | Y | CRITICAL → STOP / WARNING → RUN 가능 |
| alarm_level | enum | Y | CRITICAL / WARNING / INFO |
| hw_error_code | string | Y | 오류 코드 |
| hw_error_source | enum | Y | CAMERA / LIGHTING / VISION / PROCESS / MOTION / COOLANT |
| hw_error_detail | string | Y | 오류 상세 (GVisionWpf 클래스 경로 포함) |
| exception_detail | object | N | SW 예외 상세. 없으면 null |
| auto_recovery_attempted | bool | Y | 자동 복구 시도 여부 |
| requires_manual_intervention | bool | Y | 수동 처리 필요 여부 |
| burst_id | UUID | N | 알람 폭주 그룹 ID. 단독이면 null |
| burst_count | int | N | burst_id 그룹 내 누적 횟수. 단독이면 1 |

**exception_detail 서브오브젝트:**

| 필드명 | 타입 | 설명 |
| :--- | :--- | :--- |
| module | string | GVisionWpf 네임스페이스.클래스 |
| exception_type | string | 예외 타입명 |
| stack_trace_hash | string | 스택 트레이스 앞 8자리 해시 |

**hw_error_code 코드 목록:**

| 코드 | 소스 | 레벨 | 자동복구 | 설명 |
| :--- | :--- | :--- | :--- | :--- |
| CAM_TIMEOUT_ERR | CAMERA | CRITICAL | N | ET=30 연속 3회. 카메라 타임아웃 |
| WRITE_FAIL | VISION | CRITICAL | N | HALCON #3142. 디스크 포화/권한 |
| VISION_SCORE_ERR | VISION | WARNING | N | 비전 오류 통합 (3케이스 — hw_error_detail로 구분) |
| LIGHT_PWR_LOW | LIGHTING | WARNING | Y | 조명 파라미터 유효 범위 이탈 |
| EAP_DISCONNECTED | PROCESS | CRITICAL | N | Will 메시지 전용. 비정상 종료 |
| STAGE_HOME_FAIL | MOTION | CRITICAL | N | X축 홈 복귀 실패 (예약) |
| WATER_FLOW_LOW | COOLANT | WARNING | Y | 절삭수 유량 미달 (예약) |

**VISION_SCORE_ERR 3케이스 (hw_error_detail 기반 구분):**

| 케이스 | hw_error_detail 키워드 | exception_type |
| :--- | :--- | :--- |
| Teaching 이미지 NULL | "HALCON error #4056" / "NULL in smallest_rectangle2" | HOperatorException |
| Teaching 미완성 | "SIDE ET=52 fail rate exceeded 50%" | null |
| LOT 시작 실패 | "UnobservedTaskException in LotController.StartNewLot" | AggregateException |

**알람 ACK 시퀀스:**

```
1. EAP가 HW_ALARM 발행 (Retain=true)
2. 오퍼레이터가 모바일에서 확인 → CONTROL_CMD(ALARM_ACK) 발행
3. EAP가 수신 → ds/{eq}/alarm에 빈 페이로드 + Retain=true 발행
4. Broker가 retained 메시지 삭제 → 신규 구독자 알람 미수신
```

---

### 4.6 RECIPE_CHANGED — 레시피 전환

**토픽:** `ds/{equipment_id}/recipe` | **QoS:** 2 | **Retained:** Yes

| 필드명 | 타입 | 필수 | 설명 |
| :--- | :--- | :--- | :--- |
| message_id | UUID | Y | 공통 헤더 |
| event_type | string | Y | "RECIPE_CHANGED" 고정 |
| timestamp | ISO 8601 | Y | 변경 시각 |
| equipment_id | string | Y | 장비 ID |
| equipment_status | string | Y | 항상 "IDLE" |
| previous_recipe_id | string | Y | 변경 전 Recipe ID |
| previous_recipe_version | string | Y | 변경 전 버전 |
| new_recipe_id | string | Y | 변경 후 Recipe ID |
| new_recipe_version | string | Y | 변경 후 버전 |
| changed_by | string | Y | 엔지니어 ID 또는 MES_AUTO |

**실측 레시피 목록:**

| recipe_id | 형식 | 용도 | 비고 |
| :--- | :--- | :--- | :--- |
| Carsem_3X3 | 이름형 | 주력 양산 | 수율 96.2%. MAP 1,216건/Strip |
| ATC_1X1 | 이름형 | 교정/검증 | Sequence=9999 더미 스트립 |
| Carsem_4X6 | 이름형 | 신규 양산 (Teaching 미완성) | ET=52 전수 실패 유발 |
| 446275 | 숫자형 | 미확인 | EMAP 181개 이상치 |
| 640022 | 숫자형 | 미확인 | AggregateException 연관 |

**레시피 전환 판정:**
- 기존 이력 있는 recipe_id → 정상 전환, 즉시 양산
- Historian 이력 없는 recipe_id → 신규 투입, Teaching 모니터링 50 Strip 시작
- 숫자형 ID(446275 등) → Rule R31 트리거, DS 측 확인 알림
- equipment_status != IDLE 시 전환 → 비정상 전환 경보

---

### 4.7 CONTROL_CMD — 원격 제어 명령 [Subscribe]

**토픽:** `ds/{equipment_id}/control` | **QoS:** 2 | **방향:** Broker → EAP

| 필드명 | 타입 | 필수 | 설명 |
| :--- | :--- | :--- | :--- |
| message_id | UUID | Y | 명령 고유 ID |
| event_type | string | Y | "CONTROL_CMD" 고정 |
| timestamp | ISO 8601 | Y | 명령 발행 시각 |
| command | enum | Y | EMERGENCY_STOP / ALARM_CLEAR / RECIPE_LOAD / LOT_ABORT / STATUS_QUERY / ALARM_ACK |
| issued_by | string | Y | MES_SERVER / MOBILE_APP / OPERATOR |
| reason | string | N | 명령 사유 |
| target_lot_id | string | N | 특정 Lot 대상 명령 |
| target_burst_id | UUID | N | ALARM_ACK 시 대상 burst_id |

**모바일 허용 명령:** EMERGENCY_STOP, STATUS_QUERY, ALARM_ACK (3종만)

---

### 4.8 ORACLE_ANALYSIS — Oracle 2차 검증 결과

**토픽:** `ds/{equipment_id}/oracle` | **QoS:** 2 | **Retained:** Yes

LOT_END 수신 후 비동기 2차 검증 결과. EWMA+MAD / Isolation Forest 기반.

| 필드명 | 타입 | 필수 | 설명 |
| :--- | :--- | :--- | :--- |
| message_id | UUID | Y | 공통 헤더 |
| event_type | string | Y | "ORACLE_ANALYSIS" 고정 |
| timestamp | ISO 8601 | Y | 분석 완료 시각 |
| equipment_id | string | Y | 장비 ID |
| lot_id | string | Y | 분석 대상 Lot ID |
| recipe_id | string | Y | 레시피 ID |
| judgment | enum | Y | NORMAL / WARNING / DANGER |
| yield_status | object | Y | { actual, dynamic_threshold, lot_basis } |
| ai_comment | string | Y | AI 자연어 요약 코멘트 |
| threshold_proposal | object | N | 임계값 변경 제안. 없으면 null |

**판정 등급:**

| 등급 | EWMA 조건 | Isolation Forest | 액션 |
| :--- | :--- | :--- | :--- |
| NORMAL | mu ± 2 sigma 이내 | 점수 < 0.5 | 보고서 생성, 경보 없음 |
| WARNING | 2 sigma ~ 3 sigma 초과 | 점수 0.5~0.85 | 보고서 + 주의 알림 |
| DANGER | 3 sigma 초과 | 점수 > 0.85 | 즉시 경보 + 작업 중단 권고 |

**레시피별 독립 학습 구조:**

| LOT 누적 | 활성 모델 | 임계값 기준 |
| :--- | :--- | :--- |
| 0~4 LOT | 오퍼레이터 시딩값 | 엔지니어 입력 초기값 |
| 5~9 LOT | EWMA+MAD | 실측 데이터 동적 계산 |
| 10+ LOT | EWMA+MAD + Isolation Forest | 복합 이상 감지 추가 |

---

## 5. 시뮬레이션 시나리오

### 5.1 정상 양산 흐름 (Golden Path)

가상 EAP가 재현해야 하는 기본 이벤트 흐름이다.

```
[T+0]     HEARTBEAT (3초 주기 시작, 항상 발행)
[T+1]     RECIPE_CHANGED (Carsem_3X3, IDLE)
[T+2]     STATUS_UPDATE (RUN, lot_id 생성)
[T+3~N]   INSPECTION_RESULT × 2,792회 (takt 1,620ms)
            ├── PASS (overall_result=PASS, fail_count=0)
            └── FAIL (산발적 ET=11/12/52, fail_count >= 1)
[T+N+1]   LOT_END (COMPLETED, yield 96.2%)
[T+N+2]   STATUS_UPDATE (IDLE)
[T+N+3]   ORACLE_ANALYSIS (NORMAL, 비동기)
```

### 5.2 비정상 시나리오

가상 EAP가 재현해야 하는 비정상 케이스들이다.

| 시나리오 | 트리거 | 이벤트 시퀀스 |
| :--- | :--- | :--- |
| Teaching 미완성 | Carsem_4X6 투입 | RECIPE_CHANGED → INSPECTION_RESULT(ET=52 전수) → HW_ALARM(VISION_SCORE_ERR) |
| 카메라 타임아웃 | ET=30 연속 3회 | INSPECTION_RESULT(ET=30) × 3 → HW_ALARM(CAM_TIMEOUT_ERR, STOP) |
| 디스크 포화 | HALCON #3142 | HW_ALARM(WRITE_FAIL) × 20 → fps 저하 → LOT_END(ABORTED) |
| EAP 크래시 | AggregateException 누적 | HW_ALARM(AggEx burst) → HEARTBEAT 중단 → Will(EAP_DISCONNECTED) |
| Lot 강제 종료 | CONTROL_CMD(LOT_ABORT) | CONTROL_CMD 수신 → LOT_END(ABORTED) → STATUS_UPDATE(IDLE) |
| 긴급 정지 | CONTROL_CMD(EMERGENCY_STOP) | CONTROL_CMD 수신 → 즉시 STATUS_UPDATE(STOP) |
| 조명 열화 | WrongValueException | HW_ALARM(LIGHT_PWR_LOW) → SIDE Pass율 점진 하락 |
| LOT_END 누락 | Start/End 불균형 | LOT Start 후 24,000s 초과 → HW_ALARM(VISION_SCORE_ERR, LotController) |

### 5.3 N:1 다설비 시나리오

4대 장비 동시 시뮬레이션 시나리오. `scenarios/multi_equipment_4x.json` 기반.

| 장비 ID | 상태 | 레시피 | 시나리오 |
| :--- | :--- | :--- | :--- |
| DS-VIS-001 | RUN | Carsem_3X3 | 정상 양산 (수율 96%) |
| DS-VIS-002 | RUN | Carsem_4X6 | Teaching 미완성 (ET=52 전수 FAIL) |
| DS-VIS-003 | IDLE | ATC_1X1 | 교정 대기 |
| DS-VIS-004 | STOP | - | CAM_TIMEOUT_ERR 알람 상태 |

---

## 6. Mock 데이터 인덱스

가상 EAP가 사용하는 실제 로그 기반 Mock 데이터 27종이다.

| # | 파일 | 이벤트 | 대표 수치 |
| :--- | :--- | :--- | :--- |
| 01 | heartbeat | HEARTBEAT | 3초 주기 |
| 02 | status_run | STATUS_UPDATE | RUN / Carsem_3X3 / uptime 1528s |
| 03 | status_idle | STATUS_UPDATE | IDLE / uptime 6420s |
| 04 | inspection_pass | INSPECTION_RESULT | PASS / ET=1 전체 / blade_wear 0.31 |
| 05 | inspection_fail_side_et52 | INSPECTION_RESULT | FAIL / ET=52 8/8 / Carsem_4X6 |
| 06 | inspection_fail_side_et12 | INSPECTION_RESULT | FAIL / ET=12 8/8 / chipping 65.2um |
| 07 | inspection_fail_prs_offset | INSPECTION_RESULT | FAIL / ET=11 3/8 / x_offset 102 |
| 08 | inspection_fail_side_mixed | INSPECTION_RESULT | FAIL / ET=52+12 혼재 |
| 09 | lot_end_normal | LOT_END | COMPLETED / 96.2% / 2,792 units |
| 10 | lot_end_aborted | LOT_END | ABORTED / 94.2% / 656 units |
| 11 | alarm_cam_timeout | HW_ALARM | CRITICAL / CAM_TIMEOUT_ERR / STOP |
| 12 | alarm_write_image_fail | HW_ALARM | CRITICAL / WRITE_FAIL / HALCON#3142 |
| 13 | alarm_vision_null_object | HW_ALARM | WARNING / VISION_SCORE_ERR (NULL, #4056) |
| 14 | alarm_light_param_err | HW_ALARM | WARNING / LIGHT_PWR_LOW / 자동복구 |
| 15 | alarm_side_vision_fail | HW_ALARM | WARNING / VISION_SCORE_ERR (ET=52 48%) |
| 16 | alarm_lot_start_fail | HW_ALARM | WARNING / VISION_SCORE_ERR (AggEx) |
| 17 | alarm_eap_disconnected | HW_ALARM | CRITICAL / EAP_DISCONNECTED / Will |
| 18 | recipe_changed_normal | RECIPE_CHANGED | ATC_1X1 → Carsem_3X3 |
| 19 | recipe_changed_new_4x6 | RECIPE_CHANGED | Carsem_3X3 → Carsem_4X6 (신규) |
| 20 | recipe_changed_446275 | RECIPE_CHANGED | ATC_1X1 → 446275 (숫자형) |
| 21 | control_emergency_stop | CONTROL_CMD | EMERGENCY_STOP / MOBILE_APP |
| 22 | control_status_query | CONTROL_CMD | STATUS_QUERY / MOBILE_APP |
| 23 | oracle_normal | ORACLE_ANALYSIS | NORMAL / Carsem_3X3 / 96.2% / 28 LOT |
| 24 | oracle_warning | ORACLE_ANALYSIS | WARNING / Carsem_4X6 / 68.5% / 8 LOT |
| 25 | oracle_danger | ORACLE_ANALYSIS | DANGER / Carsem_4X6 / 58.3% / 12 LOT |
| 26 | control_alarm_ack | CONTROL_CMD | ALARM_ACK / 단독 dismiss |
| 27 | control_alarm_ack_burst | CONTROL_CMD | ALARM_ACK / burst 41건 그룹 dismiss |

---

## 7. Rule-based 판정 기준 전체 (38개)

가상 EAP는 이 Rule들에 대응하는 데이터를 생성할 수 있어야 한다. Oracle 서버 1차 검증 if-else 구현에 직결된다.

| # | 파라미터 | 정상 | WARNING | CRITICAL |
| :--- | :--- | :--- | :--- | :--- |
| R01 | Heartbeat 간격 | <= 9s | 9~30s | > 30s |
| R02 | PRS XOffset | \|x\| <= 300 | 250 < \|x\| <= 300 | \|x\| > 300 (ET=11) |
| R03 | PRS YOffset | \|y\| <= 300 | 250 < \|y\| <= 300 | \|y\| > 300 (ET=11) |
| R04 | PRS TOffset | \|t\| <= 10,000 | 8,000 < \|t\| <= 10,000 | \|t\| > 10,000 |
| R05 | PRS ET=30 발생률 | < 1% | 1~3% | > 3% → CAM_TIMEOUT |
| R06 | PRS Pass율 | >= 95% | 90~95% | < 90% |
| R07 | PRS ET=11 동시 슬롯 | 0개 | 1~2개 | >= 3개 동시 |
| R08 | SIDE Pass율 | >= 96% | 90~96% | < 90% |
| R09 | SIDE ET=52 비율 | < 5% | 5~50% | > 50% (Teaching 의심) |
| R10 | SIDE ET=52 연속 Request | 0건 | 5~9건 | >= 10건 연속 |
| R11 | SIDE ET=12 발생 | 없음 | 산발 | 전 슬롯 ET=12 |
| R12 | SIDE ET=30 연속 | 0건 | 1~2건 | >= 3건 |
| R13 | chipping_top_um | < 40um | 40~50um | > 50um |
| R14 | chipping_bottom_um | < 35um | 35~45um | > 45um |
| R15 | burr_height_um | < 5um | 5~8um | > 8um |
| R16 | blade_wear_index | < 0.70 | 0.70~0.85 | > 0.85 (교체 권고) |
| R17 | spindle_load_pct | < 70% | 70~80% | > 80% |
| R18 | cutting_water_flow_lpm | >= 1.5 | 1.2~1.5 | < 1.2 |
| R19 | MAP 응답시간 P95 | < 2,500ms | 2,500~3,000ms | > 3,000ms |
| R20 | MAP 카메라 fps | 8~16fps | 6~8fps | < 6fps |
| R21 | MapQue 잔여 | 0개 | 15~30개 | > 30개 |
| R22 | takt_time_ms | < 2,000ms | 2,000~3,000ms | > 3,000ms |
| R23 | yield_pct | >= 95% | 90~95% | < 90% |
| R24 | lot_duration_sec | < 24,000s | - | >= 24,000s |
| R25 | LOT Start/End 차이 | 0 | 1~3 누적 | >= 5 누적 |
| R26 | CAM_TIMEOUT_ERR/일 | 0건 | 1~2건 | > 3건 |
| R27 | WRITE_FAIL | 0건 | 1건 이상 | 연속 5건 이상 |
| R28 | VISION_SCORE_ERR (NULL) | 0건 | 산발 | 연속 10건 이상 |
| R29 | LIGHT_PWR_LOW | 0건 | 1건 | 연속 3건 이상 |
| R30 | 신규 레시피 Fail율 | < 10% | 10~30% | > 30% 지속 |
| R31 | 숫자형 레시피 ID | 문자형 | - | 숫자형 |
| R32 | EMAP 크기 | <= 100개 | 100~150개 | > 150개 |
| R33 | AggregateException | 0건 | 1~5건/일 | > 5건/일 |
| R34 | EAP_DISCONNECTED | 0건 | 1건/주 | > 2건/주 |
| R35 | 동일 레시피 ABORTED | 0회 | 1회 | >= 2회 연속 |
| R36 | blade_usage_count | < 20,000회 | 20,000~25,000회 | > 25,000회 |
| R37 | inspection_duration_ms | < 1,500ms | 1,500~2,000ms | > 2,000ms |
| R38a | Isolation Forest 점수 | < 0.5 | 0.5~0.85 | > 0.85 |
| R38b | EWMA 이탈 | mu +- 2 sigma | 2~3 sigma | 3 sigma 초과 |
| R38c | status 비정상 전환 | 정상 패턴 | - | RUN→STOP 무경고 |

---

## 8. MQTT ACL 및 연결 설정

### 8.1 가상 EAP 계정 권한

```
user eap_vis_001
topic write ds/DS-VIS-001/#
topic read ds/DS-VIS-001/control
```

- Publish: `ds/{id}/heartbeat`, `status`, `result`, `lot`, `alarm`, `recipe`
- Subscribe: `ds/{id}/control`

### 8.2 연결 규격

| 항목 | 값 |
| :--- | :--- |
| 프로토콜 | MQTT v5.0 |
| Broker | Eclipse Mosquitto 2.x |
| Timestamp | ISO 8601 UTC 밀리초(.fffZ) 필수 |
| 재연결 백오프 | 1s → 2s → 5s → 15s → 30s, max 60s, jitter ±20% |
| Keep Alive | 30초 권장 |
| Will 메시지 | HW_ALARM(EAP_DISCONNECTED), WillRetain=true 필수 |

### 8.3 QoS 정책

| QoS | 적용 이벤트 | 이유 |
| :--- | :--- | :--- |
| QoS 1 (At Least Once) | HEARTBEAT, STATUS_UPDATE, INSPECTION_RESULT | 주기 발행, 1건 누락 허용 |
| QoS 2 (Exactly Once) | LOT_END, HW_ALARM, RECIPE_CHANGED, CONTROL_CMD, ORACLE_ANALYSIS | 유실/중복 방지 필수 |

---

## 9. 이벤트 흐름 시퀀스

### 9.1 표준 양산 사이클

| 순서 | 이벤트 | QoS | status | 설명 |
| :--- | :--- | :--- | :--- | :--- |
| 1 | HEARTBEAT | 1 | - | 3초 주기. 항상 발행 |
| 2 | RECIPE_CHANGED | 2 | IDLE | Lot 투입 전 Recipe 확정 |
| 2a | HW_ALARM (E-5) | 2 | RUN | 신규 레시피 시 Teaching 모니터링 시작 |
| 3 | STATUS_UPDATE | 1 | RUN | Lot 시작. 6초 주기 |
| 4~N | INSPECTION_RESULT | 1 | RUN | Unit 완료 시마다 (실측 2,792회) |
| N+1 | LOT_END | 2 | IDLE | Lot 수율 집계. Oracle 2차 검증 트리거 |
| N+2 | ORACLE_ANALYSIS | 2 | - | Oracle 비동기 발행 |
| * | HW_ALARM | 2 | * | 이상 발생 즉시 |
| * | CONTROL_CMD | 2 | * | MES/모바일 명령 수신 |

### 9.2 실측 기준값 (Carsem 현장)

| 지표 | 실측값 | 비고 |
| :--- | :--- | :--- |
| Heartbeat 주기 | 3초 | 예외 중에도 정상 |
| STATUS 주기 | 6초 | |
| takt_time | ~1,620ms | MAP+PRS+SIDE 합산 |
| total_units/Lot | 2,792 | 349 Strip x 8슬롯 |
| 정상 수율 (Carsem_3X3) | 96.2% | 28 LOT 학습 기반 |
| Lot 소요시간 | 82분 (4,923s) | 정상 범위 40~180분, 최대 370분 |
| 카메라 fps (MAP) | 8~16fps | 정상. <6fps → 이상 |
| PRS 카메라 fps | 20~40fps | 고속 시 ~120fps |
| SIDE 카메라 fps | 4.4~12.7fps | |
| PRS Req당 Resp | 8건 고정 | ZAxisNum 0~7 |
| SIDE Req당 Resp | 8건 고정 | ZAxisNum 0~7 |
| MAP Req당 Resp | 1,216건 (3X3) / 672건 (4X6) | 레시피별 상이 |

---

## 10. 비기능 요구사항

| 항목 | 요구사항 |
| :--- | :--- |
| 언어 | C# (.NET) |
| MQTT 라이브러리 | MQTTnet (GVisionWpf 실제 사용 라이브러리 동일) |
| 데이터 직렬화 | System.Text.Json (UTF-8 JSON) |
| 동시 장비 수 | 최소 4대 동시 시뮬레이션 |
| 타이밍 정확도 | Heartbeat 3s ±500ms / STATUS 6s ±1s / takt 1,620ms ±200ms |
| 로깅 | 발행 메시지 타임스탬프 + 토픽 콘솔 출력 |
| 설정 관리 | 장비 수, 레시피, 시나리오를 외부 설정(JSON/YAML)으로 관리 |
| Graceful Shutdown | 프로세스 종료 시 Will 메시지 발동 전 정상 LOT_END 처리 |

---

## 부록 A. 용어 정의

| 용어 | 설명 |
| :--- | :--- |
| EAP | Equipment Automation Program. 장비 자동화 프로그램 |
| GVisionWpf | Genesem 비전 검사 소프트웨어 (C# WPF + HALCON) |
| LOT | 하나의 작업 단위. 복수의 Strip으로 구성 |
| Strip | 반도체 패키지 기판. 복수의 Unit(디바이스) 포함 |
| Unit | 개별 반도체 패키지 (검사 최소 단위) |
| PRS | Pick & Sort 검사 모듈 (CameraId=1, 위치 보정) |
| SIDE | 측면 비전 검사 모듈 (CameraId=5, 외관 판정) |
| MAP | 맵핑 검사 모듈 (CameraId=0, 전수 검사) |
| ErrorType (ET) | GVisionWpf 비전 검사 오류 코드 |
| takt | 투입~배출 1사이클 소요시간 |
| EWMA | Exponentially Weighted Moving Average. 지수가중이동평균 |
| MAD | Median Absolute Deviation. 중앙값 절대 편차 |
| Teaching | 신규 레시피 등록 시 비전 알고리즘 학습 과정 |
| EMAP | Equipment MAP. 장비 맵핑 데이터 |

---

## 부록 B. 참조 문서

| 문서 | 버전 | 설명 |
| :--- | :--- | :--- |
| DS_EAP_MQTT_API_명세서.md | v3.4 | MQTT 이벤트 메시지 API 전체 명세 |
| DS_이벤트정의서.md | v1.0 | 이벤트 분류 체계 및 Rule-based 판정 기준 |
| DS_Carsem_로그분석_참조보고서.md | v1.0 | Carsem 현장 14일 실가동 로그 분석 |
| 오라클 2차 검증 기획안.md | v1.0 | Oracle 서버 2차 검증 설계 |
| 기획안.md | v1.0 | 시스템 아키텍처 및 서버 구성 |
| 기업소개 및 요구사항.md | v1.0 | DS 주식회사 프로젝트 요구사항 |

---

*마지막 업데이트: 2026-04-12*
*문서번호: DS-EAP-VM-SPEC-001 v1.0*
