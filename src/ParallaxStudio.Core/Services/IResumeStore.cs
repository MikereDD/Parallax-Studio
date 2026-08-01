using ParallaxStudio.Core.Models;

namespace ParallaxStudio.Core.Services;

public interface IResumeStore
{
    Task<ResumeEntry?> GetAsync(string path, CancellationToken cancellationToken = default);
    Task UpsertAsync(ResumeEntry entry, CancellationToken cancellationToken = default);
    Task RemoveAsync(string path, CancellationToken cancellationToken = default);
}
