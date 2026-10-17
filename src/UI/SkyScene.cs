namespace Oblachka;

public enum SkyPhase { Dawn, Day, Sunset, Night }
public enum SkyMood { Off, Connecting, On }

// Пиксельное небо в боковой панели: кадр высотой 60 точек, который увеличивается без сглаживания.
// Ширина подстраивается под карточку (от 64 точек): шире окно — больше неба, пиксели не тянутся.
// Небо — полосы с дизерингом Байера, своя палитра на время суток; солнце или луна идут по
// дуге по часу; облака плывут по пикселю; на холме домик, ночью в нём горит окно. В центре —
// облачко-талисман: спит (VPN выключен), смотрит по сторонам (подключаемся), улыбается (подключено).
// Только расчёт пикселей: ни WPF, ни таймеров — показывает и анимирует SkyView.
public static class SkyScene
{
    public const int Height = 60, MinWidth = 64, MaxWidth = 192;

    private sealed record Palette(uint[] Sky, uint Cloud, uint Shade, uint Far, uint Near, uint Edge, uint[]? Sun, uint[]? Moon, bool Stars);

    private static readonly Palette Dawn = new([0x6D7BD1, 0x9C86CF, 0xD795B9, 0xF5B7A2, 0xFFD9A8], 0xFFF1EA, 0xEBC4CB, 0x9A8BC4, 0x6F63A0, 0xB5A8DA, [0xFFE6A3, 0xFFC66B], null, false);
    private static readonly Palette Day = new([0x3D7BEA, 0x5592F1, 0x71A8F6, 0x93C0FA, 0xBCDBFD], 0xFFFFFF, 0xD6E5FA, 0x7DC48E, 0x4FA36B, 0xA5DCAE, [0xFFF4BF, 0xFFD84D], null, false);
    private static readonly Palette Sunset = new([0x3B3D8C, 0x7550A3, 0xC45F92, 0xEC7F79, 0xFFB56A], 0xFFC8AE, 0xD98A8E, 0x5A4378, 0x352A57, 0x7E5E97, [0xFFD9A0, 0xFF8F4A], null, false);
    private static readonly Palette Night = new([0x060A20, 0x0B1230, 0x111B42, 0x182553, 0x223066], 0x3A4778, 0x2B3663, 0x141C40, 0x0A1029, 0x26305C, null, [0xF4F0DA, 0xC9C3A6], true);

    private static readonly int[,] Bayer = { { 0, 8, 2, 10 }, { 12, 4, 14, 6 }, { 3, 11, 1, 9 }, { 15, 7, 13, 5 } };
    private static readonly (int X, int Y)[] StarsAt = [(5, 4), (12, 9), (20, 3), (27, 12), (34, 6), (41, 2), (47, 10), (55, 5), (60, 13), (8, 16), (30, 18), (51, 17), (17, 22), (44, 24), (58, 27), (3, 28)];

    public static SkyPhase PhaseAt(double hour) =>
        hour is >= 5 and < 8 ? SkyPhase.Dawn : hour is >= 8 and < 17 ? SkyPhase.Day : hour is >= 17 and < 20 ? SkyPhase.Sunset : SkyPhase.Night;

