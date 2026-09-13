using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    private readonly List<Button> _buttons;
    private readonly string _restStyleKey;
    private readonly string _selectedStyleKey;
    private int _selectedIndex = -1;

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

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => Select(value, notify: false);
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
}
