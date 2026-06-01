namespace DsEap.Configuration;

public sealed class EapSettings
{
    public BrokerSettings Broker { get; set; } = new();
    public BackoffSettings Backoff { get; set; } = new();
    public TimingSettings Timing { get; set; } = new();
    public PathSettings Paths { get; set; } = new();
    public string RunMode { get; set; } = "Scenario"; // "Scenario" | "GoldenPath"
    public GoldenPathSettings GoldenPath { get; set; } = new();

    // E7 검증/통합테스트용. 0 또는 미지정이면 비활성. 양수면 해당 초 후
    // IHostApplicationLifetime.StopApplication()을 호출해 Ctrl+C와 동일한 graceful 경로 진입.
    public int AutoShutdownAfterSec { get; set; } = 0;
}

public sealed class BrokerSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public int KeepAliveSeconds { get; set; } = 30;
    public uint SessionExpirySeconds { get; set; } = 3600;
    public bool CleanStart { get; set; } = false;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    // 장비별 계정 오버라이드 (key = equipment_id, 예: "DS-VIS-001")
    // 비어있으면 Username/Password 단일 계정으로 GoldenPath 장비 1대에 사용
    public Dictionary<string, EquipmentCredential> PerEquipment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class EquipmentCredential
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class BackoffSettings
{
    public int[] StepsSeconds { get; set; } = new[] { 1, 2, 5, 15, 30, 60 };
    public int JitterPct { get; set; } = 20;
}

public sealed class TimingSettings
{
    public int HeartbeatIntervalMs { get; set; } = 3000;
    public int StatusIntervalMs { get; set; } = 6000;
    public int TaktTimeMs { get; set; } = 1620;
    public int ExpectedTotalUnits { get; set; } = 2792;
    public double PassRatio { get; set; } = 0.962;
    public int GoldenPathMaxUnits { get; set; } = 40;
    public int ShutdownTimeoutMs { get; set; } = 5000;
}

public sealed class PathSettings
{
    public string MockDataDir { get; set; } = "";
    public string ScenarioFile { get; set; } = "";
}

public sealed class GoldenPathSettings
{
    public string EquipmentId { get; set; } = "DS-VIS-001";
    public string RecipeId { get; set; } = "Carsem_3X3";
    public string RecipeVersion { get; set; } = "v1.0";
    public string OperatorId { get; set; } = "ENG-KIM";
}
