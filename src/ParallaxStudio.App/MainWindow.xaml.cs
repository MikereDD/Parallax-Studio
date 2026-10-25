using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using ParallaxStudio.App.Interop;
using ParallaxStudio.App.ViewModels;

namespace ParallaxStudio.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private const uint MonitorDefaultToNearest = 2;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private Rect _boundsBeforeFullscreen;
    private bool _isTimelineKeyboardScrubbing;
    private readonly DispatcherTimer _fullscreenUiTimer;
    private FullscreenControlsWindow? _fullscreenControlsWindow;
    private NativePoint _lastCursorPoint;
    private DateTime _lastFullscreenActivityUtc;
    private bool _fullscreenCursorHidden;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".webm", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg"
    };


    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.ToggleFullscreenRequested += OnToggleFullscreenRequested;
        _viewModel.CinemaSettingsChanged += OnCinemaSettingsChanged;
        _fullscreenUiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _fullscreenUiTimer.Tick += FullscreenUiTimer_Tick;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WindowProc);
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeViewAsync();
        RestoreWindowGeometry();
    }

    private async void VideoHost_HandleCreated(object? sender, VideoHandleCreatedEventArgs e) =>
        await _viewModel.AttachVideoAsync(e.Handle);

    private void RestoreWindowGeometry()
    {
        if (!_viewModel.RememberWindowGeometry) return;
        var settings = _viewModel.GetWindowSettings();
        Width = Math.Max(MinWidth, settings.WindowWidth);
        Height = Math.Max(MinHeight, settings.WindowHeight);

        if (settings.WindowLeft is double left && settings.WindowTop is double top &&
            IsVisibleOnAnyScreen(left, top, Width, Height))
        {
            Left = left;
            Top = top;
        }

        if (settings.IsMaximized) WindowState = WindowState.Maximized;
    }

    private static bool IsVisibleOnAnyScreen(double left, double top, double width, double height)
    {
        var candidate = new Rect(left, top, Math.Max(1, width), Math.Max(1, height));
        var virtualDesktop = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        return virtualDesktop.IntersectsWith(candidate);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _fullscreenUiTimer.Stop();
        SetFullscreenCursorVisible(true);
        if (_fullscreenControlsWindow is not null)
        {
            _fullscreenControlsWindow.Close();
            _fullscreenControlsWindow = null;
        }
        _viewModel.ToggleFullscreenRequested -= OnToggleFullscreenRequested;
        _viewModel.CinemaSettingsChanged -= OnCinemaSettingsChanged;

        if (_viewModel.RememberWindowGeometry)
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _viewModel.CaptureWindowState(
                bounds.Width,
                bounds.Height,
                bounds.Left,
                bounds.Top,
                WindowState == WindowState.Maximized);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnToggleFullscreenRequested(object? sender, EventArgs e)
    {
        if (_viewModel.IsFullscreen) EnterFullscreen();
        else ExitFullscreen();
    }

    private void EnterFullscreen()
    {
        _stateBeforeFullscreen = WindowState;
        _boundsBeforeFullscreen = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _viewModel.IsTracksPanelOpen = false;

        var monitorBounds = GetActiveMonitorBounds();
        TitleBarRow.Height = new GridLength(0);
        SidebarColumn.Width = new GridLength(0);
        MainContentGrid.Margin = new Thickness(0);
        VideoBorder.Margin = new Thickness(0);
        WindowState = WindowState.Normal;
        ResizeMode = ResizeMode.NoResize;
        Left = monitorBounds.Left;
        Top = monitorBounds.Top;
        Width = monitorBounds.Width;
        Height = monitorBounds.Height;
        Topmost = true;

        GetCursorPos(out _lastCursorPoint);
        _lastFullscreenActivityUtc = DateTime.UtcNow;
        SetFullscreenCursorVisible(true);
        _fullscreenUiTimer.Start();
        RefreshFullscreenUiState(showControls: true);
    }

    private void ExitFullscreen()
    {
        _fullscreenUiTimer.Stop();
        HideFullscreenControls();
        SetFullscreenCursorVisible(true);

        // Temporarily remove the native HWND video surface while WPF restores the
        // normal grid. This prevents the fullscreen-sized child window from
        // painting over the sidebar until its host receives the new bounds.
        VideoHost.Visibility = Visibility.Collapsed;

        Topmost = false;
        ResizeMode = ResizeMode.CanResize;
        TitleBarRow.Height = new GridLength(48);
        SidebarColumn.Width = GridLength.Auto;
        MainContentGrid.Margin = new Thickness(20, 18, 20, 12);
        VideoBorder.Margin = new Thickness(0, 0, 14, 0);

        WindowState = WindowState.Normal;
        Left = _boundsBeforeFullscreen.Left;
        Top = _boundsBeforeFullscreen.Top;
        Width = _boundsBeforeFullscreen.Width;
        Height = _boundsBeforeFullscreen.Height;

        if (_stateBeforeFullscreen == WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            MainContentGrid.InvalidateMeasure();
            MainContentGrid.InvalidateArrange();
            VideoBorder.InvalidateMeasure();
            VideoBorder.InvalidateArrange();
            UpdateLayout();

            VideoHost.Visibility = Visibility.Visible;
            VideoHost.InvalidateMeasure();
            VideoHost.InvalidateArrange();
            UpdateLayout();
        }));
    }

    private void OnCinemaSettingsChanged(object? sender, EventArgs e)
    {
        if (!_viewModel.IsFullscreen) return;
        _lastFullscreenActivityUtc = DateTime.UtcNow;
        SetFullscreenCursorVisible(true);
        RefreshFullscreenUiState(showControls: true);
    }

    private void FullscreenUiTimer_Tick(object? sender, EventArgs e)
    {
        if (!_viewModel.IsFullscreen) return;

        if (GetCursorPos(out var cursorPoint) &&
            (cursorPoint.X != _lastCursorPoint.X || cursorPoint.Y != _lastCursorPoint.Y))
        {
            _lastCursorPoint = cursorPoint;
            _lastFullscreenActivityUtc = DateTime.UtcNow;
            SetFullscreenCursorVisible(true);
            if (_viewModel.FullscreenHideTransport && _viewModel.FullscreenShowControlsOnMouseMove)
                ShowFullscreenControls();
        }

        var idleFor = DateTime.UtcNow - _lastFullscreenActivityUtc;
        if (idleFor < TimeSpan.FromSeconds(_viewModel.FullscreenControlsAutoHideSeconds)) return;

        if (_viewModel.FullscreenHideTransport) HideFullscreenControls();
        if (_viewModel.FullscreenCursorAutoHide) SetFullscreenCursorVisible(false);
    }

    private void RefreshFullscreenUiState(bool showControls)
    {
        if (!_viewModel.IsFullscreen) return;

        if (!_viewModel.FullscreenHideTransport || !_viewModel.FullscreenShowControlsOnMouseMove)
        {
            HideFullscreenControls();
            return;
        }

        if (showControls) ShowFullscreenControls();
    }

    private void ShowFullscreenControls()
    {
        if (!_viewModel.IsFullscreen || !_viewModel.FullscreenHideTransport) return;

        _fullscreenControlsWindow ??= new FullscreenControlsWindow(_viewModel)
        {
            Owner = this
        };

        var width = Math.Min(960, Math.Max(620, ActualWidth - 80));
        _fullscreenControlsWindow.Width = width;
        _fullscreenControlsWindow.Left = Left + Math.Max(20, (ActualWidth - width) / 2);
        _fullscreenControlsWindow.Top = Top + Math.Max(20, ActualHeight - _fullscreenControlsWindow.Height - 24);

        if (!_fullscreenControlsWindow.IsVisible) _fullscreenControlsWindow.Show();
    }

    private void HideFullscreenControls()
    {
        if (_fullscreenControlsWindow?.IsVisible == true)
            _fullscreenControlsWindow.Hide();
    }

    private void SetFullscreenCursorVisible(bool visible)
    {
        if (_fullscreenCursorHidden == !visible) return;
        _fullscreenCursorHidden = !visible;
        var cursor = visible ? Cursors.Arrow : Cursors.None;
        Cursor = cursor;
        VideoHost.Cursor = cursor;
    }

    private Rect GetActiveMonitorBounds()
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

        if (monitor != nint.Zero && GetMonitorInfo(monitor, ref info))
        {
            return new Rect(
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left,
                info.Monitor.Bottom - info.Monitor.Top);
        }

        return new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
    }


    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedMedia(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (TryGetDroppedMedia(e.Data, out var path))
        {
            await _viewModel.OpenPathAsync(path!);
        }
        e.Handled = true;
    }

    private static bool TryGetDroppedMedia(IDataObject data, out string? path)
    {
        path = null;
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files)
            return false;
        path = files.FirstOrDefault(file => File.Exists(file) && SupportedExtensions.Contains(Path.GetExtension(file)));
        return path is not null;
    }

    private async void TimelineSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || slider.ActualWidth <= 0) return;
        // Let the thumb receive the press so its existing drag events can scrub.
        if (IsWithin<Thumb>(e.OriginalSource as DependencyObject)) return;
        if (slider.Template.FindName("PART_Track", slider) is not Track track) return;
        var target = Math.Clamp(track.ValueFromPoint(e.GetPosition(track)), slider.Minimum, slider.Maximum);
        e.Handled = true;
        _viewModel.ScrubPosition = target;
        await _viewModel.CommitSeekAsync(target);
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

    private void TimelineSlider_DragStarted(object sender, DragStartedEventArgs e) => _viewModel.IsScrubbing = true;

    private async void TimelineSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _viewModel.IsScrubbing = false;
        await _viewModel.CommitSeekAsync(_viewModel.ScrubPosition);
    }


    private async void AudioTracks_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel.IsRefreshingMediaNavigation) return;
        if (sender is ListBox { SelectedItem: ParallaxStudio.Core.Models.MediaTrack track })
            await _viewModel.SelectAudioTrackAsync(track);
    }

    private async void SubtitleTracks_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel.IsRefreshingMediaNavigation) return;
        if (sender is ListBox { SelectedItem: ParallaxStudio.Core.Models.MediaTrack track })
            await _viewModel.SelectSubtitleTrackAsync(track);
    }

    private async void Chapters_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel.IsRefreshingMediaNavigation) return;
        if (sender is ListBox { SelectedItem: ParallaxStudio.Core.Models.MediaChapter chapter })
            await _viewModel.SelectChapterAsync(chapter);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Selectors keep their arrow keys and type-to-select letters (including F/M).
        if (IsWithin<ComboBox>(e.OriginalSource as DependencyObject) ||
            IsWithin<ComboBoxItem>(e.OriginalSource as DependencyObject) ||
            IsWithin<TextBoxBase>(e.OriginalSource as DependencyObject) ||
            IsWithin<CheckBox>(e.OriginalSource as DependencyObject) ||
            IsWithin<Slider>(e.OriginalSource as DependencyObject)) return;

        switch (e.Key)
        {
            case Key.Space:
                _viewModel.PlayPauseCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left:
                _viewModel.SeekBackCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right:
                _viewModel.SeekForwardCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                _viewModel.Volume = Math.Min(100, _viewModel.Volume + 5);
                e.Handled = true;
                break;
            case Key.Down:
                _viewModel.Volume = Math.Max(0, _viewModel.Volume - 5);
                e.Handled = true;
                break;
            case Key.M:
                _viewModel.ToggleMuteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F:
                _viewModel.ToggleFullscreenCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape when _viewModel.IsFullscreen && _viewModel.EscapeExitsFullscreen:
                _viewModel.ToggleFullscreenCommand.Execute(null);
                e.Handled = true;
                break;
        }
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
    private const int WmGetMinMaxInfo = 0x0024;

    private nint WindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            ApplyMonitorWorkArea(hwnd, lParam);
            handled = true;
        }
        return nint.Zero;
    }

    private static void ApplyMonitorWorkArea(nint hwnd, nint lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info)) return;

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMax.MaxPosition.X = Math.Abs(info.WorkArea.Left - info.Monitor.Left);
        minMax.MaxPosition.Y = Math.Abs(info.WorkArea.Top - info.Monitor.Top);
        minMax.MaxSize.X = Math.Abs(info.WorkArea.Right - info.WorkArea.Left);
        minMax.MaxSize.Y = Math.Abs(info.WorkArea.Bottom - info.WorkArea.Top);
        minMax.MaxTrackSize = minMax.MaxSize;
        Marshal.StructureToPtr(minMax, lParam, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

}
