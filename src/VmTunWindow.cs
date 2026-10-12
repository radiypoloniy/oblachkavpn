using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace RoutePilot;

internal sealed class VmTunWindow : Window
{
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 16, Margin = new Thickness(0, 18, 0, 24) };
    private readonly Button stop = new() { Content = "Остановить проверку", Padding = new Thickness(20, 10, 20, 10) };
    private readonly CancellationTokenSource cancellation = new();
    private readonly VmTunSession session = new();
    private readonly DispatcherTimer watchdog = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string path;
    private VmTunTicket? ticket;
    private bool finished;
    private bool starting = true;
    private bool closeAfterStop;

    internal VmTunWindow(string ticketPath)
    {
        path = ticketPath;
        Title = ServiceBrand.Name + " — проверка TUN в виртуалке";
        Width = 610; Height = 340; ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White; Foreground = new SolidColorBrush(Color.FromRgb(25, 38, 60));
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = "Проверка всего компьютера", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(status); panel.Children.Add(stop);
        Content = panel;
        stop.Click += (_, _) => { cancellation.Cancel(); if (!starting) Finish("Проверка остановлена."); };
        Closing += (_, args) =>
        {
            if (starting) { args.Cancel = true; closeAfterStop = true; cancellation.Cancel(); }
            else Finish("Проверка остановлена.");
        };
        Closed += (_, _) => { watchdog.Stop(); session.Dispose(); cancellation.Dispose(); };
        watchdog.Tick += (_, _) =>
        {
            if (ticket is null || finished) return;
            var ownerAlive = false;
            try { using var owner = Process.GetProcessById(ticket.OwnerId); ownerAlive = !owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == ticket.OwnerStarted; }
            catch (ArgumentException) { } catch (InvalidOperationException) { }
            if (!ownerAlive || (!starting && !session.IsAlive))
            {
                cancellation.Cancel();
                if (!starting) Finish("Проверка остановлена: приложение или ядро закрыто.");
            }
        };
        Loaded += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
    {
        status.Text = "Подготавливаем проверку…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            ticket = VmTunTickets.Read(path); watchdog.Start();
            await session.StartAsync(ticket, value => status.Text = value, timeout.Token);
        }
        catch (OperationCanceledException) { Finish("Проверка отменена или истёк срок ожидания."); }
        catch (UserError error) { Finish(error.Message); }
        catch { Finish("Не удалось выполнить проверку. Проверьте, что сборка распакована целиком и UAC использует ту же учётную запись Windows."); }
        finally { starting = false; if (closeAfterStop) Close(); }
    }

    private void Finish(string message)
    {
        if (finished) return;
        finished = true; watchdog.Stop(); session.Dispose();
        var adapterLeft = session.InterfaceName.Length > 0 && session.InterfaceExists;
        status.Text = message + (adapterLeft ? " Адаптер остался: восстановите снимок VM перед повторной проверкой." : " Ядра остановлены, адаптер этой проверки отсутствует.");
        stop.Content = "Закрыть";
        stop.Click += (_, _) => { if (!starting) Close(); };
    }

    internal static int Run(string path)
    {
        using var mutex = new System.Threading.Mutex(true, "Local\\Oblachka-VmTun-Test", out var first);
        if (!first) { MessageBox.Show("Проверка TUN уже открыта. Остановите её перед повторным запуском.", ServiceBrand.Name); return 1; }
        try { return new Application().Run(new VmTunWindow(path)); }
        catch { MessageBox.Show("Не удалось открыть проверку TUN.", "Облачка"); return 2; }
    }
}
