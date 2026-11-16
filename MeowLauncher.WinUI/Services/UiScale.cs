using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowLauncher.WinUI.Services;

/// <summary>
/// 旧版 Qt 的“字体缩放”：把所有 TextBlock 的字号按基线整体缩放。
/// 每个元素首次遇到时记录原始字号，后续缩放都基于基线，避免反复放大累积误差。
/// </summary>
public static class UiScale
{
    private static readonly ConditionalWeakTable<DependencyObject, StrongBox<double>> Baselines = new();

    public static void Apply(DependencyObject? root, double scale)
    {
        if (root is null) return;
        scale = Math.Clamp(scale, 0.85, 1.25);
        Walk(root, scale);
    }

    private static void Walk(DependencyObject node, double scale)
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is TextBlock text)
            {
                var box = Baselines.GetOrCreateValue(child);
                if (box.Value <= 0) box.Value = text.FontSize;
                var target = Math.Round(box.Value * scale, 1);
                if (Math.Abs(text.FontSize - target) > 0.05) text.FontSize = target;
            }

            Walk(child, scale);
        }
    }
}
