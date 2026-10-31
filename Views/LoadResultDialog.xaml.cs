using System.Windows;
using CompactLoader.Interop;

namespace CompactLoader;

public partial class LoadResultDialog : Window
{
    public LoadResultDialog(Window owner, string heading, string details)
    {
        InitializeComponent();
        Owner = owner;
        ResultHeading.Text = heading;
        ResultDetails.Text = details;
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);
    }

    void CloseClick(object sender, RoutedEventArgs e) => Close();
}
