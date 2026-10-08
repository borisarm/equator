using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Equator.App.Localization;

/// <summary>
/// Attached properties that bind XAML text to a string key and keep it current when the language changes:
/// <c>l:Tr.Text="tb.runAll"</c> (Text, Label or PlaceholderText, whichever the element has),
/// <c>l:Tr.ToolTip="…"</c> and <c>l:Tr.Name="…"</c> (the UI Automation name).
/// </summary>
public static class Tr
{
    private static readonly List<(WeakReference<DependencyObject> Target, DependencyProperty Kind)> Bound = [];

    static Tr()
    {
        Loc.Changed += (_, _) =>
        {
            Bound.RemoveAll(b => !b.Target.TryGetTarget(out _));
            foreach (var (target, kind) in Bound)
                if (target.TryGetTarget(out var element)) Apply(element, kind);
        };
    }

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Tr), new PropertyMetadata(null, OnKeyChanged));
    public static readonly DependencyProperty ToolTipProperty =
        DependencyProperty.RegisterAttached("ToolTip", typeof(string), typeof(Tr), new PropertyMetadata(null, OnKeyChanged));
    public static readonly DependencyProperty NameProperty =
        DependencyProperty.RegisterAttached("Name", typeof(string), typeof(Tr), new PropertyMetadata(null, OnKeyChanged));

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);
    public static string GetToolTip(DependencyObject d) => (string)d.GetValue(ToolTipProperty);
    public static void SetToolTip(DependencyObject d, string value) => d.SetValue(ToolTipProperty, value);
    public static string GetName(DependencyObject d) => (string)d.GetValue(NameProperty);
    public static void SetName(DependencyObject d, string value) => d.SetValue(NameProperty, value);

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is null) Bound.Add((new WeakReference<DependencyObject>(d), e.Property));
        Apply(d, e.Property);
    }

    private static void Apply(DependencyObject d, DependencyProperty kind)
    {
        var key = (string)d.GetValue(kind);
        if (key is null) return;
        var text = Loc.Get(key);
        if (kind == ToolTipProperty) { ToolTipService.SetToolTip(d, text); return; }
        if (kind == NameProperty) { AutomationProperties.SetName(d, text); return; }
        switch (d)
        {
            case TextBlock t: t.Text = text; break;
            case AppBarButton b: b.Label = text; break;
            case MenuFlyoutItem m: m.Text = text; break;
            case SelectorBarItem s: s.Text = text; break;
            case TextBox t: t.PlaceholderText = text; break;
            case AutoSuggestBox a: a.PlaceholderText = text; break;
            case ContentControl c: c.Content = text; break;
        }
    }
}
