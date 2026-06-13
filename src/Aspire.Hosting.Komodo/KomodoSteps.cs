using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Komodo;

/// <summary>The pipeline-step callbacks that back <see cref="KomodoExtensions.WithKomodoDeploySupport"/>.</summary>
internal static class KomodoSteps
{
    private const string ComposeFile = "docker-compose.yaml";

    // YAML flow indicators that break an UNQUOTED scalar — Komodo's compose round-trip drops Aspire's
    // quotes, so any resolved value containing these must go through a Komodo Variable ([[var]]).
    private static readonly char[] YamlFlowChars = { '{', '}', '[', ']' };

    /// <summary>
    /// PUBLISH face. Writes <c>komodo-&lt;stack&gt;.toml</c> next to the generated compose, embedding
    /// it as inline <c>file_contents</c>. Persists on disk — this is what <c>aspire publish</c> is for.
    /// </summary>
    public static async Task EmitResourceSyncAsync(PipelineStepContext context, KomodoDeployOptions options, string resourceName)
    {
        if (!options.EmitResourceSyncToml)
        {
            return;
        }

        var name = options.StackName ?? resourceName;
        var outDir = context.Services.GetRequiredService<IPipelineOutputService>().GetOutputDirectory();
        var composePath = Path.Combine(outDir, ComposeFile);
        if (!File.Exists(composePath))
        {
            context.Logger.LogWarning("Komodo: compose not found at {Path}; skipping resync TOML.", composePath);
            return;
        }

        var compose = await File.ReadAllTextAsync(composePath, context.CancellationToken);
        var tomlPath = Path.Combine(outDir, $"komodo-{name}.toml");
        await File.WriteAllTextAsync(tomlPath, KomodoResyncToml.Render(name, options.ServerName ?? "local", compose), context.CancellationToken);
        context.Logger.LogInformation("Komodo: wrote Resource-Sync TOML -> {Path}", tomlPath);
    }

    /// <summary>
    /// DEPLOY face. Reads the generated compose, upserts the Komodo Stack (inline file_contents) and
    /// triggers DeployStack via the API, waits for completion, then deletes the generated files so
    /// <c>aspire deploy</c> leaves nothing behind.
    /// </summary>
    public static async Task DeployAsync(PipelineStepContext context, KomodoDeployOptions options, string resourceName)
    {
        var ct = context.CancellationToken;
        var name = options.StackName ?? resourceName;

        if (string.IsNullOrWhiteSpace(options.CoreUrl) || string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.ApiSecret))
        {
            throw new InvalidOperationException(
                "Komodo deploy requires CoreUrl, ApiKey and ApiSecret. Set Komodo:CoreUrl / Komodo:ApiKey / Komodo:ApiSecret (user-secrets or environment).");
        }

        var outDir = context.Services.GetRequiredService<IPipelineOutputService>().GetOutputDirectory();
        var composePath = Path.Combine(outDir, ComposeFile);
        if (!File.Exists(composePath))
        {
            throw new FileNotFoundException($"Komodo deploy: generated compose not found at {composePath}.", composePath);
        }
        var compose = await File.ReadAllTextAsync(composePath, ct);

        // Aspire's deploy-time `prepare-{env}` step writes a fully-RESOLVED `.env.{environmentName}`
        // (concrete image refs, parameter values, ports) next to the keys-only publish `.env`. We use
        // it to resolve the compose's ${VAR}s ourselves (below) — secrets to Komodo Variables, the
        // rest inline.
        string? resolvedEnv = null;
        // The `.env.*` glob also matches the keys-only publish `.env` on .NET — exclude it; only the
        // `.env.{environmentName}` files carry the RESOLVED image refs/ports. Order deterministically: the
        // CI runner is persistent and Directory.GetFiles order isn't guaranteed, so picking the keys-only
        // `.env` non-deterministically left ${API_IMAGE} empty → Komodo's compose config failed with
        // "service 'api' has neither an image nor a build context".
        var resolvedEnvFiles = Directory.GetFiles(outDir, ".env.*")
            .Where(f => !string.Equals(Path.GetFileName(f), ".env", StringComparison.Ordinal))
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .ToArray();
        if (resolvedEnvFiles.Length > 0)
        {
            resolvedEnv = await File.ReadAllTextAsync(resolvedEnvFiles[0], ct);
            context.Logger.LogInformation("Komodo: using resolved env {File}.", Path.GetFileName(resolvedEnvFiles[0]));
        }
        else
        {
            context.Logger.LogWarning("Komodo: no resolved .env.* in {Dir}; stack placeholders may be unresolved.", outDir);
        }

