using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace StreamCrate.App.Presentation;

/// <summary>
/// Visual kinds used by the segmented controls in the Aurora Motion design.
/// </summary>
public enum SegmentedSelectorKind
{
    /// <summary>Selected pill filled with the accent colour (format switches).</summary>
    Accent,

    /// <summary>Selected pill filled with a neutral wash (theme switch).</summary>
    Neutral,

    /// <summary>Free-standing outlined chips (quality picker).</summary>
    Chip,
}

/// <summary>
/// Drives a row of <see cref="Button"/> elements as a single-select segmented control so the
/// shipped UI keeps the <c>SelectedIndex</c> shape the previous <see cref="ComboBox"/> controls had.
/// Selection is expressed by swapping styles, which keeps the light/dark theme resolution in XAML.
/// </summary>
public sealed class SegmentedSelector
{
    private const double PillMotionMilliseconds = 200;
    private readonly List<Button> _buttons;
    private readonly string _restStyleKey;
    private readonly string _selectedStyleKey;
    private int _selectedIndex = -1;
    private Border? _pill;
    private bool _pillPlaced;
    private DateTime _pillMotionEndsAt;

    public SegmentedSelector(SegmentedSelectorKind kind, params Button[] buttons)
    {
        _buttons = [.. buttons];
        (_restStyleKey, _selectedStyleKey) = kind switch
        {
            SegmentedSelectorKind.Accent => ("SegmentButtonStyle", "SegmentAccentSelectedButtonStyle"),
            SegmentedSelectorKind.Neutral => ("SegmentButtonStyle", "SegmentNeutralSelectedButtonStyle"),
            _ => ("ChipButtonStyle", "ChipSelectedButtonStyle"),
        };

        foreach (var button in _buttons)
        {
            button.Click += ButtonClicked;
        }
    }

    public event EventHandler? SelectionChanged;

    /// <summary>Scales pill motion by the user's tempo setting.</summary>
    public Func<double, TimeSpan> MotionTime { get; set; } = milliseconds => TimeSpan.FromMilliseconds(milliseconds);

    /// <summary>Reports whether Windows currently allows animations.</summary>
    public Func<bool> AnimationsEnabled { get; set; } = () => true;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => Select(value, notify: false);
    }

    /// <summary>
    /// Hands the selected fill to a single pill that slides between segments, so a switch reads as
    /// one thing moving rather than two buttons repainting. The pill sits behind the buttons in the
    /// same host, and the buttons give up their own selected background.
    /// </summary>
    public void AttachPill(Border pill)
    {
        _pill = pill;
        foreach (var button in _buttons)
        {
            button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            // The selected style changes font weight, so a segment's width settles a layout pass
            // after the click; a move still in flight retargets instead of snapping.
            button.SizeChanged += (_, _) => PlacePill(animate: DateTime.UtcNow < _pillMotionEndsAt);
        }

        PlacePill(animate: false);
    }

    public void Select(int index, bool notify)
    {
        if (_buttons.Count == 0)
        {
            return;
        }

        var clamped = Math.Clamp(index, 0, _buttons.Count - 1);
        var changed = clamped != _selectedIndex;
        _selectedIndex = clamped;
        ApplyVisuals();
        PlacePill(animate: changed && notify);
        if (changed && notify)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ButtonClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button button)
        {
            Select(_buttons.IndexOf(button), notify: true);
        }
    }

    private void ApplyVisuals()
    {
        for (var i = 0; i < _buttons.Count; i++)
        {
            var key = i == _selectedIndex ? _selectedStyleKey : _restStyleKey;
            _buttons[i].Style = (Style)Application.Current.Resources[key];
        }
    }

    private void PlacePill(bool animate)
    {
        if (_pill is null || _selectedIndex < 0 || _pill.Parent is not UIElement host)
        {
            return;
        }

        var button = _buttons[_selectedIndex];
        if (button.ActualWidth <= 0)
        {
            return;
        }

        var x = (float)button.TransformToVisual(host).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
        var width = (float)button.ActualWidth;
        ElementCompositionPreview.SetIsTranslationEnabled(_pill, true);
        var visual = ElementCompositionPreview.GetElementVisual(_pill);
        visual.StopAnimation("Translation");
        visual.StopAnimation("Scale");
        var shownWidth = double.IsNaN(_pill.Width) ? width : (float)_pill.Width * visual.Scale.X;
        _pill.Width = width;
        _pill.Opacity = 1;

        if (!animate || !_pillPlaced || !AnimationsEnabled())
        {
            visual.Properties.InsertVector3("Translation", new Vector3(x, 0, 0));
            visual.Scale = Vector3.One;
            _pillPlaced = true;
            return;
        }

        // Width is a layout property, so the pill takes its final width at once and a horizontal
        // scale from the left edge covers the difference while it slides.
        visual.CenterPoint = Vector3.Zero;
        var duration = MotionTime(PillMotionMilliseconds);
        var easing = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1), new Vector2(0.3f, 1));
        var translation = visual.Compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(1, new Vector3(x, 0, 0), easing);
        translation.Duration = duration;
        var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(shownWidth / width, 1, 1));
        scale.InsertKeyFrame(1, Vector3.One, easing);
        scale.Duration = duration;
        visual.StartAnimation("Translation", translation);
        visual.StartAnimation("Scale", scale);
        _pillMotionEndsAt = DateTime.UtcNow + duration;
    }
}
