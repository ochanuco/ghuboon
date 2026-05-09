namespace Ghuboon.Core.Abstractions;

/// <summary>
/// High-level error categories used by logs and surfaced to the UI status bar.
/// </summary>
public enum ErrorCategory
{
    Auth,
    Network,
    RateLimit,
    ApiCompatibility,
    Database,
    Unknown,
}
