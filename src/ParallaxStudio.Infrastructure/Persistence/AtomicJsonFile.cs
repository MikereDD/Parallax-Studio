using System.Text.Json;
using ParallaxStudio.Core.Services;

namespace ParallaxStudio.Infrastructure.Persistence;

internal sealed class AtomicJsonFile<T> where T : class, new()
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AtomicJsonFile(string path, IAppLogger logger)
    {
        _path = path;
        _logger = logger;
    }

    public async Task<T> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return new T();

            try
            {
                await using var stream = File.OpenRead(_path);
                return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false) ?? new T();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                var corruptPath = _path + $".corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
                try
                {
                    File.Move(_path, corruptPath, overwrite: true);
                }
                catch
                {
                    // Recovery logging below still records the original failure.
                }

                await _logger.ErrorAsync(
                    $"Recovered from invalid JSON store: {_path}",
                    ex,
                    cancellationToken).ConfigureAwait(false);
                return new T();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(T value, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tempPath = _path + ".tmp";

            await using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
