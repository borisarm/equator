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
    private readonly KernelHost _host = new();
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
        ResizeAndCenter(1440, 940);

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
        return args.Length > 1 && File.Exists(args[1]) ? args[1] : null;
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

    /// <summary>Diagnostics: renders the window content to a PNG (set EQUATOR_SNAPSHOT=path; EQUATOR_SNAPSHOT_DELAY=seconds).</summary>
    private async Task SnapshotAsync(string basePath)
    {
        int count = int.TryParse(Environment.GetEnvironmentVariable("EQUATOR_SNAPSHOT_PAGES"), out var n) ? n : 1;
        for (int page = 0; page < count; page++)
        {
            NotebookScroller.ChangeView(null, page * NotebookScroller.ViewportHeight * 0.9, null, true);
            await Task.Delay(900);
            await SnapshotPageAsync(count == 1 ? basePath : basePath.Replace(".png", $"{page}.png"));
        }
    }

    private async Task SnapshotPageAsync(string path)
    {
        try
        {
            // The Mica backdrop is not part of the visual tree; paint a solid background while capturing.
            var previous = RootGrid.Background;
            RootGrid.Background = new SolidColorBrush(RootGrid.ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243));
            await Task.Delay(100);
            var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await rtb.RenderAsync(RootGrid);
            RootGrid.Background = previous;
            var pixels = await rtb.GetPixelsAsync();
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
            await encoder.FlushAsync();
            var bytes = new byte[stream.Size];
            using var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            await File.WriteAllBytesAsync(path, bytes);
            App.Log("snapshot saved " + path);
        }
        catch (Exception ex)
        {
            App.Log("snapshot failed: " + ex);
        }
    }

    // ---- cells --------------------------------------------------------------------------------------

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
        if (evaluate) _ = EvaluateAllAsync();
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

    // ---- evaluation --------------------------------------------------------------------------------

    private async Task EvaluateCellAsync(CellControl cell)
    {
        var text = cell.Text;
        if (string.IsNullOrWhiteSpace(text)) { cell.ClearOutputs(); return; }
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

    private async Task EvaluateAllAsync()
    {
        foreach (var cell in Cells.ToList())
            await EvaluateCellAsync(cell);
    }

    private void BeginRunning()
    {
        _running++;
        AbortButton.IsEnabled = true;
        StatusDot.Fill = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        StatusText.Text = Loc.Get("status.running");
    }

    private void EndRunning()
    {
        _running = Math.Max(0, _running - 1);
        if (_running == 0)
        {
            AbortButton.IsEnabled = false;
            StatusDot.Fill = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
        }
    }

    private void SetStatus(string text, bool error = false)
    {
        if (_running > 1) return;
        StatusText.Text = text;
        if (error) StatusDot.Fill = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalSeconds < 1 ? $"{t.TotalMilliseconds:0} ms" : t.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";

    // ---- side panel ----------------------------------------------------------------------------------

    private void SidePanelSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        FunctionsPanel.Visibility = sender.SelectedItem == FunctionsTab ? Visibility.Visible : Visibility.Collapsed;
        VariablesPanel.Visibility = sender.SelectedItem == VariablesTab ? Visibility.Visible : Visibility.Collapsed;
        GuidePanel.Visibility = sender.SelectedItem == GuideTab ? Visibility.Visible : Visibility.Collapsed;
    }

    private static readonly string[][] SymbolRows =
    [
        ["π", "e", "I", "∞", "√", "^", "!", "'"],
        ["α", "β", "γ", "θ", "λ", "μ", "σ", "φ"],
        [":=", "=", "<", "≤", "[ ]", "( )", "%", ";"],
    ];

    private static readonly Dictionary<string, string> SymbolInsert = new()
    {
        ["√"] = "sqrt(", ["[ ]"] = "[", ["( )"] = "(", ["∞"] = "oo", ["≤"] = "<=", [":="] = " := ", ["="] = " = ",
    };

    private void BuildSymbolPalette()
    {
        foreach (var row in SymbolRows)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var sym in row)
            {
                var b = new Button { Content = sym, Style = (Style)Application.Current.Resources["SymbolButton"] };
                if (sym.Length > 1) b.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                b.Click += (_, _) => InsertIntoActive(SymbolInsert.GetValueOrDefault(sym, sym));
                ToolTipService.SetToolTip(b, Loc.Format("side.insertSymbol", SymbolInsert.GetValueOrDefault(sym, sym).Trim()));
                panel.Children.Add(b);
            }
            SymbolsPanel.Children.Add(panel);
        }
    }

    private void InsertIntoActive(string text)
    {
        var cell = _activeCell ?? Cells.LastOrDefault() ?? InsertCell(0);
        cell.InsertText(text);
    }

    private void BuildFunctionList(string filter)
    {
        FunctionList.Items.Clear();
        string? category = null;
        foreach (var b in Builtins.All)
        {
            var description = Loc.GetOr("fn." + b.Name, b.Description);
            if (filter.Length > 0 && !b.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !description.Contains(filter, StringComparison.CurrentCultureIgnoreCase) &&
                !b.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (b.Category != category)
            {
                category = b.Category;
                FunctionList.Items.Add(new ListViewItem
                {
                    Content = new TextBlock { Text = Loc.GetOr("cat." + category, category).ToUpper(CultureInfo.CurrentCulture), FontSize = 11, CharacterSpacing = 60, Style = (Style)Application.Current.Resources["AccentText"] },
                    IsEnabled = false,
                    MinHeight = 28,
                    Padding = new Thickness(12, 10, 0, 2),
                });
            }
            var panel = new StackPanel { Padding = new Thickness(0, 6, 0, 6), Spacing = 1, Tag = b };
            panel.Children.Add(new TextBlock { Text = b.Usage, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = Loc.IsRightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left });
            panel.Children.Add(new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["SecondaryText"] });
            var item = new ListViewItem { Content = panel, Tag = b };
            ToolTipService.SetToolTip(item, Loc.Format("side.insertExample", b.Example));
            FunctionList.Items.Add(item);
        }
    }

    private void FunctionSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        BuildFunctionList(sender.Text.Trim());

    private void FunctionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        var builtin = e.ClickedItem switch
        {
            ListViewItem { Tag: Builtin b } => b,
            FrameworkElement { Tag: Builtin b } => b,
            _ => null,
        };
        if (builtin is not null) InsertExample(builtin.Example);
    }

    private void InsertExample(string example)
    {
        var cell = _activeCell ?? Cells.Last();
        if (cell.Text.Trim().Length > 0)
        {
            var empty = Cells.FirstOrDefault(c => IndexOf(c) > IndexOf(cell) && c.Text.Trim().Length == 0 && !c.HasOutput);
            cell = empty ?? InsertCell(IndexOf(cell) + 1);
        }
        cell.Text = example;
        cell.FocusInput();
        cell.StartBringIntoView();
    }

    private void OnEnvironmentChanged(object? sender, EventArgs e)
    {
        // Runs on the kernel thread: take a snapshot here, then update the UI.
        var kernel = (Kernel)sender!;
        var items = new List<(string Name, string Value)>();
        foreach (var (name, value) in kernel.Variables.OrderBy(v => v.Key))
            items.Add((name, Shorten(value is GraphicsExpr ? "(graphics)" : TextFormatter.Format(value))));
        foreach (var f in kernel.UserFunctions.Values.OrderBy(f => f.Name))
            items.Add(($"{f.Name}({string.Join(", ", f.Parameters)})", Shorten(TextFormatter.Format(f.Body.Args.Count == 0 ? f.Body : SafeDisplay(kernel, f)))));
        DispatcherQueue.TryEnqueue(() => RefreshVariables(items));
    }

    private static Equator.Core.Expr SafeDisplay(Kernel kernel, UserFunction f)
    {
        try { return kernel.WithShadowed(f.Parameters, () => kernel.Eval(f.Body)); }
        catch (Exception) { return new Equator.Core.StrExpr("…"); }
    }

    private static string Shorten(string s) => s.Length > 160 ? s[..160] + "…" : s;

    private List<(string Name, string Value)> _variables = [];

    private void RefreshVariables(List<(string Name, string Value)> items)
    {
        _variables = items;
        VariableList.Items.Clear();
        if (items.Count == 0)
        {
            VariableList.Items.Add(new TextBlock
            {
                Text = Loc.Get("side.nothing"),
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["SecondaryText"],
                Margin = new Thickness(0, 8, 0, 0),
            });
            return;
        }
        foreach (var (name, value) in items)
        {
            var panel = new StackPanel { Padding = new Thickness(0, 6, 0, 6), FlowDirection = FlowDirection.LeftToRight };
            panel.Children.Add(new TextBlock { Text = name, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = value, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["SecondaryText"] });
            VariableList.Items.Add(panel);
        }
    }

    private void BuildGuide()
    {
        GuideContent.Children.Clear();
        foreach (var (id, examples) in Examples.Guide)
        {
            var title = Loc.Get($"guide.{id}.title");
            var body = Loc.Get($"guide.{id}.body");
            GuideContent.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["PanelHeader"] });
            if (body.Length > 0)
                GuideContent.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, FontSize = 13, Style = (Style)Application.Current.Resources["SecondaryText"] });
            foreach (var ex in examples)
            {
                var b = new HyperlinkButton
                {
                    Content = new TextBlock { Text = ex, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.LeftToRight },
                    Padding = new Thickness(6, 3, 6, 3),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                b.Click += (_, _) => InsertExample(ex);
                GuideContent.Children.Add(b);
            }
        }
    }

    // ---- files ---------------------------------------------------------------------------------------

    private void MarkDirty()
    {
        if (_dirty) return;
        _dirty = true;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var name = _filePath is null ? Loc.Get("doc.untitled") : Path.GetFileNameWithoutExtension(_filePath);
        DocumentTitle.Text = name + (_dirty ? " •" : "");
        Title = Loc.Format("window.title", name);
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!_dirty) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            FlowDirection = RootGrid.FlowDirection,
            Title = Loc.Get("save.title"),
            Content = Loc.Get("save.body"),
            PrimaryButtonText = Loc.Get("save.save"),
            SecondaryButtonText = Loc.Get("save.dontSave"),
            CloseButtonText = Loc.Get("save.cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) return await SaveAsync();
        return result == ContentDialogResult.Secondary;
    }

    private async Task NewNotebookAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        _filePath = null;
        await _host.RunAsync(_host.Kernel.Reset);
        LoadCells([], evaluate: false);
        SetStatus(Loc.Get("status.new"));
    }

    private async Task OpenAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".eqnb");
        picker.FileTypeFilter.Add(".txt");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        await OpenPathAsync(file.Path, evaluate: false);
    }

    private async Task OpenPathAsync(string path, bool evaluate)
    {
        try
        {
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

    private bool _closeConfirmed;

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_dirty || _closeConfirmed) return;
        args.Cancel = true;
        if (await ConfirmDiscardAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    // ---- zoom & theme ----------------------------------------------------------------------------------

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.6, 2.2);
        foreach (var cell in Cells)
        {
            cell.InputFontSize = BaseInputSize * _zoom;
            cell.Rerender(MathSize);
        }
        SetStatus(Loc.Format("status.zoom", Math.Round(_zoom * 100)));
    }

    private void ToggleTheme()
    {
        var current = RootGrid.ActualTheme;
        RootGrid.RequestedTheme = current == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        bool dark = RootGrid.RequestedTheme == ElementTheme.Dark;
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
    }

    // ---- language ------------------------------------------------------------------------------------

    private void BuildLanguageMenu()
    {
        LanguageMenu.Items.Clear();
        var system = new RadioMenuFlyoutItem { GroupName = "lang", Tag = "", IsChecked = Loc.Preference == "" };
        Tr.SetText(system, "lang.system");
        system.Click += (_, _) => Loc.SetPreference("");
        LanguageMenu.Items.Add(system);
        LanguageMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var (code, nativeName) in Loc.Languages)
        {
            // Each language is listed under its own name, so it can be found whatever language is showing.
            var item = new RadioMenuFlyoutItem { Text = nativeName, GroupName = "lang", Tag = code, IsChecked = Loc.Preference == code };
            item.Click += (_, _) => Loc.SetPreference(code);
            LanguageMenu.Items.Add(item);
        }
    }

    /// <summary>Applies the current language to everything built in code (XAML text follows by itself).</summary>
    private void ApplyLanguage()
    {
        RootGrid.FlowDirection = Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        RootGrid.Language = Loc.Current;
        // Cells hold maths: numbers there must look the way they are typed (7, not ٧), whatever the language.
        // Chinese keeps its own tag so Han characters in comments use Simplified Chinese glyphs.
        CellsPanel.Language = Loc.Current == "zh-Hans" ? "zh-Hans" : "en-US";
        BuildFunctionList(FunctionSearch.Text.Trim());
        BuildGuide();
        RefreshVariables(_variables);
        UpdateTitle();
        foreach (var cell in Cells) cell.Relocalize();
        if (_running == 0) StatusText.Text = Loc.Get("status.ready");
    }

    // ---- command handlers ----------------------------------------------------------------------------

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

    private async Task ShowShortcutsAsync()
    {
        // Key names as printed on keyboards in the interface language (Maj+Entrée, Mayús+Intro, ...).
        static string K(string keys) => string.Join("+", keys.Split('+').Select(k => k switch
        {
            "Shift" => Loc.Get("key.shift"),
            "Enter" => Loc.Get("key.enter"),
            "Backspace" => Loc.Get("key.backspace"),
            "Esc" => Loc.Get("key.esc"),
            _ => k,
        }));
        var text = string.Join("\n", new[]
        {
            K("Shift+Enter") + "\t" + Loc.Get("keys.run"),
            K("Ctrl+Enter") + "\t" + Loc.Get("keys.runStay"),
            K("Alt+Enter") + "\t" + Loc.Get("keys.runAdd"),
            "↑ / ↓\t" + Loc.Get("keys.move"),
            K("Ctrl+Shift+Backspace") + "\t" + Loc.Get("keys.delete"),
            "F5\t" + Loc.Get("keys.runAll"),
            K("Ctrl+Esc") + "\t" + Loc.Get("keys.stop"),
            "Ctrl+N / Ctrl+O / Ctrl+S\t" + Loc.Get("keys.files"),
            "Ctrl+Plus / Ctrl+Minus\t" + Loc.Get("keys.zoom"),
            "",
            Loc.Get("keys.plots"),
            Loc.Get("keys.copy"),
        });
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            FlowDirection = RootGrid.FlowDirection,
            Title = Loc.Get("keys.title"),
            Content = new TextBlock { Text = text, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = Loc.Get("keys.close"),
        };
        await dialog.ShowAsync();
    }

    private async void NewAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await NewNotebookAsync(); }
    private async void OpenAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await OpenAsync(); }
    private async void SaveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await SaveAsync(); }
    private async void EvaluateAllAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await EvaluateAllAsync(); }
    private void AbortAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; _host.Abort(); }
    private void ZoomInAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; SetZoom(_zoom * 1.1); }
    private void ZoomOutAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; SetZoom(_zoom / 1.1); }
    private void HelpAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; _ = ShowShortcutsAsync(); }
}
