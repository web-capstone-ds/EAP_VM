using DsEap.Mqtt;
using MQTTnet.Protocol;
using Xunit;

namespace DsEap.Tests;

// QoS/Retained 정책이 API 명세서 §1.1 테이블과 일치하는지 검증
public sealed class MqttPolicyTests
{
    // §1.2.1 QoS 정책 — QoS 1: heartbeat, status, result / QoS 2: lot, alarm, recipe, control, oracle
    [Theory]
    [InlineData(TopicPolicy.Kind.Heartbeat, MqttQualityOfServiceLevel.AtLeastOnce)]
    [InlineData(TopicPolicy.Kind.Status,    MqttQualityOfServiceLevel.AtLeastOnce)]
    [InlineData(TopicPolicy.Kind.Result,    MqttQualityOfServiceLevel.AtLeastOnce)]
    [InlineData(TopicPolicy.Kind.Lot,       MqttQualityOfServiceLevel.ExactlyOnce)]
    [InlineData(TopicPolicy.Kind.Alarm,     MqttQualityOfServiceLevel.ExactlyOnce)]
    [InlineData(TopicPolicy.Kind.Recipe,    MqttQualityOfServiceLevel.ExactlyOnce)]
    [InlineData(TopicPolicy.Kind.Control,   MqttQualityOfServiceLevel.ExactlyOnce)]
    [InlineData(TopicPolicy.Kind.Oracle,    MqttQualityOfServiceLevel.ExactlyOnce)]
    public void QoS_matches_spec(TopicPolicy.Kind kind, MqttQualityOfServiceLevel expected)
    {
        Assert.Equal(expected, TopicPolicy.Get(kind).Qos);
    }

    // §1.2.2 Retained 정책 — true: status, lot, alarm, recipe, oracle / false: heartbeat, result, control
    [Theory]
    [InlineData(TopicPolicy.Kind.Heartbeat, false)]
    [InlineData(TopicPolicy.Kind.Status,    true)]
    [InlineData(TopicPolicy.Kind.Result,    false)]
    [InlineData(TopicPolicy.Kind.Lot,       true)]
    [InlineData(TopicPolicy.Kind.Alarm,     true)]
    [InlineData(TopicPolicy.Kind.Recipe,    true)]
    [InlineData(TopicPolicy.Kind.Control,   false)]
    [InlineData(TopicPolicy.Kind.Oracle,    true)]
    public void Retained_matches_spec(TopicPolicy.Kind kind, bool expected)
    {
        Assert.Equal(expected, TopicPolicy.Get(kind).Retain);
    }

    // 토픽 형식: ds/{equipment_id}/{segment}
    [Theory]
    [InlineData(TopicPolicy.Kind.Heartbeat, "DS-VIS-001", "ds/DS-VIS-001/heartbeat")]
    [InlineData(TopicPolicy.Kind.Status,    "DS-VIS-002", "ds/DS-VIS-002/status")]
    [InlineData(TopicPolicy.Kind.Alarm,     "DS-VIS-004", "ds/DS-VIS-004/alarm")]
    [InlineData(TopicPolicy.Kind.Control,   "DS-VIS-001", "ds/DS-VIS-001/control")]
    public void Topic_format_is_correct(TopicPolicy.Kind kind, string eq, string expected)
    {
        Assert.Equal(expected, TopicPolicy.Topic(kind, eq));
    }
}
