using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CompactLoader;

public partial class AccentColorEditor : UserControl
{
    bool updating;
    public Color SelectedColor { get; private set; }
    public event RoutedEventHandler? ApplyRequested;

    public AccentColorEditor() { InitializeComponent(); SetColor(Color.FromRgb(160, 203, 135)); }

    public void SetColor(Color color)
    {
        updating = true;
        try
        {
            SelectedColor = Color.FromRgb(color.R, color.G, color.B);
            Red.Value = color.R; Green.Value = color.G; Blue.Value = color.B;
            HexText.Text = ThemeManager.Hex(SelectedColor);
            RefreshPreview();
        }
        finally { updating = false; }
    }
    void HexChanged(object sender, TextChangedEventArgs e)
    {
        if (updating || ApplyButton is null) return;
        if (ThemeManager.TryParseHex(HexText.Text, out var color))
        {
            var caret = HexText.CaretIndex + (HexText.Text.StartsWith('#') ? 0 : 1);
            SetColor(color); HexText.CaretIndex = Math.Min(caret, HexText.Text.Length);
        }
        else { ApplyButton.IsEnabled = false; ValidationHint.Text = "Use six hex digits: #RRGGBB."; }
    }
    void ChannelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!updating && ApplyButton is not null) SetColor(Color.FromRgb((byte)Red.Value, (byte)Green.Value, (byte)Blue.Value));
    }
    void RefreshPreview()
    {
        ColorPreview.Background = new SolidColorBrush(SelectedColor);
        RedValue.Text = SelectedColor.R.ToString(); GreenValue.Text = SelectedColor.G.ToString(); BlueValue.Text = SelectedColor.B.ToString();
        Red.Background = Gradient(Color.FromRgb(0, SelectedColor.G, SelectedColor.B), Color.FromRgb(255, SelectedColor.G, SelectedColor.B));
        Green.Background = Gradient(Color.FromRgb(SelectedColor.R, 0, SelectedColor.B), Color.FromRgb(SelectedColor.R, 255, SelectedColor.B));
        Blue.Background = Gradient(Color.FromRgb(SelectedColor.R, SelectedColor.G, 0), Color.FromRgb(SelectedColor.R, SelectedColor.G, 255));
        ApplyButton.IsEnabled = true; ValidationHint.Text = "Enter #RRGGBB or adjust RGB.";
    }
    static Brush Gradient(Color from, Color to) => new LinearGradientBrush(from, to, new Point(0, .5), new Point(1, .5));
    void ApplyClick(object sender, RoutedEventArgs e)
    {
        if (ThemeManager.TryParseHex(HexText.Text, out _)) ApplyRequested?.Invoke(this, new RoutedEventArgs());
    }
}
