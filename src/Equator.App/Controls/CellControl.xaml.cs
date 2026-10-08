using System.Globalization;
using Equator.App.Localization;
using Equator.Core.Engine;
using Equator.Core.Printing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace Equator.App.Controls;

public enum CellCommand { Evaluate, EvaluateAndAdvance, EvaluateAndInsert, Delete, InsertAbove, InsertBelow, MoveUp, MoveDown, FocusPrevious, FocusNext }

/// <summary>One notebook cell: an input box and its typeset outputs.</summary>
public sealed partial class CellControl : UserControl
{
    private bool _focused, _hovered;

    public event EventHandler<CellCommand>? Command;
    public event EventHandler<string>? UseAsInput;
    /// <summary>Raised with a history reference such as "%3" when its chip is clicked.</summary>
    public event EventHandler<string>? ReferenceRequested;
    public event EventHandler? Focused;

    public CellControl(string input = "")
    {
        InitializeComponent();
        Input.Text = input;
        SetBadge(BadgeState.Pending);
        Input.TextChanged += (_, _) => TextChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Text
    {
        get => Input.Text;
        set => Input.Text = value;
    }

    public bool HasOutput => Outputs.Children.Count > 0;

    public double InputFontSize
    {
        get => Input.FontSize;
        set => Input.FontSize = value;
    }

    public event EventHandler? TextChanged;

    private EvaluationResult? _lastResult;

    /// <summary>Re-typesets the last result (after a zoom change).</summary>
    public void Rerender(double mathSize)
    {
        if (_lastResult is not null && HasOutput) ShowResult(_lastResult, mathSize);
    }

    private double _mathSize = 19;

    /// <summary>Refreshes the text built in code (badge tooltip, result panels) after a language change.</summary>
    public void Relocalize()
    {
        if (_lastResult is not null && HasOutput) ShowResult(_lastResult, _mathSize);
        else if (_badge != BadgeState.Running) SetBadge(_badge);
    }

    public void FocusInput(bool atEnd = true)
    {
        Input.Focus(FocusState.Programmatic);
        if (atEnd) Input.Select(Input.Text.Length, 0);
    }

    public void InsertText(string text)
    {
        int start = Input.SelectionStart;
        var t = Input.Text;
        Input.Text = t[..start] + text + t[(start + Input.SelectionLength)..];
        Input.Select(start + text.Length, 0);
        Input.Focus(FocusState.Programmatic);
    }

    public void SetBusy(bool busy)
    {
        Tools.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        if (busy) SetBadge(BadgeState.Running);
    }

    public void ClearOutputs()
    {
        Outputs.Children.Clear();
        _lastResult = null;
        SetBadge(BadgeState.Pending);
    }

    // ---- step badge ---------------------------------------------------------------------------------

    private enum BadgeState { Pending, Running, Done, Error }
    private BadgeState _badge;

    private void SetBadge(BadgeState state, int step = 0)
    {
        _badge = state;
        bool running = state == BadgeState.Running;
        Busy.IsActive = running;
        Busy.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        BadgeRing.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        BadgeText.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        // Styles (with ThemeResource setters) so the badge follows the cell's light or dark theme.
        var res = Application.Current.Resources;
        BadgeRing.Style = (Style)res[state switch
        {
            BadgeState.Done => "BadgeRingDone",
            BadgeState.Error => "BadgeRingError",
            _ => _focused ? "BadgeRingFocused" : "BadgeRingPending",
        }];
        BadgeText.Style = state == BadgeState.Done ? (Style)res["BadgeTextDone"] : (Style)res["ErrorText"];
        BadgeText.Text = state switch
        {
            BadgeState.Done => step.ToString(CultureInfo.InvariantCulture),
            BadgeState.Error => "!",
            _ => "",
        };
        BadgeText.FontSize = state == BadgeState.Done && step >= 100 ? 10 : state == BadgeState.Error ? 13 : 12;
        ToolTipService.SetToolTip(Badge, state switch
        {
            BadgeState.Done => Loc.Format("badge.done", step),
            BadgeState.Error => Loc.Get("badge.error"),
            BadgeState.Running => Loc.Get("badge.running"),
            _ => Loc.Get("badge.pending"),
        });
    }

    // ---- results -----------------------------------------------------------------------------------

    public void ShowResult(EvaluationResult result, double mathSize)
    {
        _lastResult = result;
        _mathSize = mathSize;
        Outputs.Children.Clear();
        bool failed = result.Outputs.Any(o => o.Kind == OutputKind.Error);
        SetBadge(failed ? BadgeState.Error : BadgeState.Done, result.Index);
        // The value recorded for this step is the last result: that panel carries the %n chip.
        int last = -1;
        for (int i = 0; i < result.Outputs.Count; i++)
            if (result.Outputs[i].Kind is OutputKind.Expression or OutputKind.Plot) last = i;
        for (int i = 0; i < result.Outputs.Count; i++)
        {
            var output = result.Outputs[i];
            var elapsed = i == result.Outputs.Count - 1 && result.Elapsed.TotalSeconds >= 0.5
                ? result.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s"
                : null;
            Outputs.Children.Add(output.Kind == OutputKind.Error
                ? ErrorContent(output)
                : AnswerPanel(output, mathSize, i == last && !failed ? result.Index : null, elapsed));
        }
    }

    /// <summary>
    /// A result in a rounded panel: an accent arrow, the typeset value (or plot, or text) and, for the value
    /// of the step, a chip that inserts a reference to it (%n) into the current cell.
    /// </summary>
    private FrameworkElement AnswerPanel(CellOutput output, double mathSize, int? reference, string? elapsed)
    {
        var res = Application.Current.Resources;
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (output.Kind == OutputKind.Expression)
        {
            grid.Children.Add(new TextBlock { Text = "\u2192", Style = (Style)res["AnswerArrow"] });
        }

        FrameworkElement content = output.Kind switch
        {
            OutputKind.Expression => ExpressionContent(output, mathSize),
            OutputKind.Plot => PlotContent(output),
            _ => TextContent(output),
        };
        content.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        var side = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        if (reference is int n)
        {
            var refText = "%" + n.ToString(CultureInfo.InvariantCulture);
            var chip = new Button
            {
                Content = refText,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                Padding = new Thickness(8, 2, 8, 3),
                MinHeight = 0,
                CornerRadius = new CornerRadius(10),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            ToolTipService.SetToolTip(chip, Loc.Format("chip.tip", refText));
            AutomationProperties.SetName(chip, Loc.Format("chip.name", refText));
            chip.Click += (_, _) => ReferenceRequested?.Invoke(this, refText);
            side.Children.Add(chip);
        }
        if (elapsed is not null)
            side.Children.Add(new TextBlock { Text = elapsed, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)res["TertiaryText"] });
        if (side.Children.Count > 0)
        {
            Grid.SetColumn(side, 2);
            grid.Children.Add(side);
        }

        return new Border { Child = grid, Style = (Style)res["AnswerPanel"] };
    }

    private FrameworkElement ExpressionContent(CellOutput output, double mathSize)
    {
        MathView? view = null;
        FrameworkElement inner;
        try
        {
            view = new MathView { MathFontSize = mathSize, Box = BoxFormatter.Format(output.Value!) };
            inner = view;
        }
        catch (Exception)
        {
            inner = new TextBlock { Text = output.Text, FontFamily = new FontFamily("Cascadia Mono, Consolas"), IsTextSelectionEnabled = true };
        }
        var scroller = new ScrollViewer
        {
            Content = inner,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 0, 2),
        };

        var menu = new MenuFlyout();
        AddItem(menu, Loc.Get("menu.copyText"), "", () => CopyText(output.Text));
        if (output.Latex is not null) AddItem(menu, Loc.Get("menu.copyLatex"), "", () => CopyText(output.Latex));
        if (view is not null) AddItem(menu, Loc.Get("menu.copyImage"), "", async () => CopyImage(await view.RenderPngAsync()));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddItem(menu, Loc.Get("menu.useAsInput"), "", () => UseAsInput?.Invoke(this, output.Text));
        scroller.ContextFlyout = menu;
        ToolTipService.SetToolTip(scroller, output.Text.Length > 300 ? output.Text[..300] + "…" : output.Text);
        return scroller;
    }

