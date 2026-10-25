using System.Text.Json;

namespace ParallaxStudio.Infrastructure.Playback;

/// <summary>
/// Server side of the DVD host control protocol: one JSON request per line, one JSON
/// response per line, strictly in order. Shared by ParallaxStudio.DvdPlaybackHost and the
/// test stub so the lifecycle tests exercise the real request/response loop.
/// </summary>
internal static class DvdHostRequestLoop
{
    public const string ShutdownCommand = "shutdown";

    /// <summary>
    /// Serves requests until the client closes the pipe, <paramref name="stop"/> is
    /// cancelled, or a <see cref="ShutdownCommand"/> request has been acknowledged.
    /// The shutdown acknowledgment is written before the loop returns; the caller begins
    /// teardown only afterwards. (Cancelling the stop token inside the handler, as the host
    /// did before, made the acknowledgment write observe a cancelled token, so the client
    /// saw the pipe close without a response.)
    /// </summary>
    /// <returns>True if the loop ended because shutdown was acknowledged.</returns>
    public static async Task<bool> RunAsync(
        TextReader reader,
        TextWriter writer,
        Func<DvdHostRequest, CancellationToken, Task<DvdHostResponse>> handler,
        CancellationToken stop)
    {
        while (await reader.ReadLineAsync(stop).ConfigureAwait(false) is { } line)
        {
            DvdHostRequest? request = null;
            DvdHostResponse response;
            try
            {
                request = JsonSerializer.Deserialize<DvdHostRequest>(line)
                    ?? throw new InvalidOperationException("Invalid DVD host request.");
                response = await handler(request, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                response = new DvdHostResponse(false, ex.Message);
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), stop).ConfigureAwait(false);
            await writer.FlushAsync(stop).ConfigureAwait(false);

            if (request?.Command == ShutdownCommand && response.Ok)
                return true;
        }

        return false;
    }
}
