using Equator.App.Controls;
using Equator.App.Services;
using Equator.Core.Checking;
using Equator.Recognition;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;

namespace Equator.App;

/// <summary>
/// Correction mode: a page of handwritten work (photo, scan, pasted image or pen ink) is read line by line
/// with the on-device model, the lines are shown as editable steps, and every step is checked with the CAS.
/// </summary>
public sealed partial class CheckWindow : Window
{
    private sealed class StepRow
    {
        public required Grid Root { get; init; }
        public required FontIcon Icon { get; init; }
        public required TextBox Input { get; init; }
        public required TextBlock Message { get; init; }
        public int? SourceLine { get; set; }
    }

    private readonly List<StepRow> _rows = [];
    private readonly Action<IReadOnlyList<string>>? _insert;
    private IReadOnlyList<RecognizedLine> _lines = [];
    private CancellationTokenSource? _work;
    private CancellationTokenSource? _download;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _recheckTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _inkTimer;
    private bool _suppressRecheck;

    public CheckWindow(ElementTheme theme, Action<IReadOnlyList<string>>? insertIntoNotebook)
    {
        InitializeComponent();
        _insert = insertIntoNotebook;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Equator.ico"));
        RootGrid.RequestedTheme = theme;
        ResizeAndCenter(1280, 860);

        _recheckTimer = DispatcherQueue.CreateTimer();
        _recheckTimer.Interval = TimeSpan.FromMilliseconds(700);
        _recheckTimer.IsRepeating = false;
        _recheckTimer.Tick += async (_, _) => await CheckAsync();

        // Pen input: read the page shortly after the writer pauses.
        _inkTimer = DispatcherQueue.CreateTimer();
        _inkTimer.Interval = TimeSpan.FromMilliseconds(1600);
        _inkTimer.IsRepeating = false;
        _inkTimer.Tick += async (_, _) => { if (HandwritingService.ModelAvailable && Pad.HasInk) await ReadAsync(); };
        Pad.InkChanged += (_, _) => { _inkTimer.Stop(); if (Pad.HasInk) _inkTimer.Start(); };

        UpdateModelBar();
        AddRow("", null);
        Closed += (_, _) => { _work?.Cancel(); _download?.Cancel(); };
        Activated += OnFirstActivated;
    }

