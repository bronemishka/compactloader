using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using CompactLoader.Interop;

namespace CompactLoader;

public partial class MainWindow : Window
{
    List<ProcessListItem> targets = new();
    string? dllPath;
    readonly string sessionPreferencesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CompactLoader", "session.json");
    SessionPreferences sessionPreferences = new();
    bool sessionReady;
    readonly System.Threading.SemaphoreSlim sessionSaveGate = new(1, 1);
    Task pendingSessionSave = Task.CompletedTask;
    Task pendingLogSave = Task.CompletedTask;
    bool closingAfterSave;
    int fileSelectionVersion;
    bool busy;
    bool refreshing;
    ProcessListItem? selectedProcess;
    ProcessListItem? selectionBeforePicker;
    int logEntries;
    readonly OutputWindow output;
    TextBox Log => output.Log;
    bool systemAccentRefreshQueued;
    bool appearanceClosed;
    public MainWindow()
    {
        InitializeComponent();
        output = new OutputWindow();
        output.Log.TextChanged += LogTextChanged;
        output.SaveButton.Click += SaveLogClick;
        output.CopyButton.Click += CopyLogClick;
        output.ClearButton.Click += ClearLogClick;
        var version = typeof(MainWindow).Assembly.GetName().Version!;
        AppVersion.Text = $"v{version.Major}.{version.Minor}.{version.Build}";
        InitializeAppearanceControls();
        SelectedIcon.Source = ProcessIconProvider.Get(-1);
        Architecture.Text = Environment.Is64BitProcess ? "win / x64" : "win / x86";
        Loaded += async (_, _) =>
        {
            SetBusy(true);
            try { await RestoreSession(); }
            finally { SetBusy(false); }
            if (!appearanceClosed) await RefreshTargets();
        };
        PreviewMouseDown += DismissPickersOnOutsideClick;
        Deactivated += (_, _) => ClosePickers();
        LocationChanged += (_, _) => ClosePickers();
        SizeChanged += (_, _) => ClosePickers();
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);
        SystemParameters.StaticPropertyChanged += SystemAccentChanged;
        Closed += (_, _) =>
        {
            output.AllowClose = true;
            output.Close();
            appearanceClosed = true;
            SystemParameters.StaticPropertyChanged -= SystemAccentChanged;
        };
    }
    void MinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    void CloseClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    void ClosePickers()
    {
        ProcessPickerToggle.IsChecked = false;
        AccentPickerToggle.IsChecked = false;
    }
    void DismissPickersOnOutsideClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (ProcessPicker.IsOpen && !IsWithin(source, ProcessPickerToggle) && !IsWithin(source, ProcessPicker.Child))
            ProcessPickerToggle.IsChecked = false;
        if (AccentPicker.IsOpen && !IsWithin(source, AccentPickerToggle) && !IsWithin(source, AccentPicker.Child))
            AccentPickerToggle.IsChecked = false;
    }
    static bool IsWithin(DependencyObject? source, DependencyObject ancestor)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor)) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
    void InitializeAppearanceControls()
    {
        foreach (var option in ThemeManager.Accents)
        {
            var button = new Button { Tag = option.Name, ToolTip = option.Name, Style = (Style)FindResource("AccentSwatch") };
            System.Windows.Automation.AutomationProperties.SetName(button, option.Name + " accent");
            button.Click += AccentClick;
            AccentColors.Children.Add(button);
        }
        UpdateAppearanceControls();
    }
    void UpdateAppearanceControls()
    {
        WindowsAccentPreview.Background = new SolidColorBrush(ThemeManager.WindowsAccentColor);
        WindowsAccentState.Text = ThemeManager.AccentName == "Windows" ? "active" : "system";
        WindowsAccentButton.BorderBrush = (Brush)FindResource(ThemeManager.AccentName == "Windows" ? "AccentOutline" : "FrameBorder");
        if (ThemeManager.TryParseHex(ThemeManager.CustomAccentHex, out var customColor)) CustomAccentEditor.SetColor(customColor);
        AccentPickerToggle.ToolTip = ThemeManager.AccentName == "Custom" ? "Custom accent: " + ThemeManager.CustomAccentHex : "Accent color: " + ThemeManager.AccentName;
        ThemeToggleButton.ToolTip = ThemeManager.IsLight ? "Switch to dark theme" : "Switch to light theme";
        ThemeGlyph.Data = Geometry.Parse(ThemeManager.IsLight
            ? "M 8 4 A 4 4 0 1 0 8 12 A 4 4 0 1 0 8 4 M 8 0 L 8 2 M 8 14 L 8 16 M 0 8 L 2 8 M 14 8 L 16 8 M 2 2 L 3.5 3.5 M 12.5 12.5 L 14 14 M 2 14 L 3.5 12.5 M 12.5 3.5 L 14 2"
            : "M 12 1 A 7 7 0 1 0 15 12 A 6 6 0 0 1 12 1");
        foreach (Button button in AccentColors.Children)
        {
            var option = ThemeManager.Accents.First(a => a.Name == (string)button.Tag);
            button.Background = new SolidColorBrush(ThemeManager.Preview(option));
            button.BorderBrush = option.Name == ThemeManager.AccentName ? (Brush)FindResource("Text") : Brushes.Transparent;
        }
        WindowAppearance.Apply(this);
        WindowAppearance.Apply(output);
    }
    void SystemAccentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or "" or "WindowGlassColor" or "WindowGlassBrush")) return;
        if (appearanceClosed || systemAccentRefreshQueued || Dispatcher.HasShutdownStarted) return;
        systemAccentRefreshQueued = true;
        Dispatcher.BeginInvoke(new Action(RefreshSystemAccent));
    }
    void RefreshSystemAccent()
    {
        systemAccentRefreshQueued = false;
        if (appearanceClosed) return;
        // Reapply without writing preferences: Windows remains the selected mode,
        // rather than becoming a snapshot of the current system color.
        if (ThemeManager.AccentName == "Windows") ThemeManager.Apply(ThemeManager.IsLight, "Windows", false);
        UpdateAppearanceControls();
    }
    async void ToggleThemeClick(object sender, RoutedEventArgs e)
    {
        ThemeManager.Apply(!ThemeManager.IsLight, ThemeManager.AccentName, false);
        UpdateAppearanceControls();
        var saved = await ThemeManager.Save();
        if (!saved) Status.Text = "Theme applied. Could not save preferences.";
    }
    async void AccentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;
        ThemeManager.Apply(ThemeManager.IsLight, name, false);
        UpdateAppearanceControls();
        AccentPickerToggle.IsChecked = false;
        var saved = await ThemeManager.Save();
        if (!saved) Status.Text = "Accent applied. Could not save preferences.";
    }
    async void CustomAccentApply(object sender, RoutedEventArgs e)
    {
        ThemeManager.ApplyCustom(ThemeManager.Hex(CustomAccentEditor.SelectedColor), false);
        UpdateAppearanceControls();
        AccentPickerToggle.IsChecked = false;
        var saved = await ThemeManager.Save();
        if (!saved) Status.Text = "Accent applied. Could not save preferences.";
    }
    void AccentPickerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        AccentPickerToggle.IsChecked = false; AccentPickerToggle.Focus(); e.Handled = true;
    }
    void FocusSearch(object sender, ExecutedRoutedEventArgs e)
    {
        if (busy) return;
        ProcessPickerToggle.IsChecked = true;
        Search.Focus(); Search.SelectAll();
    }
    void ProcessPickerOpened(object sender, EventArgs e)
    {
        selectionBeforePicker = selectedProcess;
        Search.Dispatcher.BeginInvoke(new Action(() => { Search.Focus(); Search.SelectAll(); }));
    }
    void ProcessSelectorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || busy) return;
        ProcessPickerToggle.IsChecked = true; e.Handled = true;
    }
    void ProcessPickerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            selectedProcess = targets.FirstOrDefault(p => p.Id == selectionBeforePicker?.Id && p.Target.Started == selectionBeforePicker.Target.Started);
            UpdateSelectedProcess(); Filter();
            ProcessPickerToggle.IsChecked = false; ProcessPickerToggle.Focus(); e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            var item = Processes.SelectedItem as ProcessListItem ?? Processes.Items.OfType<ProcessListItem>().FirstOrDefault();
            if (item is not null) { selectedProcess = item; UpdateSelectedProcess(); ProcessPickerToggle.IsChecked = false; ProcessPickerToggle.Focus(); }
            e.Handled = true;
        }
        else if (e.Key == Key.Down && Search.IsKeyboardFocusWithin && Processes.Items.Count > 0)
        {
            if (Processes.SelectedIndex < 0) Processes.SelectedIndex = 0;
            Processes.Focus();
            (Processes.ItemContainerGenerator.ContainerFromIndex(Processes.SelectedIndex) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
    }
    void ProcessPicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(Processes, source) is ListBoxItem)
        { ProcessPickerToggle.IsChecked = false; ProcessPickerToggle.Focus(); }
    }
    void OpenLibrary(object sender, ExecutedRoutedEventArgs e) { if (!busy) BrowseClick(sender, e); }
    async void RefreshProcesses(object sender, ExecutedRoutedEventArgs e) => await RefreshTargets();
    void OpenOutputClick(object sender, RoutedEventArgs e) => ShowOutput();
    void ShowOutput()
    {
        if (!output.IsVisible)
        {
            var area = SystemParameters.WorkArea;
            output.Left = Math.Clamp(Left + ActualWidth + 10, area.Left, Math.Max(area.Left, area.Right - output.Width));
            output.Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - output.Height));
            output.Owner = this;
            output.Show();
        }
        output.Activate();
    }
    void LogTextChanged(object sender, TextChangedEventArgs e)
    {
        if (LogPreview is null) return;
        var lines = Log.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        LogPreview.Text = lines.Count == 0 ? "No activity yet." : lines[^1];
    }
    void ClearLogClick(object sender, RoutedEventArgs e) { Log.Clear(); logEntries = 0; LogCount.Text = "0"; }
    void CopyLogClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(Log.Text)) return;
        try { Clipboard.SetText(Log.Text); Status.Text = "Output copied."; }
        catch (System.Runtime.InteropServices.ExternalException) { Status.Text = "Clipboard is busy. Try again."; }
    }
    void MethodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MethodHint is null) return;
        MethodHint.Text = ((Method.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "LdrLoadDll" => "Native Windows loader through a small stub.",
            "ThreadHijacking" => "Uses an existing thread; temporarily changes its context.",
            "ManualMap" => "Experimental. No TLS, delay imports or unload.",
            _ => "Standard Windows loader."
        };
    }
    void Write(string text)
    {
        Log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        LogCount.Text = (++logEntries).ToString(); Log.ScrollToEnd();
    }
    async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshTargets();
    async Task RefreshTargets()
    {
        if (busy || refreshing) return;
        refreshing = true; RefreshButton.IsEnabled = false; UpdateAction(); Status.Text = "Enumerating processes…";
        try
        {
            targets = await Task.Run(() => ProcessCatalog.GetTargets()
                .Select(target => new ProcessListItem(target, ProcessIconProvider.Get(target.Id))).ToList());
            selectedProcess = targets.FirstOrDefault(p => p.Id == selectedProcess?.Id && p.Target.Started == selectedProcess.Target.Started);
            UpdateSelectedProcess();
            Filter(); Status.Text = $"{targets.Count} processes available.";
        }
        catch (Exception ex) { Write("Process enumeration failed: " + ex.Message); Status.Text = "Process enumeration failed"; ShowOutput(); }
        finally { refreshing = false; RefreshButton.IsEnabled = true; UpdateAction(); }
    }
    void SearchChanged(object sender, TextChangedEventArgs e) { if (Processes is not null) Filter(); }
    void WindowFilterChanged(object sender, RoutedEventArgs e)
    {
        if (Processes is not null) Filter();
        if (sessionReady) { sessionPreferences.MainWindowsOnly = MainWindowsOnly.IsChecked == true; SaveSession(); }
    }
    void Filter()
    {
        var selectedId = selectedProcess?.Id;
        var shown = targets.Where(p => (MainWindowsOnly.IsChecked != true || !string.IsNullOrWhiteSpace(p.Target.WindowTitle)) && $"{p.Name} {p.Id} {p.Target.WindowTitle}".Contains(Search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        Processes.ItemsSource = shown;
        Processes.SelectedItem = shown.FirstOrDefault(p => p.Id == selectedId);
        ProcessCount.Text = shown.Count == targets.Count ? $"{targets.Count}" : $"{shown.Count}/{targets.Count}";
    }
    void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML initialization can raise events before all named controls exist.
        if (SelectedName is null || LoadButton is null) return;
        if (Processes.SelectedItem is not ProcessListItem item) return;
        selectedProcess = item;
        UpdateSelectedProcess();
    }
    void UpdateSelectedProcess()
    {
        var target = selectedProcess?.Target;
        SelectedName.Text = target?.Name ?? "Select a process…";
        SelectedDetail.Text = target is null ? "Name / PID / architecture" : $"PID {target.Id}  /  {target.Architecture}";
        SelectedIcon.Source = selectedProcess?.Icon ?? ProcessIconProvider.Get(-1);
        UpdateAction();
    }
    void UpdateAction()
    {
        LoadButton.IsEnabled = !busy && !refreshing && selectedProcess is not null && dllPath is not null;
        ActionHint.Text = busy ? "Working. Check output." : refreshing ? "Refreshing processes…" :
            selectedProcess is null ? "Select a process to continue." :
            dllPath is null ? "Choose a DLL to continue." : "Ready to load.";
    }
    void BrowseClick(object sender, RoutedEventArgs e)
    {
        ProcessPickerToggle.IsChecked = false;
        var dialog = new OpenFileDialog { Filter = "Native DLL (*.dll)|*.dll", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) SelectFile(dialog.FileName);
    }
    async void SelectFile(string path)
    {
        if (busy) return;
        var version = ++fileSelectionVersion;
        var exists = await Task.Run(() => File.Exists(path));
        if (version != fileSelectionVersion || busy || appearanceClosed) return;
        if (!exists || !string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase)) { Write("Select an existing .dll file."); return; }
                dllPath = Path.GetFullPath(path); FileNameLabel.Text = dllPath; UpdateAction();
        sessionPreferences.RecentLibraries.RemoveAll(p => string.Equals(p, dllPath, StringComparison.OrdinalIgnoreCase));
        sessionPreferences.RecentLibraries.Insert(0, dllPath);
        sessionPreferences.RecentLibraries = sessionPreferences.RecentLibraries.Take(8).ToList();
        UpdateRecentLibraries(); SaveSession();
    }
    void FileDragOver(object sender, DragEventArgs e)
    {
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        e.Effects = !busy && files is { Length: 1 } && string.Equals(Path.GetExtension(files[0]), ".dll", StringComparison.OrdinalIgnoreCase) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    void FileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files) SelectFile(files[0]);
        e.Handled = true;
    }
    async void LoadClick(object sender, RoutedEventArgs e)
    {
        if (busy || refreshing || selectedProcess is not { Target: var target } || dllPath is null) return;
        if (!Enum.TryParse<LoadMethod>((Method.SelectedItem as ComboBoxItem)?.Tag as string, out var method)) return;
        var progress = new Progress<string>(message => { Write(message); Status.Text = message; });
        var path = dllPath; SetBusy(true); Status.Text = "Loading library…";
        string heading;
        string details;
        try
        {
            Write($"Loading library → {target.Name}, PID {target.Id} / {method}");
            details = await Task.Run(() => Loader.Load(target, path, method, progress));
            Write(details);
            heading = method == LoadMethod.ManualMap ? "Image initialization completed" : "Library load verified";
            Status.Text = heading;
        }
        catch (Exception ex)
        {
            Write("Error: " + ex.Message);
            heading = "Load not verified";
            details = ex.Message;
            Status.Text = "Load not verified. See output.";
            ShowOutput();
        }
        finally { SetBusy(false); }
        new LoadResultDialog(this, heading, $"{target.Name} · PID {target.Id}\n{System.IO.Path.GetFileName(path)} · {method}\n\n{details}").ShowDialog();
    }
    void SetBusy(bool value)
    {
        busy = value; Method.IsEnabled = Processes.IsEnabled = Search.IsEnabled = BrowseButton.IsEnabled = RefreshButton.IsEnabled = DropArea.AllowDrop = !value;
        RecentLibraries.IsEnabled = !value; MainWindowsOnly.IsEnabled = !value;
        InspectButton.IsEnabled = !value;
        ProcessPickerToggle.IsEnabled = !value;
        if (value) ProcessPickerToggle.IsChecked = false;
        LoadButton.Content = value ? "Loading…" : "Load DLL"; UpdateAction();
    }
    async Task RestoreSession()
    {
        sessionPreferences = await Task.Run(() =>
        {
            try
            {
                if (File.Exists(sessionPreferencesPath))
                    return System.Text.Json.JsonSerializer.Deserialize<SessionPreferences>(File.ReadAllText(sessionPreferencesPath)) ?? new();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
            return new SessionPreferences();
        });
        sessionPreferences.RecentLibraries = (sessionPreferences.RecentLibraries ?? new()).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        MainWindowsOnly.IsChecked = sessionPreferences.MainWindowsOnly;
        UpdateRecentLibraries(); sessionReady = true;
    }
    void SaveSession()
    {
        var snapshot = new SessionPreferences
        {
            MainWindowsOnly = sessionPreferences.MainWindowsOnly,
            RecentLibraries = sessionPreferences.RecentLibraries.ToList()
        };
        pendingSessionSave = SaveSessionSnapshot(snapshot);
    }
    async Task SaveSessionSnapshot(SessionPreferences snapshot)
    {
        await sessionSaveGate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sessionPreferencesPath)!);
                var temporary = sessionPreferencesPath + ".tmp";
                File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(snapshot));
                File.Move(temporary, sessionPreferencesPath, true);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = "Could not save session preferences."; }
        finally { sessionSaveGate.Release(); }
    }
    void UpdateRecentLibraries()
    {
        RecentLibraries.Items.Clear();
        RecentLibraries.Items.Add(new ComboBoxItem { Content = sessionPreferences.RecentLibraries.Count == 0 ? "No recent DLLs yet" : "Recent DLLs", IsEnabled = false });
        foreach (var path in sessionPreferences.RecentLibraries)
            RecentLibraries.Items.Add(new ComboBoxItem { Content = new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis }, ToolTip = path, Tag = path });
        RecentLibraries.SelectedIndex = 0;
        RecentLibraries.Visibility = Visibility.Visible;
    }
    async void RecentLibrarySelected(object sender, SelectionChangedEventArgs e)
    {
        if (busy || RecentLibraries.SelectedItem is not ComboBoxItem { Tag: string path }) return;
        var version = ++fileSelectionVersion;
        var exists = await Task.Run(() => File.Exists(path));
        if (version != fileSelectionVersion || busy || appearanceClosed) return;
        if (!exists)
        {
            Write("Recent DLL no longer exists: " + path);
            sessionPreferences.RecentLibraries.Remove(path); UpdateRecentLibraries(); SaveSession(); return;
        }
        SelectFile(path);
    }
    async void InspectDllClick(object sender, RoutedEventArgs e)
    {
        if (busy || refreshing) return;
        var path = dllPath;
        if (path is null)
        {
            var dialog = new OpenFileDialog { Filter = "DLL (*.dll)|*.dll", CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            path = dialog.FileName;
        }
        SetBusy(true);
        LoadButton.Content = "Inspecting…";
        Status.Text = "Reading DLL metadata…";
        try
        {
            var report = await Task.Run(() => DllInspector.Inspect(path));
            Write("DLL inspection:" + Environment.NewLine + report);
            ShowOutput();
            Status.Text = "DLL metadata inspected.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            Write("DLL inspection failed: " + ex.Message);
            ShowOutput();
            Status.Text = "DLL inspection failed.";
        }
        finally { SetBusy(false); }
    }
    async void SaveLogClick(object sender, RoutedEventArgs e)
    {
        if (!pendingLogSave.IsCompleted) { Status.Text = "Saving output…"; return; }
        if (string.IsNullOrEmpty(Log.Text)) { Status.Text = "No output to save."; return; }
        var dialog = new SaveFileDialog { Filter = "Text log (*.txt)|*.txt", FileName = $"CompactLoader-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog(this) != true) return;
        var text = Log.Text;
        var path = dialog.FileName;
        Status.Text = "Saving output…";
        try
        {
            pendingLogSave = Task.Run(() => File.WriteAllText(path, text));
            await pendingLogSave;
            Status.Text = "Output saved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Write("Could not save output: " + ex.Message); }
    }
    async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closingAfterSave) return;
        if (busy) { e.Cancel = true; Write("Wait for the current operation to complete."); return; }
        if (pendingSessionSave.IsCompleted && ThemeManager.PendingSave.IsCompleted && pendingLogSave.IsCompleted) return;
        e.Cancel = true;
        SetBusy(true);
        IsEnabled = false;
        try { await Task.WhenAll(pendingSessionSave, ThemeManager.PendingSave, pendingLogSave); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Write("Could not save output: " + ex.Message); }
        closingAfterSave = true;
        Close();
    }

}
