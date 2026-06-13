namespace Aspire.Hosting.Komodo;

/// <summary>
/// Pluggable secret injection. The secret VALUES never enter the stored compose — the provider produces a
/// Komodo <c>compose_cmd_wrapper</c> that injects them into the compose command's environment, and chooses
/// the reference form. A pluggable plane: swap the provider to swap the vault.
/// <see cref="KomodoVariablesSecretProvider"/> (the default) uses Komodo Variables; to back secrets with
/// an external vault, implement <see cref="ISecretProvider"/> to wrap the compose command in your vault's CLI.
/// </summary>
public interface ISecretProvider
{
    /// <summary>
    /// Build the Komodo <c>compose_cmd_wrapper</c> that injects <paramref name="secrets"/> into the compose
    /// command's environment (so their values never enter the compose body, which keeps <c>${KEY}</c>).
    /// </summary>
    /// <param name="stackName">The Komodo stack name (e.g. for namespacing vault entries).</param>
    /// <param name="secrets">The secret env <c>(KEY, resolved value)</c> pairs the container needs.</param>
    /// <param name="vaultSecret">Registers a value in Komodo's Variable store (<c>is_secret</c>) and returns
    /// its <c>[[name]]</c> reference. The Komodo-Variables provider uses it; an external-vault provider
    /// ignores it and emits its own refs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The wrapper string, or <see langword="null"/> when there are no secrets.</returns>
    Task<string?> BuildWrapperAsync(
        string stackName,
        IReadOnlyList<KeyValuePair<string, string>> secrets,
        Func<string, string, CancellationToken, Task<string>> vaultSecret,
        CancellationToken ct);
}

/// <summary>
/// Default secret provider: registers each secret as an <c>is_secret</c> Komodo Variable and refs it via
/// <c>[[var]]</c> in the wrapper — so the value lives only in Komodo's vault, never in the stored compose.
/// Komodo interpolates <c>[[var]]</c> in the wrapper (which wraps the compose <em>command</em>), NOT in the
/// compose body, so this sidesteps Komodo's compose-flatten-on-interpolate bug entirely.
/// </summary>
public sealed class KomodoVariablesSecretProvider : ISecretProvider
{
    /// <inheritdoc />
    public async Task<string?> BuildWrapperAsync(
        string stackName,
        IReadOnlyList<KeyValuePair<string, string>> secrets,
        Func<string, string, CancellationToken, Task<string>> vaultSecret,
        CancellationToken ct)
    {
        if (secrets.Count == 0)
        {
            return null;
        }

        var assignments = new List<string>(secrets.Count);
        foreach (var secret in secrets)
        {
            // Register the value in the vault; get back its [[name]] reference. Komodo substitutes
            // [[name]] → value into THIS wrapper string before the command runs, so quote the assignment
            // to keep a value with spaces intact for the shell that executes the wrapper.
            var reference = await vaultSecret(secret.Key, secret.Value, ct).ConfigureAwait(false);
            assignments.Add($"{secret.Key}=\"{reference}\"");
        }

        // No `--` after the assignments: `env` treats the first non-NAME=VALUE token as the command, so
        // `env A=b docker compose up` is correct. (A bare `--` here makes env try to exec `--` itself.)
        return $"env {string.Join(" ", assignments)} [[COMPOSE_COMMAND]]";
    }
}
