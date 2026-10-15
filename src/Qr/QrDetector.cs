namespace Oblachka;

// Поиск QR-кода на картинке: снимок экрана или изображение из буфера обмена. Рассчитан на
// чистые картинки (QR в Telegram, на сайте): порог Оцу, три поисковых узора 1:1:3:1:1,
// аффинная сетка модулей по их центрам. Поворот кратный 90° и масштаб — любые.
internal static class QrDetector
{
    public sealed record Finder(double X, double Y, double Module, int Hits);

    // Модули кода: true — тёмный. Несколько вариантов размера, если оценка неточна.
    public static IEnumerable<bool[,]> Grids(byte[] luminance, int width, int height)
    {
        var dark = Binarize(luminance);
        var finders = FindFinders(dark, width, height);
        foreach (var (tl, tr, bl) in Triples(finders))
        {
            var module = (tl.Module + tr.Module + bl.Module) / 3;
            var estimate = (Distance(tl, tr) + Distance(tl, bl)) / 2 / module + 7;
            var dimension = (int)Math.Round(estimate);
            var aligned = (dimension & 3) switch { 0 => dimension + 1, 2 => dimension - 1, 3 => dimension - 2, _ => dimension };
            foreach (var size in new[] { aligned, aligned + 4, aligned - 4 })
                if (size >= 21 && size <= 177) yield return Sample(dark, width, height, tl, tr, bl, size);
        }
    }

    private static bool[] Binarize(byte[] luminance)
    {
        var histogram = new int[256];
        foreach (var l in luminance) histogram[l]++;
        long total = luminance.Length, sum = 0;
        for (var i = 0; i < 256; i++) sum += (long)i * histogram[i];
        long sumBackground = 0, weightBackground = 0; double best = -1; var threshold = 128;
        for (var t = 0; t < 256; t++)
        {
            weightBackground += histogram[t]; if (weightBackground == 0) continue;
            var weightForeground = total - weightBackground; if (weightForeground == 0) break;
            sumBackground += (long)t * histogram[t];
            var meanBackground = (double)sumBackground / weightBackground;
            var meanForeground = (double)(sum - sumBackground) / weightForeground;
            var between = (double)weightBackground * weightForeground * (meanBackground - meanForeground) * (meanBackground - meanForeground);
            if (between > best) { best = between; threshold = t; }
        }
        return luminance.Select(l => l <= threshold).ToArray();
    }

    private static bool RatioOk(int[] runs)
    {
        var total = runs.Sum();
        if (total < 7) return false;
        var module = total / 7.0; var tolerance = module * 0.7;
        return Math.Abs(runs[0] - module) < tolerance && Math.Abs(runs[1] - module) < tolerance &&
               Math.Abs(runs[2] - 3 * module) < 3 * tolerance && Math.Abs(runs[3] - module) < tolerance && Math.Abs(runs[4] - module) < tolerance;
    }

    private static List<Finder> FindFinders(bool[] dark, int width, int height)
    {
        var found = new List<Finder>();
        for (var y = 0; y < height; y++)
        {
            var runs = new int[5]; var state = 0;
            for (var x = 0; x < width; x++)
            {
                var isDark = dark[y * width + x];
                if (isDark == (state % 2 == 0)) { runs[state]++; continue; }
                if (state < 4) { state++; runs[state] = 1; continue; }
                // Закрыт пятый отрезок: проверяем пропорцию и сдвигаемся на два отрезка.
                if (RatioOk(runs) && Confirm(dark, width, height, x - runs[4] - runs[3] - runs[2] / 2.0, y, runs.Sum()) is { } finder) Add(found, finder);
                runs = new[] { runs[2], runs[3], runs[4], 1, 0 }; state = 3;
            }
            if (state == 4 && RatioOk(runs) && Confirm(dark, width, height, width - runs[4] - runs[3] - runs[2] / 2.0, y, runs.Sum()) is { } last) Add(found, last);
        }
        return found.Where(f => f.Hits >= 2).ToList();
    }

