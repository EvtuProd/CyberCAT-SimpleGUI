using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using WolvenKit.Common.FNV1A;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.CR2W.JSON;
using WolvenKit.RED4.Save;
using WolvenKit.RED4.Save.Classes;
using WolvenKit.RED4.Save.IO;
using WolvenKit.RED4.Types;
using static WolvenKit.RED4.Types.Enums;
using EFileReadErrorCodes = WolvenKit.RED4.Save.IO.EFileReadErrorCodes;

namespace CyberCAT.Core;

public sealed class SaveDocument
{
    private readonly Dictionary<string, byte[]> _sidecars = new();
    private readonly Dictionary<NodeEntry, PreservingSaveWriter.Snapshot> _snapshots = new();
    private byte[]? _sourceHash;
    public CyberpunkSaveFile File { get; }
    public string SourcePath { get; private set; }
    public bool IsDirty { get; private set; }
    public event Action? Changed;

    public SaveDocument(CyberpunkSaveFile file, string sourcePath)
    {
        File = file;
        SourcePath = Path.GetFullPath(sourcePath);
    }

    public void MarkChanged()
    {
        IsDirty = true;
        Changed?.Invoke();
    }

    public static SaveDocument Load(string path)
    {
        GameData.Initialize();
        using var stream = System.IO.File.OpenRead(path);
        using var reader = new CyberpunkSaveReader(stream);
        var status = reader.ReadFile(out var save);
        if (status != EFileReadErrorCodes.NoError || save == null)
            throw new InvalidDataException(status == EFileReadErrorCodes.UnsupportedVersion
                ? "This save version is not supported by the bundled WolvenKit parser. Cyberpunk 2077 2.0 or newer is required."
                : "This file is not a readable Cyberpunk 2077 save. Select sav.dat inside a save folder.");
        var document = new SaveDocument(save, path);
        document._sourceHash = SHA256.HashData(System.IO.File.ReadAllBytes(path));
        document.SnapshotOriginalNodes(path);
        foreach (var name in new[] { "metadata.9.json", "screenshot.png" })
        {
            var sidecar = Path.Combine(Path.GetDirectoryName(document.SourcePath)!, name);
            if (System.IO.File.Exists(sidecar)) document._sidecars[name] = System.IO.File.ReadAllBytes(sidecar);
        }
        return document;
    }

    private void SnapshotOriginalNodes(string path)
    {
        using var input = System.IO.File.OpenRead(path);
        using var binary = new BinaryReader(input);
        using var raw = WolvenKit.RED4.Save.Compression.Decompress(binary, out _);
        foreach (var node in File.Nodes.Where(x => x.Children.Count == 0))
        {
            raw.Position = node.Offset + 4;
            var payload = new byte[checked(node.Size - 4 + node.TrailingSize)];
            raw.ReadExactly(payload);
            var warnings = new PackageWarnings();
            var trackedFields = new List<TrackedField>();
            if (node.Value.GetType() == typeof(Package))
            {
                using var packageStream = new MemoryStream(payload, false);
                using var reader = new BinaryReader(packageStream);
                var length = reader.ReadInt32();
                using var data = new MemoryStream(reader.ReadBytes(length), false);
                using var packageReader = new TrackingPackageReader(data) { LoggerService = warnings };
                packageReader.Settings.RedPackageType = node.Name == "ScriptableSystemsContainer" ? RedPackageType.ScriptableSystem : RedPackageType.InkLibResource;
                var buffer = new RedBuffer();
                if (packageReader.ReadBuffer(buffer) != WolvenKit.RED4.Archive.IO.EFileReadErrorCodes.NoError)
                    throw new InvalidDataException($"Cannot verify {node.Name}.");
                node.Value = new Package { Content = buffer.Data! };
                trackedFields = packageReader.Fields;
            }
            _snapshots[node] = new(payload, PreservingSaveWriter.SerializePayload(node), warnings.Messages.ToArray(), trackedFields);
        }
    }

