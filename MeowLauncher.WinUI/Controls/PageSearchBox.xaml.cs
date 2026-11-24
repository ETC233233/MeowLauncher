using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MeowLauncher.WinUI.Controls;

/// <summary>页内搜索框（所有列表页共用）。输入即触发 <see cref="QueryChanged"/>，回车触发 <see cref="QuerySubmitted"/>。</summary>
public sealed partial class PageSearchBox : UserControl
{
    private bool _suppress;

    /// <summary>用户输入（含清除按钮）后触发，参数是当前全文；用于列表实时过滤。</summary>
    public event EventHandler<string>? QueryChanged;

    /// <summary>用户按回车后触发；用于联网搜索这类需要主动提交的页面。</summary>
    public event EventHandler<string>? QuerySubmitted;

    public PageSearchBox()
    {
        InitializeComponent();
    }

    private bool _isFilter;

    /// <summary>本页筛选（只过滤当前列表，不联网、不跨页）：图标换成漏斗，和顶栏「搜索全部」的放大镜区分开。</summary>
    public bool IsFilter
    {
        get => _isFilter;
        set
        {
            _isFilter = value;
            // 放大镜始终是「提交搜索」；漏斗由单独的 FilterButton 承担筛选/排序入口。
            Box.QueryIcon = new SymbolIcon(Symbol.Find);
        }
    }

    public string PlaceholderText
    {
        get => Box.PlaceholderText;
        set => Box.PlaceholderText = value ?? string.Empty;
    }

    /// <summary>程序设置文本不会触发 <see cref="QueryChanged"/>，避免页面自己改词时又回头搜一次。</summary>
    public string Text
    {
        get => Box.Text ?? string.Empty;
        set
        {
            value ??= string.Empty;
            if (Box.Text == value) return;
            _suppress = true;
            try { Box.Text = value; }
            finally { _suppress = false; }
        }
    }

    public bool FocusInput() => Box.Focus(FocusState.Programmatic);

    /// <summary>
    /// 在搜索框右侧挂一个「筛选与排序」漏斗（点击弹出面板）。只有真的提供筛选/排序的页面才调用它；
    /// 没调用的页面不会显示这个图标，避免点开一个空面板。
    /// </summary>
    public void AttachFilterPanel(FrameworkElement panel)
    {
        if (panel is null) return;
        FilterButton.Flyout = new Flyout
        {
            Content = panel,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
        };
        FilterButton.Visibility = Visibility.Visible;
        Box.Margin = new Thickness(0, 0, 40, 0);
    }

    private void Box_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // 程序改词（页面自己同步关键词）不当成用户输入；清除按钮和键盘输入都算用户输入。
        if (_suppress || args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange) return;
        QueryChanged?.Invoke(this, sender.Text ?? string.Empty);
    }

    private void Box_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        QuerySubmitted?.Invoke(this, args.QueryText ?? sender.Text ?? string.Empty);
    }
}
