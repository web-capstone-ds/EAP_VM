using System.Text.Json;
using DsEap.Events.Models;
using Microsoft.Extensions.Logging;

namespace DsEap.MockData;

// ../../DS-Document/EAP_mock_data/*.json 로딩 + DTO 파싱
public sealed class MockDataLoader
{
    private readonly ILogger<MockDataLoader> _log;
    private readonly Dictionary<string, string> _rawByFileStem = new(StringComparer.OrdinalIgnoreCase);

    public string MockDir { get; }

    public MockDataLoader(string mockDir, ILogger<MockDataLoader> log)
    {
        _log = log;
        MockDir = ResolveMockDir(mockDir);
        LoadAll();
    }

    private void LoadAll()
    {
        if (!Directory.Exists(MockDir))
        {
            _log.LogError("Mock directory not found: {Dir}", MockDir);
            return;
        }
        foreach (var path in Directory.EnumerateFiles(MockDir, "*.json"))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            _rawByFileStem[stem] = File.ReadAllText(path);
        }
        _log.LogInformation("Loaded {Count} mock JSON files from {Dir}", _rawByFileStem.Count, MockDir);
    }

    public string GetRaw(string fileStem) =>
        _rawByFileStem.TryGetValue(fileStem, out var raw)
            ? raw
            : throw new FileNotFoundException($"Mock '{fileStem}.json' not loaded");

    public T Get<T>(string fileStem) where T : class =>
        JsonSerializer.Deserialize<T>(GetRaw(fileStem), EventJson.Options)
            ?? throw new InvalidOperationException($"Failed to deserialize mock '{fileStem}' as {typeof(T).Name}");

    // 설정 경로(상대) → 실제 디렉토리. tests와 동일한 상향 탐색 전략으로 fallback.
    private static string ResolveMockDir(string configPath)
    {
        if (Path.IsPathRooted(configPath) && Directory.Exists(configPath))
            return configPath;

        var baseDir = AppContext.BaseDirectory;
        var combined = Path.GetFullPath(Path.Combine(baseDir, configPath));
        if (Directory.Exists(combined)) return combined;

        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            foreach (var name in new[] { "DS-Document", "ds-document" })
            {
                var c = Path.Combine(dir.FullName, name, "EAP_mock_data");
                if (Directory.Exists(c)) return c;
            }
        }
        return combined; // 존재하지 않더라도 경로 자체는 반환 → LoadAll에서 경고 로그
    }
}
