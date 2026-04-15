using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

// E4 검증용 CONTROL_CMD 송신 CLI
//
// 사용법:
//   DsEapControlCli <command> <equipment_id> [--burst-id <id>] [--issuer <name>] [--reason <text>]
//
// command: emergency-stop | status-query | alarm-ack | alarm-clear | recipe-load | lot-abort
// 예시:
//   DsEapControlCli emergency-stop DS-VIS-001
//   DsEapControlCli status-query   DS-VIS-002
//   DsEapControlCli alarm-ack      DS-VIS-004
//   DsEapControlCli alarm-ack      DS-VIS-004 --burst-id BURST-001

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: DsEapControlCli <command> <equipment_id> [--burst-id <id>] [--issuer <name>] [--reason <text>]");
    Console.Error.WriteLine("       DsEapControlCli watch [<topic_filter>] [--seconds <N>]");
    return 2;
}

if (args[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
{
    var topicFilter = args.Length >= 2 && !args[1].StartsWith("--") ? args[1] : "ds/+/alarm";
    var seconds = 30;
    for (int i = 1; i < args.Length - 1; i++)
        if (args[i] == "--seconds") seconds = int.Parse(args[i + 1]);

    var hostW = Environment.GetEnvironmentVariable("EAP_BROKER_HOST") ?? "localhost";
    var portW = int.TryParse(Environment.GetEnvironmentVariable("EAP_BROKER_PORT"), out var pw) ? pw : 1883;
    var userW = Environment.GetEnvironmentVariable("EAP_BROKER_USER") ?? "mes_server";
    var passW = Environment.GetEnvironmentVariable("EAP_BROKER_PASS") ?? "mes_admin_99";

    var watcher = new MqttFactory().CreateMqttClient();
    watcher.ApplicationMessageReceivedAsync += e =>
    {
        var t = e.ApplicationMessage.Topic;
        var len = e.ApplicationMessage.PayloadSegment.Count;
        var retain = e.ApplicationMessage.Retain;
        var body = len == 0 ? "<empty/clear>" : Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
        Console.WriteLine($"[watch {DateTime.Now:HH:mm:ss.fff}] {t} retain={retain} bytes={len} body={body}");
        return Task.CompletedTask;
    };

    var optsW = new MqttClientOptionsBuilder()
        .WithProtocolVersion(MqttProtocolVersion.V500)
        .WithTcpServer(hostW, portW)
        .WithClientId($"ds-eap-watcher-{Guid.NewGuid():N}")
        .WithCleanStart(true)
        .WithCredentials(userW, passW)
        .Build();

    await watcher.ConnectAsync(optsW);
    Console.WriteLine($"[watch] connected to {hostW}:{portW}, subscribing {topicFilter} for {seconds}s");
    var subOpts = new MqttClientSubscribeOptionsBuilder()
        .WithTopicFilter(f => f.WithTopic(topicFilter).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce))
        .Build();
    await watcher.SubscribeAsync(subOpts);

    await Task.Delay(TimeSpan.FromSeconds(seconds));
    await watcher.DisconnectAsync();
    return 0;
}

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: DsEapControlCli <command> <equipment_id> [--burst-id <id>] [--issuer <name>] [--reason <text>]");
    return 2;
}

var command = args[0].ToUpperInvariant() switch
{
    "EMERGENCY-STOP" => "EMERGENCY_STOP",
    "STATUS-QUERY"   => "STATUS_QUERY",
    "ALARM-ACK"      => "ALARM_ACK",
    "ALARM-CLEAR"    => "ALARM_CLEAR",
    "RECIPE-LOAD"    => "RECIPE_LOAD",
    "LOT-ABORT"      => "LOT_ABORT",
    _ => args[0].ToUpperInvariant(),
};

var equipmentId = args[1];
string? burstId = null;
var issuer = "MOBILE-APP-TEST";
var reason = "E4 검증용 송신";

for (int i = 2; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--burst-id": burstId = args[i + 1]; i++; break;
        case "--issuer":   issuer  = args[i + 1]; i++; break;
        case "--reason":   reason  = args[i + 1]; i++; break;
    }
}

var host = Environment.GetEnvironmentVariable("EAP_BROKER_HOST") ?? "localhost";
var port = int.TryParse(Environment.GetEnvironmentVariable("EAP_BROKER_PORT"), out var p) ? p : 1883;
var user = Environment.GetEnvironmentVariable("EAP_BROKER_USER") ?? "mes_server";
var pass = Environment.GetEnvironmentVariable("EAP_BROKER_PASS") ?? "mes_admin_99";

var payload = new Dictionary<string, object?>
{
    ["message_id"]  = Guid.NewGuid().ToString(),
    ["event_type"]  = "CONTROL_CMD",
    ["timestamp"]   = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
    ["equipment_id"] = equipmentId,
    ["command"]     = command,
    ["issued_by"]   = issuer,
    ["reason"]      = reason,
};
if (!string.IsNullOrEmpty(burstId))
    payload["target_burst_id"] = burstId;

var json = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions
{
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
});

var topic = $"ds/{equipmentId}/control";

var client = new MqttFactory().CreateMqttClient();
var opts = new MqttClientOptionsBuilder()
    .WithProtocolVersion(MqttProtocolVersion.V500)
    .WithTcpServer(host, port)
    .WithClientId($"ds-eap-control-cli-{Guid.NewGuid():N}")
    .WithCleanStart(true)
    .WithCredentials(user, pass)
    .Build();

await client.ConnectAsync(opts);
Console.WriteLine($"[control-cli] connected to {host}:{port}");

var msg = new MqttApplicationMessageBuilder()
    .WithTopic(topic)
    .WithPayload(json)
    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce)
    .WithRetainFlag(false)
    .WithContentType("application/json")
    .Build();

await client.PublishAsync(msg);
Console.WriteLine($"[control-cli] PUB {topic} cmd={command} bytes={json.Length}");
Console.WriteLine($"[control-cli] payload: {Encoding.UTF8.GetString(json)}");

await client.DisconnectAsync();
return 0;
