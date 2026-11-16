using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowLauncher.WinUI.Controls;

/// <summary>列表空态/提示的统一外观：图标 + 主文案 + 可选说明。</summary>
public sealed partial class EmptyState : UserControl
{
    public EmptyState()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(EmptyState),
        new PropertyMetadata("\uE7B8", (d, e) =>
        {
            if (d is EmptyState state && state.IconGlyph is not null)
                state.IconGlyph.Glyph = e.NewValue as string ?? "\uE7B8";
        }));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyState),
        new PropertyMetadata("", (d, e) =>
        {
            if (d is EmptyState state && state.TitleBlock is not null)
                state.TitleBlock.Text = e.NewValue as string ?? "";
        }));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(EmptyState),
        new PropertyMetadata("", (d, e) =>
        {
            if (d is not EmptyState state || state.HintBlock is null) return;
            var text = e.NewValue as string ?? "";
            state.HintBlock.Text = text;
            state.HintBlock.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }));

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }
}
