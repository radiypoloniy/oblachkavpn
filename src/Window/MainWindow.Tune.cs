using System.Windows;

namespace Oblachka;

// «Подобрать лучшее» (TuneSearch). «Автовыбор» подключает сразу, по готовым замерам; подбор же
// проверяет лучшие серверы с поправками (ServerTweak) настоящей загрузкой и оставляет то, что
// быстрее и стабильнее. Поправка хранится по серверу: её применяют подключение и фоновые замеры,
// поэтому и автовыбор потом сравнивает серверы уже подстроенными.
public partial class MainWindow
{
    private readonly Dictionary<string, ServerTweak> serverTweaks = new(StringComparer.Ordinal);
    private CancellationTokenSource? tuning;
    // Что было до последнего подбора — для «Вернуть как было».
    private (string Key, ServerTweak? Previous)? tuneUndo;

    private void WireTune()
    {
        ServerTweak.Lookup = item => serverTweaks.GetValueOrDefault(ServerTweak.Key(item));
        Servers.BestButton.Click += async (_, _) => { if (tuning is not null) { tuning.Cancel(); return; } await TuneAsync(); };
        Servers.Tune.CloseButton.Click += (_, _) => Servers.Tune.Visibility = Visibility.Collapsed;
        Servers.Tune.UndoButton.Click += async (_, _) => await UndoTuneAsync();
    }

    private void RestoreTweaks(Dictionary<string, ServerTweak>? saved)
    {
        serverTweaks.Clear();
        foreach (var (key, tweak) in saved ?? [])
            if (tweak is { Valid: true, IsEmpty: false }) serverTweaks[key] = tweak;
    }

    private async Task TuneAsync()
    {
        if (operation is not null || measuring || checkingServer is not null) { DetailText.Text = "Дождитесь окончания текущей проверки."; return; }
        var pool = autoPool.Members(importedProfiles.ToArray()).Where(p => MeasureRoute(p) is not null).ToArray();
        if (pool.Length == 0) { DetailText.Text = "Подбирать не из чего: в автовыборе нет серверов, которые сейчас можно проверить."; return; }
        tuning = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        Servers.BestButton.Content = "Остановить"; UpdateControls();
        ShowTune("Подбираем лучшее…", "Сначала — пинг всех серверов", "Около двух минут. Подключение не трогаем, пока не найдём лучшее.");
        try
        {
            // Свежая задержка всех: от неё зависит, у кого пробовать поправки.
            await MeasureServersAsync(pool, quiet: true);
            tuning.Token.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            var known = EffectiveMeasurements();
            ServerMeasurement? Fresh(Profile p) => known.TryGetValue(Profiles.Fingerprint(p), out var m) && m is { Success: true, Handshake: false } && ServerMeasurements.Fresh(m, now) ? m : null;
            var ranked = pool.Where(p => Fresh(p) is not null).OrderBy(p => ServerMeasurements.Score(Fresh(p)!)).ToArray();
            if (ranked.Length == 0) { ShowTune("Ни один сервер не ответил", "Проверьте интернет или обновите подписку.", ""); return; }
            var steps = TuneSearch.Steps(ranked);
            var step = 0;
            var result = await TuneSearch.RunAsync(ranked, MeasureTrialAsync,
                trial => { if (!closed) ShowTune("Подбираем лучшее…", trial.Server.DisplayName + " · " + trial.Tweak.Describe() + " — " + Numbers(trial.Result), $"Шаг {Math.Min(++step, steps)} из {steps}. Можно остановить."); },
                tuning.Token);
            if (closed) return;
            if (result is null) { ShowTune("Ни один вариант не заработал стабильно", "Подключение не меняли.", "Попробуйте позже или в другой сети."); return; }
            await ApplyTuneAsync(result);
        }
        catch (OperationCanceledException) { if (!closed) ShowTune("Подбор остановлен", "Ничего не поменялось.", ""); }
        finally { tuning.Dispose(); tuning = null; if (!closed) { Servers.BestButton.Content = "Подобрать лучшее"; UpdateControls(); } }
    }

    // Один вариант: отдельное ядро с пробной поправкой, задержка и скорость.
    private async Task<ServerMeasurement> MeasureTrialAsync(Profile server, ServerTweak tweak, CancellationToken token)
    {
        ServerMeasurement? result = null;
        await ServerMeasurements.MeasureAsync([server], () => new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"), "") { Trial = tweak, Physical = systemTunnel is not null },
            measured => result = measured, token, MeasureRoute, throughput: true);
        return result ?? new(Profiles.Fingerprint(server), DateTimeOffset.UtcNow, false, 0);
    }

    private async Task ApplyTuneAsync(TuneResult result)
    {
        var key = ServerTweak.Key(result.Server);
        var previous = serverTweaks.GetValueOrDefault(key);
        var next = result.Tweak.IsEmpty ? null : result.Tweak;
        if (next is null) serverTweaks.Remove(key); else serverTweaks[key] = next;
        tuneUndo = previous == next ? null : (key, previous);
        // Итог подбора — и замер сервера: автовыбор сравнивает его уже подстроенным.
        measurements[Profiles.Fingerprint(result.Server)] = result.After;
        QueueStateSave();
        ShowTune("Лучшее — " + result.Server.DisplayName, "Было " + Numbers(result.Before) + "  →  стало " + Numbers(result.After),
            next is null ? "Подстройка не понадобилась: лучше всего — как в подписке." : "Подстроено: " + next.Describe() + ".", undo: tuneUndo is not null);
        var running = runningProfile is not null && ServerTweak.Key(runningProfile) == key;
        PickServer(result.Server);
        // Тот же сервер уже работает, а поправка сменилась — переподключаемся, чтобы она заработала.
        if (running && previous != next && ConnectionActive) await ReconnectAsync();
        RenderSheet();
    }

    private async Task UndoTuneAsync()
    {
        if (tuneUndo is not { } undo) return;
        if (undo.Previous is null) serverTweaks.Remove(undo.Key); else serverTweaks[undo.Key] = undo.Previous;
        tuneUndo = null;
        QueueStateSave();
        ShowTune("Вернули как было", "Поправка снята — сервер работает с прежними настройками.", "");
        if (runningProfile is not null && ServerTweak.Key(runningProfile) == undo.Key && ConnectionActive) await ReconnectAsync();
    }

    private void ShowTune(string title, string line, string detail, bool undo = false)
    {
        Servers.Tune.Title.Text = title; Servers.Tune.Line.Text = line; Servers.Tune.Detail.Text = detail;
        Servers.Tune.Detail.Visibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Servers.Tune.UndoButton.Visibility = undo ? Visibility.Visible : Visibility.Collapsed;
        Servers.Tune.Visibility = Visibility.Visible;
    }

    internal static string Numbers(ServerMeasurement m) => !m.Success ? "не ответил"
        : m.Milliseconds + " мс" + (m.Throughput > 0 ? " · " + (m.Throughput / 1_000_000.0).ToString("0.#") + " МБ/с" : "");
}
