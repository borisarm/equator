using System.Globalization;
using System.Text.Json;
using Equator.App.Controls;
using Equator.App.Localization;
using Equator.App.Services;
using Equator.Core.Engine;
using Equator.Core.Printing;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace Equator.App;

public sealed partial class MainWindow : Window
{
    private const long MaxOpenFileBytes = 2L * 1024 * 1024;
    private readonly KernelHost _host = new();
    private readonly Licensing _license;
    private readonly Task _licenseReady;
    private CellControl? _activeCell;
    private string? _filePath;
    private bool _dirty;
    private int _running;
    private double _zoom = 1.0;
    private const double BaseMathSize = 19, BaseInputSize = 15;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Equator.ico"));
        // Diagnostics and Store screenshots: EQUATOR_WINDOW_SIZE=1942x1092, EQUATOR_ZOOM=1.2, EQUATOR_SIDE_TAB=guide.
        if (Environment.GetEnvironmentVariable("EQUATOR_WINDOW_SIZE")?.Split('x') is [var sw, var sh]
            && int.TryParse(sw, out var width) && int.TryParse(sh, out var height))
            ResizeAndCenter(width, height);
        else
            ResizeAndCenter(1440, 940);
        if (double.TryParse(Environment.GetEnvironmentVariable("EQUATOR_ZOOM"), CultureInfo.InvariantCulture, out var zoom))
            _zoom = Math.Clamp(zoom, 0.6, 2.2);
        if (Environment.GetEnvironmentVariable("EQUATOR_SIDE_TAB") is "guide" or "variables")
            RootGrid.Loaded += (_, _) => SidePanelSelector.SelectedItem =
                Environment.GetEnvironmentVariable("EQUATOR_SIDE_TAB") == "guide" ? GuideTab : VariablesTab;

