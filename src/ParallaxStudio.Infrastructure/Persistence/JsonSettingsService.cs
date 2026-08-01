using ParallaxStudio.Core.Models;
using ParallaxStudio.Core.Services;

namespace ParallaxStudio.Infrastructure.Persistence;

public sealed class JsonSettingsService : ISettingsService
{
    private readonly AtomicJsonFile<AppSettings> _store;

    public JsonSettingsService(AppPaths paths, IAppLogger logger)
    {
        paths.EnsureCreated();
        _store = new AtomicJsonFile<AppSettings>(paths.SettingsFile, logger);
    }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        _store.LoadAsync(cancellationToken);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        _store.SaveAsync(settings, cancellationToken);
}
