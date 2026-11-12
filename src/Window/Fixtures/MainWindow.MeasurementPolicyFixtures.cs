using System.Windows;
using System.Windows.Controls.Primitives;

namespace Oblachka;

public partial class MainWindow
{
    private void CheckMeasurementPolicy(List<string> checks)
    {
        var before = runtime.Workspace.Measurements;
        try
        {
            runtime.Workspace.Measurements = new(); RenderAutoPool();
            var page = Settings.AutoPool;
            if (page.ManualMeasurements.IsChecked != false || page.ConnectedMeasurements.IsChecked != false ||
                page.SpeedMeasurements.IsChecked != false || page.MeasurementInterval.SelectedIndex != 1 || page.MeasurementParallelism.SelectedIndex != 3)
                throw new InvalidOperationException("Measurement settings lost their conservative defaults");
            page.ManualMeasurements.IsChecked = true;
            page.ManualMeasurements.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            page.MeasurementInterval.SelectedIndex = 2; page.MeasurementParallelism.SelectedIndex = 0;
            if (CaptureState().MeasurementOptions is not { ManualOnly: true, AllowConnected: false, BackgroundSpeed: false, IntervalMinutes: 30, Parallelism: 1 })
                throw new InvalidOperationException("Measurement controls did not update the persisted policy");
            RenderAutoPool();
            if (page.ManualMeasurements.IsChecked != true || page.MeasurementInterval.SelectedIndex != 2)
                throw new InvalidOperationException("Reopening measurement settings discarded the policy");
            checks.Add("Server measurement controls preserve manual mode, interval and parallelism in snapshots");
        }
        finally { runtime.Workspace.Measurements = before; FillMeasurementOptions(); runtime.Measurements.PolicyChanged(); }
    }
}