    /// <returns>The backup path, if an existing save was replaced.</returns>
    public string? SaveTo(string path)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        string? backup = null;
        try
        {
            if (path == SourcePath && _sourceHash != null && System.IO.File.Exists(path)
                && !SHA256.HashData(System.IO.File.ReadAllBytes(path)).SequenceEqual(_sourceHash))
                throw new IOException("This save changed on disk since it was opened. Reopen it or save into a different slot.");
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                PreservingSaveWriter.Write(stream, File, _snapshots);
                stream.Flush(true);
            }
            // Re-read before touching the destination. A serialization failure must
            // leave the original save intact.
            using (var stream = System.IO.File.OpenRead(temporaryPath))
            using (var reader = new CyberpunkSaveReader(stream))
                if (reader.ReadFile(out _) != EFileReadErrorCodes.NoError)
                    throw new InvalidDataException("The written save failed verification. The destination was not changed.");

            // Copy auxiliary files before committing the save. Existing sidecars
            // belong to the chosen slot and must not be replaced implicitly.
            foreach (var (name, bytes) in _sidecars)
            {
                var target = Path.Combine(directory, name);
                if (!System.IO.File.Exists(target))
                {
                    var content = bytes;
                    if (name == "metadata.9.json" && JsonNode.Parse(bytes) is JsonObject metadata
                        && metadata["Data"]?["metadata"] is JsonObject data)
                    {
                        data["name"] = Path.GetFileName(directory);
                        data["fileSize"] = new FileInfo(temporaryPath).Length;
                        data["timestampString"] = DateTime.Now.ToString("HH:mm:ss, d.M.yyyy", System.Globalization.CultureInfo.InvariantCulture);
                        content = Encoding.UTF8.GetBytes(metadata.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    }
                    System.IO.File.WriteAllBytes(target, content);
                }
            }
            if (System.IO.File.Exists(path))
            {
                backup = path + $".{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bak";
                System.IO.File.Copy(path, backup, false);
            }
            System.IO.File.Move(temporaryPath, path, true);
            SourcePath = path;
            _sourceHash = SHA256.HashData(System.IO.File.ReadAllBytes(path));
            IsDirty = false;
            Changed?.Invoke();
            return backup;
        }
        finally
        {
            if (System.IO.File.Exists(temporaryPath)) System.IO.File.Delete(temporaryPath);
        }
    }

    public T? Node<T>(string name) where T : class => File.Nodes.FirstOrDefault(x => x.Name == name)?.Value as T;
    public Inventory? Inventory => Node<Inventory>("inventory");
    public RedPackage? Scriptable => (Node<Package>("ScriptableSystemsContainer")?.Content as RedPackage);
    public PlayerDevelopmentData? Player => Scriptable?.Chunks.OfType<PlayerDevelopmentSystem>()
        .FirstOrDefault()?.PlayerData.Select(x => x.Chunk).FirstOrDefault(x => x != null && x.OwnerID.Hash == 1);
    public gameStatsStateMapStructure? Stats => Node<Package>("StatsSystem")?.Content is RedPackage package
        ? package.RootChunk as gameStatsStateMapStructure : null;
    public gameSavedStatsData? PlayerStats
    {
        get
        {
            var stats = Stats;
            if (stats == null) return null;
            var key = stats.Keys.FirstOrDefault(x => x.IdType == gameStatIDType.EntityID && x.EntityHash == 1);
            var index = key == null ? -1 : stats.Keys.IndexOf(key);
            return index < 0 ? null : stats.Values[index];
        }
    }
    public vehicleGarageComponentPS? Garage => File.Nodes.Select(x => x.Value).OfType<PersistencySystem2>()
        .SelectMany(x => x.Entries).Select(x => x.Data).OfType<vehicleGarageComponentPS>().FirstOrDefault();
    public IEnumerable<FactsTable> FactTables => Descendants(File.Nodes).Select(x => x.Value).OfType<FactsTable>();
    public IEnumerable<FactsTable.FactEntry> Facts => FactTables.SelectMany(x => x.FactEntries);
    public gameuiCharacterCustomizationPresetWrapper? Appearance => Node<gameuiCharacterCustomizationPresetWrapper>("CharacetrCustomization_Appearances");
    public bool CanEditPlayerStats => PlayerStats != null || OptimizedStats.HasPlayer(Stats);
    public bool CanRewritePackage(string name) => !File.Nodes.Any(node => node.Name == name
        && _snapshots.TryGetValue(node, out var snapshot) && snapshot.Warnings.Length > 0);

    private static IEnumerable<NodeEntry> Descendants(IEnumerable<NodeEntry> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Descendants(node.Children)) yield return child;
        }
    }

    public void SetStat(gamedataStatType type, int value)
    {
        var stats = PlayerStats;
        if (stats == null)
        {
            if (Stats == null) throw new InvalidOperationException("Player stats are missing.");
            OptimizedStats.Set(Stats, type, value);
            return;
        }
        if (stats.ForcedModifiersBuffer?.Data is not ModifiersBuffer modifiers)
        {
            modifiers = new ModifiersBuffer();
            stats.ForcedModifiersBuffer = new DataBuffer { Data = modifiers };
        }
        var stat = modifiers.Entries.OfType<gameConstantStatModifierData_Deprecated>()
            .FirstOrDefault(x => x.StatType == type && x.ModifierType == gameStatModifierType.Additive);
        if (stat == null)
        {
            stat = new gameConstantStatModifierData_Deprecated { StatType = type, ModifierType = gameStatModifierType.Additive };
            modifiers.Entries.Add(stat);
        }
        stat.Value = value;
    }

    public void SetFact(string nameOrHash, uint value)
    {
        if (string.IsNullOrWhiteSpace(nameOrHash)) throw new ArgumentException("Enter a fact name or numeric hash.");
        var hash = uint.TryParse(nameOrHash, out var numericHash) ? numericHash : FNV1A32HashAlgorithm.HashString(nameOrHash);
        var existing = Facts.FirstOrDefault(x => (uint)x.FactName == hash);
        if (existing != null) existing.Value = value;
        else
        {
            var table = FactTables.FirstOrDefault() ?? throw new InvalidOperationException("No facts database was found in this save.");
            table.FactEntries.Add(new FactsTable.FactEntry { FactName = hash, Value = value });
        }
        MarkChanged();
    }

    public string ExportAppearance() => RedJsonSerializer.Serialize(new AppearancePreset { Data = Appearance
        ?? throw new InvalidOperationException("This save has no appearance preset.") });

    public void ImportAppearance(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Invalid preset JSON.");
        gameuiCharacterCustomizationPresetWrapper? preset;
        if (root["JsonVersion"] is { } version)
            preset = RedJsonSerializer.Deserialize<AppearancePreset>(json, new RedJsonSerializerOptions { JsonVersion = version.GetValue<string>() })?.Data;
        else
            preset = RedJsonSerializer.Deserialize<gameuiCharacterCustomizationPresetWrapper>(json, new RedJsonSerializerOptions { JsonVersion = "0.0.6" });
        if (preset?.Preset == null) throw new InvalidDataException("The file does not contain an appearance preset.");
        var current = Appearance ?? throw new InvalidOperationException("This save has no appearance preset.");
        if ((bool)preset.Preset.IsMale != (bool)current.Preset.IsMale)
            throw new InvalidOperationException("Choose a preset with the same body type as this character. Changing body type requires additional game data changes.");
        File.Nodes.First(x => x.Name == "CharacetrCustomization_Appearances").Value = preset;
        MarkChanged();
    }
}

public sealed class AppearancePreset
{
    public string JsonVersion { get; set; } = "0.0.9";
    public gameuiCharacterCustomizationPresetWrapper? Data { get; set; }
}
