using System;
using System.Threading;
using System.Threading.Tasks;
using Ghuboon.Core.Abstractions;
using Serilog;

namespace Ghuboon.App.Services;

/// <summary>
/// Post-fetch local persistence for a single timeline row. Pulled out
/// of <c>TimelineItemViewModel.EnsureBodyLoadedCoreAsync</c> so the row
/// VM stops carrying three near-identical try/catch/log loops around
/// repository writes.
/// <para>
/// Each helper is guard-aware: missing repository, missing id, or empty
/// payload short-circuits to a no-op so call sites no longer need to
/// repeat the same null-check ladder. Failures are logged at
/// Information level (non-fatal — the in-memory body / actor stays
/// rendered) and never re-thrown except for
/// <see cref="OperationCanceledException"/>, which matches the previous
/// inline behaviour.
/// </para>
/// </summary>
internal static class TimelineItemPersistence
{
    /// <summary>
    /// Persist a freshly fetched body (and its author, when present) to
    /// the per-event cache. No-op if the body is empty, the event repo
    /// isn't wired, or the row doesn't have a local event id.
    /// </summary>
    public static async Task PersistBodyAsync(
        INotificationEventRepository? eventRepo,
        long? eventId,
        string? body,
        string? authorLogin,
        ILogger? log,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(body) || eventRepo is null || eventId is not { } id)
        {
            return;
        }
        try
        {
            await eventRepo.SetBodyAsync(id, body, authorLogin, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Information(ex, "Persisting body for event {EventId} failed (non-fatal)", id);
        }
    }

    /// <summary>
    /// Persist the resolved actor login both at the per-event level
    /// (event repo) and at the per-notification level (notifications
    /// repo). Each leg has independent try/catch so one failure doesn't
    /// poison the other. No-op if the author is empty.
    /// </summary>
    public static async Task PersistActorLoginAsync(
        INotificationEventRepository? eventRepo,
        INotificationRepository? notifRepo,
        long? eventId,
        string notificationId,
        string? authorLogin,
        ILogger? log,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(authorLogin)) return;

        if (eventRepo is not null && eventId is { } id)
        {
            try
            {
                await eventRepo.SetActorLoginAsync(id, authorLogin, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log?.Information(ex, "Persisting actor_login for event {EventId} failed (non-fatal)", id);
            }
        }

        if (notifRepo is not null && !string.IsNullOrEmpty(notificationId))
        {
            try
            {
                await notifRepo.SetActorLoginAsync(notificationId, authorLogin, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log?.Information(ex, "Persisting actor_login for notification {NotificationId} failed (non-fatal)", notificationId);
            }
        }
    }
}
