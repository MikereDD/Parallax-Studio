namespace ParallaxStudio.Core.Services;

public interface IAppLogger
{
    Task InfoAsync(string message, CancellationToken cancellationToken = default);
    Task WarningAsync(string message, CancellationToken cancellationToken = default);
    Task ErrorAsync(string message, Exception? exception = null, CancellationToken cancellationToken = default);
}
