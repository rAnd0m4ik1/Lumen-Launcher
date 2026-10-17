using System.Windows;
using CraftDeck.Views;

namespace CraftDeck.Dialogs;

public static class Prompt
{
    public static string? Show(string title, string initial = "")
    {
        var box = new System.Windows.Controls.TextBox
        {
            Text = initial,
            Margin = new Thickness(0, 10, 0, 16),
        };
        var ok = new System.Windows.Controls.Button
        {
            Content = "OK",
            Style = (Style)Application.Current.FindResource("Accent"),
            IsDefault = true,
        };
        var cancel = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Style = (Style)Application.Current.FindResource("Ghost"),
            IsCancel = true,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.FindResource("H2"),
        });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BgBrush"),
            MinWidth = 340,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => { win.DialogResult = true; };
        box.SelectAll();
        box.Focus();
        return win.ShowDialog() == true ? box.Text : null;
    }
}
