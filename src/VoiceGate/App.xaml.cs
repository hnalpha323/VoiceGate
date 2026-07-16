using System.Windows;

namespace VoiceGate;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Two instances would both feed the virtual cable and double the audio.
        _singleInstanceMutex = new Mutex(true, @"Local\VoiceGate_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("VoiceGate is already running.", "VoiceGate",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "Unexpected error:\n\n" + args.Exception.Message,
                "VoiceGate", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            // If the main window never became visible the startup failed; without
            // this the app would linger as a headless process (ShutdownMode is
            // OnLastWindowClose and no window ever opened).
            if (MainWindow is not { IsVisible: true })
                Shutdown(1);
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