        using var http = new HttpClient();
        var client = new KomodoApiClient(http, options.CoreUrl!, options.ApiKey!, options.ApiSecret!);

        var serverName = options.ServerName ?? "local";
        context.Logger.LogInformation("Komodo: resolving server '{Server}'…", serverName);
        var serverId = await client.ResolveServerIdAsync(serverName, ct);

        // Resolve ${KEY} placeholders into the compose ourselves and pass NO environment. Komodo's own
        // env/secret interpolation re-serialises the compose into an unparseable single-line string (a
        // CONFIRMED Core bug — the SAME compose with an empty environment deploys fine), and a [[var]]
        // Variable ref placed in the compose body is itself mangled by Komodo's YAML round-trip ('[' is a
        // flow char). Secrets are still registered as is_secret Komodo Variables (the vault + detection is
        // proven); they're just referenced inline for now. Flip inline -> [[var]] once Komodo's env
        // interpolation no longer mangles the compose.
        var secretKeys = context.Model.Resources.OfType<ParameterResource>()
            .Where(p => p.Secret)
            .Select(p => p.Name.ToUpperInvariant().Replace("-", "_"))
            .ToHashSet(StringComparer.Ordinal);
        context.Logger.LogInformation("Komodo: {Count} secret param(s) detected: {Keys}", secretKeys.Count, string.Join(", ", secretKeys));
        var (resolvedCompose, secrets) = ResolveComposeInline(compose, resolvedEnv, secretKeys, context);

        // Every detected secret must resolve to a non-empty value, else it would deploy blank → runtime auth failure.
        foreach (var kv in secrets)
        {
            if (string.IsNullOrEmpty(kv.Value))
            {
                throw new InvalidOperationException(
                    $"Komodo deploy: secret '{kv.Key}' resolved to an empty value — it would deploy blank. " +
                    $"Ensure the deploy injected its value (e.g. a GitHub Actions secret as Parameters__<name>).");
            }
        }

        resolvedCompose = SkipExistingServices(resolvedCompose, context);

        // Secrets reach the container through Komodo's compose_cmd_wrapper, NOT the compose body: the
        // SecretProvider registers each value in its vault (Komodo Variables by default) and emits a wrapper
        // that injects them into the compose command's env. The value never enters the stored compose, and
        // because Komodo only interpolates [[var]] in the wrapper field, this never trips the flatten bug.
        // Swap options.SecretProvider to retarget the vault (e.g. an external secrets manager) — it's just a different wrapper.
        var composeWrapper = await options.SecretProvider.BuildWrapperAsync(
            name, secrets,
            async (key, value, token) =>
            {
                var varName = $"{name}_{key}".ToLowerInvariant().Replace("-", "_");
                await client.UpsertVariableAsync(varName, value, true, token);
                return $"[[{varName}]]";
            },
            ct);
        if (composeWrapper is not null)
        {
            context.Logger.LogInformation(
                "Komodo: {Count} secret(s) vaulted + injected via compose_cmd_wrapper (values never enter the stored compose).", secrets.Count);
        }

        context.Logger.LogInformation("Komodo: upserting stack '{Stack}' on '{Server}'…", name, serverName);
        await client.UpsertStackAsync(name, serverId, resolvedCompose, ct,
            registryProvider: options.RegistryProvider, registryAccount: options.RegistryAccount,
            composeCmdWrapper: composeWrapper);
        if (!string.IsNullOrEmpty(options.RegistryAccount))
        {
            context.Logger.LogInformation("Komodo: stack pulls private images via registry account '{Account}' ({Provider}).",
                options.RegistryAccount, options.RegistryProvider);
        }

        context.Logger.LogInformation("Komodo: deploying stack '{Stack}'…", name);
        var updateId = await client.DeployStackAsync(name, ct);
        await client.WaitForUpdateAsync(updateId, TimeSpan.FromMinutes(3), ct);
        context.Logger.LogInformation("Komodo: stack '{Stack}' deployed (update {Update}).", name, updateId);

