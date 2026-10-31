namespace CompactLoader;

internal sealed class SessionPreferences
{
    public bool MainWindowsOnly { get; set; }
    public List<string> RecentLibraries { get; set; } = new();
}