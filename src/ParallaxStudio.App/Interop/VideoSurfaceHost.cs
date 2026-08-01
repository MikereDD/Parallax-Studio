using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ParallaxStudio.App.Interop;

public sealed class VideoSurfaceHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int SsBlackRect = 0x00000004;
    private nint _handle;

    public event EventHandler<VideoHandleCreatedEventArgs>? HandleCreated;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _handle = CreateWindowEx(
            0,
            "STATIC",
            string.Empty,
            WsChild | WsVisible | SsBlackRect,
            0,
            0,
            1,
            1,
            hwndParent.Handle,
            nint.Zero,
            nint.Zero,
            nint.Zero);

        if (_handle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the native video surface.");
        }

        Dispatcher.BeginInvoke(new Action(() => HandleCreated?.Invoke(this, new VideoHandleCreatedEventArgs(_handle))));
        return new HandleRef(this, _handle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != nint.Zero)
        {
            DestroyWindow(hwnd.Handle);
        }
        _handle = nint.Zero;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);
}
