using System.ComponentModel;
using System.Windows;
using CompactLoader.Interop;

namespace CompactLoader;

public partial class OutputWindow : Window
{
    internal bool AllowClose { get; set; }

    public OutputWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);
        Closing += OnClosing;
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose) return;
        e.Cancel = true;
        Hide();
    }

    void CloseClick(object sender, RoutedEventArgs e) => Close();
}
