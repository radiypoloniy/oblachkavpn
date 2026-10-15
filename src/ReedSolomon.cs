namespace Oblachka;

// Исправление ошибок Рида — Соломона для QR: поле GF(256) с многочленом 0x11D, корни генератора
// α^0…α^(n-1). Синдромы → Берлекэмп — Мэсси → поиск Ченя → Форни. Неисправимый блок — false.
internal static class ReedSolomon
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static ReedSolomon()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x; Log[x] = (byte)i;
            x <<= 1; if (x >= 256) x ^= 0x11D;
        }
        for (var i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    private static int Mul(int a, int b) => a == 0 || b == 0 ? 0 : Exp[Log[a] + Log[b]];
    private static int Div(int a, int b) => a == 0 ? 0 : Exp[(Log[a] + 255 - Log[b]) % 255];
    private static int Pow(int power) => Exp[((power % 255) + 255) % 255];

    // block — данные и проверочные слова, старший коэффициент первым; ec — число проверочных.
    public static bool Correct(byte[] block, int ec)
    {
        var n = block.Length;
        var syndromes = new int[ec];
        var clean = true;
        for (var i = 0; i < ec; i++)
        {
            var root = Pow(i); var value = 0;
            foreach (var b in block) value = Mul(value, root) ^ b;
            syndromes[i] = value; if (value != 0) clean = false;
        }
        if (clean) return true;

        // Берлекэмп — Мэсси: многочлен локаторов ошибок lambda.
        var lambda = new int[ec + 1]; lambda[0] = 1;
        var previous = new int[ec + 1]; previous[0] = 1;
        int length = 0, shift = 1, lastDiscrepancy = 1;
        for (var step = 0; step < ec; step++)
        {
            var discrepancy = syndromes[step];
            for (var i = 1; i <= length; i++) discrepancy ^= Mul(lambda[i], syndromes[step - i]);
            if (discrepancy == 0) { shift++; continue; }
            var copy = (int[])lambda.Clone();
            var factor = Div(discrepancy, lastDiscrepancy);
            for (var i = 0; i + shift <= ec; i++) lambda[i + shift] ^= Mul(factor, previous[i]);
            if (2 * length <= step) { length = step + 1 - length; previous = copy; lastDiscrepancy = discrepancy; shift = 1; }
            else shift++;
        }
        if (length * 2 > ec) return false;

        // Поиск Ченя: позиция i (степень x^i) ошибочна, если lambda(α^-i) = 0.
        var positions = new List<int>();
        for (var i = 0; i < n; i++)
        {
            var inverse = Pow(-i); var value = 0;
            for (var k = length; k >= 0; k--) value = Mul(value, inverse) ^ lambda[k];
            if (value == 0) positions.Add(i);
        }
        if (positions.Count != length) return false;

        // Форни: omega = S(x)·lambda(x) mod x^ec; величина e = X · omega(X^-1) / lambda'(X^-1).
        var omega = new int[ec];
        for (var i = 0; i < ec; i++)
            for (var k = 0; k <= Math.Min(i, length); k++) omega[i] ^= Mul(syndromes[i - k], lambda[k]);
        foreach (var degree in positions)
        {
            var x = Pow(degree); var inverse = Pow(-degree);
            int num = 0, den = 0;
            for (var k = ec - 1; k >= 0; k--) num = Mul(num, inverse) ^ omega[k];
            for (var k = 1; k <= length; k += 2) den ^= Mul(lambda[k], Pow(-degree * (k - 1)));
            if (den == 0) return false;
            block[n - 1 - degree] ^= (byte)Mul(x, Div(num, den));
        }
        // Проверка: после исправления синдромы обязаны обнулиться.
        for (var i = 0; i < ec; i++)
        {
            var root = Pow(i); var value = 0;
            foreach (var b in block) value = Mul(value, root) ^ b;
            if (value != 0) return false;
        }
        return true;
    }
}
