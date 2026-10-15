using System.Text;

namespace Oblachka;

// Чтение QR-кода по сетке модулей (QrDetector): формат, маска, кодовые слова, блоки
// Рида — Соломона, данные. Режимы: цифры, буквы с цифрами, байты (UTF-8), ECI.
public static class QrDecoder
{
    // Картинка в градациях серого (0 — чёрный) → текст кода или null.
    public static string? Decode(byte[] luminance, int width, int height)
    {
        if (luminance.Length != width * height || width < 21 || height < 21) return null;
        foreach (var grid in QrDetector.Grids(luminance, width, height))
        {
            // Повреждённая или чужая картинка не должна ронять окно: просто следующий вариант.
            try { if (Read(grid) is { } text) return text; }
            catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentException) { }
        }
        return null;
    }

    internal static string? Read(bool[,] grid)
    {
        var size = grid.GetLength(0);
        var version = (size - 17) / 4;
        if (version is < 1 or > 40 || size != version * 4 + 17) return null;
        bool Get(int x, int y) => grid[y, x];
        int formatA = 0, formatB = 0;
        for (var i = 0; i <= 5; i++) formatA = formatA << 1 | (Get(i, 8) ? 1 : 0);
        formatA = formatA << 1 | (Get(7, 8) ? 1 : 0); formatA = formatA << 1 | (Get(8, 8) ? 1 : 0); formatA = formatA << 1 | (Get(8, 7) ? 1 : 0);
        for (var j = 5; j >= 0; j--) formatA = formatA << 1 | (Get(8, j) ? 1 : 0);
        for (var j = size - 1; j >= size - 7; j--) formatB = formatB << 1 | (Get(8, j) ? 1 : 0);
        for (var i = size - 8; i < size; i++) formatB = formatB << 1 | (Get(i, 8) ? 1 : 0);
        var format = Format(formatA, formatB);
        if (format < 0) return null;
        var (ecBits, mask) = (format >> 3, format & 7);

        var function = FunctionPattern(version, size);
        var codewords = new List<byte>();
        int current = 0, bits = 0;
        var up = true;
        for (var x = size - 1; x > 0; x -= 2)
        {
            if (x == 6) x--;
            for (var count = 0; count < size; count++)
            {
                var y = up ? size - 1 - count : count;
                for (var col = 0; col < 2; col++)
                {
                    var cx = x - col;
                    if (function[y, cx]) continue;
                    var bit = Get(cx, y) ^ Masked(mask, y, cx);
                    current = current << 1 | (bit ? 1 : 0);
                    if (++bits == 8) { codewords.Add((byte)current); current = 0; bits = 0; }
                }
            }
            up = !up;
        }
        var data = Correct(codewords.ToArray(), QrTables.BlocksFor(version, ecBits));
        return data is null ? null : Payload(data, version);
    }

    private static int Format(int a, int b)
    {
        int best = -1, bestDistance = 4;
        for (var d = 0; d < 32; d++)
        {
            var code = d << 10; var remainder = code;
            for (var i = 14; i >= 10; i--) if ((remainder >> i & 1) != 0) remainder ^= 0x537 << (i - 10);
            code = (code | remainder) ^ 0x5412;
            var distance = Math.Min(System.Numerics.BitOperations.PopCount((uint)(code ^ a)), System.Numerics.BitOperations.PopCount((uint)(code ^ b)));
            if (distance < bestDistance) { bestDistance = distance; best = d; }
        }
        return best;
    }

    private static bool Masked(int mask, int i, int j) => mask switch
    {
        0 => (i + j) % 2 == 0,
        1 => i % 2 == 0,
        2 => j % 3 == 0,
        3 => (i + j) % 3 == 0,
        4 => (i / 2 + j / 3) % 2 == 0,
        5 => (i * j) % 2 + (i * j) % 3 == 0,
        6 => ((i * j) % 2 + (i * j) % 3) % 2 == 0,
        _ => ((i + j) % 2 + (i * j) % 3) % 2 == 0
    };

    private static bool[,] FunctionPattern(int version, int size)
    {
        var map = new bool[size, size];
        void Fill(int left, int top, int w, int h) { for (var y = top; y < top + h; y++) for (var x = left; x < left + w; x++) map[y, x] = true; }
        Fill(0, 0, 9, 9); Fill(size - 8, 0, 8, 9); Fill(0, size - 8, 9, 8);
        var align = QrTables.AlignmentFor(version);
        for (var i = 0; i < align.Length; i++)
            for (var j = 0; j < align.Length; j++)
            {
                if (i == 0 && (j == 0 || j == align.Length - 1) || i == align.Length - 1 && j == 0) continue;
                Fill(align[i] - 2, align[j] - 2, 5, 5);
            }
        Fill(6, 9, 1, size - 17); Fill(9, 6, size - 17, 1);
        if (version > 6) { Fill(size - 11, 0, 3, 6); Fill(0, size - 11, 6, 3); }
        return map;
    }

    // Разбор чередования блоков и исправление каждого; результат — данные подряд.
    private static byte[]? Correct(byte[] raw, QrTables.Group[] groups)
    {
        var blocks = groups.SelectMany(g => Enumerable.Repeat(g, g.Count)).Select(g => (g, words: new byte[g.Total])).ToArray();
        if (raw.Length < blocks.Sum(b => b.g.Total)) return null;
        var ec = groups[0].Total - groups[0].Data;
        var shortData = blocks[0].g.Data;
        var offset = 0;
        for (var i = 0; i < shortData; i++) foreach (var b in blocks) b.words[i] = raw[offset++];
        foreach (var b in blocks.Where(b => b.g.Data > shortData)) b.words[shortData] = raw[offset++];
        for (var i = 0; i < ec; i++) foreach (var b in blocks) b.words[b.g.Data + i] = raw[offset++];
        var result = new List<byte>();
        foreach (var (g, words) in blocks)
        {
            if (!ReedSolomon.Correct(words, ec)) return null;
            result.AddRange(words.Take(g.Data));
        }
        return result.ToArray();
    }

    private static string? Payload(byte[] data, int version)
    {
        var reader = new BitReader(data);
        var text = new StringBuilder();
        var range = version <= 9 ? 0 : version <= 26 ? 1 : 2;
        const string Alnum = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
        while (reader.Left >= 4)
        {
            var mode = reader.Read(4);
            if (mode == 0) break;
            switch (mode)
            {
                case 1:
                    for (var count = reader.Read(new[] { 10, 12, 14 }[range]); count > 0; count -= 3)
                    {
                        var digits = Math.Min(count, 3);
                        text.Append(reader.Read(digits == 3 ? 10 : digits == 2 ? 7 : 4).ToString("D" + digits));
                    }
                    break;
                case 2:
                    for (var count = reader.Read(new[] { 9, 11, 13 }[range]); count > 0; count -= 2)
                    {
                        if (count >= 2) { var v = reader.Read(11); text.Append(Alnum[v / 45 % 45]).Append(Alnum[v % 45]); }
                        else text.Append(Alnum[reader.Read(6) % 45]);
                    }
                    break;
                case 4:
                    var length = reader.Read(new[] { 8, 16, 16 }[range]);
                    if (reader.Left < length * 8) return null;
                    var bytes = new byte[length];
                    for (var i = 0; i < length; i++) bytes[i] = (byte)reader.Read(8);
                    try { text.Append(new UTF8Encoding(false, true).GetString(bytes)); }
                    catch (DecoderFallbackException) { text.Append(Encoding.Latin1.GetString(bytes)); }
                    break;
                case 7:
                    // ECI: номер кодировки 1–3 байта. Байты всё равно читаем как UTF-8.
                    var first = reader.Read(8);
                    if ((first & 0x80) != 0) reader.Read((first & 0x40) == 0 ? 8 : 16);
                    break;
                case 3: reader.Read(16); break;
                case 5: break;
                case 9: reader.Read(8); break;
                default: return text.Length > 0 ? text.ToString() : null;
            }
        }
        return text.Length > 0 ? text.ToString() : null;
    }

    private sealed class BitReader(byte[] data)
    {
        private int position;
        public int Left => data.Length * 8 - position;
        public int Read(int count)
        {
            if (count > Left) throw new FormatException();
            var value = 0;
            for (var i = 0; i < count; i++, position++) value = value << 1 | (data[position >> 3] >> (7 - (position & 7)) & 1);
            return value;
        }
    }
}