        // Clean up so `aspire deploy` leaves no temp files behind (`aspire publish` keeps them).
        TryDelete(composePath);
        foreach (var env in Directory.GetFiles(outDir, ".env*"))
        {
            TryDelete(env);
        }
        TryDelete(Path.Combine(outDir, $"komodo-{name}.toml"));
        context.Logger.LogInformation("Komodo: cleaned up generated files in {Dir}.", outDir);
    }

    /// <summary>
    /// DESTROY face. Tears the Komodo stack down on <c>aspire destroy</c>: DestroyStack (bring containers
    /// down) → wait → DeleteStack (remove the record). Mirrors the deploy step; a missing stack is success
    /// (already gone). The local <c>docker compose down</c> is neutralized in <see cref="KomodoExtensions"/>.
    /// </summary>
    public static async Task DestroyAsync(PipelineStepContext context, KomodoDeployOptions options, string resourceName)
    {
        var ct = context.CancellationToken;
        var name = options.StackName ?? resourceName;

        if (string.IsNullOrWhiteSpace(options.CoreUrl) || string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.ApiSecret))
        {
            throw new InvalidOperationException(
                "Komodo destroy requires CoreUrl, ApiKey and ApiSecret. Set Komodo:CoreUrl / Komodo:ApiKey / Komodo:ApiSecret (user-secrets or environment).");
        }

        using var http = new HttpClient();
        var client = new KomodoApiClient(http, options.CoreUrl!, options.ApiKey!, options.ApiSecret!);

        var existingId = await client.FindStackIdAsync(name, ct);
        if (existingId is null)
        {
            context.Logger.LogInformation("Komodo: stack '{Stack}' not found — nothing to destroy.", name);
            return;
        }

        context.Logger.LogInformation("Komodo: destroying stack '{Stack}'…", name);
        var updateId = await client.DestroyStackAsync(name, ct);
        if (updateId is not null)
        {
            try
            {
                await client.WaitForUpdateAsync(updateId, TimeSpan.FromMinutes(2), ct);
            }
            catch (Exception ex)
            {
                // A down that errors (e.g. already partly down) shouldn't block removing the record.
                context.Logger.LogWarning(
                    "Komodo: DestroyStack update {Update} didn't complete cleanly ({Msg}); deleting the record anyway.", updateId, ex.Message);
            }
        }
        await client.DeleteStackAsync(existingId, ct);
        context.Logger.LogInformation("Komodo: stack '{Stack}' destroyed + deleted.", name);
    }

    /// <summary>
    /// Resolve Aspire's <c>${KEY}</c> placeholders and send the compose with NO environment. Komodo's own
    /// env interpolation re-serialises the compose into an unparseable single-line string (a confirmed Core
    /// bug — the same compose with an empty environment deploys fine), so we resolve ourselves.
    /// <para>
    /// NON-secret values are inlined into the compose (and must be free of YAML flow chars <c>{}[]</c>,
    /// which break the parse once Komodo drops Aspire's quotes). SECRET values are NOT inlined — their
    /// <c>${KEY}</c> stays in the compose and is injected at run time via the SecretProvider's
    /// compose_cmd_wrapper (the returned secret list), so the value never enters the stored compose. Because
    /// secrets travel through the wrapper, not the compose body, they are exempt from the flow-char rule.
    /// </para>
    /// </summary>
    /// <returns>The compose with non-secret placeholders inlined, plus the secret <c>(KEY, value)</c> pairs
    /// to route through the wrapper.</returns>
    private static (string Compose, List<KeyValuePair<string, string>> Secrets) ResolveComposeInline(
        string compose, string? resolvedEnv, HashSet<string> secretKeys, PipelineStepContext context)
    {
        var secrets = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(resolvedEnv))
        {
            return (compose, secrets);
        }
        var total = 0;
        var flowWarned = false;
        foreach (var raw in resolvedEnv.Split('\n'))
        {
            var trimmed = raw.Trim();
            var eq = trimmed.IndexOf('=');
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || eq <= 0)
            {
                continue;
            }
            var key = trimmed[..eq];
            var value = trimmed[(eq + 1)..];
            if (secretKeys.Contains(key))
            {
                // Secret: do NOT inline. Leave ${KEY} in the compose; the value is vaulted and injected via
                // the compose_cmd_wrapper (see DeployAsync) — it never gets written into the stored compose.
                secrets.Add(new KeyValuePair<string, string>(key, value));
                continue;
            }
            if (!flowWarned && value.IndexOfAny(YamlFlowChars) >= 0)
            {
                context.Logger.LogWarning(
                    "Komodo: '{Key}' has a YAML flow char ({{}}[]); inline resolution may break Komodo's parse — prefer a safe-charset value (secrets are exempt; they go through the wrapper).", key);
                flowWarned = true;
            }
            compose = compose.Replace("${" + key + "}", value, StringComparison.Ordinal);
            total++;
        }
        context.Logger.LogInformation(
            "Komodo: resolved {Total} non-secret placeholder(s) inline; {Secret} secret(s) routed to the vault wrapper.", total, secrets.Count);
        return (compose, secrets);
    }

    /// <summary>
    /// Drop the compose service for any resource marked <c>PublishAsExisting</c> (KomodoSkipServiceAnnotation):
    /// it's referenced as an already-running instance, so no container is emitted. Dependents still get a
    /// correct <c>ConnectionStrings__&lt;name&gt;</c> via the ConnectionStringRedirectAnnotation set at the
    /// model level. When the annotation carries a <c>Network</c>, dependents are first joined to that external
    /// network so they can REACH the instance (a redirected connection string isn't reachable if the instance
    /// is off the stack's own network). We then strip any <c>depends_on</c> entry pointing at the dropped
    /// service (else compose errors on an undefined dependency) and remove the depends_on block if empty.
    /// </summary>
    private static string SkipExistingServices(string compose, PipelineStepContext context)
    {
        var skip = context.Model.Resources
            .Select(r => (Name: r.Name.ToLowerInvariant(), Annotation: r.Annotations.OfType<KomodoSkipServiceAnnotation>().FirstOrDefault()))
            .Where(x => x.Annotation is not null)
            .Select(x => (x.Name, x.Annotation!.Network))
            .ToList();
        if (skip.Count == 0)
        {
            return compose;
        }

        // Pass 1 — reachability. For an existing instance pinned to an external network, join its dependents
        // to that network: the connection string was redirected, but a redirect isn't reachable if the
        // instance sits off the stack's own network. Done BEFORE the depends_on entries are stripped below —
        // that's how we identify the dependents.
        foreach (var (name, network) in skip)
        {
            if (string.IsNullOrWhiteSpace(network))
            {
                continue;
            }
            var dependents = FindDependentServices(compose, name);
            foreach (var dependent in dependents)
            {
                compose = JoinServiceToNetwork(compose, dependent, network!, context);
            }
            if (dependents.Count == 0)
            {
                context.Logger.LogWarning(
                    "Komodo: PublishAsExisting — '{Svc}' has network '{Net}' but no dependents (no depends_on) were found to join.", name, network);
                continue;
            }
            if (!compose.Contains($"\n  {network}:", StringComparison.Ordinal))
            {
                compose = Regex.Replace(compose, @"(?m)^networks:[ \t]*$",
                    $"networks:\n  {network}:\n    name: {network}\n    external: true");
            }
            context.Logger.LogInformation(
                "Komodo: PublishAsExisting — joined {Count} dependent(s) of '{Svc}' to external network '{Net}': {Deps}",
                dependents.Count, name, network, string.Join(", ", dependents));
        }

        // Pass 2 — drop the existing service + any depends_on entry pointing at it.
        foreach (var (name, _) in skip)
        {
            compose = RemoveYamlKeyBlock(compose, name, 2);  // the service under services:
            compose = RemoveYamlKeyBlock(compose, name, 6);  // any `depends_on: <svc>` entry (6-space)
            context.Logger.LogInformation(
                "Komodo: PublishAsExisting — dropped service '{Svc}'; dependents use the existing instance.", name);
        }
        // Remove now-empty depends_on: blocks (a depends_on: immediately followed by a same-indent sibling).
        compose = Regex.Replace(compose, @"(?m)^(?<ind>[ ]+)depends_on:[ ]*\r?\n(?=\k<ind>[^ ])", "");
        return compose;
    }

    /// <summary>
    /// Find the compose services whose <c>depends_on</c> references <paramref name="target"/> (map form
    /// <c>target:</c> or list form <c>- target</c>) — i.e. the dependents that reference the existing
    /// resource. Line-based; no YAML re-serialize.
    /// </summary>
    private static List<string> FindDependentServices(string compose, string target)
    {
        var dependents = new List<string>();
        string? current = null;
        var inDependsOn = false;
        foreach (var raw in compose.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var body = line.Trim();
            if (body.Length == 0)
            {
                continue;
            }
            var indent = line.Length - line.TrimStart(' ').Length;
            if (indent == 2 && body.EndsWith(':'))            // a service under services: (or a top-level map entry)
            {
                current = body[..^1];
                inDependsOn = false;
            }
            else if (indent == 4)                             // a key directly under the service
            {
                inDependsOn = body.StartsWith("depends_on:", StringComparison.Ordinal);
            }
            else if (inDependsOn && indent == 6 && current is not null && current != target
                     && (body == $"{target}:" || body == $"- {target}"))
            {
                dependents.Add(current);
            }
        }
        return dependents.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Append an external network to one service's <c>networks:</c> list (line-based, no
    /// re-serialize). Adds a networks block if the service has none; no-ops if already joined.</summary>
    private static string JoinServiceToNetwork(string compose, string service, string network, PipelineStepContext context)
    {
        var lines = compose.Split('\n').ToList();
        var prefix = $"  {service}:";
        var header = lines.FindIndex(l => l.StartsWith(prefix, StringComparison.Ordinal) && l[prefix.Length..].Trim().Length == 0);
        if (header < 0)
        {
            context.Logger.LogWarning("Komodo: PublishAsExisting — dependent '{Service}' not found in compose; network '{Net}' not joined.", service, network);
            return compose;
        }
        // Block end = next non-blank line indented <= 2 (a sibling service or a top-level key).
        var end = header + 1;
        while (end < lines.Count)
        {
            var l = lines[end];
            var indent = l.Length - l.TrimStart(' ').Length;
            if (l.Trim().Length != 0 && indent <= 2)
            {
                break;
            }
            end++;
        }
        var netIdx = -1;
        for (var i = header + 1; i < end; i++)
        {
            if (lines[i].TrimEnd() == "    networks:")
            {
                netIdx = i;
                break;
            }
        }
        if (netIdx < 0)
        {
            lines.Insert(header + 1, $"    networks:\n      - \"{network}\"");
            return string.Join("\n", lines);
        }
        var insertAt = netIdx + 1;
        while (insertAt < end && lines[insertAt].TrimStart().StartsWith("- ", StringComparison.Ordinal))
        {
            if (lines[insertAt].Contains($"\"{network}\"", StringComparison.Ordinal) || lines[insertAt].Trim() == $"- {network}")
            {
                return compose;  // already joined
            }
            insertAt++;
        }
        lines.Insert(insertAt, $"      - \"{network}\"");
        return string.Join("\n", lines);
    }

    /// <summary>Remove a YAML mapping key and its nested block at an EXACT indent (line-based — no YAML
    /// re-serialize, which Komodo would mangle). Stops at the first sibling/parent line.</summary>
    private static string RemoveYamlKeyBlock(string yaml, string key, int indent)
    {
        var prefix = new string(' ', indent) + key + ":";
        var lines = yaml.Split('\n');
        var result = new List<string>(lines.Length);
        var skipping = false;
        foreach (var line in lines)
        {
            if (skipping)
            {
                var curIndent = line.Length - line.TrimStart(' ').Length;
                if (line.Trim().Length != 0 && curIndent <= indent)
                {
                    skipping = false; // boundary — a sibling/parent line; fall through and keep it
                }
                else
                {
                    continue; // still inside the removed block
                }
            }
            if (line.StartsWith(prefix, StringComparison.Ordinal) && line[prefix.Length..].Trim().Length == 0)
            {
                skipping = true;
                continue; // drop the key line itself
            }
            result.Add(line);
        }
        return string.Join("\n", result);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}
