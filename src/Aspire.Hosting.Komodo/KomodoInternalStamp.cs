using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Komodo;

/// <summary>
/// Publish-phase step that makes INTERNAL compose service references stack-unique, so docker's embedded DNS
/// can no longer round-robin one stack's traffic onto another stack's same-named service across a shared
/// external network.
/// </summary>
/// <remarks>
/// Aspire names each compose service by its resource name (<c>api</c>, <c>web</c>), which becomes a network
/// alias. When stacks share an external network, a bare reference like <c>http://api:8080</c> can resolve to
/// ANY stack's <c>api</c>. The ingress provider already neutralises this for externally-exposed services by
/// stamping a stack-unique <c>container_name</c> and routing ingress to it; internal services get no such
/// treatment, forcing consumers to hand-pin references. This step generalises that fix:
/// <list type="number">
///   <item>Stamp <c>container_name: {stack}-{service}</c> on every emitted INTERNAL service that lacks one.
///   Externally-exposed services are left entirely to the ingress provider, so the two writers never compete
///   on <c>container_name</c> regardless of step order — there is no ordering dependency and no risk of a
///   mismatched ingress backend hostname (502).</item>
///   <item>Read each service's EFFECTIVE <c>container_name</c> back out of the compose and rewrite the HOST of
///   <c>scheme://host</c> URLs in environment VALUES whose host is exactly an emitted service, to that
///   read-back name. Rewriting toward the value actually on disk (never a recomputed guess) keeps east-west
///   references and ingress agreeing even if the ingress provider used a different stack-name source.</item>
/// </list>
/// The emitted-service set is taken from the MODEL minus <see cref="KomodoSkipServiceAnnotation"/> resources
/// (the <c>*AsExisting</c> family): their service blocks are still present at publish time (stripped later, in
/// the deploy step) but must never be stamped/rewritten — they resolve to an external instance. Compose is
/// edited AS TEXT (line/regex), never re-serialised, because a downstream deploy may re-upload it verbatim.
/// Publish-only (Publish-phase steps don't run in <c>aspire run</c>) and idempotent.
/// </remarks>
internal static class KomodoInternalStamp
{
    private const string ComposeFile = "docker-compose.yaml";

    public static async Task StampAndRewriteAsync(
        PipelineStepContext context, KomodoDeployOptions options, string resourceName)
    {
        var outDir = context.Services.GetRequiredService<IPipelineOutputService>().GetOutputDirectory();
        var composePath = Path.Combine(outDir, ComposeFile);
        if (!File.Exists(composePath))
        {
            context.Logger.LogWarning("Komodo: compose not found at {Path}; skipping internal-ref stamp.", composePath);
            return;
        }

        // The Komodo stack name == the compose project name; the SAME value DeployAsync/EmitResourceSync use.
        var stack = string.IsNullOrWhiteSpace(options.StackName) ? resourceName : options.StackName;

        // Emitted-service set from the MODEL, not the text: subtract the *AsExisting resources, whose service
        // blocks are still in the compose at publish time but must NOT be stamped/rewritten.
        var skip = new HashSet<string>(
            context.Model.Resources
                .Where(r => r.Annotations.OfType<KomodoSkipServiceAnnotation>().Any())
                .Select(r => r.Name.ToLowerInvariant()),
            StringComparer.Ordinal);
        var services = context.Model.Resources
            .OfType<IComputeResource>()
            .Select(r => r.Name.ToLowerInvariant())
            .Where(n => !skip.Contains(n))
            .ToHashSet(StringComparer.Ordinal);
        // Externally-exposed services are the ingress provider's to stamp; we leave their container_name alone
        // (detected the same way the ingress provider does — an external endpoint annotation).
        var external = context.Model.Resources
            .OfType<IComputeResource>()
            .Where(r => r.Annotations.OfType<EndpointAnnotation>().Any(e => e.IsExternal))
            .Select(r => r.Name.ToLowerInvariant())
            .Where(n => !skip.Contains(n))
            .ToHashSet(StringComparer.Ordinal);
        if (services.Count == 0)
        {
            return;
        }

        var compose = await File.ReadAllTextAsync(composePath, context.CancellationToken);
        var result = ApplyStackUniqueNaming(compose, services, external, stack, out var stamped, out var rewrites);
        await File.WriteAllTextAsync(composePath, result, context.CancellationToken);
        context.Logger.LogInformation(
            "Komodo: internal-ref stamp — {Stamped} service(s) stamped, {Rewrites} host reference(s) rewritten in {File}.",
            stamped, rewrites, ComposeFile);
    }

