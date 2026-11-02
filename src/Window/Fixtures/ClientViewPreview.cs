using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Oblachka;

// Настоящее отдельное окно стенда. Не создаёт свой ClientRuntime и не владеет подключением.
internal static class ClientViewPreview
{
    internal static int Run(string pipe, int ownerPid, string report, string mode)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var text = new TextBlock { Margin = new Thickness(12) };
        var stop = new Button { Content = "Остановить", Margin = new Thickness(12) };
        var panel = new StackPanel(); panel.Children.Add(text); panel.Children.Add(stop);
        var window = new Window { Title = "Изолированная проверка канала интерфейса", Width = 360, Height = 180,
            ShowInTaskbar = false, ShowActivated = false, Left = -32000, Top = -32000, Content = panel };
        window.Loaded += async (_, _) =>
        {
            ClientViewClient? client = null; var code = 2;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                using var owner = Process.GetProcessById(ownerPid);
                client = await ClientViewClient.ConnectAsync(pipe, owner, timeout.Token);
                var initial = await client.SendAsync(ClientViewOperation.ReadState, timeout.Token);
                if (initial.State?.Connection is not (ConnectionState.Running or ConnectionState.Verified)) throw new UserError("Стенд не подключён.");
                text.Text = initial.State.ServerName + " — " + initial.State.Connection;
                var probe = await client.SendAsync(ClientViewOperation.Probe, timeout.Token);
                if (probe.Failure != ClientViewFailure.None || probe.State?.Verified != true) throw new UserError("Проверка связи через владельца не прошла.");
                ClientViewReply? stopped = null;
                if (mode == "stop")
                {
                    var clicked = new TaskCompletionSource<ClientViewReply>(TaskCreationOptions.RunContinuationsAsynchronously);
                    stop.Click += async (_, _) =>
                    {
                        try { clicked.TrySetResult(await client.SendAsync(ClientViewOperation.Stop, timeout.Token)); }
                        catch (Exception error) { clicked.TrySetException(error); }
                    };
                    stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    stopped = await clicked.Task.WaitAsync(timeout.Token);
                    if (stopped.State?.Connection != ConnectionState.Idle || stopped.State.StopRequested != true)
                        throw new UserError("Команда остановки не применилась.");
                }
                else if (mode is not ("close" or "crash")) throw new UserError("Неизвестный режим стенда.");
                File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = true, InstanceId = initial.State.InstanceId,
                    WindowLoaded = window.IsLoaded, ProbeVerified = probe.State.Verified, Stopped = stopped?.State?.StopRequested == true }));
                if (mode == "crash") Environment.Exit(23); // Обрыв процесса без Dispose: владелец должен пережить его.
                code = 0;
            }
            catch (Exception error) { File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = false, Error = error.ToString() })); }
            finally
            {
                if (client is not null) await client.DisposeAsync();
                window.Close(); app.Shutdown(code);
            }
        };
        app.Startup += (_, _) => window.Show();
        return app.Run();
    }
}
