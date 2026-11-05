using System.Text;
using WolvenKit.Core.Extensions;
using WolvenKit.RED4.Save;
using WolvenKit.RED4.Save.IO;

namespace CyberCAT.Core;

// Keep untouched leaf nodes byte-for-byte, including package fields the pinned
// parser does not understand. Changed packages with skipped fields are refused.
internal static class PreservingSaveWriter
{
    internal sealed record Snapshot(byte[] OriginalPayload, byte[] ParsedPayload, string[] Warnings, List<TrackedField> Fields);

    public static byte[] SerializePayload(NodeEntry node)
    {
        using var stream = new MemoryStream();
        using var writer = new NodeWriter(stream);
        ParserHelper.GetParser(node.Name).Write(writer, node);
        return stream.ToArray();
    }

    public static void Write(Stream output, CyberpunkSaveFile file, Dictionary<NodeEntry, Snapshot> snapshots)
    {
        byte[] nodeBytes;
        List<NodeInfo> infos;
        using (var stream = new MemoryStream())
        {
            using var nodes = new NodeWriter(stream);
            foreach (var node in file.Nodes)
            {
                if (snapshots.TryGetValue(node, out var snapshot))
                {
                    var current = SerializePayload(node);
                    if (current.SequenceEqual(snapshot.ParsedPayload))
                    {
                        WriteRaw(nodes, node, snapshot.OriginalPayload);
                        continue;
                    }
                    if (TryPatchScalars(node, snapshot, out var patched))
                    {
                        WriteRaw(nodes, node, patched);
                        continue;
                    }
                    if (snapshot.Warnings.Length > 0)
                        throw new InvalidDataException($"Cannot safely rewrite {node.Name}: {snapshot.Warnings[0]} Other sections can still be edited.");
                }
                nodes.Write(node);
            }
            infos = nodes.GetFinalizedInfos();
            nodeBytes = stream.ToArray();
        }
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(CyberpunkSaveFile.MAGIC);
        file.FileHeader.Write(writer);
        var dataOffset = checked((int)output.Position + 8 + file.CompressionSettings.TableEntriesCount * 12);
        foreach (var info in infos) info.Offset += dataOffset;
        Compression.Write(writer, nodeBytes, file.CompressionSettings, true);
        var footerOffset = checked((int)output.Position);
        writer.Write(CyberpunkSaveFile.NODE);
        writer.WriteVLQInt32(infos.Count);
        foreach (var info in infos)
        {
            writer.WriteLengthPrefixedString(info.Name);
            writer.Write(info.NextId);
            writer.Write(info.ChildId);
            writer.Write(info.Offset);
            writer.Write(info.Size);
        }
        writer.Write(footerOffset);
        writer.Write(CyberpunkSaveFile.DONE);
    }

    private static bool TryPatchScalars(NodeEntry node, Snapshot snapshot, out byte[] patched)
    {
        patched = snapshot.OriginalPayload.ToArray();
        var changes = snapshot.Fields.Select(field => (Field: field, Value: field.Owner.GetProperty(field.Name)!, Bytes: field.CurrentBytes))
            .Where(x => x.Bytes != null && !x.Bytes.SequenceEqual(x.Field.OriginalBytes)).ToList();
        if (changes.Count == 0 || changes.Any(x => x.Bytes!.Length != x.Field.OriginalBytes.Length)) return false;
        try
        {
            foreach (var change in changes) change.Field.RestoreOriginal();
            if (!SerializePayload(node).SequenceEqual(snapshot.ParsedPayload)) return false;
        }
        finally
        {
            foreach (var change in changes) change.Field.RestoreCurrent(change.Value, change.Bytes!);
        }
        foreach (var change in changes) change.Bytes!.CopyTo(patched, change.Field.Offset);
        return true;
    }

    private static void WriteRaw(NodeWriter writer, NodeEntry node, byte[] bytes)
    {
        var originalFlag = node.WritesOwnTrailingSize;
        node.WritesOwnTrailingSize = true;
        try { writer.Write(node, new RawParser(bytes)); }
        finally { node.WritesOwnTrailingSize = originalFlag; }
    }

    private sealed class RawParser(byte[] bytes) : INodeParser
    {
        public void Read(BinaryReader reader, NodeEntry node) => throw new NotSupportedException();
        public void Write(NodeWriter writer, NodeEntry node) => writer.Write(bytes);
    }
}
