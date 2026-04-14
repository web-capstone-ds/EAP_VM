using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DsEap.Events.Models;

// 8종 이벤트 공통 JSON 직렬화 옵션
// - PropertyNamingPolicy = SnakeCaseLower (snake_case 기본)
// - inspection_detail 내부는 [JsonPropertyName] 로 PascalCase 고정 (AxisResult)
// - null 필드 제외, 압축 출력
// - Mock JSON의 _source/_note/_metadata 등 _ prefix 필드는 DTO에 대응 멤버가 없어 자연스럽게 무시됨
public static class EventJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy  = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return opts;
    }

    // ISO 8601 UTC 밀리초 (.fffZ) — API 명세서 §1 공통 헤더
    public static string NowIsoUtc() =>
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public static string ToIsoUtc(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public static byte[] SerializeToUtf8<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<T>(utf8, Options);

    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options);
}
