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
    private bool _cleared;

    public InstanceSettingsDialog(Instance inst)
    {
        InitializeComponent();
        WindowCorners.Apply(this);
        _inst = inst;
        NameBox.Text = inst.Config.Name;
        GlyphBox.Text = InstanceIcons.FileOf(inst) == null ? inst.Config.Icon : "";
        GlyphBox.TextChanged += (_, _) => Preview();
        Preview();
    }

    private string? ImagePath() => _picked ?? (_cleared ? null : InstanceIcons.FileOf(_inst));

    private void Preview()
    {
        var glyph = GlyphBox.Text.Trim();
        var img = glyph.Length > 0 ? null : InstanceIcons.LoadFile(ImagePath());
        PreviewImage.Source = img;
        PreviewImage.Visibility = img != null ? Visibility.Visible : Visibility.Collapsed;
        PreviewGlyph.Text = img == null ? glyph : "";
        PreviewGlyph.Visibility = img == null && glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewStar.Visibility = img == null && glyph.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoveBtn.IsEnabled = glyph.Length > 0 || ImagePath() != null;
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
        _cleared = false;
        GlyphBox.Text = "";
        Preview();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        _picked = null;
        _cleared = true;
        GlyphBox.Text = "";
        Preview();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length > 0) _inst.Config.Name = name;
        var glyph = GlyphBox.Text.Trim();
        try
        {
            if (_picked != null) InstanceIcons.Store(_inst, _picked);
            else if (glyph.Length > 0) { InstanceIcons.Clear(_inst); _inst.Config.Icon = glyph; }
            else InstanceIcons.Clear(_inst);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, "Couldn't save the icon: " + ex.Message, "Lumen");
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
