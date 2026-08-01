using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Threading;
using ParallaxStudio.App.Interop;
using ParallaxStudio.App.ViewModels;

namespace ParallaxStudio.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private const uint MonitorDefaultToNearest = 2;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private Rect _boundsBeforeFullscreen;
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
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _viewModel.CaptureWindowState(
            bounds.Width,
            bounds.Height,
            bounds.Left,
            bounds.Top,
            WindowState == WindowState.Maximized);
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
    }

    private void ExitFullscreen()
    {
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
        var point = e.GetPosition(slider);
        var target = Math.Clamp(point.X / slider.ActualWidth * slider.Maximum, slider.Minimum, slider.Maximum);
        _viewModel.ScrubPosition = target;
        await _viewModel.CommitSeekAsync(target);
        e.Handled = true;
    }

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
            case Key.Escape when _viewModel.IsFullscreen:
                _viewModel.ToggleFullscreenCommand.Execute(null);
                e.Handled = true;
                break;
        }
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
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

}