    // Пропорция по вертикали через центр и уточнение обеих координат.
    private static Finder? Confirm(bool[] dark, int width, int height, double cx, int cy, int horizontal)
    {
        var x = (int)cx;
        if (!dark[cy * width + x]) return null;
        int Run(int dx, int dy, int startX, int startY, bool color, int limit)
        {
            int count = 0, px = startX + dx, py = startY + dy;
            while (px >= 0 && py >= 0 && px < width && py < height && dark[py * width + px] == color && count < limit) { count++; px += dx; py += dy; }
            return count;
        }
        var limit = horizontal * 2;
        var upCenter = Run(0, -1, x, cy, true, limit); var downCenter = Run(0, 1, x, cy, true, limit);
        var upWhite = Run(0, -1, x, cy - upCenter, false, limit); var downWhite = Run(0, 1, x, cy + downCenter, false, limit);
        var upOuter = Run(0, -1, x, cy - upCenter - upWhite, true, limit); var downOuter = Run(0, 1, x, cy + downCenter + downWhite, true, limit);
        var vertical = new[] { upOuter, upWhite, upCenter + downCenter + 1, downWhite, downOuter };
        if (!RatioOk(vertical) || Math.Abs(vertical.Sum() - horizontal) > horizontal * 0.5) return null;
        var centerY = cy - upCenter + (upCenter + downCenter + 1) / 2.0 - 0.5;
        return new(cx, centerY, (vertical.Sum() + horizontal) / 14.0, 1);
    }

    private static void Add(List<Finder> found, Finder candidate)
    {
        for (var i = 0; i < found.Count; i++)
        {
            var f = found[i];
            if (Math.Abs(f.X - candidate.X) <= f.Module * 2 && Math.Abs(f.Y - candidate.Y) <= f.Module * 2)
            {
                var hits = f.Hits + 1;
                found[i] = new((f.X * f.Hits + candidate.X) / hits, (f.Y * f.Hits + candidate.Y) / hits, (f.Module * f.Hits + candidate.Module) / hits, hits);
                return;
            }
        }
        // В больших кодах похожих на узор мест в данных много: лимит с запасом, иначе нижний левый
        // узор не попадал в список (сверху его вытесняли ложные).
        if (found.Count < 4096) found.Add(candidate);
    }

    // Тройки узоров: близкий размер модуля и прямой угол; вершина прямого угла — верхний левый.
    private static IEnumerable<(Finder, Finder, Finder)> Triples(List<Finder> finders)
    {
        var ordered = finders.OrderByDescending(f => f.Hits).Take(12).ToArray();
        var triples = new List<(double, Finder, Finder, Finder)>();
        for (var a = 0; a < ordered.Length; a++)
        for (var b = a + 1; b < ordered.Length; b++)
        for (var c = b + 1; c < ordered.Length; c++)
        {
            var p = new[] { ordered[a], ordered[b], ordered[c] };
            var modules = p.Select(f => f.Module).ToArray();
            if (modules.Max() > modules.Min() * 1.6) continue;
            var sides = new[] { (Distance(p[1], p[2]), 0), (Distance(p[0], p[2]), 1), (Distance(p[0], p[1]), 2) }.OrderByDescending(s => s.Item1).ToArray();
            var corner = p[sides[0].Item2];
            var others = p.Where(f => !ReferenceEquals(f, corner)).ToArray();
            var (d1, d2) = (Distance(corner, others[0]), Distance(corner, others[1]));
            if (Math.Abs(d1 - d2) > Math.Max(d1, d2) * 0.25 || Math.Abs(sides[0].Item1 - Math.Sqrt(d1 * d1 + d2 * d2)) > sides[0].Item1 * 0.12) continue;
            var cross = (others[0].X - corner.X) * (others[1].Y - corner.Y) - (others[0].Y - corner.Y) * (others[1].X - corner.X);
            var (tr, bl) = cross > 0 ? (others[0], others[1]) : (others[1], others[0]);
            triples.Add((Math.Abs(d1 - d2), corner, tr, bl));
        }
        return triples.OrderBy(t => t.Item1).Take(4).Select(t => (t.Item2, t.Item3, t.Item4));
    }

    private static double Distance(Finder a, Finder b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static bool[,] Sample(bool[] dark, int width, int height, Finder tl, Finder tr, Finder bl, int size)
    {
        var grid = new bool[size, size];
        var span = size - 7.0;
        var (ux, uy) = ((tr.X - tl.X) / span, (tr.Y - tl.Y) / span);
        var (vx, vy) = ((bl.X - tl.X) / span, (bl.Y - tl.Y) / span);
        for (var row = 0; row < size; row++)
            for (var col = 0; col < size; col++)
            {
                var px = (int)Math.Round(tl.X + (col - 3.0) * ux + (row - 3.0) * vx);
                var py = (int)Math.Round(tl.Y + (col - 3.0) * uy + (row - 3.0) * vy);
                grid[row, col] = px >= 0 && py >= 0 && px < width && py < height && dark[py * width + px];
            }
        return grid;
    }
}
