using System.Windows;
using System.Windows.Threading;

namespace BFCrewSync;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Keep the process's own footprint minimal from the first frame.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"Unhandled error:\n{args.Exception.Message}",
                "BFCrewSync",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);
    }
}
