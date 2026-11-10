using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ParallaxStudio.App.Common;
using ParallaxStudio.App.ViewModels;

namespace ParallaxStudio.App;

public partial class FullscreenControlsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isTimelineKeyboardScrubbing;

    public FullscreenControlsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    // This window only receives key events once one of its own controls has focus;
    // until then MainWindow handles the same keys, so nothing is handled twice.
    private void FullscreenControls_KeyDown(object sender, KeyEventArgs e)
    {
        // Interactive controls keep their own keyboard behavior.
        if (IsWithin<ComboBox>(e.OriginalSource as DependencyObject) ||
            IsWithin<ComboBoxItem>(e.OriginalSource as DependencyObject) ||
            IsWithin<TextBoxBase>(e.OriginalSource as DependencyObject) ||
            IsWithin<CheckBox>(e.OriginalSource as DependencyObject) ||
            IsWithin<Slider>(e.OriginalSource as DependencyObject)) return;

        e.Handled = TransportKeyboardShortcuts.TryHandle(_viewModel, e.Key);
    }

    private void TimelineSlider_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsTimelineNavigationKey(e.Key)) return;
        _isTimelineKeyboardScrubbing = true;
        _viewModel.IsScrubbing = true;
    }

    private async void TimelineSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (IsTimelineNavigationKey(e.Key)) await CommitKeyboardSeekAsync();
    }

    private async void TimelineSlider_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        await CommitKeyboardSeekAsync();

    private async Task CommitKeyboardSeekAsync()
    {
        if (!_isTimelineKeyboardScrubbing) return;
        _isTimelineKeyboardScrubbing = false;
        var target = _viewModel.ScrubPosition;
        _viewModel.IsScrubbing = false;
        await _viewModel.CommitSeekAsync(target);
    }

    private static bool IsTimelineNavigationKey(Key key) =>
        key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    private async void TimelineSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || slider.ActualWidth <= 0) return;
        if (IsWithin<Thumb>(e.OriginalSource as DependencyObject)) return;
        if (slider.Template.FindName("PART_Track", slider) is not Track track) return;

        var target = Math.Clamp(track.ValueFromPoint(e.GetPosition(track)), slider.Minimum, slider.Maximum);
        e.Handled = true;
        _viewModel.ScrubPosition = target;
        await _viewModel.CommitSeekAsync(target);
    }

    private static bool IsWithin<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void TimelineSlider_DragStarted(object sender, DragStartedEventArgs e) =>
        _viewModel.IsScrubbing = true;

    private async void TimelineSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _viewModel.IsScrubbing = false;
        await _viewModel.CommitSeekAsync(_viewModel.ScrubPosition);
    }
}
