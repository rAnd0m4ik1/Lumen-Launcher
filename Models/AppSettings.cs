using System.IO;
using System.Text.Json.Serialization;

namespace CraftDeck.Models;

public class AppSettings
{
    [JsonIgnore]
    public string RootDir { get; } = AppContext.BaseDirectory;

    public string Username { get; set; } = "Player";
    public string? AccessToken { get; set; }
    public string? UUID { get; set; }
    public string? AccountType { get; set; }
    public List<Account> Accounts { get; set; } = new();
    public string? ActiveAccountId { get; set; }
    public string? JavaPath { get; set; }
    public int MinMemoryMb { get; set; } = 1024;
    public int MaxMemoryMb { get; set; } = 4096;
    public string JVMArguments { get; set; } = "";
    public string GameArguments { get; set; } = "";
    public bool CloseOnLaunch { get; set; }
    public int DownloadThreads { get; set; } = 8;
    public string AccentColor { get; set; } = "#FF1FDB62";
    public string ThemeMode { get; set; } = "dark";
    public string BackgroundPreset { get; set; } = "midnight";
    public string BackgroundStyle { get; set; } = "gradient";
    public string BackgroundFx { get; set; } = "triangles";
    public string FxColor { get; set; } = Services.FxColorCatalog.Default;

    public string InstancesDir => Path.Combine(RootDir, "instances");
    public string SharedDir => Path.Combine(RootDir, "shared");
    public string AssetsDir => Path.Combine(SharedDir, "assets");
    public string LibrariesDir => Path.Combine(SharedDir, "libraries");
    public string VersionsDir => Path.Combine(SharedDir, "versions");
    public string MetaDir => Path.Combine(SharedDir, "meta");
    public string JavaDir => Path.Combine(RootDir, "java");

    public static AppSettings Load()
    {
        try
        {
            var def = new AppSettings();
            var file = Path.Combine(def.RootDir, "settings.json");
            if (File.Exists(file))
            {
                var s = Services.Json.Load<AppSettings>(File.ReadAllText(file)) ?? def;
                s.MigrateAccounts();
                return s;
            }
        }
        catch { }
        return new AppSettings();
    }

    private void MigrateAccounts()
    {
        if (Accounts.Count == 0)
        {
            var a = new Account
            {
                Type = AccountType ?? "Offline",
                Username = Username,
                UUID = UUID,
                AccessToken = AccessToken,
            };
            Accounts.Add(a);
            ActiveAccountId = a.Id;
        }
        else if (Accounts.All(x => x.Id != ActiveAccountId))
            ActiveAccountId = Accounts[0].Id;
    }

    public void Save()
    {
        Directory.CreateDirectory(RootDir);
        File.WriteAllText(Path.Combine(RootDir, "settings.json"), Services.Json.Save(this));
    }
}
