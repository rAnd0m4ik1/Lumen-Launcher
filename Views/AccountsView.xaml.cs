using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CraftDeck.Dialogs;
using CraftDeck.Models;
using CraftDeck.Services;

namespace CraftDeck.Views;

public record AccountCard(Account A)
{
    public string Id => A.Id;
    public string Name => A.Username;
    public string Initial => A.Username.Length > 0 ? char.ToUpperInvariant(A.Username[0]).ToString() : "?";
    public string TypeBadge => A.IsMicrosoft ? "Microsoft" : "offline";
    public bool IsActive { get; init; }
    public Visibility ActiveBadge => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public Brush AvatarBrush => IsActive
        ? (Brush)Application.Current.Resources["AccentBrush"]
        : (Brush)Application.Current.Resources["CardHoverBrush"];
}

public partial class AccountsView : UserControl
{
    public AccountsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        var active = AccountService.Active()?.Id;
        AccountsList.ItemsSource = AccountService.All()
            .Select(a => new AccountCard(a) { IsActive = a.Id == active }).ToList();
    }

    private void Hint(string text)
    {
        AddHint.Text = text;
        AddHint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        Hint("");
        OfflineRow.Visibility = Visibility.Collapsed;
        AddPopup.IsOpen = true;
    }

    private void Popup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        AddPopup.IsOpen = false;
        e.Handled = true;
    }

    private void OfflineTile_Click(object sender, RoutedEventArgs e)
    {
        OfflineRow.Visibility = Visibility.Visible;
        OfflineBox.Focus();
    }

    private void Offline_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddOffline_Click(sender, e);
    }

    private void AddOffline_Click(object sender, RoutedEventArgs e)
    {
        var name = OfflineBox.Text.Trim();
        if (name.Length == 0)
        {
            Hint("Type a nickname first.");
            OfflineBox.Focus();
            return;
        }
        var a = AccountService.AddOffline(name);
        if (AccountService.All().Count == 1) AccountService.SetActive(a.Id);
        OfflineBox.Text = "";
        AddPopup.IsOpen = false;
        Refresh();
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject d) return;
        for (DependencyObject? o = d; o != null; o = VisualTreeHelper.GetParent(o))
            if (o is System.Windows.Controls.Primitives.ButtonBase) return;
        var id = FindId(d);
        if (id == null || id == AccountService.Active()?.Id) return;
        AccountService.SetActive(id);
        Refresh();
    }

    private static string? FindId(DependencyObject d)
    {
        while (d != null)
        {
            if (d is FrameworkElement fe && fe.DataContext is AccountCard card) return card.Id;
            d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) as DependencyObject;
        }
        return null;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var a = AccountService.All().FirstOrDefault(x => x.Id == id);
        if (a == null) return;
        if (ConfirmDialog.Ask(Window.GetWindow(this), "Remove account",
                $"Remove account “{a.Username}”?", "The instance files stay untouched.", "Remove"))
        {
            AccountService.Remove(id);
            Refresh();
        }
    }

    private string? _msCode;

    private async void AddMicrosoft_Click(object sender, RoutedEventArgs e)
    {
        MsTile.IsEnabled = false;
        CopyCodeText.Text = "Copy code";
        AddPopup.StaysOpen = true;
        var main = MainWindow.Main!;
        main.SetProgress("Microsoft browser sign-in…", 30);
        Hint("Opening the browser — the sign-in code will show here.");
        try
        {
            var res = await MicrosoftAuth.LoginInBrowserAsync(
                new Progress<string>(t => { main.SetProgress(t, 50); Hint(t); }),
                c =>
                {
                    _msCode = c;
                    MsCodeText.Text = c;
                    MsCodeRow.Visibility = Visibility.Visible;
                });
            var acc = AccountService.AddMicrosoft(res);
            AccountService.SetActive(acc.Id);
            Hint("");
            AddPopup.IsOpen = false;
        }
        catch (Exception ex)
        {
            Hint("Microsoft login failed — " + ex.Message);
        }
        finally
        {
            MsTile.IsEnabled = true;
            MsCodeRow.Visibility = Visibility.Collapsed;
            AddPopup.StaysOpen = false;
            main.SetIdle();
            Refresh();
        }
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (_msCode == null) return;
        try
        {
            Clipboard.SetText(_msCode);
        }
        catch
        {
            Hint("Couldn't reach the clipboard — type the code " + _msCode + " yourself.");
            return;
        }
        CopyCodeText.Text = "Copied";
    }
}
