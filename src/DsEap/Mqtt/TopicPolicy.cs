using MQTTnet.Protocol;

namespace DsEap.Mqtt;

// eap-spec §1.2 / API 명세서 §1.1 — 토픽별 QoS·Retained 정책
public static class TopicPolicy
{
    public enum Kind { Heartbeat, Status, Result, Lot, Alarm, Recipe, Control, Oracle }

    public readonly record struct Policy(MqttQualityOfServiceLevel Qos, bool Retain);

    private static readonly IReadOnlyDictionary<Kind, Policy> Map = new Dictionary<Kind, Policy>
    {
        [Kind.Heartbeat] = new(MqttQualityOfServiceLevel.AtLeastOnce, false),
        [Kind.Status]    = new(MqttQualityOfServiceLevel.AtLeastOnce, true),
        [Kind.Result]    = new(MqttQualityOfServiceLevel.AtLeastOnce, false),
        [Kind.Lot]       = new(MqttQualityOfServiceLevel.ExactlyOnce, true),
        [Kind.Alarm]     = new(MqttQualityOfServiceLevel.ExactlyOnce, true),
        [Kind.Recipe]    = new(MqttQualityOfServiceLevel.ExactlyOnce, true),
        [Kind.Control]   = new(MqttQualityOfServiceLevel.ExactlyOnce, false),
        [Kind.Oracle]    = new(MqttQualityOfServiceLevel.ExactlyOnce, true),
    };

    public static Policy Get(Kind kind) => Map[kind];

    public static string Segment(Kind kind) => kind switch
    {
        Kind.Heartbeat => "heartbeat",
        Kind.Status    => "status",
        Kind.Result    => "result",
        Kind.Lot       => "lot",
        Kind.Alarm     => "alarm",
        Kind.Recipe    => "recipe",
        Kind.Control   => "control",
        Kind.Oracle    => "oracle",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Topic(Kind kind, string equipmentId) =>
        $"ds/{equipmentId}/{Segment(kind)}";
}
