using System.Runtime.InteropServices;

namespace ParallaxStudio.Infrastructure.Playback;

internal sealed class LibMpvLibrary : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint MpvCreate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvInitialize(nint context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MpvTerminateDestroy(nint context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvSetOptionString(nint context, nint name, nint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvSetPropertyString(nint context, nint name, nint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint MpvGetPropertyString(nint context, nint name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MpvFree(nint data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvCommand(nint context, nint arguments);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong MpvClientApiVersion();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint MpvErrorString(int error);

    private nint _libraryHandle;
    private MpvCreate? _create;
    private MpvInitialize? _initialize;
    private MpvTerminateDestroy? _terminateDestroy;
    private MpvSetOptionString? _setOptionString;
    private MpvSetPropertyString? _setPropertyString;
    private MpvGetPropertyString? _getPropertyString;
    private MpvFree? _free;
    private MpvCommand? _command;
    private MpvClientApiVersion? _clientApiVersion;
    private MpvErrorString? _errorString;

    public string? LoadedPath { get; private set; }
    public bool IsLoaded => _libraryHandle != nint.Zero;

    public bool TryLoad(out string? error)
    {
        error = null;
        if (IsLoaded) return true;

        foreach (var candidate in GetCandidates())
        {
            try
            {
                if (!NativeLibrary.TryLoad(candidate, out _libraryHandle)) continue;
                LoadedPath = candidate;
                BindExports();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (_libraryHandle != nint.Zero)
                {
                    NativeLibrary.Free(_libraryHandle);
                    _libraryHandle = nint.Zero;
                }
            }
        }

        error ??= "libmpv-2.dll was not found in the application folder, runtimes folder, or PARALLAX_STUDIO_LIBMPV path.";
        return false;
    }

    public string GetClientApiVersion()
    {
        EnsureLoaded();
        var version = _clientApiVersion!();
        var major = version >> 16;
        var minor = version & 0xffff;
        return $"libmpv client API {major}.{minor}";
    }

    public nint Create()
    {
        EnsureLoaded();
        var handle = _create!();
        if (handle == nint.Zero) throw new InvalidOperationException("mpv_create returned a null context.");
        return handle;
    }

    public void SetOption(nint context, string name, string value) =>
        InvokeWithUtf8(name, value, (namePointer, valuePointer) =>
            Check(_setOptionString!(context, namePointer, valuePointer), $"set option {name}"));

    public void SetProperty(nint context, string name, string value) =>
        InvokeWithUtf8(name, value, (namePointer, valuePointer) =>
            Check(_setPropertyString!(context, namePointer, valuePointer), $"set property {name}"));

    public void Initialize(nint context) => Check(_initialize!(context), "initialize libmpv");

    public string? GetProperty(nint context, string name)
    {
        var namePointer = nint.Zero;
        var valuePointer = nint.Zero;
        try
        {
            namePointer = Marshal.StringToCoTaskMemUTF8(name);
            valuePointer = _getPropertyString!(context, namePointer);
            return valuePointer == nint.Zero ? null : Marshal.PtrToStringUTF8(valuePointer);
        }
        finally
        {
            if (valuePointer != nint.Zero) _free!(valuePointer);
            if (namePointer != nint.Zero) Marshal.FreeCoTaskMem(namePointer);
        }
    }

    public void Command(nint context, params string[] arguments)
    {
        var pointers = new nint[arguments.Length];
        var array = nint.Zero;
        try
        {
            for (var i = 0; i < arguments.Length; i++)
            {
                pointers[i] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
            }

            array = Marshal.AllocHGlobal((arguments.Length + 1) * nint.Size);
            for (var i = 0; i < pointers.Length; i++)
            {
                Marshal.WriteIntPtr(array, i * nint.Size, pointers[i]);
            }
            Marshal.WriteIntPtr(array, arguments.Length * nint.Size, nint.Zero);

            Check(_command!(context, array), $"command {string.Join(' ', arguments)}");
        }
        finally
        {
            if (array != nint.Zero) Marshal.FreeHGlobal(array);
            foreach (var pointer in pointers)
            {
                if (pointer != nint.Zero) Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    public void TerminateDestroy(nint context)
    {
        if (context != nint.Zero && _terminateDestroy is not null)
        {
            _terminateDestroy(context);
        }
    }

    private void BindExports()
    {
        _create = Bind<MpvCreate>("mpv_create");
        _initialize = Bind<MpvInitialize>("mpv_initialize");
        _terminateDestroy = Bind<MpvTerminateDestroy>("mpv_terminate_destroy");
        _setOptionString = Bind<MpvSetOptionString>("mpv_set_option_string");
        _setPropertyString = Bind<MpvSetPropertyString>("mpv_set_property_string");
        _getPropertyString = Bind<MpvGetPropertyString>("mpv_get_property_string");
        _free = Bind<MpvFree>("mpv_free");
        _command = Bind<MpvCommand>("mpv_command");
        _clientApiVersion = Bind<MpvClientApiVersion>("mpv_client_api_version");
        _errorString = Bind<MpvErrorString>("mpv_error_string");
    }

    private T Bind<T>(string exportName) where T : Delegate
    {
        var export = NativeLibrary.GetExport(_libraryHandle, exportName);
        return Marshal.GetDelegateForFunctionPointer<T>(export);
    }

    private void Check(int result, string operation)
    {
        if (result >= 0) return;

        var pointer = _errorString?.Invoke(result) ?? nint.Zero;
        var detail = pointer == nint.Zero ? $"error {result}" : Marshal.PtrToStringUTF8(pointer) ?? $"error {result}";
        throw new InvalidOperationException($"libmpv failed to {operation}: {detail}");
    }

    private static void InvokeWithUtf8(string name, string value, Action<nint, nint> action)
    {
        var namePointer = nint.Zero;
        var valuePointer = nint.Zero;
        try
        {
            namePointer = Marshal.StringToCoTaskMemUTF8(name);
            valuePointer = Marshal.StringToCoTaskMemUTF8(value);
            action(namePointer, valuePointer);
        }
        finally
        {
            if (valuePointer != nint.Zero) Marshal.FreeCoTaskMem(valuePointer);
            if (namePointer != nint.Zero) Marshal.FreeCoTaskMem(namePointer);
        }
    }

    private static IEnumerable<string> GetCandidates()
    {
        var explicitPath = Environment.GetEnvironmentVariable("PARALLAX_STUDIO_LIBMPV");
        if (!string.IsNullOrWhiteSpace(explicitPath)) yield return explicitPath;

        yield return Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll");
        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "libmpv-2.dll");
        yield return "libmpv-2.dll";
    }

    private void EnsureLoaded()
    {
        if (!IsLoaded) throw new InvalidOperationException("libmpv is not loaded.");
    }

    public void Dispose()
    {
        if (_libraryHandle == nint.Zero) return;
        NativeLibrary.Free(_libraryHandle);
        _libraryHandle = nint.Zero;
    }
}