    private FrameworkElement PlotContent(CellOutput output)
    {
        var spec = output.Plot!;
        FrameworkElement view;
        var menu = new MenuFlyout();
        if (spec.Kind == PlotKind.Surface)
        {
            var s = new SurfaceView(spec);
            AddItem(menu, Loc.Get("menu.copyPlot"), "", async () => CopyImage(await s.RenderPngAsync(ActualTheme == ElementTheme.Dark)));
            view = s;
        }
        else
        {
            var p = new PlotView(spec);
            AddItem(menu, Loc.Get("menu.copyPlot"), "", async () => CopyImage(await p.RenderPngAsync(ActualTheme == ElementTheme.Dark)));
            view = p;
        }
        view.ContextFlyout = menu;
        view.Margin = new Thickness(0, 2, 0, 2);
        return view;
    }

    private static FrameworkElement TextContent(CellOutput output) => new TextBlock
    {
        Text = output.Text,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        Margin = new Thickness(0, 2, 0, 2),
        Style = (Style)Application.Current.Resources["SecondaryText"],
    };

    private static FrameworkElement ErrorContent(CellOutput output)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(new FontIcon
        {
            Glyph = "",
            FontSize = 14,
            Style = (Style)Application.Current.Resources["ErrorIcon"],
        });
        panel.Children.Add(new TextBlock
        {
            Text = output.Text,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Style = (Style)Application.Current.Resources["ErrorText"],
        });
        return panel;
    }

    private static void AddItem(MenuFlyout menu, string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static void CopyText(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    private static void CopyImage(Windows.Storage.Streams.IRandomAccessStream? png)
    {
        if (png is null) return;
        var data = new DataPackage();
        data.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(png));
        Clipboard.SetContent(data);
    }

    // ---- keyboard & focus ----------------------------------------------------------------------------

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void Input_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = IsDown(VirtualKey.Shift), ctrl = IsDown(VirtualKey.Control), alt = IsDown(VirtualKey.Menu);
        if (e.Key == VirtualKey.Enter && (shift || ctrl || alt))
        {
            e.Handled = true;
            Command?.Invoke(this, shift ? CellCommand.EvaluateAndAdvance : alt ? CellCommand.EvaluateAndInsert : CellCommand.Evaluate);
        }
        else if (e.Key == VirtualKey.Up && !shift && Input.SelectionStart == 0 && Input.SelectionLength == 0)
        {
            e.Handled = true;
            Command?.Invoke(this, CellCommand.FocusPrevious);
        }
        else if (e.Key == VirtualKey.Down && !shift && Input.SelectionStart >= Input.Text.Length)
        {
            e.Handled = true;
            Command?.Invoke(this, CellCommand.FocusNext);
        }
        else if (e.Key == VirtualKey.Back && ctrl && shift)
        {
            e.Handled = true;
            Command?.Invoke(this, CellCommand.Delete);
        }
    }

    private void Input_GotFocus(object sender, RoutedEventArgs e)
    {
        _focused = true;
        if (_badge == BadgeState.Pending) SetBadge(BadgeState.Pending);
        UpdateTools();
        Focused?.Invoke(this, EventArgs.Empty);
    }

    private void Input_LostFocus(object sender, RoutedEventArgs e)
    {
        _focused = false;
        if (_badge == BadgeState.Pending) SetBadge(BadgeState.Pending);
        UpdateTools();
    }

    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e) { _hovered = true; UpdateTools(); }
    private void Root_PointerExited(object sender, PointerRoutedEventArgs e) { _hovered = false; UpdateTools(); }
    private void UpdateTools() => Tools.Opacity = _focused || _hovered ? 1 : 0;

    private void Run_Click(object sender, RoutedEventArgs e) => Command?.Invoke(this, CellCommand.Evaluate);
    private void InsertAbove_Click(object sender, RoutedEventArgs e) => Command?.Invoke(this, CellCommand.InsertAbove);
    private void InsertBelow_Click(object sender, RoutedEventArgs e) => Command?.Invoke(this, CellCommand.InsertBelow);
    private void MoveUp_Click(object sender, RoutedEventArgs e) => Command?.Invoke(this, CellCommand.MoveUp);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Command?.Invoke(this, CellCommand.MoveDown);
    private void Delete_Click(object sender, RoutedEventArgs e) => Command?.Invoke(this, CellCommand.Delete);
    private void ClearOutput_Click(object sender, RoutedEventArgs e) => ClearOutputs();
}
