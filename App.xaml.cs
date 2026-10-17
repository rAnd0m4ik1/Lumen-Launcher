using System.IO;
using System.Windows;
using System.Windows.Threading;
using CraftDeck.Services;

namespace CraftDeck;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("AppDomain: " + args.ExceptionObject);
        try
        {
            AppServices.Init();
            var settings = AppServices.Settings;
            ThemeService.ApplyTheme(settings.ThemeMode, settings.BackgroundPreset, settings.BackgroundStyle, settings.AccentColor);
            var w = new MainWindow();
            w.Show();
            _ = AccountService.RefreshAllMicrosoftAsync();
            Log("startup ok");
        }
        catch (Exception ex)
        {
            Log("startup FAILED: " + ex);
            MessageBox.Show("Startup failed: " + ex.Message, "Lumen");
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("UI: " + e.Exception);
        e.Handled = true;
    }

    public static void Log(string text)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "lumen-error.log"),
                $"{DateTime.Now:O} {text}{Environment.NewLine}");
        }
        catch { }
    }
}
