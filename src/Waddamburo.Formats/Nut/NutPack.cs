using System.Buffers.Binary;

namespace Waddamburo.Formats.Nut;

/// <summary>
/// Multi-texture NTP3 packs (v1 sequential header+payload, v2 header table then payloads): extracts one
/// texture as a standalone single-texture v1 NUT for editing, and splices edited textures back.
/// </summary>
public static class NutPack
{
    /// <summary>Texture <paramref name="index"/> as a single-texture NTP3 v1 file (the Green DDP layout).</summary>
    public static byte[] Extract(ReadOnlyMemory<byte> pack, int index)
    {
        var file = NutFile.Parse(pack);
        if ((uint)index >= (uint)file.Textures.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var texture = file.Textures[index];
        if (file.Version == 1 && file.Textures.Length == 1) return pack.ToArray();
        var output = new byte[0x10 + texture.HeaderSize + texture.DataSize];
        "NTP3"u8.CopyTo(output);
        output[4] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(6), 1);
        texture.HeaderBytes.AsSpan().CopyTo(output.AsSpan(0x10));
        var header = output.AsSpan(0x10, texture.HeaderSize);
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(texture.HeaderSize + texture.DataSize));
        // v2 stores a header-relative payload offset at +0x20; v1 places the payload after the header.
        BinaryPrimitives.WriteUInt32BigEndian(header[0x20..], 0);
        texture.Data.Span.CopyTo(output.AsSpan(0x10 + texture.HeaderSize));
        _ = NutFile.Parse(output);
        return output;
    }

    /// <summary>Replaces textures (given as single-texture NUTs) and re-lays the pack in its own version's layout.</summary>
    public static byte[] Rebuild(ReadOnlyMemory<byte> pack, IReadOnlyDictionary<int, byte[]> replacements)
    {
        var file = NutFile.Parse(pack);
        if (replacements.Count == 0) return pack.ToArray();
        if (file.Version == 1 && file.Textures.Length == 1 && replacements.TryGetValue(0, out var only)) return only.ToArray();
        var parts = new List<(byte[] Header, byte[] Payload)>();
        for (var i = 0; i < file.Textures.Length; i++)
        {
            NutTexture texture;
            if (replacements.TryGetValue(i, out var single))
            {
                var parsed = NutFile.Parse(single);
                if (parsed.Textures.Length != 1) throw new InvalidDataException($"Replacement for texture {i} must contain one texture.");
                texture = parsed.Textures[0];
                if (texture.HeaderSize != file.Textures[i].HeaderSize) throw new InvalidDataException($"Replacement for texture {i} has a different header size.");
            }
            else texture = file.Textures[i];
            var header = texture.HeaderBytes.ToArray();
            var payload = texture.Data.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(header.Length + payload.Length));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)payload.Length);
            parts.Add((header, payload));
        }
        using var output = new MemoryStream();
        output.Write(file.HeaderBytes.AsSpan());
        if (file.Version == 1)
        {
            foreach (var (header, payload) in parts)
            {
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x20), 0);
                output.Write(header); output.Write(payload);
            }
        }
        else
        {
            var tableEnd = 0x10 + parts.Sum(p => p.Header.Length);
            var headerOffset = 0x10;
            long payloadOffset = tableEnd;
            foreach (var (header, payload) in parts)
            {
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x20), checked((uint)(payloadOffset - headerOffset)));
                output.Write(header);
                headerOffset += header.Length;
                payloadOffset += payload.Length;
            }
            foreach (var (_, payload) in parts) output.Write(payload);
        }
        var bytes = output.ToArray();
        var check = NutFile.Parse(bytes);
        if (check.Textures.Length != file.Textures.Length) throw new InvalidDataException("NUT pack rebuild changed the texture count.");
        for (var i = 0; i < check.Textures.Length; i++)
            if (!replacements.ContainsKey(i) && !check.Textures[i].Data.Span.SequenceEqual(file.Textures[i].Data.Span))
                throw new InvalidDataException($"NUT pack rebuild changed untouched texture {i}.");
        return bytes;
    }
}
