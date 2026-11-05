using System.Buffers.Binary;
using WolvenKit.Core.Extensions;
using WolvenKit.RED4.Save;
using WolvenKit.RED4.Types;
using static WolvenKit.RED4.Types.Enums;

namespace CyberCAT.Core;

// These RED schemas were added to WolvenKit after the editor's pinned commit.
// Registering them also prevents the older reader from skipping their arrays.
public sealed class gameSavedStatsDataOptimized : RedBaseClass
{
    [RED("statModifiers")]
    public CArray<CHandle<gameStatModifierData_Deprecated>> StatModifiers { get => GetPropertyValue<CArray<CHandle<gameStatModifierData_Deprecated>>>(); set => SetPropertyValue(value); }
    [RED("bufferSliceIndices")]
    public CArray<CUInt16> BufferSliceIndices { get => GetPropertyValue<CArray<CUInt16>>(); set => SetPropertyValue(value); }
    [RED("inactiveStatsIndices")]
    public CArray<CUInt16> InactiveStatsIndices { get => GetPropertyValue<CArray<CUInt16>>(); set => SetPropertyValue(value); }
    [RED("entityHash")]
    public CUInt64 EntityHash { get => GetPropertyValue<CUInt64>(); set => SetPropertyValue(value); }
    [RED("seed")]
    public CUInt32 Seed { get => GetPropertyValue<CUInt32>(); set => SetPropertyValue(value); }
    [RED("recordIDIndex")]
    public CUInt16 RecordIDIndex { get => GetPropertyValue<CUInt16>(); set => SetPropertyValue(value); }
    [RED("idType")]
    public CEnum<gameStatIDType> IdType { get => GetPropertyValue<CEnum<gameStatIDType>>(); set => SetPropertyValue(value); }

    public gameSavedStatsDataOptimized()
    {
        StatModifiers = new();
        BufferSliceIndices = new();
        InactiveStatsIndices = new();
    }
}

public sealed class gameBufferSlice : RedBaseClass
{
    [RED("offset")]
    public CUInt32 Offset { get => GetPropertyValue<CUInt32>(); set => SetPropertyValue(value); }
    [RED("combined")]
    public CUInt32 Combined { get => GetPropertyValue<CUInt32>(); set => SetPropertyValue(value); }
}

public static class OptimizedStats
{
    public static CArray<gameSavedStatsDataOptimized>? Entries(gameStatsStateMapStructure? stats) => stats?.HasProperty("valuesOptimized") == true
        ? stats.GetProperty("valuesOptimized") as CArray<gameSavedStatsDataOptimized> : null;

    public static bool HasPlayer(gameStatsStateMapStructure? stats) => Entries(stats)?.Any(x => x.EntityHash == 1 && x.IdType == gameStatIDType.EntityID) == true;

    public static float Get(gameStatsStateMapStructure stats, gamedataStatType type)
    {
        var (buffer, offset) = Locate(stats, type);
        return BinaryPrimitives.ReadSingleLittleEndian(buffer.Buffer.GetBytes().AsSpan(offset, 4));
    }

    public static void Set(gameStatsStateMapStructure stats, gamedataStatType type, float value)
    {
        var (buffer, offset) = Locate(stats, type);
        var bytes = buffer.Buffer.GetBytes().ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, 4), value);
        buffer.Buffer.SetBytes(bytes);
    }

    private static (DataBuffer Buffer, int Offset) Locate(gameStatsStateMapStructure stats, gamedataStatType type)
    {
        var entries = Entries(stats) ?? throw new InvalidDataException("Optimized stats are missing.");
        var players = entries.Where(x => x.EntityHash == 1 && x.IdType == gameStatIDType.EntityID).ToList();
        if (players.Count != 1) throw new InvalidDataException("A unique player stats entry was not found.");
        if (!stats.HasProperty("sharedBufferSlices") || stats.GetProperty("sharedBufferSlices") is not CArray<gameBufferSlice> slices
            || !stats.HasProperty("sharedDataBuffer") || stats.GetProperty("sharedDataBuffer") is not DataBuffer buffer)
            throw new InvalidDataException("Optimized stats buffers are missing.");
        var bytes = buffer.Buffer.GetBytes();
        var found = new List<int>();
        var statHash = SaveHashHelper.GetStatHash(type);
        foreach (var indexValue in players[0].BufferSliceIndices)
        {
            var index = (ushort)indexValue;
            if (index >= slices.Count) throw new InvalidDataException("Invalid stats slice index.");
            var slice = slices[index];
            var combined = (uint)slice.Combined;
            if (combined >> 30 != 1) continue; // Only forced modifiers.
            if (entries.Count(x => x.BufferSliceIndices.Contains(indexValue)) != 1)
                throw new InvalidOperationException("This player's forced stat buffer is shared with another entity and cannot be edited safely.");
            var start = checked((int)(uint)slice.Offset);
            var end = checked(start + (int)(combined & 0x3fffffff));
            if (start < 0 || end > bytes.Length) throw new InvalidDataException("Stats slice is outside its buffer.");
            using var stream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(stream);
            stream.Position = start;
            while (stream.Position < end)
            {
                var name = reader.ReadLengthPrefixedString();
                var hash = reader.ReadUInt64();
                var modifierType = reader.ReadUInt32();
                switch (name)
                {
                    case "gameConstantStatModifier":
                        if (hash == statHash && modifierType == (uint)gameStatModifierType.Additive) found.Add(checked((int)stream.Position));
                        reader.ReadSingle();
                        break;
                    case "gameCombinedStatModifier":
                        stream.Position += 20;
                        break;
                    case "gameCurveStatModifier":
                        reader.ReadLengthPrefixedString();
                        reader.ReadLengthPrefixedString();
                        stream.Position += 12;
                        break;
                    default:
                        throw new InvalidDataException($"Unsupported stat modifier: {name}");
                }
            }
            if (stream.Position != end) throw new InvalidDataException("Stats slice length does not match its contents.");
        }
        if (found.Count != 1) throw new InvalidOperationException($"A unique forced modifier for {type} was not found. No change was made.");
        return (buffer, found[0]);
    }
}
