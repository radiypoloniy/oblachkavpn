using System.Diagnostics;
using System.Windows;

namespace RoutePilot;

public partial class MainWindow
{
    private bool vmTestActive;
    private bool vmTestAllowed;
    private Process? vmTestProcess;

    private void WireVmTun()
    {
        vmTestAllowed = stateStore is not null && !isolatedPersistence && VmTunTickets.IsVirtualMachine();
        Diagnostics.VmTunButton.IsEnabled = vmTestAllowed;
        Diagnostics.VmTunNote.Text = vmTestAllowed ?
            "Выбранный сервер подготовится автоматически. Windows попросит права администратора. Проверка меняет сеть только этой виртуалки; кнопка остановки появится в отдельном окне." :
            "Проверка системного TUN доступна в Windows VM. На основном компьютере используется обычный SOCKS-режим.";
        Diagnostics.VmTunButton.Click += StartVmTunClick;
    }

    private async void StartVmTunClick(object sender, RoutedEventArgs args)
    {
        if (!vmTestAllowed || vmTestActive || operation is not null) return;
        if (profile is null) { DetailText.Text = "Сначала выбери сервер из своей подписки."; return; }
        vmTestActive = true; IsEnabled = false;
        manualStop = true; recoveryWait?.Cancel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            DetailText.Text = "Готовим выбранный сервер для проверки TUN…";
            var plan = await TunnelEndpoints.PrepareAsync(profile, timeout.Token);
            if (closed) return;
            var ticket = VmTunTickets.Save(plan);
            engine.Stop();
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
            start.Arguments = "--vm-tun-host \"" + ticket + "\"";
            vmTestProcess = Process.Start(start) ?? throw new UserError("Windows не запустила проверку.");
            await vmTestProcess.WaitForExitAsync();
            if (!closed) DetailText.Text = "Окно проверки закрыто. Для обычного подключения нажми включение VPN.";
        }
        catch (UserError error) { if (!closed) DetailText.Text = error.Message; }
        catch (OperationCanceledException) { if (!closed) DetailText.Text = "Не удалось вовремя получить адрес выбранного сервера."; }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
        { if (!closed) DetailText.Text = "Запрос прав администратора отменён. Можно повторить проверку."; }
        catch { if (!closed) DetailText.Text = "Не удалось подготовить проверку. Проверь доступность выбранного сервера и целостность сборки."; }
        finally { vmTestProcess?.Dispose(); vmTestProcess = null; vmTestActive = false; if (!closed) { IsEnabled = true; UpdateControls(); } }
    }
}
