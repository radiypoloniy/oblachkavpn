using System.Text;

namespace Oblachka;

internal static class GeoDatStream
{
    private readonly record struct Message(int Field, int Wire, long Start, long End);

    internal static void Read(Stream data, IReadOnlyCollection<string> tags, Action<string, byte[]> consume)
    {
        var wanted = new HashSet<string>(tags.Select(t => t.ToLowerInvariant()));
        while (Next(data, data.Length) is { } message)
        {
            if (message.Field != 1 || message.Wire != 2) continue;
            var code = Code(data, message);
            if (code is null || !wanted.Contains(code))
            {
                data.Position = message.End;
                continue;
            }
            data.Position = message.Start;
            consume(code, ReadBytes(data, message.End - message.Start));
        }
    }

    internal static bool LooksValid(Stream data)
    {
        try
        {
            if (Next(data, data.Length) is not { Field: 1, Wire: 2 } entry) return false;
            data.Position = entry.Start;
            return Next(data, entry.End) is { Field: 1, Wire: 2 } code && code.End > code.Start;
        }
        catch (Exception ex) when (ex is IOException or FormatException) { return false; }
    }

    private static string? Code(Stream data, Message entry)
    {
        data.Position = entry.Start;
        while (data.Position < entry.End && Next(data, entry.End) is { } field)
            if (field is { Field: 1, Wire: 2 })
            {
                data.Position = field.Start;
                return Encoding.UTF8.GetString(ReadBytes(data, field.End - field.Start)).ToLowerInvariant();
            }
        return null;
    }

    private static Message? Next(Stream data, long end)
    {
        if (data.Position >= end) return null;
        var key = Varint(data, end);
        var field = (int)(key >> 3);
        var wire = (int)(key & 7);
        long start;
        long finish;
        switch (wire)
        {
            case 0:
                Varint(data, end);
                start = finish = data.Position;
                break;
            case 1:
                start = data.Position;
                finish = start + 8;
                break;
            case 5:
                start = data.Position;
                finish = start + 4;
                break;
            case 2:
                var length = Varint(data, end);
                if (length > int.MaxValue) throw new FormatException("Повреждённая база адресов.");
                start = data.Position;
                finish = start + (long)length;
                break;
            default:
                throw new FormatException("Повреждённая база адресов.");
        }
        if (finish > end) throw new FormatException("Повреждённая база адресов.");
        data.Position = finish;
        return new(field, wire, start, finish);
    }

    private static byte[] ReadBytes(Stream data, long length)
    {
        if (length > int.MaxValue) throw new FormatException("Повреждённая база адресов.");
        var result = new byte[length];
        data.ReadExactly(result);
        return result;
    }

    private static ulong Varint(Stream data, long end)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (data.Position >= end) throw new FormatException("Повреждённая база адресов.");
            var value = data.ReadByte();
            if (value < 0) throw new EndOfStreamException();
            var b = (byte)value;
            result |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return result;
        }
        throw new FormatException("Повреждённая база адресов.");
    }
}