    private async void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        // Diagnostics and automation: EQUATOR_CHECK_IMAGE loads a page, EQUATOR_CHECK_SNAPSHOT renders the result.
        if (Environment.GetEnvironmentVariable("EQUATOR_CHECK_IMAGE") is { Length: > 0 } image && File.Exists(image))
            await OpenPathAsync(image);
        if (Environment.GetEnvironmentVariable("EQUATOR_CHECK_SNAPSHOT") is { Length: > 0 } snap)
        {
            await Task.Delay(1500);
            await SnapshotAsync(snap);
        }
    }

    private void ResizeAndCenter(int width, int height)
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min(width, area.Width - 40), h = Math.Min(height, area.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    // ---- model -------------------------------------------------------------------------------------

    private void UpdateModelBar()
    {
        bool available = HandwritingService.ModelAvailable;
        ModelBar.IsOpen = !available;
        ReadButton.IsEnabled = available;
        DownloadButton.Content = $"Download ({HandwritingService.DownloadMegabytes:0} MB)";
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        DownloadButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        ModelBar.Title = "Downloading the handwriting model…";
        _download = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(p => DownloadProgress.Value = p);
            await HandwritingService.DownloadAsync(progress, _download.Token);
            UpdateModelBar();
            SetStatus("Handwriting model installed. Recognition runs on this computer.");
            if (Pad.HasContent) await ReadAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
        {
            ModelBar.Title = "The download failed";
            ModelBar.Severity = InfoBarSeverity.Error;
            ModelBar.Message = ex.Message + " You can try again, or type the steps on the right.";
            DownloadButton.IsEnabled = true;
        }
        finally { DownloadProgress.Visibility = Visibility.Collapsed; }
    }

    // ---- loading pages -----------------------------------------------------------------------------

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".webp" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await OpenFileAsync(file);
    }

    private async Task OpenPathAsync(string path) => await OpenFileAsync(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)));

    private async Task OpenFileAsync(StorageFile file)
    {
        try
        {
            using var stream = await file.OpenReadAsync();
            await LoadPageAsync(stream, file.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            SetStatus("Could not open the image: " + ex.Message, error: true);
        }
    }

    private async Task LoadPageAsync(IRandomAccessStream stream, string name)
    {
        var (bgra, w, h) = await HandwritingService.DecodeAsync(stream);
        await Pad.SetPageAsync(bgra, w, h);
        _lines = [];
        SetStatus($"Loaded {name} ({w}×{h}).");
        if (HandwritingService.ModelAvailable) await ReadAsync();
    }

    private async void Paste_Click(object sender, RoutedEventArgs e) => await PasteAsync();

    private async Task PasteAsync()
    {
        var content = Clipboard.GetContent();
        if (content.Contains(StandardDataFormats.Bitmap))
        {
            var reference = await content.GetBitmapAsync();
            using var stream = await reference.OpenReadAsync();
            await LoadPageAsync(stream, "pasted image");
        }
        else if (content.Contains(StandardDataFormats.StorageItems))
        {
            var items = await content.GetStorageItemsAsync();
            if (items.OfType<StorageFile>().FirstOrDefault() is { } file) await OpenFileAsync(file);
        }
        else if (content.Contains(StandardDataFormats.Text))
        {
            // Typed or copied steps, one per line.
            var text = await content.GetTextAsync();
            SetSteps(text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => (l, (int?)null)).ToList());
            await CheckAsync();
        }
        else SetStatus("The clipboard has no image or text.", error: true);
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.OfType<StorageFile>().FirstOrDefault() is { } file) await OpenFileAsync(file);
    }

    private void Draw_Click(object sender, RoutedEventArgs e)
    {
        Pad.Clear();
        _lines = [];
        SetSteps([("", null)]);
        SummaryBar.IsOpen = false;
        SetStatus("Write the task on the first line and one step per line below it.");
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Pad.Undo();

    // ---- recognition -------------------------------------------------------------------------------

    private async void Read_Click(object sender, RoutedEventArgs e) => await ReadAsync();

    private async Task ReadAsync()
    {
        if (!HandwritingService.ModelAvailable) { UpdateModelBar(); return; }
        var capture = Pad.Capture();
        if (capture is null) { SetStatus("There is nothing to read yet.", error: true); return; }
        _work?.Cancel();
        _work = new CancellationTokenSource();
        var token = _work.Token;
        Busy.IsActive = true;
        SetStatus("Reading handwriting…");
        try
        {
            var started = DateTime.Now;
            var (lines, steps) = await HandwritingService.ReadAsync(capture.Value.Image, capture.Value.Clean, token);
            if (token.IsCancellationRequested) return;
            _lines = lines;
            SetSteps(steps.Select(s => (s.Text, (int?)s.SourceLine)).ToList());
            SetStatus($"Read {lines.Count} line{(lines.Count == 1 ? "" : "s")} in {(DateTime.Now - started).TotalSeconds:0.0} s. Check the transcription and edit any misread symbols.");
            await CheckAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
        {
            SetStatus("Recognition failed: " + ex.Message, error: true);
            App.Log("recognition failed: " + ex);
        }
        finally { if (!token.IsCancellationRequested) Busy.IsActive = false; }
    }

    // ---- steps ---------------------------------------------------------------------------------------

    private void SetSteps(List<(string Text, int? Source)> steps)
    {
        _suppressRecheck = true;
        StepsPanel.Children.Clear();
        _rows.Clear();
        foreach (var (text, source) in steps) AddRow(text, source);
        if (_rows.Count == 0) AddRow("", null);
        _suppressRecheck = false;
    }

    private StepRow AddRow(string text, int? source)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var icon = new FontIcon { Glyph = "", FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] };
        var input = new TextBox
        {
            Text = text,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            PlaceholderText = _rows.Count == 0 ? "Task, e.g. 2x + 3 = 7" : "Next step",
            AcceptsReturn = false,
        };
        AutomationProperties.SetName(input, $"Step {_rows.Count + 1}");
        var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 11 }, Padding = new Thickness(8, 6, 8, 6), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(remove, "Remove this step");
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 4), Visibility = Visibility.Collapsed };
        Grid.SetColumn(input, 1);
        Grid.SetColumn(remove, 2);
        Grid.SetRow(message, 1);
        Grid.SetColumn(message, 1);
        grid.Children.Add(icon);
        grid.Children.Add(input);
        grid.Children.Add(remove);
        grid.Children.Add(message);
        var row = new StepRow { Root = grid, Icon = icon, Input = input, Message = message, SourceLine = source };
        input.TextChanged += (_, _) => { if (!_suppressRecheck) { _recheckTimer.Stop(); _recheckTimer.Start(); } };
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            int i = _rows.IndexOf(row);
            var next = i + 1 < _rows.Count ? _rows[i + 1] : InsertRowAfter(row);
            next.Input.Focus(FocusState.Keyboard);
        };
        remove.Click += (_, _) =>
        {
            StepsPanel.Children.Remove(grid);
            _rows.Remove(row);
            if (_rows.Count == 0) AddRow("", null);
            _recheckTimer.Stop();
            _recheckTimer.Start();
        };
        _rows.Add(row);
        StepsPanel.Children.Add(grid);
        return row;
    }

    private StepRow InsertRowAfter(StepRow row)
    {
        var added = AddRow("", null);
        int target = _rows.IndexOf(row) + 1;
        _rows.Remove(added);
        _rows.Insert(target, added);
        StepsPanel.Children.Remove(added.Root);
        StepsPanel.Children.Insert(target, added.Root);
        return added;
    }

    private void AddStep_Click(object sender, RoutedEventArgs e) => AddRow("", null).Input.Focus(FocusState.Programmatic);

    // ---- checking ------------------------------------------------------------------------------------

    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private async Task CheckAsync()
    {
        _recheckTimer.Stop();
        var texts = _rows.Select(r => r.Input.Text.Trim()).ToList();
        var nonEmpty = texts.Where(t => t.Length > 0).ToList();
        if (nonEmpty.Count == 0) { SummaryBar.IsOpen = false; return; }
        WorkReport report;
        try { report = await Task.Run(() => WorkChecker.Check(texts)); }
        catch (Exception ex) when (ex is Equator.Core.MathException or InvalidOperationException or ArgumentException)
        {
            SetStatus("Checking failed: " + ex.Message, error: true);
            return;
        }
        // Results are indexed by line; rows with empty text have no result.
        var byIndex = report.Steps.ToDictionary(s => s.Index);
        var worstBySource = new Dictionary<int, Verdict>();
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (!byIndex.TryGetValue(i, out var step))
            {
                row.Icon.Glyph = "";
                row.Icon.Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
                row.Message.Visibility = Visibility.Collapsed;
                continue;
            }
            row.Icon.Glyph = Glyph(step.Verdict);
            row.Icon.Foreground = new SolidColorBrush(ColorOf(step.Verdict));
            ToolTipService.SetToolTip(row.Icon, Describe(step.Verdict));
            row.Message.Text = step.Message.Length > 0 ? Capitalise(step.Message) : step.Verdict == Verdict.Given ? "The task." : "";
            row.Message.Foreground = step.Verdict is Verdict.Incorrect or Verdict.Warning ? new SolidColorBrush(ColorOf(step.Verdict)) : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            row.Message.Visibility = row.Message.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (row.SourceLine is int src)
                worstBySource[src] = worstBySource.TryGetValue(src, out var w) ? Worst(w, step.Verdict) : step.Verdict;
        }
        Pad.SetMarkers(worstBySource.Where(kv => kv.Key < _lines.Count)
            .Select(kv => new LineMarker(_lines[kv.Key].Bounds, ColorOf(kv.Value), Label(kv.Value))).ToList());

        SummaryBar.Severity = report.Overall switch
        {
            Verdict.Correct => InfoBarSeverity.Success,
            Verdict.Warning => InfoBarSeverity.Warning,
            Verdict.Incorrect => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
        SummaryBar.Title = report.Overall switch
        {
            Verdict.Correct => "Correct",
            Verdict.Warning => "Correct, with a remark",
            Verdict.Incorrect => "There is a mistake",
            _ => "Could not check",
        };
        SummaryBar.Message = report.Summary;
        SummaryBar.IsOpen = true;
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static int Rank(Verdict v) => v switch { Verdict.Given => 0, Verdict.Correct => 1, Verdict.Warning => 2, Verdict.Unchecked => 3, _ => 4 };
    private static Verdict Worst(Verdict a, Verdict b) => Rank(a) >= Rank(b) ? a : b;

    private static string Glyph(Verdict v) => v switch
    {
        Verdict.Given => "",
        Verdict.Correct => "",
        Verdict.Warning => "",
        Verdict.Incorrect => "",
        _ => "",
    };

    private static string Label(Verdict v) => v switch { Verdict.Given => "•", Verdict.Correct => "✓", Verdict.Warning => "!", Verdict.Incorrect => "✗", _ => "?" };

    private static string Describe(Verdict v) => v switch
    {
        Verdict.Given => "The task",
        Verdict.Correct => "Follows from the previous step",
        Verdict.Warning => "Correct, but see the remark",
        Verdict.Incorrect => "Does not follow from the previous step",
        _ => "Could not be checked",
    };

    private static Color ColorOf(Verdict v) => v switch
    {
        Verdict.Given => Color.FromArgb(255, 0, 103, 192),
        Verdict.Correct => Color.FromArgb(255, 15, 123, 15),
        Verdict.Warning => Color.FromArgb(255, 157, 93, 0),
        Verdict.Incorrect => Color.FromArgb(255, 196, 43, 28),
        _ => Color.FromArgb(255, 110, 110, 110),
    };

    // ---- notebook --------------------------------------------------------------------------------------

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        var steps = _rows.Select(r => r.Input.Text.Trim()).Where(t => t.Length > 0).ToList();
        if (steps.Count == 0) { SetStatus("There are no steps to insert.", error: true); return; }
        _insert?.Invoke(steps);
        SetStatus("Inserted a check(...) cell into the notebook.");
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = error ? new SolidColorBrush(ColorOf(Verdict.Incorrect)) : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    // ---- accelerators -----------------------------------------------------------------------------------

    private async void PasteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Let text boxes paste text normally.
        if (FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox) return;
        args.Handled = true;
        await PasteAsync();
    }

    private void OpenAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; Open_Click(this, new RoutedEventArgs()); }

    private void UndoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox) return;
        args.Handled = true;
        Pad.Undo();
    }

    private async void CheckAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await CheckAsync(); }

    // ---- diagnostics ------------------------------------------------------------------------------------

    private async Task SnapshotAsync(string path)
    {
        try
        {
            // Wait for recognition and checking to finish (bounded).
            for (int i = 0; i < 120 && Busy.IsActive; i++) await Task.Delay(250);
            await Task.Delay(800);
            var previous = RootGrid.Background;
            RootGrid.Background = new SolidColorBrush(RootGrid.ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243));
            await Task.Delay(100);
            var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await rtb.RenderAsync(RootGrid);
            RootGrid.Background = previous;
            var pixels = await rtb.GetPixelsAsync();
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
            await encoder.FlushAsync();
            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            await File.WriteAllBytesAsync(path, bytes);
            App.Log("check snapshot saved " + path);
        }
        catch (Exception ex) { App.Log("check snapshot failed: " + ex); }
    }
}
