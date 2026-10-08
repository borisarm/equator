using System.Globalization;
using Equator.Core.Engine;
using Equator.Core.Printing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
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
    public event EventHandler? Focused;

    public CellControl(string input = "")
    {
        InitializeComponent();
        Input.Text = input;
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
        Busy.IsActive = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Tools.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        if (busy) InLabel.Text = "In[*]:=";
    }

    public void ClearOutputs()
    {
        Outputs.Children.Clear();
        InLabel.Text = "In[ ]:=";
    }

    // ---- results -----------------------------------------------------------------------------------

    public void ShowResult(EvaluationResult result, double mathSize)
    {
        _lastResult = result;
        Outputs.Children.Clear();
        InLabel.Text = $"In[{result.Index}]:=";
        bool first = true;
        foreach (var output in result.Outputs)
        {
            Outputs.Children.Add(BuildOutputRow(output, first ? $"Out[{result.Index}]=" : "", mathSize));
            first = false;
        }
        if (result.Elapsed.TotalSeconds >= 0.5)
        {
            Outputs.Children.Add(new TextBlock
            {
                Text = result.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s",
                FontSize = 11,
                Margin = new Thickness(84, -4, 0, 0),
                Style = (Style)Application.Current.Resources["TertiaryText"],
            });
        }
    }

    private FrameworkElement BuildOutputRow(CellOutput output, string label, double mathSize)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (output.Kind is OutputKind.Expression && label.Length > 0)
        {
            var labelBlock = new TextBlock
            {
                Text = label,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                Margin = new Thickness(0, 6, 10, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Style = (Style)Application.Current.Resources["SecondaryText"],
            };
            row.Children.Add(labelBlock);
        }

        FrameworkElement content = output.Kind switch
        {
            OutputKind.Expression => ExpressionContent(output, mathSize),
            OutputKind.Plot => PlotContent(output),
            OutputKind.Text => TextContent(output),
            _ => ErrorContent(output),
        };
        Grid.SetColumn(content, 1);
        row.Children.Add(content);
        return row;
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
        AddItem(menu, "Copy as text", "", () => CopyText(output.Text));
        if (output.Latex is not null) AddItem(menu, "Copy as LaTeX", "", () => CopyText(output.Latex));
        if (view is not null) AddItem(menu, "Copy as image", "", async () => CopyImage(await view.RenderPngAsync()));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddItem(menu, "Use as new input", "", () => UseAsInput?.Invoke(this, output.Text));
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
            AddItem(menu, "Copy image", "", async () => CopyImage(await s.RenderPngAsync(ActualTheme == ElementTheme.Dark)));
            view = s;
        }
        else
        {
            var p = new PlotView(spec);
            AddItem(menu, "Copy image", "", async () => CopyImage(await p.RenderPngAsync(ActualTheme == ElementTheme.Dark)));
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
        FocusBar.Opacity = 1;
        UpdateTools();
        Focused?.Invoke(this, EventArgs.Empty);
    }

    private void Input_LostFocus(object sender, RoutedEventArgs e)
    {
        _focused = false;
        FocusBar.Opacity = 0;
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
