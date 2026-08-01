using ParallaxStudio.Core.Models;

namespace ParallaxStudio.Core.Services;

public interface IDiscService
{
    Task<IReadOnlyList<DiscSource>> GetAvailableDiscsAsync(CancellationToken cancellationToken = default);
}
