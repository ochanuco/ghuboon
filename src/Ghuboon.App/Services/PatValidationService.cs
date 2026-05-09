using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ghuboon.Core.Abstractions;

namespace Ghuboon.App.Services;

/// <summary>
/// Outcome of a PAT validation attempt, surfaced to the Settings UI.
/// </summary>
public enum PatValidationStatus
{
    None = 0,
    Valid,
    Invalid,
}

/// <summary>
/// Result of validating a PAT in the App layer. Always free of the PAT itself
/// (ADR-007, ADR-012): callers should never echo back the user's input.
/// </summary>
public sealed record PatValidationOutcome(
    PatValidationStatus Status,
    string? Login,
    string Message,
    ErrorCategory? ErrorCategory)
{
    public static PatValidationOutcome None { get; } =
        new(PatValidationStatus.None, null, string.Empty, null);
}

/// <summary>
/// Wraps <see cref="IGitHubApiClient.ValidateAsync"/> and translates results into
/// human-readable messages. Performs a basic shape check so we never send a string
/// that is obviously not a PAT to the GitHub API.
/// </summary>
public interface IPatValidationService
{
    Task<PatValidationOutcome> ValidateAsync(string pat, CancellationToken ct = default);
}

/// <inheritdoc cref="IPatValidationService"/>
public sealed class PatValidationService : IPatValidationService
{
    private readonly IGitHubApiClient _client;

    public PatValidationService(IGitHubApiClient client)
    {
        _client = client;
    }

    public async Task<PatValidationOutcome> ValidateAsync(string pat, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pat))
        {
            return new PatValidationOutcome(
                PatValidationStatus.Invalid,
                null,
                "Enter a Personal Access Token before validating.",
                ErrorCategory.Auth);
        }

        // Defensive shape-check. GitHub classic PATs start with ghp_, fine-grained with
        // github_pat_, but we do not require either prefix here (ADR-006 still permits
        // legacy hex tokens). We only reject obvious junk like raw whitespace.
        if (pat.Length < 20 || pat.Any(char.IsWhiteSpace))
        {
            return new PatValidationOutcome(
                PatValidationStatus.Invalid,
                null,
                "Token format looks invalid. Paste the token without surrounding whitespace.",
                ErrorCategory.Auth);
        }

        UserValidationResult result;
        try
        {
            result = await _client.ValidateAsync(pat, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The IGitHubApiClient contract returns structured failures; if the
            // implementation throws unexpectedly, surface a generic error rather
            // than leak exception text that could include sensitive data.
            return new PatValidationOutcome(
                PatValidationStatus.Invalid,
                null,
                "Could not contact GitHub. Check your network connection.",
                ErrorCategory.Network);
        }

        if (result.IsValid)
        {
            var login = result.Login ?? "(unknown user)";
            return new PatValidationOutcome(
                PatValidationStatus.Valid,
                result.Login,
                $"Valid for @{login}.",
                null);
        }

        var message = MapErrorMessage(result.Error, result.Message);
        return new PatValidationOutcome(
            PatValidationStatus.Invalid,
            null,
            message,
            result.Error);
    }

    internal static string MapErrorMessage(ErrorCategory? category, string? rawMessage)
    {
        return category switch
        {
            ErrorCategory.Auth => "Token rejected. Check the value and the required `notifications` scope.",
            ErrorCategory.RateLimit => "Rate limit hit. Try again later.",
            ErrorCategory.Network => "Network error. Check your connection and try again.",
            ErrorCategory.ApiCompatibility => "GitHub returned an unexpected response. Try again later.",
            ErrorCategory.Database => "A local storage error occurred while saving the token.",
            ErrorCategory.Unknown or null => string.IsNullOrWhiteSpace(rawMessage)
                ? "Token validation failed. Try again."
                : rawMessage,
            _ => "Token validation failed. Try again.",
        };
    }
}
