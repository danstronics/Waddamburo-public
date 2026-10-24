using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Waddamburo.Formats.Nut;

namespace Waddamburo.Formats.Ddp;

/// <summary>One named NUT in a <c>NUT_PACK_TYPE1</c> archive.</summary>
public sealed record NdpEntry(int Index, string Name, uint RelativeOffset, uint Length, int TableOffset, long Offset);

/// <summary>
/// <c>NUT_PACK_TYPE1</c> (<c>nutdatapack.ndp</c>): the DDP preamble, a table of named NUTs
/// (<c>nameLength, name, [5 bytes after the first], offset, length</c>), the data length, then the NUTs.
/// </summary>
public sealed class NdpArchive
{
    private static ReadOnlySpan<byte> Magic => "NUT_PACK_TYPE1"u8;

    private NdpArchive(ImmutableArray<NdpEntry> entries, int lengthOffset, long dataOffset, uint dataLength)
    {
        Entries = entries; LengthOffset = lengthOffset; DataOffset = dataOffset; DataLength = dataLength;
    }

    public ImmutableArray<NdpEntry> Entries { get; }
    public int LengthOffset { get; }
    public long DataOffset { get; }
    public uint DataLength { get; }

    public static bool IsNdp(ReadOnlySpan<byte> data) => data.StartsWith(Magic);

    public static NdpArchive Parse(ReadOnlySpan<byte> data)
    {
        if (!IsNdp(data)) throw new InvalidDataException("Not a NUT_PACK_TYPE1 archive.");
        var skip = checked((int)u(data, 16));
        var position = checked(20 + skip + 20);
        var count = checked((int)u(data, position));
        if (count > 100_000) throw new InvalidDataException("NDP entry count is implausible.");
        position += 4 + 9;
        var raw = new List<(string Name, uint Offset, uint Length, int Table)>();
        for (var i = 0; i < count; i++)
        {
            var length = checked((int)u(data, position)); position += 4;
            if (length is < 1 or > 4096 || position + length > data.Length) throw new InvalidDataException("NDP name is invalid.");
            var name = Encoding.UTF8.GetString(data.Slice(position, length)).TrimEnd('\0');
            position += length;
            if (i == 0) position += 5;
            raw.Add((name, u(data, position), u(data, position + 4), position));
            position += 8;
        }
        var dataLength = u(data, position);
        var dataOffset = (long)position + 4;
        if (dataOffset + dataLength > data.Length) throw new InvalidDataException("NDP data extends beyond the file.");
        var entries = raw.Select((e, i) =>
        {
            if ((long)e.Offset + e.Length > dataLength) throw new InvalidDataException($"NDP entry {e.Name} is outside the data block.");
            return new NdpEntry(i, e.Name, e.Offset, e.Length, e.Table, dataOffset + e.Offset);
        }).ToImmutableArray();
        return new NdpArchive(entries, position, dataOffset, dataLength);
    }

    private static uint u(ReadOnlySpan<byte> data, int offset) => offset >= 0 && offset + 4 <= data.Length
        ? BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4)) : throw new InvalidDataException("NDP table is truncated.");

    public ReadOnlyMemory<byte> Read(ReadOnlyMemory<byte> data, int index) =>
        data.Slice(checked((int)Entries[index].Offset), checked((int)Entries[index].Length));

    /// <summary>Replaces NUT entries (any length), laying the data block out contiguously at 16-byte granularity.</summary>
    public static byte[] Rebuild(ReadOnlyMemory<byte> source, IReadOnlyDictionary<int, byte[]> replacements)
    {
        var archive = Parse(source.Span);
        if (replacements.Count == 0) return source.ToArray();
        var blocks = new byte[archive.Entries.Length][];
        for (var i = 0; i < blocks.Length; i++)
        {
            var replaced = replacements.TryGetValue(i, out var replacement);
            var data = replaced ? replacement! : archive.Read(source, i).ToArray();
            if (replaced) _ = NutFile.Parse(data);
            if (replaced && (data.Length & 15) != 0) { var padded = new byte[(data.Length + 15) & ~15]; data.CopyTo(padded, 0); data = padded; }
            blocks[i] = data;
        }
        var total = blocks.Sum(b => (long)b.Length);
        var end = archive.DataOffset + archive.DataLength;
        var output = new byte[checked(archive.DataOffset + total + (source.Length - end))];
        source.Span[..checked((int)archive.DataOffset)].CopyTo(output);
        long relative = 0;
        for (var i = 0; i < blocks.Length; i++)
        {
            blocks[i].CopyTo(output, checked((int)(archive.DataOffset + relative)));
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(archive.Entries[i].TableOffset), checked((uint)relative));
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(archive.Entries[i].TableOffset + 4), checked((uint)blocks[i].Length));
            relative += blocks[i].Length;
        }
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(archive.LengthOffset), checked((uint)total));
        source.Span[checked((int)end)..].CopyTo(output.AsSpan(checked((int)(archive.DataOffset + total))));
        var check = Parse(output);
        for (var i = 0; i < blocks.Length; i++)
            if (!replacements.ContainsKey(i) && !check.Read(output, i).Span.SequenceEqual(archive.Read(source, i).Span))
                throw new InvalidDataException($"NDP rebuild changed untouched entry {archive.Entries[i].Name}.");
        return output;
    }
}
