using System.IO;
using CraftDeck.Models;

namespace CraftDeck.Services;

public static class AppServices
{
    public static AppSettings Settings { get; private set; } = null!;
    public static InstanceService Instances { get; private set; } = null!;
    public static MinecraftService Minecraft { get; private set; } = null!;
    public static LaunchService Launch { get; private set; } = null!;

    public static void Init()
    {
        MigrateFromAppData();
        Settings = AppSettings.Load();
        Instances = new InstanceService(Settings);
        Minecraft = new MinecraftService(Settings);
        Launch = new LaunchService(Settings, Minecraft);
        Directory.CreateDirectory(Settings.InstancesDir);
        Directory.CreateDirectory(Settings.SharedDir);
    }

    private static void MigrateFromAppData()
    {
        var oldRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CraftDeck");
        var newRoot = AppContext.BaseDirectory;
        if (!Directory.Exists(oldRoot) ||
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldRoot)),
                          Path.TrimEndingDirectorySeparator(Path.GetFullPath(newRoot)),
                          StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            foreach (var dir in new[] { "instances", "shared", "java" })
            {
                var from = Path.Combine(oldRoot, dir);
                var to = Path.Combine(newRoot, dir);
                if (Directory.Exists(from) && !Directory.Exists(to)) Directory.Move(from, to);
            }
            var settings = Path.Combine(oldRoot, "settings.json");
            if (File.Exists(settings) && !File.Exists(Path.Combine(newRoot, "settings.json")))
                File.Move(settings, Path.Combine(newRoot, "settings.json"));
        }
        catch { }
    }

    public static void ReloadSettings()
    {
        Settings = AppSettings.Load();
        Instances = new InstanceService(Settings);
        Minecraft = new MinecraftService(Settings);
        Launch = new LaunchService(Settings, Minecraft);
    }
}
