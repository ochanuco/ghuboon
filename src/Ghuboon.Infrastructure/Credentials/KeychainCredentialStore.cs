using System.Diagnostics;
using System.Text;
using Ghuboon.Core.Abstractions;

namespace Ghuboon.Infrastructure.Credentials;

/// <summary>
/// macOS Keychain-backed credential store using the <c>security</c> CLI.
/// </summary>
/// <remarks>
/// MVP trade-off: <c>security add-generic-password -w &lt;value&gt;</c> places the secret on the
/// command line, so during the brief subprocess lifetime the value is visible to <c>ps</c> for
/// the same user. ADR-006 accepts this for simplicity. TODO: replace with Security framework
/// P/Invoke (SecKeychain* APIs) or a <c>pinentry</c>-style helper that reads from stdin.
/// </remarks>
public sealed class KeychainCredentialStore : ICredentialStore
{
    private const int ItemNotFoundExitCode = 44;

    private readonly string _service;

    public KeychainCredentialStore(string service = "com.ghuboon.dev")
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            throw new ArgumentException("Service must be non-empty.", nameof(service));
        }

        _service = service;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        ValidateKey(key);

        var result = await RunSecurityAsync(
            new[] { "find-generic-password", "-s", _service, "-a", key, "-w" },
            ct).ConfigureAwait(false);

        if (result.ExitCode == ItemNotFoundExitCode)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"security find-generic-password failed with exit code {result.ExitCode}: {result.Stderr}");
        }

        // -w prints the password followed by a newline.
        return result.Stdout.TrimEnd('\n', '\r');
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);

        // -U updates the entry if it already exists.
        // NOTE: value travels via argv; do NOT log this argument list. See class remarks.
        var result = await RunSecurityAsync(
            new[] { "add-generic-password", "-U", "-s", _service, "-a", key, "-w", value },
            ct,
            redactLastArg: true).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"security add-generic-password failed with exit code {result.ExitCode}: {result.Stderr}");
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ValidateKey(key);

        var result = await RunSecurityAsync(
            new[] { "delete-generic-password", "-s", _service, "-a", key },
            ct).ConfigureAwait(false);

        if (result.ExitCode == 0 || result.ExitCode == ItemNotFoundExitCode)
        {
            return;
        }

        throw new InvalidOperationException(
            $"security delete-generic-password failed with exit code {result.ExitCode}: {result.Stderr}");
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key must be non-empty.", nameof(key));
        }
    }

    private static async Task<ProcessResult> RunSecurityAsync(
        IReadOnlyList<string> args,
        CancellationToken ct,
        bool redactLastArg = false)
    {
        // redactLastArg is kept as an explicit signal for any future logging — never log argv
        // when this flag is true. We do not log argv anywhere today.
        _ = redactLastArg;

        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/security",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.Append(e.Data).Append('\n');
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.Append(e.Data).Append('\n');
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation arrived while the security CLI was still running. Tear the
            // child process tree down so we do not leak a subprocess holding the
            // Keychain prompt, then drain the exit without re-honoring the cancelled
            // token. This is a credential-store cleanup path; rethrow the OCE so the
            // caller can react to cancellation (do not remap to a network/API
            // exception — the operation was cancelled, it did not fail on the wire).
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Process already exited between the HasExited check and Kill.
            }
            catch (NotSupportedException)
            {
                // Some platforms cannot terminate the whole tree; accept partial cleanup.
            }

            try
            {
                using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(drainCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Drain timed out — best effort; the process is detached and will be
                // reaped by the OS. We still need to surface the original cancellation.
            }

            throw;
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private readonly record struct ProcessResult(int ExitCode, string Stdout, string Stderr);
}
