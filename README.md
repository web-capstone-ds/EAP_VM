# DS EAP VM (가상 EAP 서버)

반도체 후공정 비전 검사 장비(EAP)를 소프트웨어로 시뮬레이션하는 가상 장비 서버.  
실제 GVisionWpf 장비를 대신하여 MQTT로 8종 이벤트를 발행하며, 전체 시스템의 데이터 소스 역할을 합니다.

## 기술 스택

| 항목 | 내용 |
|---|---|
| Language | C# (.NET 8) |
| MQTT | MQTTnet 4.x (MQTT v5.0) |
| 직렬화 | System.Text.Json |

## 디렉토리 구조

```
EAP_VM/
├── src/DsEap/
│   ├── Configuration/   # EAP 설정 (appsettings.json 바인딩)
│   ├── Equipment/       # VirtualEquipment, EquipmentManager, 상태 관리
│   ├── Events/
│   │   ├── Models/      # 8종 이벤트 페이로드 DTO
│   │   └── Publishers/  # HeartbeatLoop, InspectionLoop, StatusLoop 등
│   ├── MockData/        # Mock 데이터 로더 및 페이로드 변환기
│   ├── Mqtt/            # MQTT 클라이언트 매니저, 구독, Will 정책
│   └── Scenarios/       # 다중 장비 시나리오 러너
└── tests/
    ├── DsEap.Tests/     # 단위 테스트 (11종)
    └── DsEap.ControlCli/ # 수동 제어 CLI 도구
```

## 발행하는 MQTT 이벤트 (8종)

| 토픽 | 이벤트 | QoS |
|---|---|---|
| `ds/{eq}/heartbeat` | HEARTBEAT | 1 |
| `ds/{eq}/status` | STATUS_UPDATE | 1 |
| `ds/{eq}/result` | INSPECTION_RESULT | 1 |
| `ds/{eq}/lot` | LOT_END | 2 |
| `ds/{eq}/alarm` | HW_ALARM | 2 |
| `ds/{eq}/recipe` | RECIPE_CHANGED | 2 |
| `ds/{eq}/control` | CONTROL_CMD | 2 |
| `ds/{eq}/oracle` | ORACLE_ANALYSIS (구독) | 2 |

## Mock 데이터

`../DS-Document/EAP_mock_data/` 에서 27종 Mock JSON을 참조합니다 (Carsem 14일 실측값 기반).  
다중 장비(4대) 동시 시뮬레이션 시나리오: `scenarios/multi_equipment_4x.json`

## 실행 방법

```bash
cd EAP_VM/src/DsEap
dotnet run
```

설정 파일: `config/appsettings.json`
- MQTT Broker 주소, 장비 ID 목록, 시나리오 설정 등

## 테스트

```bash
cd EAP_VM
dotnet test tests/DsEap.Tests/
```
