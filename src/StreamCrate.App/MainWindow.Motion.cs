using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using StreamCrate.App.Presentation;

namespace StreamCrate.App;

/// <summary>
/// Small feedback motion: press scale, the queue badge, the completion check, the status bar,
/// the delayed probe spinner and thumbnail fade-ins. Every piece honours the Windows animation
/// setting and the user's tempo through <see cref="MotionTime"/>.
/// </summary>
public sealed partial class MainWindow
{
    // Longer probes show the spinner; shorter ones finish before it can flash on and off.
    private const int ProbeBusyRevealDelayMilliseconds = 300;
    private int _queueBadgeCount;
    private bool _compactSidebar;
    private bool _queueBadgeHiding;
    private int _queueBadgeGeneration;
    private bool _statusBarHiding;
    private bool _statusBarCloseAnimated;
    private int _statusBarGeneration;
    private int _probeBusyGeneration;

    private void AttachMotionFeedback()
    {
        foreach (var button in new[] { ProbeButton, QueueButton, SaveSettingsButton })
        {
            AttachPressFeedback(button);
        }

        foreach (var (selector, pill) in new[]
        {
            (_formatSegments, FormatSegmentPill),
            (_defaultFormatSegments, DefaultFormatSegmentPill),
            (_themeSegments, ThemeSegmentPill),
        })
        {
            selector.MotionTime = MotionTime;
            selector.AnimationsEnabled = () => _uiSettings.AnimationsEnabled;
            selector.AttachPill(pill);
        }
    }

