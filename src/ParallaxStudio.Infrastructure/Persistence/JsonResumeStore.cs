using ParallaxStudio.Core.Models;
using ParallaxStudio.Core.Services;

namespace ParallaxStudio.Infrastructure.Persistence;

public sealed class JsonResumeStore : IResumeStore
{
    private sealed class ResumeDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, ResumeEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly AtomicJsonFile<ResumeDocument> _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonResumeStore(AppPaths paths, IAppLogger logger)
    {
        paths.EnsureCreated();
        _store = new AtomicJsonFile<ResumeDocument>(paths.ResumeFile, logger);
    }

    public async Task<ResumeEntry?> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        var key = Normalize(path);
        var document = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return document.Entries.GetValueOrDefault(key);
    }

    public async Task UpsertAsync(ResumeEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            document.Entries[Normalize(entry.Path)] = entry with { Path = Normalize(entry.Path) };
            await _store.SaveAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string path, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (document.Entries.Remove(Normalize(path)))
            {
                await _store.SaveAsync(document, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
}
