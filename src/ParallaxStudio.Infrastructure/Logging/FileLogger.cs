using System.Text;
using ParallaxStudio.Core.Services;

namespace ParallaxStudio.Infrastructure.Logging;

public sealed class FileLogger : IAppLogger, IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public FileLogger(AppPaths paths)
    {
        paths.EnsureCreated();
        _path = paths.LogFile;
    }

    public Task InfoAsync(string message, CancellationToken cancellationToken = default) =>
        WriteAsync("INFO", message, null, cancellationToken);

    public Task WarningAsync(string message, CancellationToken cancellationToken = default) =>
        WriteAsync("WARN", message, null, cancellationToken);

    public Task ErrorAsync(string message, Exception? exception = null, CancellationToken cancellationToken = default) =>
        WriteAsync("ERROR", message, exception, cancellationToken);

    private async Task WriteAsync(
        string level,
        string message,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(
                _path,
                line + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