    private static CubicBezierEasingFunction EaseOut(Visual visual) =>
        visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1), new Vector2(0.3f, 1));

    /// <summary>Keeps scale anchored on the element's centre whatever size layout gives it.</summary>
    private static void CenterScaleOrigin(Visual visual)
    {
        var center = visual.Compositor.CreateExpressionAnimation("Vector3(this.Target.Size.X / 2, this.Target.Size.Y / 2, 0)");
        visual.StartAnimation("CenterPoint", center);
    }

    private void AttachPressFeedback(Button button)
    {
        var visual = ElementCompositionPreview.GetElementVisual(button);
        CenterScaleOrigin(visual);
        // Buttons mark pointer events handled for Click, so the handlers opt in to handled events.
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => AnimatePress(visual, 0.97f)), true);
        var release = new PointerEventHandler((_, _) => AnimatePress(visual, 1f));
        button.AddHandler(UIElement.PointerReleasedEvent, release, true);
        button.AddHandler(UIElement.PointerCanceledEvent, release, true);
        button.AddHandler(UIElement.PointerCaptureLostEvent, release, true);
        button.AddHandler(UIElement.PointerExitedEvent, release, true);
    }

    private void AnimatePress(Visual visual, float scale)
    {
        if (!_uiSettings.AnimationsEnabled)
        {
            visual.Scale = Vector3.One;
            return;
        }

        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1, new Vector3(scale, scale, 1), EaseOut(visual));
        animation.Duration = MotionTime(120);
        visual.StartAnimation("Scale", animation);
    }

    /// <summary>
    /// Shows the sidebar's active-job count. Appearing pops the badge in, a rising count gives it a
    /// small bump, and dropping to zero shrinks it out before it collapses, so adding to the queue
    /// visibly lands in the queue.
    /// </summary>
    private void UpdateQueueBadge(int count, bool animate)
    {
        var previous = _queueBadgeCount;
        _queueBadgeCount = count;
        QueueCountBadgeText.Text = count.ToString();
        var shouldShow = count > 0 && !_compactSidebar;
        var visual = ElementCompositionPreview.GetElementVisual(QueueCountBadge);
        var isShown = QueueCountBadge.Visibility == Visibility.Visible && !_queueBadgeHiding;

        if (!animate || !_uiSettings.AnimationsEnabled)
        {
            _queueBadgeGeneration++;
            _queueBadgeHiding = false;
            visual.StopAnimation("Scale");
            visual.StopAnimation("Opacity");
            visual.Scale = Vector3.One;
            visual.Opacity = 1;
            QueueCountBadge.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        CenterScaleOrigin(visual);
        var easing = EaseOut(visual);
        if (shouldShow && !isShown)
        {
            _queueBadgeGeneration++;
            _queueBadgeHiding = false;
            QueueCountBadge.Visibility = Visibility.Visible;
            var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0, new Vector3(0.8f, 0.8f, 1));
            scale.InsertKeyFrame(1, Vector3.One, easing);
            scale.Duration = MotionTime(200);
            var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0, 0);
            opacity.InsertKeyFrame(1, 1, easing);
            opacity.Duration = MotionTime(200);
            visual.StartAnimation("Scale", scale);
            visual.StartAnimation("Opacity", opacity);
        }
        else if (shouldShow && count > previous)
        {
            var bump = visual.Compositor.CreateVector3KeyFrameAnimation();
            bump.InsertKeyFrame(0.4f, new Vector3(1.12f, 1.12f, 1), easing);
            bump.InsertKeyFrame(1, Vector3.One, easing);
            bump.Duration = MotionTime(160);
            visual.StartAnimation("Scale", bump);
        }
        else if (!shouldShow && isShown)
        {
            _queueBadgeHiding = true;
            var generation = ++_queueBadgeGeneration;
            var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(1, new Vector3(0.9f, 0.9f, 1), easing);
            scale.Duration = MotionTime(150);
            var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(1, 0, easing);
            opacity.Duration = MotionTime(150);
            visual.StartAnimation("Scale", scale);
            visual.StartAnimation("Opacity", opacity);
            batch.End();
            batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (generation != _queueBadgeGeneration)
                {
                    return;
                }

                _queueBadgeHiding = false;
                QueueCountBadge.Visibility = Visibility.Collapsed;
                visual.Scale = Vector3.One;
                visual.Opacity = 1;
            });
        }
    }

    /// <summary>
    /// Pops the check mark on a card that just finished while the queue is on screen. A card
    /// realised later, on a revisit or after scrolling, shows its check at rest.
    /// </summary>
    private void CompletedBadgeLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: QueueItem item } badge
            || !item.TryConsumeCompletionCue()
            || QueuePanel.Visibility != Visibility.Visible
            || !_uiSettings.AnimationsEnabled)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(badge);
        CenterScaleOrigin(visual);
        visual.Opacity = 0;
        visual.Scale = new Vector3(0.6f, 0.6f, 1);
        // A completion is rare enough to earn a little overshoot; the card's own entrance leads.
        var overshoot = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.34f, 1.4f), new Vector2(0.64f, 1));
        var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1, Vector3.One, overshoot);
        scale.DelayTime = MotionTime(120);
        scale.Duration = MotionTime(300);
        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(1, 1, EaseOut(visual));
        opacity.DelayTime = MotionTime(120);
        opacity.Duration = MotionTime(150);
        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", opacity);
    }

    private void OpenStatusBar()
    {
        var wasShown = StatusBar.IsOpen && !_statusBarHiding;
        _statusBarGeneration++;
        _statusBarHiding = false;
        StatusBar.IsOpen = true;
        ElementCompositionPreview.SetIsTranslationEnabled(StatusBar, true);
        if (wasShown)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(StatusBar);

        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        if (!_uiSettings.AnimationsEnabled)
        {
            visual.Opacity = 1;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            return;
        }

        var easing = EaseOut(visual);
        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(1, 1, easing);
        opacity.Duration = MotionTime(200);
        var translation = visual.Compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(0, new Vector3(0, -8, 0));
        translation.InsertKeyFrame(1, Vector3.Zero, easing);
        translation.Duration = MotionTime(200);
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", translation);
    }

    /// <summary>
    /// Holds the bar open for a short fade before it collapses, whether the user closed it or the
    /// code did. The collapse itself still moves the page up in one step; animating that height
    /// would relayout the whole scroll area every frame.
    /// </summary>
    private void StatusBarClosing(InfoBar sender, InfoBarClosingEventArgs args)
    {
        if (_statusBarCloseAnimated || !_uiSettings.AnimationsEnabled)
        {
            return;
        }

        args.Cancel = true;
        if (_statusBarHiding)
        {
            return;
        }

        _statusBarHiding = true;
        var generation = ++_statusBarGeneration;
        var visual = ElementCompositionPreview.GetElementVisual(StatusBar);
        var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(1, 0, EaseOut(visual));
        opacity.Duration = MotionTime(150);
        visual.StartAnimation("Opacity", opacity);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _statusBarGeneration)
            {
                return;
            }

            _statusBarHiding = false;
            _statusBarCloseAnimated = true;
            StatusBar.IsOpen = false;
            _statusBarCloseAnimated = false;
            visual.Opacity = 1;
        });
    }

    private async void RevealProbeBusyPanelAfterDelay()
    {
        var generation = ++_probeBusyGeneration;
        await Task.Delay(ProbeBusyRevealDelayMilliseconds);
        if (generation != _probeBusyGeneration)
        {
            return;
        }

        ProbeBusyPanel.Visibility = Visibility.Visible;
        if (_uiSettings.AnimationsEnabled)
        {
            AnimateOpacity(ProbeBusyPanel, 0);
        }
        else
        {
            SetImmediatelyVisible(ProbeBusyPanel);
        }
    }

    private void HideProbeBusyPanel()
    {
        _probeBusyGeneration++;
        ProbeBusyPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Hides a thumbnail whose picture is still downloading so it can fade in when it arrives; a
    /// picture already decoded (cached from an earlier row) shows at once.
    /// </summary>
    private void ThumbnailImageLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Image image)
        {
            PrepareThumbnailFade(image);
        }
    }

    private void PrepareThumbnailFade(Image image)
    {
        image.OpacityTransition = null;
        image.Opacity = _uiSettings.AnimationsEnabled && image.Source is BitmapImage { PixelWidth: 0 } ? 0 : 1;
    }

    private void ThumbnailImageOpened(object sender, RoutedEventArgs args)
    {
        if (sender is Image { Opacity: < 1 } image)
        {
            image.OpacityTransition = new ScalarTransition { Duration = MotionTime(180) };
            image.Opacity = 1;
        }
    }
}
