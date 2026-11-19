using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace MeowLauncher.WinUI.Controls;

/// <summary>
/// 轻量动效：页面进场、行悬停、图标旋转。全部走 Storyboard/依赖动画，
/// 时长控制在 160–280ms，避免 WinUI 默认动画那种「弹一下」的廉价感。
/// </summary>
public static class Motion
{
    private static readonly Windows.UI.ViewManagement.UISettings Ui = new();

    /// <summary>系统「显示动画」关掉时，所有进场动画直接跳过（元素保持最终状态）。</summary>
    public static bool Enabled => Ui.AnimationsEnabled;

    public static NavigationTransitionInfo PageRefresh() => Enabled
        ? new EntranceNavigationTransitionInfo() : new SuppressNavigationTransitionInfo();

    public static NavigationTransitionInfo Sibling(bool fromRight) => Enabled
        ? new SlideNavigationTransitionInfo { Effect = fromRight ? SlideNavigationTransitionEffect.FromRight : SlideNavigationTransitionEffect.FromLeft }
        : new SuppressNavigationTransitionInfo();

    public static void AttachVisibility(FrameworkElement element, bool active = true)
    {
        if (!Enabled || !active)
        {
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetImplicitShowAnimation(element, null);
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetImplicitHideAnimation(element, null);
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");
            visual.Opacity = 1;
            visual.Properties.InsertVector3("Translation", System.Numerics.Vector3.Zero);
            return;
        }
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element).Compositor;
        var show = compositor.CreateScalarKeyFrameAnimation();
        show.Target = "Opacity";
        show.InsertKeyFrame(0, 0); show.InsertKeyFrame(1, 1);
        show.Duration = TimeSpan.FromMilliseconds(83);
        var move = compositor.CreateVector3KeyFrameAnimation();
        move.Target = "Translation";
        move.InsertKeyFrame(0, new System.Numerics.Vector3(0, 8, 0));
        move.InsertKeyFrame(1, System.Numerics.Vector3.Zero,
            compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(0, 1)));
        move.Duration = TimeSpan.FromMilliseconds(167);
        var showGroup = compositor.CreateAnimationGroup(); showGroup.Add(show); showGroup.Add(move);
        var hide = compositor.CreateScalarKeyFrameAnimation();
        hide.Target = "Opacity";
        hide.InsertKeyFrame(0, 1); hide.InsertKeyFrame(1, 0);
        hide.Duration = TimeSpan.FromMilliseconds(83);
        var hideGroup = compositor.CreateAnimationGroup(); hideGroup.Add(hide);
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetImplicitShowAnimation(element, showGroup);
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetImplicitHideAnimation(element, hideGroup);
    }

    public static void AttachLayout(DependencyObject root, bool active = true)
    {
        // Walk the declared content rather than the ScrollViewer template:
        // collapsed settings and their neighbouring rows must be wired too.
        if (root is Microsoft.UI.Xaml.Controls.Panel panel)
        {
            panel.ChildrenTransitions = Enabled && active ? new TransitionCollection { new RepositionThemeTransition { IsStaggeringEnabled = false } } : new TransitionCollection();
            foreach (var child in panel.Children) AttachLayout(child, active);
        }
        else if (root is Microsoft.UI.Xaml.Controls.Border border && border.Child is { } child)
            AttachLayout(child, active);
        else if (root is Microsoft.UI.Xaml.Controls.ScrollViewer scroll && scroll.Content is DependencyObject content)
            AttachLayout(content, active);
    }

    /// <summary>淡入 + 上移归位。delayMs 用来做 30–40ms 的错峰，不要超过 6 个元素。</summary>
    public static void Enter(FrameworkElement element, double delayMs = 0, double fromY = 14, int durationMs = 260, Action<Storyboard>? onCreated = null)
    {
        if (element is null || !Enabled) return;
        var slide = element.RenderTransform as TranslateTransform;
        if (slide is null)
        {
            slide = new TranslateTransform();
            element.RenderTransform = slide;
        }

        slide.Y = fromY;
        element.Opacity = 0;

        var storyboard = new Storyboard();
        if (delayMs > 0) storyboard.BeginTime = TimeSpan.FromMilliseconds(delayMs);

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var move = new DoubleAnimation
        {
            From = fromY,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(move, slide);
        Storyboard.SetTargetProperty(move, "Y");

        storyboard.Children.Add(fade);
        storyboard.Children.Add(move);
        onCreated?.Invoke(storyboard);
        storyboard.Begin();
    }

    /// <summary>淡入 + 横向归位（fromX 为负 = 从左边滑出来），用于「在线资源」后面展开的来源切换。</summary>
    public static void EnterX(FrameworkElement element, double delayMs = 0, double fromX = -14, int durationMs = 220)
    {
        if (element is null || !Enabled) return;
        var slide = element.RenderTransform as TranslateTransform;
        if (slide is null)
        {
            slide = new TranslateTransform();
            element.RenderTransform = slide;
        }
        slide.Y = 0;
        slide.X = fromX;
        element.Opacity = 0;
        var storyboard = new Storyboard();
        if (delayMs > 0) storyboard.BeginTime = TimeSpan.FromMilliseconds(delayMs);
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(durationMs), EnableDependentAnimation = true };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var move = new DoubleAnimation
        {
            From = fromX, To = 0, Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(move, slide);
        Storyboard.SetTargetProperty(move, "X");
        storyboard.Children.Add(fade);
        storyboard.Children.Add(move);
        storyboard.Begin();
    }

    /// <summary>行悬停时把右箭头往右推 3px，松手弹回；比整行缩放克制。</summary>
    public static void Nudge(FrameworkElement element, double to, int durationMs = 150)
    {
        if (element is null) return;
        var slide = element.RenderTransform as TranslateTransform;
        if (slide is null)
        {
            slide = new TranslateTransform();
            element.RenderTransform = slide;
        }
        if (!Enabled) { slide.X = to; return; }

        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, slide);
        Storyboard.SetTargetProperty(animation, "X");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    /// <summary>整圈旋转，用于刷新按钮点下去那一下。</summary>
    public static void Spin(FrameworkElement element, int durationMs = 620)
    {
        if (element is null || !Enabled) return;
        var rotate = element.RenderTransform as RotateTransform;
        if (rotate is null)
        {
            rotate = new RotateTransform();
            element.RenderTransform = rotate;
            element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        }

        var animation = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, rotate);
        Storyboard.SetTargetProperty(animation, "Angle");
        var storyboard = new Storyboard();
        storyboard.BeginTime = TimeSpan.Zero;
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) => rotate.Angle = 0;
        storyboard.Begin();
    }

    /// <summary>小弹入：从 0.985 到 1，用于子视图切换。</summary>
    public static void Settle(FrameworkElement element, int durationMs = 220)
    {
        if (element is null || !Enabled) return;
        var scale = element.RenderTransform as ScaleTransform;
        if (scale is null)
        {
            scale = new ScaleTransform();
            element.RenderTransform = scale;
            element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        }

        element.Opacity = 0;
        scale.ScaleX = 0.985;
        scale.ScaleY = 0.985;

        var storyboard = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var grow = new DoubleAnimation
        {
            From = 0.985,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(grow, scale);
        Storyboard.SetTargetProperty(grow, "ScaleX");
        var growY = new DoubleAnimation
        {
            From = 0.985,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(growY, scale);
        Storyboard.SetTargetProperty(growY, "ScaleY");

        storyboard.Children.Add(fade);
        storyboard.Children.Add(grow);
        storyboard.Children.Add(growY);
        storyboard.Begin();
    }
}
