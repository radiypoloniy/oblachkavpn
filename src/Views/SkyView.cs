using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Oblachka.Views;

// Живое небо боковой панели (SkyScene): кадр высотой 60 точек в WriteableBitmap, увеличение без сглаживания.
// Два кадра в секунду и только пока небо видно: свёрнутое или скрытое окно не тратит процессор,
// а окно в фоне (не активное) — кадр в две секунды: каждый кадр без видеокарты стоит процессора.
// Движение выключают «Живое небо» (Animate) и системная настройка анимации Windows — тогда
// остаётся неподвижная картинка текущего часа.
// Небо замирает, когда на окно не смотрят: нет мыши над окном и клавиш 45 с (в фоне — 15 с).
// Замер 18.10.2026 (VM): небо — 70–80% процессора окна (подключено: 7,5–10,6% против 2,1%
// без него), а окно висит на экране весь день. Первое движение мыши над окном — небо идёт дальше
// с того же кадра.
public sealed class SkyView : Image
{
    private static readonly TimeSpan IdleActive = TimeSpan.FromSeconds(45), IdleBackground = TimeSpan.FromSeconds(15);
    private WriteableBitmap bitmap = new(SkyScene.MinWidth, SkyScene.Height, 96, 96, PixelFormats.Bgra32, null);
    private uint[] pixels = new uint[SkyScene.MinWidth * SkyScene.Height];
    private int sceneWidth = SkyScene.MinWidth;
    private readonly DispatcherTimer timer;
    private SkyMood mood = SkyMood.Off;
    private bool animate = true;
    private int frame, lastHash, lastWidth;
    private Window? host;
    private DateTime lastActivity = DateTime.UtcNow;

    // Сколько нижних строк неба затемнить под подпись статуса.
    public int ShadeRows { get; set; }

    // Для превью и проверок: фиксированный час вместо часов компьютера.
    public double? FixedHour { get; set; }

    public SkyView()
    {
        Source = bitmap;
        Stretch = Stretch.Fill;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(500) };
        // В простое кадр раз в минуту и без движения: только солнце и время суток по часам.
        timer.Tick += (_, _) => { if (!Idle) frame++; Sync(); Draw(); };
        IsVisibleChanged += (_, _) => Sync();
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } window && !ReferenceEquals(window, host))
            {
                host = window;
                window.Activated += (_, _) => Touch();
                window.Deactivated += (_, _) => Sync();
                // Мышь над окном (и над неактивным тоже) и клавиши — окно смотрят, небо живёт.
                window.PreviewMouseMove += (_, _) => Touch();
                window.PreviewKeyDown += (_, _) => Touch();
            }
            Draw(); Sync();
        };
        Unloaded += (_, _) => timer.Stop();
        // Сцена шире или уже под размер карточки: пиксели остаются квадратными.
        SizeChanged += (_, e) =>
        {
            if (e.NewSize.Height <= 0) return;
            var wanted = Math.Clamp((int)Math.Round(e.NewSize.Width / e.NewSize.Height * SkyScene.Height), SkyScene.MinWidth, SkyScene.MaxWidth);
            if (wanted == sceneWidth) return;
            sceneWidth = wanted;
            bitmap = new WriteableBitmap(sceneWidth, SkyScene.Height, 96, 96, PixelFormats.Bgra32, null);
            pixels = new uint[sceneWidth * SkyScene.Height];
            Source = bitmap;
            Draw();
        };
        // Первый кадр сразу: снимок окна (превью, миниатюра) бывает раньше Loaded.
        Draw();
    }

    public SkyMood Mood { get => mood; set { if (mood == value) return; mood = value; Draw(); } }

    public bool Animate { get => animate; set { animate = value; Sync(); Draw(); } }

    // Анимация включена вообще; остановка на время (свёрнуто, не смотрят) кадр не сбрасывает.
    private bool Animated => animate && SystemParameters.ClientAreaAnimation;

    private bool Idle => DateTime.UtcNow - lastActivity > (host is { IsActive: false } ? IdleBackground : IdleActive);

    private bool Moving => Animated && IsVisible &&
        Window.GetWindow(this) is not { WindowState: WindowState.Minimized };

    private TimeSpan Pace => Idle ? TimeSpan.FromMinutes(1) : TimeSpan.FromMilliseconds(host is { IsActive: false } ? 2000 : 500);

    // Вызывается на каждое движение мыши: только отметка времени; небо в простое — сразу в ход.
    private void Touch()
    {
        var idle = Idle;
        lastActivity = DateTime.UtcNow;
        if (idle || !timer.IsEnabled) Sync();
    }

    // Вызывается и при сворачивании окна (MainWindow.StateChanged): невидимое небо не рисуется.
    public void Sync()
    {
        var pace = Pace;
        if (timer.Interval != pace) timer.Interval = pace;
        if (Moving) timer.Start(); else timer.Stop();
    }

    private void Draw()
    {
        var now = DateTime.Now;
        var hour = FixedHour ?? now.Hour + now.Minute / 60.0;
        SkyScene.Render(pixels, sceneWidth, SkyScene.PhaseAt(hour), hour, mood, Animated ? frame : 0, ShadeRows);
        // Кадр не изменился (между шагами облаков и мерцания) — картинку не трогаем: перерисовка
        // окна без видеокарты стоит процессора, а таких кадров больше половины.
        var hash = new HashCode(); foreach (var p in pixels) hash.Add(p);
        var signature = hash.ToHashCode();
        if (signature == lastHash && lastWidth == sceneWidth) return;
        lastHash = signature; lastWidth = sceneWidth;
        bitmap.WritePixels(new Int32Rect(0, 0, sceneWidth, SkyScene.Height), pixels, sceneWidth * 4, 0);
    }
}