    // Пиксели — 0xAARRGGBB, построчно сверху (формат Bgra32 у WriteableBitmap).
    // shadeRows — нижние строки затемняются к низу: на них лежит подпись статуса. Дымка — в самой
    // картинке, а не слоем поверх: лишний слой перерисовывался бы с каждым кадром.
    public static void Render(uint[] pixels, int width, SkyPhase phase, double hour, SkyMood mood, int frame, int shadeRows = 0)
    {
        if (width is < MinWidth or > MaxWidth || pixels.Length != width * Height) throw new ArgumentException("Буфер не совпадает с размером сцены.", nameof(pixels));
        var p = phase switch { SkyPhase.Dawn => Dawn, SkyPhase.Day => Day, SkyPhase.Sunset => Sunset, _ => Night };
        void Px(int x, int y, uint rgb) { if (x >= 0 && y >= 0 && x < width && y < Height) pixels[y * width + x] = 0xFF000000 | rgb; }

        var bands = p.Sky.Length - 1;
        for (var y = 0; y < Height; y++)
        {
            var t = Math.Min(1, y / (Height * 0.8)) * bands;
            var k = (int)t; var fraction = t - k;
            for (var x = 0; x < width; x++) Px(x, y, p.Sky[Math.Min(bands, k + (fraction * 16 > Bayer[y & 3, x & 3] ? 1 : 0))]);
        }

        if (p.Stars)
            for (var i = 0; i < StarsAt.Length * (width / MinWidth + 1); i++)
            {
                var (sx, sy) = StarsAt[i % StarsAt.Length];
                sx += i / StarsAt.Length * MinWidth;
                var twinkle = (frame + i * 3) % 14;
                if (twinkle == 0) continue;
                Px(sx, sy, twinkle < 3 ? 0xFFFFFFu : 0x9FB0E0u);
                if (twinkle == 1 && i % 3 == 0) { Px(sx - 1, sy, 0x6F80B8); Px(sx + 1, sy, 0x6F80B8); Px(sx, sy - 1, 0x6F80B8); Px(sx, sy + 1, 0x6F80B8); }
            }

        // Солнце днём и луна ночью идут по дуге слева направо: в полдень солнце в зените.
        var arc = phase == SkyPhase.Night ? ((hour + 24 - 20) % 24) / 9 : Math.Clamp((hour - 5) / 15, 0, 1);
        var bx = (int)Math.Round(8 + arc * (width - 18)); var by = (int)Math.Round(7 + Math.Pow(2 * arc - 1, 2) * 16);
        if (p.Sun is { } sun)
        {
            for (var yy = -6; yy <= 6; yy++)
                for (var xx = -6; xx <= 6; xx++)
                {
                    var r2 = xx * xx + yy * yy;
                    if (r2 <= 12) Px(bx + xx, by + yy, sun[0]); else if (r2 <= 20) Px(bx + xx, by + yy, sun[1]);
                }
            if (frame % 8 < 5)
            {
                (int, int)[] rays = [(0, -6), (0, 6), (-6, 0), (6, 0), (-4, -4), (4, -4), (-4, 4), (4, 4)];
                for (var i = 0; i < rays.Length; i++)
                    if ((i + frame / 4) % 2 == 0)
                    {
                        var (rx, ry) = rays[i];
                        Px(bx + Math.Sign(rx) * (Math.Abs(rx) + 1), by + Math.Sign(ry) * (Math.Abs(ry) + 1), sun[1]);
                    }
            }
        }
        else if (p.Moon is { } moon)
            for (var my = -4; my <= 4; my++)
                for (var mx = -4; mx <= 4; mx++)
                    if (mx * mx + my * my <= 17 && (mx - 2) * (mx - 2) + (my + 1) * (my + 1) > 9) Px(bx + mx, by + my, mx < -1 ? moon[1] : moon[0]);

        // Фоновые облака — выше облачка-талисмана, чтобы не проплывали сквозь него.
        var drift = frame / 2;
        Cloud(((drift + 6) % (width + 26)) - 18, 10, 0.6, p.Cloud, p.Shade);
        Cloud(((int)(drift * 0.6 + 50) % (width + 26)) - 18, 15, 0.45, p.Cloud, p.Shade);

        for (var x = 0; x < width; x++)
        {
            var far = (int)Math.Round(41 + Math.Sin(x / 9.0) * 3 + Math.Sin(x / 4.3));
            var near = (int)Math.Round(46 + Math.Sin(x / 7.0 + 2) * 2.5);
            for (var y = far; y < Height; y++) Px(x, y, y == far ? p.Edge : p.Far);
            for (var y = near; y < Height; y++) Px(x, y, y == near ? p.Edge : p.Near);
        }

        var night = phase == SkyPhase.Night;
        const int hx = 7;
        var hy = (int)Math.Round(46 + Math.Sin(hx / 7.0 + 2) * 2.5) - 5;
        for (var a = 0; a < 6; a++) for (var b = 0; b < 4; b++) Px(hx + a, hy + 1 + b, night ? 0x2A3260u : 0xF3E6D2u);
        for (var r = 0; r < 4; r++) for (var c = r; c < 7 - r; c++) Px(hx - 1 + c, hy - 2 + r, night ? 0x3A2440u : 0xC4573Eu);
        Px(hx + 2, hy + 2, night ? (frame % 15 < 14 ? 0xFFD86Bu : 0xE0B44Cu) : 0x7FA7D8u);

        Mascot(mood, night, frame);
        if (shadeRows > 0) Shade(pixels, width, shadeRows);

        void Cloud(int cx, int cy, double s, uint body, uint shade)
        {
            (double X, double Y, double R)[] blobs = [(0, 0, 4), (5, -2, 5), (10, 0, 4), (3, 2, 4), (8, 2, 4)];
            for (var yy = -9; yy <= 7; yy++)
                for (var xx = -6; xx <= 18; xx++)
                    foreach (var (bxx, byy, br) in blobs)
                    {
                        double dx = xx - bxx * s, dy = yy - byy * s, rr = br * s;
                        if (dx * dx + dy * dy > rr * rr) continue;
                        Px(cx + xx, cy + yy, yy > 2 * s ? shade : body);
                        break;
                    }
        }

        void Mascot(SkyMood m, bool dark, int f)
        {
            var bob = m == SkyMood.Off ? f / 5 % 2 : m == SkyMood.Connecting ? f % 2 : f / 3 % 2;
            int x0 = width / 2 - 8, y0 = 24 - bob;
            uint light = dark ? 0xE7ECFAu : 0xFFFFFFu, shade = dark ? 0xB9C4E4u : 0xD9E4F7u;
            const uint ink = 0x26324F, blush = 0xF59AA8;
            (int X, int Y, int R)[] body = [(3, 1, 4), (8, -1, 5), (13, 1, 4), (6, 3, 4), (11, 3, 4)];
            bool Inside(int xx, int yy) { foreach (var (qx, qy, qr) in body) if ((xx - qx) * (xx - qx) + (yy - qy) * (yy - qy) <= qr * qr) return true; return false; }
            for (var yy = -7; yy <= 8; yy++)
                for (var xx = -2; xx <= 18; xx++)
                    if (Inside(xx, yy)) Px(x0 + xx, y0 + yy, !Inside(xx, yy + 1) || yy >= 6 ? shade : light);
            int ex1 = x0 + 6, ex2 = x0 + 11, ey = y0 + 1;
            if (m == SkyMood.Off)
            {
                Px(ex1, ey, ink); Px(ex1 + 1, ey, ink); Px(ex2, ey, ink); Px(ex2 + 1, ey, ink);
                var z = f % 6; int zx = x0 + 16 + (z > 2 ? 1 : 0), zy = y0 - 6 - z;
                foreach (var (zx0, zy0) in new[] { (0, 0), (1, 0), (2, 0), (1, 1), (0, 2), (1, 2), (2, 2) }) Px(zx + zx0, zy + zy0, 0xFFFFFF);
            }
            else if (m == SkyMood.Connecting)
            {
                var look = f / 2 % 3 - 1;
                Px(ex1 + look, ey, ink); Px(ex1 + look, ey - 1, ink); Px(ex2 + look, ey, ink); Px(ex2 + look, ey - 1, ink);
                Px(x0 + 8 + look, ey + 3, ink); Px(x0 + 9 + look, ey + 3, ink);
            }
            else
            {
                foreach (var (ox, oy) in new[] { (0, 0), (1, -1), (2, 0) }) { Px(ex1 - 1 + ox, ey + oy, ink); Px(ex2 - 1 + ox, ey + oy, ink); }
                Px(ex1 - 2, ey + 2, blush); Px(ex1 - 1, ey + 2, blush); Px(ex2 + 2, ey + 2, blush); Px(ex2 + 3, ey + 2, blush);
                Px(x0 + 8, ey + 3, ink); Px(x0 + 9, ey + 4, ink); Px(x0 + 10, ey + 3, ink);
            }
        }
    }

    // Затемнение к низу (до 62% к #080C20) — та же дымка, что была градиентом поверх неба.
    private static void Shade(uint[] pixels, int width, int rows)
    {
        for (var r = 0; r < rows; r++)
        {
            var y = Height - rows + r;
            var a = 0.62 * (r + 1) / rows;
            for (var x = 0; x < width; x++)
            {
                var c = pixels[y * width + x];
                uint Mix(int shift, uint target) => (uint)Math.Round(((c >> shift) & 0xFF) * (1 - a) + target * a);
                pixels[y * width + x] = 0xFF000000 | Mix(16, 0x08) << 16 | Mix(8, 0x0C) << 8 | Mix(0, 0x20);
            }
        }
    }
}
