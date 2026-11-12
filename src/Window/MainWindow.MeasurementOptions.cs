using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    private bool fillingMeasurementOptions;
    private void WireMeasurementOptions()
    {
        var page = Settings.AutoPool;
        page.ManualMeasurements.Click += (_, _) => SaveMeasurementOptions();
        page.ConnectedMeasurements.Click += (_, _) => SaveMeasurementOptions();
        page.SpeedMeasurements.Click += (_, _) => SaveMeasurementOptions();
        page.MeasurementInterval.SelectionChanged += (_, _) => SaveMeasurementOptions();
        page.MeasurementParallelism.SelectionChanged += (_, _) => SaveMeasurementOptions();
    }
    private void FillMeasurementOptions()
    {
        fillingMeasurementOptions = true;
        try
        {
            var options = runtime.Workspace.Measurements.Normalize(); var page = Settings.AutoPool;
            page.ManualMeasurements.IsChecked = options.ManualOnly;
            page.ConnectedMeasurements.IsChecked = options.AllowConnected;
            page.SpeedMeasurements.IsChecked = options.BackgroundSpeed;
            page.MeasurementInterval.SelectedIndex = options.IntervalMinutes == 5 ? 0 : options.IntervalMinutes == 30 ? 2 : 1;
            page.MeasurementParallelism.SelectedIndex = options.Parallelism - 1;
        }
        finally { fillingMeasurementOptions = false; }
    }
    private void SaveMeasurementOptions()
    {
        if (fillingMeasurementOptions || restoringState) return;
        var page = Settings.AutoPool;
        runtime.Workspace.Measurements = new MeasurementOptions
        {
            ManualOnly = page.ManualMeasurements.IsChecked == true,
            AllowConnected = page.ConnectedMeasurements.IsChecked == true,
            BackgroundSpeed = page.SpeedMeasurements.IsChecked == true,
            IntervalMinutes = page.MeasurementInterval.SelectedIndex == 0 ? 5 : page.MeasurementInterval.SelectedIndex == 2 ? 30 : 10,
            Parallelism = page.MeasurementParallelism.SelectedIndex + 1
        }.Normalize();
        runtime.Measurements.PolicyChanged(); QueueStateSave();
    }
}
