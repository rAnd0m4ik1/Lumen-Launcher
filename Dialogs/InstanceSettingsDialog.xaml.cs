using System.IO;
using System.Windows;
using CraftDeck.Controls;
using CraftDeck.Models;
using CraftDeck.Services;

namespace CraftDeck.Dialogs;

public partial class InstanceSettingsDialog : Window
{
    private readonly Instance _inst;
    private string? _picked;
    private string _glyph = "";

    public InstanceSettingsDialog(Instance inst)
    {
        InitializeComponent();
        WindowCorners.Apply(this);
        _inst = inst;
        NameBox.Text = inst.Config.Name;
        _glyph = InstanceIcons.FileOf(inst) == null ? inst.Config.Icon : "";
        Preview();
    }

    private string? ImagePath() => _picked ?? InstanceIcons.FileOf(_inst);

    private void Preview()
    {
        var glyph = _glyph.Trim();
        var img = glyph.Length > 0 ? null : InstanceIcons.LoadFile(ImagePath());
        PreviewImage.Source = img;
        PreviewImage.Visibility = img != null ? Visibility.Visible : Visibility.Collapsed;
        PreviewGlyph.Text = img == null ? glyph : "";
        PreviewGlyph.Visibility = img == null && glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewStar.Visibility = img == null && glyph.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an icon",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.ico)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.ico",
        };
        if (ofd.ShowDialog(this) != true) return;
        _picked = ofd.FileName;
        _glyph = "";
        Preview();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length > 0) _inst.Config.Name = name;
        if (_picked != null)
        {
            try
            {
                InstanceIcons.Store(_inst, _picked);
            }
            catch (IOException ex)
            {
                MessageBox.Show(this, "Couldn't save the icon: " + ex.Message, "Lumen");
                return;
            }
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
