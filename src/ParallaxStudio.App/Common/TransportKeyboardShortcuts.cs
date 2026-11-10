using System.Windows.Input;
using ParallaxStudio.App.ViewModels;

namespace ParallaxStudio.App.Common;

/// <summary>
/// Shared transport keyboard handling for the main window and the floating fullscreen
/// controls window. The two windows never receive the same key event (separate HWNDs),
/// so handling these keys in both cannot duplicate a command, and AsyncRelayCommand
/// rejects re-entry while a previous execution is still in flight.
/// </summary>
public static class TransportKeyboardShortcuts
{
    public static bool TryHandle(MainViewModel viewModel, Key key)
    {
        switch (key)
        {
            case Key.Space:
                viewModel.PlayPauseCommand.Execute(null);
                return true;
            case Key.Left:
                viewModel.SeekBackCommand.Execute(null);
                return true;
            case Key.Right:
                viewModel.SeekForwardCommand.Execute(null);
                return true;
            case Key.Up:
                viewModel.Volume = Math.Min(100, viewModel.Volume + 5);
                return true;
            case Key.Down:
                viewModel.Volume = Math.Max(0, viewModel.Volume - 5);
                return true;
            case Key.M:
                viewModel.ToggleMuteCommand.Execute(null);
                return true;
            case Key.F:
                viewModel.ToggleFullscreenCommand.Execute(null);
                return true;
            case Key.Escape when viewModel.IsFullscreen && viewModel.EscapeExitsFullscreen:
                viewModel.ToggleFullscreenCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }
}
