using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;
using System.Buffers.Binary;

namespace CyberCAT.Core;

internal sealed class TrackingPackageReader(Stream input) : RedPackageReader(input)
{
    public List<TrackedField> Fields { get; } = new();
    public PackageLayout Layout { get; } = new();

    public override EFileReadErrorCodes ReadBuffer(RedBuffer buffer)
    {
        var start = BaseStream.Position;
        using (var reader = new BinaryReader(BaseStream, System.Text.Encoding.UTF8, true))
        {
            reader.ReadByte(); reader.ReadByte();
            var sections = reader.ReadUInt16();
            reader.ReadUInt32();
            if (sections == 7) { reader.ReadUInt32(); reader.ReadUInt32(); }
            reader.ReadUInt32(); reader.ReadUInt32();
            var descriptors = reader.ReadUInt32();
            var data = reader.ReadUInt32();
            if (Settings.RedPackageType == WolvenKit.RED4.Archive.Buffer.RedPackageType.ScriptableSystem)
            {
                var count = reader.ReadUInt32();
                BaseStream.Position += count * 8;
            }
            Layout.Base = checked((int)BaseStream.Position + 4);
            BaseStream.Position += descriptors;
            for (var i = 0; i < (data - descriptors) / 8; i++)
            {
                reader.ReadUInt32();
                var offsetPosition = checked((int)BaseStream.Position + 4);
                Layout.Chunks.Add((offsetPosition, checked(Layout.Base + (int)reader.ReadUInt32())));
            }
        }
        BaseStream.Position = start;
        var result = base.ReadBuffer(buffer);
        Layout.UnspentName = _namesList.FindIndex(x => x.GetResolvedText() == "unspent");
        Layout.Int32Name = _namesList.FindIndex(x => x.GetResolvedText() == "Int32");
        return result;
    }

    public override void ReadClass(RedBaseClass instance, uint size)
    {
        var start = BaseStream.Position;
        var descriptors = new List<(string Name, string Type, int Offset, int Descriptor)>();
        using (var reader = new BinaryReader(BaseStream, System.Text.Encoding.UTF8, true))
        {
            var count = reader.ReadUInt16();
            for (var i = 0; i < count; i++)
            {
                var name = GetStringValue(reader.ReadUInt16()).GetResolvedText()!;
                var type = GetStringValue(reader.ReadUInt16()).GetResolvedText()!;
                var descriptor = checked((int)BaseStream.Position + 4);
                descriptors.Add((name, type, checked((int)(start + reader.ReadUInt32())), descriptor));
            }
        }
        BaseStream.Position = start;
        base.ReadClass(instance, size);
        var layout = new ClassLayout(instance, checked((int)start + 4), checked((int)BaseStream.Position + 4),
            descriptors.Select(x => (x.Descriptor, x.Offset + 4)).ToList());
        Layout.Classes.Add(layout);
        if (instance is SDevelopmentPoints && descriptors.All(x => x.Name != "unspent"))
            Layout.MissingPoints.Add((layout, instance.GetProperty("unspent")!));
        foreach (var (name, type, offset, _) in descriptors)
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

internal sealed record ClassLayout(RedBaseClass Owner, int Start, int End, List<(int Descriptor, int Value)> Fields);

internal sealed class PackageLayout
{
    public int Base;
    public int UnspentName = -1;
    public int Int32Name = -1;
    public List<(int Descriptor, int Value)> Chunks { get; } = new();
    public List<ClassLayout> Classes { get; } = new();
    public List<(ClassLayout Class, IRedType Original)> MissingPoints { get; } = new();

    public byte[] InsertPointFields(byte[] original, List<(ClassLayout Class, byte[] Bytes)> additions, List<(TrackedField Field, byte[] Bytes)> changes)
    {
        if (UnspentName < 0 || Int32Name < 0) throw new InvalidDataException("The save has no name entry for development points yet. Earn or spend a point in-game and save first.");
        var inserts = new List<(int Position, byte[] Bytes)>();
        foreach (var (cls, bytes) in additions)
        {
            inserts.Add((cls.Start + 2 + cls.Fields.Count * 8, new byte[8]));
            inserts.Add((cls.End, bytes));
        }
        inserts = inserts.OrderBy(x => x.Position).ToList();
        int Move(int position) => position + inserts.Where(x => x.Position <= position).Sum(x => x.Bytes.Length);
        int InsertPosition(int position) => position + inserts.Where(x => x.Position < position).Sum(x => x.Bytes.Length);
        using var stream = new MemoryStream();
        var cursor = 0;
        foreach (var (position, bytes) in inserts)
        {
            stream.Write(original, cursor, position - cursor);
            stream.Write(bytes);
            cursor = position;
        }
        stream.Write(original, cursor, original.Length - cursor);
        var output = stream.ToArray();
        foreach (var cls in Classes)
            foreach (var (descriptor, value) in cls.Fields)
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(Move(descriptor), 4), checked((uint)(Move(value) - Move(cls.Start))));
        foreach (var (descriptor, value) in Chunks)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(Move(descriptor), 4), checked((uint)(Move(value) - Base)));
        foreach (var (cls, _) in additions)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(Move(cls.Start), 2), checked((ushort)(cls.Fields.Count + 1)));
            var descriptor = InsertPosition(cls.Start + 2 + cls.Fields.Count * 8);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(descriptor, 2), checked((ushort)UnspentName));
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(descriptor + 2, 2), checked((ushort)Int32Name));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(descriptor + 4, 4), checked((uint)(InsertPosition(cls.End) - Move(cls.Start))));
        }
        foreach (var (field, bytes) in changes) bytes.CopyTo(output, Move(field.Offset));
        BinaryPrimitives.WriteInt32LittleEndian(output, checked(BinaryPrimitives.ReadInt32LittleEndian(original) + output.Length - original.Length));
        return output;
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
