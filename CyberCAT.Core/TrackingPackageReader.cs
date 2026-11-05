using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

namespace CyberCAT.Core;

internal sealed class TrackingPackageReader(Stream input) : RedPackageReader(input)
{
    public List<TrackedField> Fields { get; } = new();

    public override void ReadClass(RedBaseClass instance, uint size)
    {
        var start = BaseStream.Position;
        var descriptors = new List<(string Name, string Type, int Offset)>();
        using (var reader = new BinaryReader(BaseStream, System.Text.Encoding.UTF8, true))
        {
            var count = reader.ReadUInt16();
            for (var i = 0; i < count; i++)
            {
                var name = GetStringValue(reader.ReadUInt16()).GetResolvedText()!;
                var type = GetStringValue(reader.ReadUInt16()).GetResolvedText()!;
                descriptors.Add((name, type, checked((int)(start + reader.ReadUInt32()))));
            }
        }
        BaseStream.Position = start;
        base.ReadClass(instance, size);
        foreach (var (name, type, offset) in descriptors)
        {
            if (type is not ("Int32" or "Uint32" or "Float" or "Bool" or "DataBuffer")) continue;
            if (!instance.HasProperty(name) || instance.GetProperty(name) is not { } value) continue;
            if (value is DataBuffer { Data: not null }) continue;
            var bytes = TrackedField.Encode(value);
            if (bytes == null) continue;
            var position = BaseStream.Position;
            BaseStream.Position = offset;
            var original = new byte[bytes.Length];
            BaseStream.ReadExactly(original);
            BaseStream.Position = position;
            // Do not patch a field unless its exact original encoding is known.
            if (original.SequenceEqual(bytes)) Fields.Add(new(instance, name, offset + 4, value, bytes));
        }
    }
}

internal sealed record TrackedField(RedBaseClass Owner, string Name, int Offset, IRedType OriginalValue, byte[] OriginalBytes)
{
    public byte[]? CurrentBytes => Encode(Owner.GetProperty(Name));
    public static byte[]? Encode(IRedType? value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        switch (value)
        {
            case CInt32 number: writer.Write((int)number); break;
            case CUInt32 number: writer.Write((uint)number); break;
            case CFloat number: writer.Write((float)number); break;
            case CBool boolean: writer.Write((bool)boolean); break;
            case DataBuffer buffer:
                var bytes = buffer.Buffer.GetBytes();
                writer.Write(bytes.Length);
                writer.Write(bytes);
                break;
            default: return null;
        }
        return stream.ToArray();
    }

    public void RestoreOriginal()
    {
        if (OriginalValue is DataBuffer buffer) buffer.Buffer.SetBytes(OriginalBytes[4..]);
        else Owner.SetProperty(Name, OriginalValue);
    }

    public void RestoreCurrent(IRedType value, byte[] bytes)
    {
        if (value is DataBuffer buffer) buffer.Buffer.SetBytes(bytes[4..]);
        else Owner.SetProperty(Name, value);
    }
}
