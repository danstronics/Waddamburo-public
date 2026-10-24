using System.Buffers.Binary;
static class Ndp
{
    public static void Run(string path)
    {
        var b = File.ReadAllBytes(path);
        uint u(int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o, 4));
        var skip = (int)u(16);
        var p = 20 + skip + 20;
        var count = (int)u(p); p += 4 + 9;
        long maxEnd = 0, maxSize = 0;
        for (var i = 0; i < count; i++)
        {
            var len = (int)u(p); p += 4;
            var name = System.Text.Encoding.UTF8.GetString(b, p, len); p += len;
            if (i == 0) p += 5;
            var off = u(p); var size = u(p + 4); p += 8;
            maxEnd = Math.Max(maxEnd, off + size); maxSize = Math.Max(maxSize, size);
            if (i < 2 || i == count - 1) Console.WriteLine($"{i} {name} off=0x{off:X} size=0x{size:X}");
        }
        Console.WriteLine($"table end=0x{p:X} file=0x{b.Length:X} maxEnd=0x{maxEnd:X} maxSize=0x{maxSize:X}");
        Console.WriteLine("after table: " + Convert.ToHexString(b, p, Math.Min(48, b.Length - p)));
        Console.WriteLine($"data start guess=0x{b.Length - maxEnd:X}");
        var start = (int)(b.Length - maxEnd);
        Console.WriteLine("at data start: " + Convert.ToHexString(b, start, 32));
    }
}