    /// <summary>
    /// Pure text transform (no IO): stamp a stack-unique <c>container_name</c> on every <paramref name="services"/>
    /// member NOT in <paramref name="external"/> that lacks one, then rewrite <c>scheme://host</c> URL hosts in
    /// environment VALUES whose host is one of <paramref name="services"/> to its effective <c>container_name</c>.
    /// Idempotent. Exposed internally for unit testing.
    /// </summary>
    /// <param name="services">All emitted real services (rewrite targets).</param>
    /// <param name="external">The subset that is externally-exposed — left unstamped (the ingress provider owns
    /// their <c>container_name</c>); still valid rewrite targets via their read-back name.</param>
    internal static string ApplyStackUniqueNaming(
        string compose, ISet<string> services, ISet<string> external, string stack, out int stamped, out int rewrites)
    {
        stamped = 0;
        rewrites = 0;
        if (services.Count == 0)
        {
            return compose;
        }

        // PASS 1 — stamp a stack-unique container_name on every emitted INTERNAL service that lacks one. Skip
        // externally-exposed services (the ingress provider owns those) and any service already carrying a
        // container_name (prior run / user-declared). Per-service re-split keeps index math trivial.
        foreach (var s in services)
        {
            if (external.Contains(s))
            {
                continue;
            }
            var lines = compose.Split('\n').ToList();
            var (header, end) = FindServiceBlock(lines, s);
            if (header < 0 || HasContainerName(lines, header, end))
            {
                continue;
            }
            lines.Insert(header + 1, $"    container_name: {stack}-{s}");
            compose = string.Join("\n", lines);
            stamped++;
        }

        // PASS 2 — read each service's EFFECTIVE container_name back out of the (now-stamped) compose. Internals
        // map to our stamp; externals to whatever the ingress provider stamped (if it has run / is installed).
        var effective = new Dictionary<string, string>(StringComparer.Ordinal);
        var scan = compose.Split('\n').ToList();
        foreach (var s in services)
        {
            var (header, end) = FindServiceBlock(scan, s);
            if (header < 0)
            {
                continue;
            }
            for (var i = header + 1; i < end; i++)
            {
                var t = scan[i].TrimStart();
                if (t.StartsWith("container_name:", StringComparison.Ordinal))
                {
                    var val = t["container_name:".Length..].Trim().Trim('"');
                    if (val.Length > 0)
                    {
                        effective[s] = val;
                    }
                    break;
                }
            }
        }
        if (effective.Count == 0)
        {
            return compose;
        }

        // PASS 3 — rewrite the HOST of scheme://host URLs in environment VALUES whose host is exactly an emitted
        // service, to that service's effective container_name. Only the value side of each `KEY: VALUE` env entry
        // is touched, so a service-discovery KEY (e.g. `services__api__http__0`) stays byte-identical while its
        // value host becomes stack-unique. Longest-name-first alternation avoids any prefix ambiguity (the host
        // terminator already prevents prefix matches). The scheme:// prefix (+ an optional userinfo segment, so
        // scheme://user:pass@host binds the host group to the real host) + exact-membership + terminator make
        // false positives (http://api.example.com, http://apiserver, Host=postgres) impossible.
        var alternation = string.Join("|",
            effective.Keys.OrderByDescending(k => k.Length).ThenBy(k => k, StringComparer.Ordinal).Select(Regex.Escape));
        var rx = new Regex($@"(?<pre>[a-zA-Z][a-zA-Z0-9+.\-]*://(?:[^@/?#\s]+@)?)(?<host>{alternation})(?<post>[:/?#""'\s]|$)");

        var localRewrites = 0;
        var outLines = compose.Split('\n');
        var inServices = false;
        var inEnv = false;
        for (var i = 0; i < outLines.Length; i++)
        {
            var line = outLines[i];
            if (line.Length == 0)
            {
                continue;
            }
            var indent = line.Length - line.TrimStart(' ').Length;
            var trimmed = line.Trim();
            if (indent == 0)
            {
                inServices = trimmed == "services:";
                inEnv = false;
                continue;
            }
            if (indent == 2 && inServices && trimmed.EndsWith(":", StringComparison.Ordinal))
            {
                inEnv = false; // new service block
                continue;
            }
            if (indent == 4 && inServices)
            {
                inEnv = trimmed == "environment:";
                continue;
            }
            if (inEnv && indent >= 6)
            {
                var idx = line.IndexOf(": ", StringComparison.Ordinal);
                if (idx < 0)
                {
                    continue;
                }
                var key = line[..idx];
                var value = line[(idx + 2)..];
                var newValue = rx.Replace(value, m =>
                {
                    if (effective.TryGetValue(m.Groups["host"].Value, out var c))
                    {
                        localRewrites++;
                        return m.Groups["pre"].Value + c + m.Groups["post"].Value;
                    }
                    return m.Value;
                });
                if (!string.Equals(newValue, value, StringComparison.Ordinal))
                {
                    outLines[i] = key + ": " + newValue;
                }
            }
        }

        rewrites = localRewrites;
        return string.Join("\n", outLines);
    }

    /// <summary>Locate a compose service block: the header line index and the exclusive end index (the next
    /// non-blank line indented &lt;= 2), or (-1, -1) if not found.</summary>
    private static (int header, int end) FindServiceBlock(List<string> lines, string service)
    {
        var prefix = $"  {service}:";
        var header = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.StartsWith(prefix, StringComparison.Ordinal) && l[prefix.Length..].Trim().Length == 0)
            {
                header = i;
                break;
            }
        }
        if (header < 0)
        {
            return (-1, -1);
        }
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
        return (header, end);
    }

    private static bool HasContainerName(List<string> lines, int header, int end)
    {
        for (var i = header + 1; i < end; i++)
        {
            if (lines[i].TrimStart().StartsWith("container_name:", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