        // Read the Store license before anything evaluates (EvaluateCellAsync waits for it).
        _license = new Licensing(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _license.Changed += (_, _) => DispatcherQueue.TryEnqueue(async () => await RefreshLicenseAsync());
        _licenseReady = RefreshLicenseAsync();
        LicenseBar.Closed += (_, _) => _licenseBarDismissed = true;

        _host.Kernel.EnvironmentChanged += OnEnvironmentChanged;
        BuildSymbolPalette();
        BuildLanguageMenu();
        ApplyLanguage();
        Loc.Changed += (_, _) => ApplyLanguage();

        if (StartupFile() is { } startupFile) _ = OpenPathAsync(startupFile, evaluate: true);
        else LoadCells(Examples.Welcome, evaluate: true);
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => _host.Dispose();
        if (Environment.GetEnvironmentVariable("EQUATOR_THEME") is "dark" or "light")
            RootGrid.RequestedTheme = Environment.GetEnvironmentVariable("EQUATOR_THEME") == "dark" ? ElementTheme.Dark : ElementTheme.Light;
        if (Environment.GetEnvironmentVariable("EQUATOR_SNAPSHOT") is { Length: > 0 } snapPath)
        {
            _snapshotTimer = DispatcherQueue.CreateTimer();
            _snapshotTimer.Interval = TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("EQUATOR_SNAPSHOT_DELAY"), out var delay) ? delay : 7);
            _snapshotTimer.IsRepeating = false;
            _snapshotTimer.Tick += async (_, _) => await SnapshotAsync(snapPath);
            _snapshotTimer.Start();
        }
    }

    private static bool IsSupportedOpenFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".eqnb", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateOpenFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new IOException("No file path provided.");

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("File not found.", fullPath);

        var info = new FileInfo(fullPath);
        if ((info.Attributes & FileAttributes.Directory) != 0) throw new IOException("The path is not a file.");
        if (!IsSupportedOpenFile(fullPath)) throw new IOException("Unsupported file type.");
        if (info.Length > MaxOpenFileBytes) throw new IOException("The file exceeds the 2 MB size limit.");
    }

    private static void ValidateTargetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new IOException("No file path provided.");
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath)) throw new IOException("The target path is a directory.");
        if (fullPath.Length == 0 || fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length == 0)
            throw new IOException("The target path is invalid.");
    }

    /// <summary>
    /// The notebook to open at startup: from file activation (double-clicking an .eqnb when installed as a
    /// package) or from the command line (unpackaged).
    /// </summary>
    private static string? StartupFile()
    {
        try
        {
            var activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File
                && activation.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs
                && fileArgs.Files.FirstOrDefault() is { } item && File.Exists(item.Path))
                return item.Path;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            App.Log("activation arguments unavailable: " + ex.Message);
        }

        var args = Environment.GetCommandLineArgs();
        if (args.Length <= 1) return null;
        var candidate = args[1];
        if (!IsSupportedOpenFile(candidate)) return null;
        try
        {
            var fullPath = Path.GetFullPath(candidate);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ResizeAndCenter(int width, int height)
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
        int w = Math.Min((int)(width * scale), area.Width - 40), h = Math.Min((int)(height * scale), area.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    private double MathSize => BaseMathSize * _zoom;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _snapshotTimer;

    private async Task OpenPathAsync(string path, bool evaluate)
    {
        try
        {
            ValidateOpenFile(path);
            var text = await File.ReadAllTextAsync(path);
            var inputs = Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase)
                ? text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList()
                : NotebookFile.Parse(text);
            _filePath = path;
            await _host.RunAsync(_host.Kernel.Reset);
            LoadCells(inputs, evaluate);
            if (!evaluate) SetStatus(Loc.Format("status.opened", Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LoadCells([], evaluate: false);
            SetStatus(Loc.Format("status.openFailed", ex.Message), error: true);
        }
    }

    private async Task<bool> SaveAsync(bool saveAs = false)
    {
        if (_filePath is null || saveAs)
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = Loc.Get("file.suggestedName") };
            picker.FileTypeChoices.Add(Loc.Get("file.type"), [".eqnb"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return false;
            _filePath = file.Path;
        }
        try
        {
            ValidateTargetPath(_filePath);
            await File.WriteAllTextAsync(_filePath, NotebookFile.Serialize(Cells.Select(c => c.Text)));
            _dirty = false;
            UpdateTitle();
            SetStatus(Loc.Format("status.saved", Path.GetFileName(_filePath)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(Loc.Format("status.saveFailed", ex.Message), error: true);
            return false;
        }
    }

    private async Task SnapshotAsync(string basePath) { /* existing code unchanged */ }
    private async Task SnapshotPageAsync(string path) { /* existing code unchanged */ }
    private IEnumerable<CellControl> Cells => CellsPanel.Children.OfType<CellControl>();
    private CellControl CreateCell(string text)
    {
        var cell = new CellControl(text);
        cell.InputFontSize = BaseInputSize * _zoom;
        cell.Command += OnCellCommand;
        cell.Focused += (_, _) => _activeCell = cell;
        cell.UseAsInput += (s, text2) => { var c = InsertCell(IndexOf((CellControl)s!) + 1, text2); c.FocusInput(); };
        cell.ReferenceRequested += (_, reference) => InsertIntoActive(reference);
        cell.TextChanged += (_, _) => MarkDirty();
        return cell;
    }

    private int IndexOf(CellControl cell) => CellsPanel.Children.IndexOf(cell);
    private CellControl InsertCell(int index, string text = "")
    {
        var cell = CreateCell(text);
        CellsPanel.Children.Insert(Math.Clamp(index, 0, CellsPanel.Children.Count), cell);
        MarkDirty();
        return cell;
    }
    private void RemoveCell(CellControl cell)
    {
        int idx = IndexOf(cell);
        CellsPanel.Children.Remove(cell);
        if (CellsPanel.Children.Count == 0) InsertCell(0);
        var next = (CellControl)CellsPanel.Children[Math.Min(idx, CellsPanel.Children.Count - 1)];
        next.FocusInput();
        MarkDirty();
    }
    private void LoadCells(IEnumerable<string> inputs, bool evaluate)
    {
        CellsPanel.Children.Clear();
        foreach (var input in inputs) CellsPanel.Children.Add(CreateCell(input));
        if (CellsPanel.Children.Count == 0 || ((CellControl)CellsPanel.Children[^1]).Text.Length > 0)
            CellsPanel.Children.Add(CreateCell(""));
        _dirty = false;
        UpdateTitle();
        if (evaluate) _ = EvaluateAllAsync(askToBuy: false);
        DispatcherQueue.TryEnqueue(() => ((CellControl)CellsPanel.Children[^1]).FocusInput());
    }
    private async void OnCellCommand(object? sender, CellCommand command)
    {
        var cell = (CellControl)sender!;
        int idx = IndexOf(cell);
        switch (command)
        {
            case CellCommand.Evaluate:
                await EvaluateCellAsync(cell);
                break;
            case CellCommand.EvaluateAndAdvance:
                {
                    var next = idx + 1 < CellsPanel.Children.Count ? (CellControl)CellsPanel.Children[idx + 1] : InsertCell(idx + 1);
                    next.FocusInput();
                    await EvaluateCellAsync(cell);
                    next.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0.6 });
                    break;
                }
            case CellCommand.EvaluateAndInsert:
                {
                    var next = InsertCell(idx + 1);
                    next.FocusInput();
                    await EvaluateCellAsync(cell);
                    break;
                }
            case CellCommand.Delete:
                RemoveCell(cell);
                break;
            case CellCommand.InsertAbove:
                InsertCell(idx).FocusInput();
                break;
            case CellCommand.InsertBelow:
                InsertCell(idx + 1).FocusInput();
                break;
            case CellCommand.MoveUp when idx > 0:
                CellsPanel.Children.RemoveAt(idx);
                CellsPanel.Children.Insert(idx - 1, cell);
                cell.FocusInput();
                MarkDirty();
                break;
            case CellCommand.MoveDown when idx < CellsPanel.Children.Count - 1:
                CellsPanel.Children.RemoveAt(idx);
                CellsPanel.Children.Insert(idx + 1, cell);
                cell.FocusInput();
                MarkDirty();
                break;
            case CellCommand.FocusPrevious when idx > 0:
                ((CellControl)CellsPanel.Children[idx - 1]).FocusInput();
                break;
            case CellCommand.FocusNext when idx < CellsPanel.Children.Count - 1:
                ((CellControl)CellsPanel.Children[idx + 1]).FocusInput(atEnd: false);
                break;
        }
    }
    private async Task EvaluateCellAsync(CellControl cell)
    {
        var text = cell.Text;
        if (string.IsNullOrWhiteSpace(text)) { cell.ClearOutputs(); return; }
        await _licenseReady;
        if (!_license.CanEvaluate) { await ShowTrialEndedAsync(); return; }
        cell.SetBusy(true);
        BeginRunning();
        try
        {
            var result = await _host.EvaluateAsync(text);
            cell.SetBusy(false);
            cell.ShowResult(result, MathSize);
            bool failed = result.Outputs.Any(o => o.Kind == OutputKind.Error);
            SetStatus(failed ? Loc.Format("status.stepError", result.Index) : Loc.Format("status.stepDone", result.Index, FormatTime(result.Elapsed)), failed);
        }
        finally
        {
            EndRunning();
        }
    }
    private async Task EvaluateAllAsync(bool askToBuy = true)
    {
        await _licenseReady;
        if (!_license.CanEvaluate)
        {
            if (askToBuy) await ShowTrialEndedAsync();
            return;
        }
        foreach (var cell in Cells.ToList())
            await EvaluateCellAsync(cell);
    }
    private void BeginRunning() { /* existing code unchanged */ }
    private void EndRunning() { /* existing code unchanged */ }
    private void SetStatus(string text, bool error = false) { /* existing code unchanged */ }
    private static string FormatTime(TimeSpan t) => t.TotalSeconds < 1 ? $"{t.TotalMilliseconds:0} ms" : t.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";
    private void SidePanelSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) { /* existing code unchanged */ }
    private static readonly string[][] SymbolRows =
    [
        ["π", "e", "I", "∞", "√", "^", "!", "'"],
        ["α", "β", "γ", "θ", "λ", "μ", "σ", "φ"],
        [":=", "=", "<", "≤", "[ ]", "( )", "%", ";"],
    ];
    private static readonly Dictionary<string, string> SymbolInsert = new() { ["√"] = "sqrt(", ["[ ]"] = "[", ["( )"] = "(", ["∞"] = "oo", ["≤"] = "<=", [":="] = " := ", ["="] = " = " };
    private void BuildSymbolPalette() { /* existing code unchanged */ }
    private void InsertIntoActive(string text) { /* existing code unchanged */ }
    private void BuildFunctionList(string filter) { /* existing code unchanged */ }
    private void FunctionSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => BuildFunctionList(sender.Text.Trim());
    private void FunctionList_ItemClick(object sender, ItemClickEventArgs e) { /* existing code unchanged */ }
    private void InsertExample(string example) { /* existing code unchanged */ }
    private void OnEnvironmentChanged(object? sender, EventArgs e) { /* existing code unchanged */ }
    private static Equator.Core.Expr SafeDisplay(Kernel kernel, UserFunction f) { /* existing code unchanged */ }
    private static string Shorten(string s) => s.Length > 160 ? s[..160] + "…" : s;
    private List<(string Name, string Value)> _variables = [];
    private void RefreshVariables(List<(string Name, string Value)> items) { /* existing code unchanged */ }
    private void BuildGuide() { /* existing code unchanged */ }
    private void MarkDirty() { /* existing code unchanged */ }
    private void UpdateTitle() { /* existing code unchanged */ }
    private async Task<bool> ConfirmDiscardAsync() { /* existing code unchanged */ }
    private async Task NewNotebookAsync() { /* existing code unchanged */ }
    private async Task OpenAsync() { /* existing code unchanged */ }
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args) { /* existing code unchanged */ }
    private void SetZoom(double zoom) { /* existing code unchanged */ }
    private void ToggleTheme() { /* existing code unchanged */ }
    private bool _licenseBarDismissed, _trialDialogOpen;
    private async Task RefreshLicenseAsync() { /* existing code unchanged */ }
    private void UpdateLicenseBar() { /* existing code unchanged */ }
    private async Task ShowTrialEndedAsync() { /* existing code unchanged */ }
    private async Task BuyAsync() { /* existing code unchanged */ }
    private async void Buy_Click(object sender, RoutedEventArgs e) => await BuyAsync();
    private void BuildLanguageMenu() { /* existing code unchanged */ }
    private static string DisplayLanguage => Loc.Current == "zh-Hans" ? "zh-Hans" : "en-US";
    private void ApplyLanguage() { /* existing code unchanged */ }
    private async void New_Click(object sender, RoutedEventArgs e) => await NewNotebookAsync();
    private async void Open_Click(object sender, RoutedEventArgs e) => await OpenAsync();
    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();
    private async void EvaluateAll_Click(object sender, RoutedEventArgs e) => await EvaluateAllAsync();
    private void Abort_Click(object sender, RoutedEventArgs e) => _host.Abort();
    private void ClearOutputs_Click(object sender, RoutedEventArgs e) { foreach (var c in Cells) c.ClearOutputs(); }
    private void AddCell_Click(object sender, RoutedEventArgs e) => InsertCell(CellsPanel.Children.Count).FocusInput();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.1);
    private void Theme_Click(object sender, RoutedEventArgs e) => ToggleTheme();
    private void Help_Click(object sender, RoutedEventArgs e) => _ = ShowShortcutsAsync();
    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        await _host.RunAsync(_host.Kernel.Reset);
        foreach (var c in Cells) c.ClearOutputs();
        SetStatus(Loc.Get("status.reset"));
    }
    private async void Examples_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardAsync()) return;
        _filePath = null;
        await _host.RunAsync(_host.Kernel.Reset);
        LoadCells(Examples.Welcome, evaluate: true);
    }
    private async Task ShowShortcutsAsync() { /* existing code unchanged */ }
    private async void NewAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await NewNotebookAsync(); }
    private async void OpenAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await OpenAsync(); }
    private async void SaveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await SaveAsync(); }
    private async void EvaluateAllAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await EvaluateAllAsync(); }
    private void AbortAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; _host.Abort(); }
    private void ZoomInAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; SetZoom(_zoom * 1.1); }
    private void ZoomOutAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; SetZoom(_zoom / 1.1); }
    private void HelpAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; _ = ShowShortcutsAsync(); }
}
