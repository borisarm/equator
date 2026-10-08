using Microsoft.UI.Xaml;

namespace Equator.App;

public partial class App : Application
{
    private Window? _window;

    public static string LogPath { get; } = Path.Combine(Path.GetTempPath(), "Equator", "equator.log");

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log("Unhandled: " + e.Exception);
            // Keep the notebook alive for UI-level glitches; the kernel guards its own work.
            e.Handled = true;
        };
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Localization.Loc.Initialize();
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            Log("Startup failed: " + ex);
            throw;
        }
    }
}
