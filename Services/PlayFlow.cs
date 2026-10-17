using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CraftDeck.Models;

namespace CraftDeck.Services;

public static class PlayFlow
{
    public static async Task PlayAsync(Instance inst, FrameworkElement owner, Button? playBtn = null)
    {
        var wnd = Window.GetWindow(owner);
        var main = MainWindow.Main!;
        if (RunningGames.IsRunning(inst.Config.Id)) return;
        if (playBtn != null) playBtn.IsEnabled = false;
        var log = new GameLog();
        try
        {
            var status = new Progress<string>(s => main.SetProgress(s, 5));
            var details = await AppServices.Launch.EnsureInstalledAsync(inst, status, CancellationToken.None);
            main.SetProgress("Preparing launch…", 80);

            var need = JavaLocator.RequiredMajor(details);
            string? java = null;
            try
            {
                java = await JavaManager.EnsureJavaAsync(need, AppServices.Settings,
                    new Progress<string>(s => main.SetProgress(s, 70)), CancellationToken.None);
            }
            catch (Exception ex)
            {
                App.Log($"Java: {ex.Message}");
            }

            var plan = AppServices.Launch.BuildPlan(inst, details, java);
            var proc = await AppServices.Launch.LaunchAsync(inst, plan, log);
            RunningGames.Track(inst, proc, log);

            if (AppServices.Settings.CloseOnLaunch) main.Close();
            return;
        }
        catch (Exception ex)
        {
            log.Add("[launcher] " + ex.Message);
            main.SetIdle();
            MessageBox.Show(wnd, "Could not launch: " + ex.Message, "Lumen",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (playBtn != null) playBtn.IsEnabled = true;
        }
    }
}
