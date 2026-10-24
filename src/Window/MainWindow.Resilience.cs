using System.Windows;

namespace Oblachka;

// Устойчивость подключения: «Обход DPI» (TlsFragment) и выбор попыток после отказа
// по диагнозу (ConnectionDiagnosis → RouteLadder). Само восстановление — в MainWindow.Health.
public partial class MainWindow
{
    private FragmentMode fragmentMode = FragmentMode.Auto;
    // В режиме «Авто» подбор нашёл рабочий вариант только с разрезанным рукопожатием:
    // следующие подключения в этом запуске сразу идут так же.
    private bool fragmentLearned;

    private void WireDpi()
    {
        void Choose(FragmentMode mode) { fragmentMode = mode; fragmentLearned = false; RefreshDpi(); QueueStateSave(); }
        Diagnostics.DpiAuto.Click += (_, _) => Choose(FragmentMode.Auto);
        Diagnostics.DpiAlways.Click += (_, _) => Choose(FragmentMode.Always);
        Diagnostics.DpiOff.Click += (_, _) => Choose(FragmentMode.Off);
    }

    private void RefreshDpi()
    {
        var general = Diagnostics;
        MarkSegment(general.DpiAuto, fragmentMode == FragmentMode.Auto);
        MarkSegment(general.DpiAlways, fragmentMode == FragmentMode.Always);
        MarkSegment(general.DpiOff, fragmentMode == FragmentMode.Off);
        general.DpiNote.Text = fragmentMode switch
        {
            FragmentMode.Always => "Рукопожатие режется всегда: медленнее на десятки миллисекунд",
            FragmentMode.Off => "Не режем рукопожатие, даже если фильтр его обрывает",
            _ => fragmentLearned ? "В этой сети помогло: режем рукопожатие"
                : ProviderFragment is { Enabled: true } ? "Режем рукопожатие: так советует сервис. Соединение чуть медленнее"
                : "Режем рукопожатие, если фильтр его обрывает"
        };
    }

    // Резать ли рукопожатие при обычном подключении (не в подборе).
    // Провайдер подписки сервиса может включить его для всех (fragmentation-enable) — в «Авто».
    private bool FragmentWanted => fragmentMode == FragmentMode.Always || fragmentMode == FragmentMode.Auto && (fragmentLearned || ProviderFragment is { Enabled: true });
    private ProviderFragment? ProviderFragment => ServiceGroup?.Metadata?.Provider?.Fragment;

    private Action<bool> FragmentSetter => cut => { if (engine is Engine core) core.Fragment = cut; };

    // Порядок попыток после отказа: сначала понять почему, потом выбирать.
    private async Task<IReadOnlyList<LadderStep>> PlanRecoveryAsync(Profile[] profiles, int[] candidates, Profile failed, CancellationToken token)
    {
        Diagnosis diagnosis;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            diagnosis = await ConnectionDiagnosis.RunAsync(failed, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { diagnosis = new(Blockage.Unknown, "Проверка сервера не уложилась во время."); }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException) { diagnosis = new(Blockage.Unknown, ""); }
        lastDiagnosis = diagnosis;
        if (!closed && diagnosis.Explanation.Length > 0) DetailText.Text = diagnosis.Explanation;
        return RouteLadder.Plan(profiles, candidates, failed, diagnosis.Kind, fragmentMode);
    }

    private Diagnosis? lastDiagnosis;

    // Итог подбора: рабочий вариант с разрезанным рукопожатием запоминается до конца запуска.
    private void LearnFragment(bool used)
    {
        if (fragmentMode == FragmentMode.Auto) fragmentLearned = used;
        RefreshDpi();
    }
}
