using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapretHub.Core;

public sealed class LocalState(string directory)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public HubSettings LoadSettings()
    {
        var settings = Read("settings.json", () => new HubSettings());
        settings.Validate();
        return settings;
    }

    public void SaveSettings(HubSettings settings)
    {
        settings.Validate();
        Write("settings.json", settings);
    }

    public StrategyHistory LoadHistory() => Read("history.json", () => new StrategyHistory());
    public void SaveHistory(StrategyHistory history) => Write("history.json", history);
    public VpnSettings? LoadFallback() => Read<VpnSettings?>("vpn-fallback.json", () => null);
    public void SaveFallback(VpnSettings settings) => Write("vpn-fallback.json", settings);
    public void ClearFallback()
    {
        if (Directory.Exists(directory))
            File.Delete(Path.Combine(directory, "vpn-fallback.json"));
    }

    private T Read<T>(string name, Func<T> defaults)
    {
        string path = Path.Combine(directory, name);
        if (!File.Exists(path))
            return defaults();
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Json) ?? throw new InvalidDataException($"Файл {name} пуст.");
    }

    private void Write<T>(string name, T value)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        string temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}

public sealed class StrategyHistory
{
    public StrategyId? LastWorking { get; set; }
    public Dictionary<string, List<StrategyId>> Networks { get; init; } = new(StringComparer.Ordinal);

    public IEnumerable<StrategyId> Preferred(string network)
    {
        if (Networks.TryGetValue(network, out var strategies))
            foreach (var strategy in strategies)
                yield return strategy;
        if (LastWorking is not null)
            yield return LastWorking;
    }

    public void Remember(string network, StrategyId strategy)
    {
        LastWorking = strategy;
        if (!Networks.TryGetValue(network, out var strategies))
        {
            if (Networks.Count >= 32)
                Networks.Remove(Networks.Keys.First());
            Networks[network] = strategies = [];
        }
        strategies.Remove(strategy);
        strategies.Insert(0, strategy);
        if (strategies.Count > 5)
            strategies.RemoveAt(strategies.Count - 1);
    }
}
