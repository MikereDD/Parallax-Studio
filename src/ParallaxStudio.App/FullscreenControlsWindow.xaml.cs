using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ParallaxStudio.App.ViewModels;

namespace ParallaxStudio.App;

public partial class FullscreenControlsWindow : Window
{
    private readonly MainViewModel _viewModel;

    public FullscreenControlsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

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
