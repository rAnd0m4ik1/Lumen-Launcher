using System.Windows;
using CraftDeck.Controls;

namespace CraftDeck.Dialogs;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string question, string? detail, string confirmText)
    {
        InitializeComponent();
        WindowCorners.Apply(this);
        Title = title;
        TitleText.Text = title;
        Question.Text = question;
        Detail.Text = detail ?? "";
        Detail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        ConfirmBtn.Content = confirmText;
    }

    public static bool Ask(Window owner, string title, string question, string? detail, string confirmText) =>
        new ConfirmDialog(title, question, detail, confirmText) { Owner = owner }.ShowDialog() == true;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
