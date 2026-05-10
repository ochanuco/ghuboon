namespace Ghuboon.Core.Abstractions;

/// <summary>
/// High-level error categories used by logs and surfaced to the UI status bar.
/// </summary>
/// <remarks>
/// Values are explicitly assigned so numeric serialization (e.g., logs, metrics, persisted
/// telemetry) stays stable across refactors. Append new members with the next free integer;
/// never reorder or repurpose an existing value.
/// </remarks>
public enum ErrorCategory
{
    Auth = 0,
    Network = 1,
    RateLimit = 2,
    ApiCompatibility = 3,
    Database = 4,
    Unknown = 5,
}
