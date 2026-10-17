using CraftDeck.Models;

namespace CraftDeck.Services;

public static class AccountService
{
    private static AppSettings S => AppServices.Settings;

    public static List<Account> All() => S.Accounts;

    public static Account? Active() =>
        S.Accounts.FirstOrDefault(a => a.Id == S.ActiveAccountId) ?? S.Accounts.FirstOrDefault();

    public static Account AddOffline(string name)
    {
        var a = new Account { Type = "Offline", Username = name };
        S.Accounts.Add(a);
        S.Save();
        return a;
    }

    public static Account AddMicrosoft(AuthResult r)
    {
        var a = new Account
        {
            Type = "Microsoft",
            Username = r.Username,
            UUID = r.UUID,
            AccessToken = r.AccessToken,
            RefreshToken = r.RefreshToken,
        };
        S.Accounts.Add(a);
        S.Save();
        return a;
    }

    public static void SetActive(string id)
    {
        var a = S.Accounts.FirstOrDefault(x => x.Id == id);
        if (a == null) return;
        S.ActiveAccountId = a.Id;
        S.Username = a.Username;
        S.UUID = a.UUID;
        S.AccessToken = a.AccessToken;
        S.AccountType = a.Type;
        S.Save();
        MainWindow.Main?.RefreshAccount();
    }

    public static void Remove(string id)
    {
        S.Accounts.RemoveAll(a => a.Id == id);
        if (S.ActiveAccountId == id)
        {
            var first = S.Accounts.FirstOrDefault();
            if (first != null)
            {
                SetActive(first.Id);
                return;
            }
            S.ActiveAccountId = null;
            S.Username = "Player";
            S.UUID = null;
            S.AccessToken = null;
            S.AccountType = "Offline";
            S.Save();
            MainWindow.Main?.RefreshAccount();
        }
        else S.Save();
    }

    public static async Task RefreshAllMicrosoftAsync()
    {
        foreach (var a in S.Accounts.Where(a => a.IsMicrosoft && a.RefreshToken != null).ToList())
        {
            try { await RefreshMicrosoftAsync(a); }
            catch (Exception ex) { App.Log($"token refresh {a.Username}: {ex.Message}"); }
        }
    }

    public static async Task RefreshMicrosoftAsync(Account a)
    {
        if (!a.IsMicrosoft) throw new Exception("Not a Microsoft account");
        var refreshToken = a.RefreshToken
            ?? throw new Exception("No refresh token stored — sign in again");
        var r = await MicrosoftAuth.RefreshAsync(refreshToken);
        a.AccessToken = r.AccessToken;
        a.UUID = r.UUID;
        a.Username = r.Username;
        if (r.RefreshToken != null) a.RefreshToken = r.RefreshToken;
        S.Save();
        if (S.ActiveAccountId == a.Id) SetActive(a.Id);
    }
}
