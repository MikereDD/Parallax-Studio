namespace ParallaxStudio.Core.Services;

/// <summary>
/// A disc title was deliberately not started because it could not be identified with
/// enough confidence (for example an ambiguous DVD title map). This is an expected, explained
/// refusal, not a playback failure. The message is suitable for showing to the user.
/// </summary>
public sealed class OpticalTitleUnavailableException(string message) : InvalidOperationException(message);
