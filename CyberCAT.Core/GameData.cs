using System.Text.Json;
using CP2077SaveEditor.ModSupport;
using WolvenKit.Core.Compression;
using WolvenKit.RED4.Types;

namespace CyberCAT.Core;

public static class GameData
{
    private static readonly Lazy<bool> Initialization = new(InitializeCore);
    public static readonly Dictionary<uint, string> Facts = ReadJson<Dictionary<uint, string>>("Facts.json");
    public static readonly Dictionary<ulong, ItemRecord> Items = ReadJson<Dictionary<ulong, ItemRecord>>("ItemClasses.json");
    public static readonly List<string> Vehicles = ReadJson<List<string>>("Vehicles.json");
    public static readonly List<FastTravelRecord> FastTravel = ReadJson<List<FastTravelRecord>>("FastTravel.json");

    public static void Initialize() => _ = Initialization.Value;

    private static bool InitializeCore()
    {
        CompressionSettings.Get().UseOodle = false;
        RedReflection.AddRedType(typeof(gameSavedStatsDataOptimized));
        RedReflection.AddRedType(typeof(gameBufferSlice));
        ModManager.LoadTypes();
        _ = new WolvenKit.Common.Services.HashService();
        return true;
    }

    private static Stream Resource(string name) => typeof(GameData).Assembly.GetManifestResourceStream("GameData." + name)
        ?? throw new InvalidDataException($"Missing bundled resource: {name}");

    private static T ReadJson<T>(string name)
    {
        using var stream = Resource(name);
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException(name);
    }

}

public sealed class ItemRecord
{
    public string Type { get; set; } = "Unknown";
    public bool IsSingleInstance { get; set; }
}

public sealed class FastTravelRecord
{
    public string PointRecord { get; set; } = "";
    public string MarkerRef { get; set; } = "";
}
