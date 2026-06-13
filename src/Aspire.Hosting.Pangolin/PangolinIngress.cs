using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Pangolin;

/// <summary>One externally-exposed endpoint to route: the compose service + the port behind it.</summary>
/// <param name="ResourceName">The Aspire resource name (e.g. <c>web</c>).</param>
/// <param name="ServiceName">The compose service to locate + stamp (the resource name, lower-cased).</param>
/// <param name="Hostname">The stable, stack-unique name the ingress forwards to — the stamp sets it as the
/// service's <c>container_name</c> so it resolves unambiguously on a shared external ingress network (where the
/// bare service alias <c>web</c> would collide across preview stacks). Typically <c>{stack}-{service}</c>.</param>
/// <param name="Subdomain">The desired subdomain label (the stamp defaults it to the resource name).</param>
/// <param name="TargetPort">The container port the ingress forwards to.</param>
/// <param name="Scheme">Backend scheme — <c>http</c> or <c>https</c> (or <c>tcp</c>/<c>udp</c> for raw L4).</param>
/// <param name="Sso">Per-resource SSO override: <c>null</c> = use the provider's stack-wide default; a non-null
/// value forces this endpoint public (<c>false</c>) or gated (<c>true</c>) regardless of that default. Set via
/// <see cref="PangolinIngressExtensions.WithPublicIngress{T}"/> — e.g. an IdP that must stay reachable un-gated
/// inside a stack whose app endpoints are SSO-gated.</param>
/// <param name="UpstreamMethod">The scheme the ingress DIALS the backend with (Pangolin
/// <c>targets[].method</c>): <c>http</c>, <c>https</c>, or <c>h2c</c> (cleartext HTTP/2 — required for gRPC
/// backends that multiplex gRPC + HTTP on one cleartext port). <c>null</c> = derive from <see cref="Scheme"/>. Distinct from
/// the resource-level <c>protocol</c> (http|tcp|udp), which says what KIND of resource this is. Set via
/// <see cref="PangolinIngressExtensions.WithIngressUpstreamMethod{T}"/>.</param>
internal sealed record IngressTarget(
    string ResourceName,
    string ServiceName,
    string Hostname,
    string Subdomain,
    int TargetPort,
    string Scheme = "http",
    bool? Sso = null,
    string? UpstreamMethod = null);

/// <summary>
/// Pangolin ingress provider. Stamps <c>pangolin.public-resources.&lt;id&gt;.*</c> labels that a
/// <c>newt</c> watcher (mounting <c>docker.sock</c>) reports to Pangolin → Traefik routes
/// <c>&lt;subdomain&gt;.&lt;domain&gt;</c> to the service with automatic wildcard Let's Encrypt TLS. No
/// per-service Traefik/cert config is needed; the service only has to join the shared ingress
/// (<c>ingress_shared</c>) external network.
/// <para>
/// The ONLY coupling between the compute/deploy plane and the ingress plane is the docker labels stamped on a
/// service — so this provider is a drop-in alongside ANY compose deploy target, knowing nothing about it.
/// </para>
/// </summary>
public sealed class PangolinIngress
{
    private readonly string _domain;
    private readonly bool _sso;
    private readonly string? _ssoIdp;
    private readonly string[] _whitelistUsers;
    private readonly string? _apiUrl;
    private readonly string? _apiKey;
    private readonly string? _org;

    /// <param name="domain">Base domain, e.g. <c>example.com</c>. full-domain = <c>&lt;subdomain&gt;.&lt;domain&gt;</c>.</param>
    /// <param name="sso">Gate the resource behind Pangolin SSO.</param>
    /// <param name="ssoIdp">Auto-login IdP id when <paramref name="sso"/> is true. Defaults to <c>"1"</c>,
    /// Pangolin's first-configured IdP; set it to your IdP's id if different.</param>
    /// <param name="whitelistUsers">Allowed users when <paramref name="sso"/> is true.</param>
    /// <param name="apiUrl">Pangolin Integration API base URL (e.g. <c>https://api.&lt;domain&gt;</c>) — enables
    /// resource teardown on <c>aspire destroy</c> (newt doesn't reconcile removals; fosrl/pangolin#1864).</param>
    /// <param name="apiKey">Integration API bearer key. Teardown is a no-op unless apiUrl/apiKey/org are all set.</param>
    /// <param name="org">Pangolin org slug the resources live under (e.g. <c>my-org</c>).</param>
    public PangolinIngress(string domain, bool sso = false, string? ssoIdp = "1", string[]? whitelistUsers = null,
        string? apiUrl = null, string? apiKey = null, string? org = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        _domain = domain.Trim().TrimStart('.');
        _sso = sso;
        _ssoIdp = ssoIdp;
        _whitelistUsers = whitelistUsers ?? [];
        _apiUrl = string.IsNullOrWhiteSpace(apiUrl) ? null : apiUrl.TrimEnd('/');
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _org = string.IsNullOrWhiteSpace(org) ? null : org;
    }

    /// <summary>The shared ingress network — newt/Traefik reach the service over it.</summary>
    public string? RequiredNetwork => "ingress_shared";

    // Takes the internal IngressTarget DTO → internal surface (only the stamp step calls it). The public
    // overloads below take a bare subdomain string and are the consumer-facing surface.
    internal string GetExternalUrl(IngressTarget target) => GetExternalUrl(target.Subdomain);

    /// <summary>The browsable URL for an endpoint published under <paramref name="subdomain"/>.</summary>
    public string GetExternalUrl(string subdomain) => $"https://{subdomain}.{_domain}";

    /// <summary>The bare external host (no scheme/port) for an endpoint published under <paramref name="subdomain"/>
    /// — e.g. an IdP's external-domain setting that wants the host, not the full URL.</summary>
    public string GetExternalHost(string subdomain) => $"{subdomain}.{_domain}";

    // Takes the internal IngressTarget DTO → internal surface (only the stamp step calls it).
    internal IReadOnlyDictionary<string, string> GetLabels(IngressTarget target)
    {
        // Key the resource off the stack-unique Hostname, not the bare resource name: the label-key suffix
        // becomes the Pangolin resource's niceId, so `web` would make every preview slot collide on one
        // resource. `{stack}-{service}` gives each slot its own resource (and its own backend hostname).
        var id = target.Hostname;
        var p = $"pangolin.public-resources.{id}";
        // Per-resource override wins; otherwise the provider's stack-wide default.
        var sso = target.Sso ?? _sso;
        // `protocol` (http|tcp|udp) is the resource KIND; `targets[].method` (http|https|h2c) is the scheme
        // Traefik DIALS the backend with. They are different enums in Pangolin's blueprint schema — stamping
        // one value into both made h2c upstreams (gRPC backends) inexpressible: protocol=h2c fails schema
        // validation and newt silently skips the whole resource.
        var protocol = target.Scheme is "tcp" or "udp" ? target.Scheme : "http";
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{p}.name"] = target.Hostname,
            [$"{p}.protocol"] = protocol,
            [$"{p}.full-domain"] = FullDomain(target),
            [$"{p}.auth.sso-enabled"] = sso ? "true" : "false",
            [$"{p}.targets[0].hostname"] = target.Hostname,
            [$"{p}.targets[0].port"] = target.TargetPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (protocol == "http")
        {
            // method is only valid (and required) on http-kind resources.
            labels[$"{p}.targets[0].method"] = target.UpstreamMethod ?? target.Scheme;
        }
        if (sso)
        {
            if (!string.IsNullOrEmpty(_ssoIdp))
            {
                labels[$"{p}.auth.auto-login-idp"] = _ssoIdp;
            }
            for (var i = 0; i < _whitelistUsers.Length; i++)
            {
                labels[$"{p}.auth.whitelist-users[{i}]"] = _whitelistUsers[i];
            }
        }
        return labels;
    }

    private string FullDomain(IngressTarget target) => $"{target.Subdomain}.{_domain}";

    /// <summary>
    /// Delete the ingress resource named <paramref name="niceId"/> on <c>aspire destroy</c>. Label-driven
    /// ingress is NOT reconciled on removal (newt creates a resource from the labels but never deletes it when
    /// they vanish — fosrl/pangolin#1864), so a destroyed stack leaves a route to a dead backend (502). Delete
    /// it explicitly via the Integration API. No-op unless apiUrl/apiKey/org are all configured. REMOVE this
    /// method + its caller once #1864 / its linked feature request lands and newt reconciles.
    /// </summary>
    public async Task TeardownAsync(HttpClient http, string niceId, CancellationToken ct)
    {
        if (_apiUrl is null || _apiKey is null || _org is null)
        {
            return;
        }

        var resourceId = await FindResourceIdAsync(http, niceId, ct);
        if (resourceId is null)
        {
            return; // already gone
        }

        using var del = new HttpRequestMessage(HttpMethod.Delete, $"{_apiUrl}/v1/resource/{resourceId}");
        del.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var resp = await http.SendAsync(del, ct);
        // 404 = already gone; treat as success. Anything else non-2xx throws.
        if (resp.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound))
        {
            resp.EnsureSuccessStatusCode();
        }
    }

    private async Task<int?> FindResourceIdAsync(HttpClient http, string niceId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_apiUrl}/v1/org/{_org}/resources");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("resources", out var resources) ||
            resources.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var r in resources.EnumerateArray())
        {
            if (r.TryGetProperty("niceId", out var n) && n.GetString() == niceId &&
                r.TryGetProperty("resourceId", out var id) && id.TryGetInt32(out var rid))
            {
                return rid;
            }
        }
        return null;
    }
}

/// <summary>
/// Adds Pangolin ingress to an Aspire Docker Compose environment, plus per-resource ingress overrides.
/// </summary>
/// <remarks>
/// Ingress is a SEPARATE plane from whatever deploys the compose (any compose deploy target): the only coupling is the
/// docker labels this package stamps on each externally-exposed service. The stamp + network join run as a
/// PUBLISH-phase pipeline step (so the labels are already on disk before any deploy target reads the compose),
/// and a DESTROY-phase step deletes the created Pangolin resources (newt doesn't reconcile removals).
/// </remarks>
public static class PangolinIngressExtensions
{
    private const string ComposeFile = "docker-compose.yaml";

    /// <summary>
    /// Installs the Pangolin ingress provider from an <c>Ingress</c> config section. Value-driven: if the
    /// section has no <c>Domain</c> (e.g. local <c>aspire run</c>), this is a NO-OP and no ingress is
    /// configured. Registers its OWN publish-time stamp step + destroy-time teardown step on the compose
    /// environment — no dependency on any deploy-target package. Does NOT set any subdomain — the per-PR
    /// subdomain is stamped on the web resource via <see cref="WithCustomDomain{T}"/>.
    /// </summary>
    /// <remarks>
    /// The deployment/stack name used for the <c>{stackName}-{resource}</c> subdomain default is read from the
    /// section as <c>StackName</c>, falling back to the compose environment resource's name. (It used to come
    /// from a deploy-target-owned <c>StackName</c> option; reading it from the ingress section decouples the
    /// two planes — see SEAM-4.)
    /// </remarks>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithPangolinIngress(
        this IResourceBuilder<DockerComposeEnvironmentResource> builder,
        IConfigurationSection ingressSection)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(ingressSection);

        var domain = ingressSection["Domain"];
        if (string.IsNullOrWhiteSpace(domain))
        {
            return builder; // no ingress config (e.g. local run) → no ingress provider
        }

        var provider = new PangolinIngress(
            domain,
            ingressSection.GetValue("Sso", false),
            ingressSection["SsoIdp"],
            ingressSection.GetSection("WhitelistUsers").Get<string[]>() ?? [],
            // Integration API (optional) — enables resource teardown on `aspire destroy`. Absent → teardown
            // is a no-op (the resource is left for a sweep, since newt doesn't reconcile removals; #1864).
            ingressSection["ApiUrl"],
            ingressSection["ApiKey"],
            ingressSection["Org"]);

        // SEAM-4: the deployment/stack name for the `{stackName}-{resource}` subdomain default. Read it from
        // the ingress section so this package never references a deploy-target type; fall back to the compose
        // environment resource's name when the section omits it.
        var deploymentName = ingressSection["StackName"];
        if (string.IsNullOrWhiteSpace(deploymentName))
        {
            deploymentName = builder.Resource.Name;
        }

        // Carry the live provider + deployment name on the env resource so the per-resource URL helpers
        // (WithPangolinPublicUrl / …Host / GetPangolinPublicUrl) and the pipeline steps can read them back.
        builder.WithAnnotation(new PangolinIngressAnnotation(provider, deploymentName));

        var envName = builder.Resource.Name;

        return builder
            // STAMP face: stamp labels + join the ingress network onto each external service. A PUBLISH-phase
            // step (requiredBy Publish, dependsOn publish-{env}) so it completes before ANY Deploy step — the
            // deploy target reads the SAME docker-compose.yaml afterward, so the labels must already be on disk
            // (a same-phase Deploy step would have no guaranteed ordering against the deploy). See the class
            // remarks for the cross-package ordering contract.
            .WithPipelineStepFactory(
                stepName: $"pangolin-stamp-{envName}",
                callback: (PipelineStepContext context) => StampAsync(context, provider, deploymentName),
                dependsOn: [$"publish-{envName}"],
                requiredBy: [WellKnownPipelineSteps.Publish],
                tags: [],
                description: "Stamps Pangolin ingress labels + joins the ingress network on each external service.")
            // TEARDOWN face: delete the Pangolin resources this stack created on `aspire destroy` (newt does
            // NOT reconcile removals; fosrl/pangolin#1864). No-op unless the Integration API is configured.
            .WithPipelineStepFactory(
                stepName: $"pangolin-teardown-{envName}",
                callback: (PipelineStepContext context) => TeardownAsync(context, provider, deploymentName),
                dependsOn: [],
                requiredBy: [WellKnownPipelineSteps.Destroy],
                tags: [],
                description: "Deletes the Pangolin ingress resources on `aspire destroy`.");
    }

    /// <summary>
    /// Override the ingress subdomain/host label for this resource's external endpoint (e.g. a vanity domain).
    /// Defaults to the resource name.
    /// </summary>
    public static IResourceBuilder<T> WithCustomDomain<T>(this IResourceBuilder<T> builder, string subdomain)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(subdomain);
        builder.Resource.Annotations.Add(new PangolinIngressSubdomainAnnotation(subdomain));
        return builder;
    }

    /// <summary>
    /// Force this resource's external endpoint(s) PUBLIC (ingress SSO off), overriding the provider's stack-wide
    /// SSO default. For an IdP / auth surface that must stay reachable un-gated inside a stack whose app endpoints
    /// ARE SSO-gated — gating the IdP itself would be circular (you'd need to be logged in to log in).
    /// </summary>
    public static IResourceBuilder<T> WithPublicIngress<T>(this IResourceBuilder<T> builder)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Resource.Annotations.Add(new PangolinIngressSsoAnnotation(false));
        return builder;
    }

    /// <summary>
    /// Override the scheme the ingress DIALS this resource's backend with (Pangolin <c>targets[].method</c>):
    /// <c>http</c>, <c>https</c>, or <c>h2c</c>. Use <c>h2c</c> for backends multiplexing gRPC + HTTP on one
    /// cleartext port — gRPC needs HTTP/2 end-to-end, and the proxy only originates it
    /// when the upstream is dialed as h2c. Defaults to the endpoint's scheme. The resource-level
    /// <c>protocol</c> (http|tcp|udp) is unaffected.
    /// </summary>
    public static IResourceBuilder<T> WithIngressUpstreamMethod<T>(this IResourceBuilder<T> builder, string method)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (method is not ("http" or "https" or "h2c"))
        {
            throw new ArgumentException(
                $"Invalid ingress upstream method '{method}' — Pangolin's targets[].method accepts http, https, or h2c.",
                nameof(method));
        }
        builder.Resource.Annotations.Add(new PangolinIngressUpstreamMethodAnnotation(method));
        return builder;
    }

    /// <summary>
    /// Wire <paramref name="environmentVariable"/> to this resource's public URL, derived from the model:
    /// in run mode the local http endpoint (e.g. http://localhost:3000); in publish mode the Pangolin URL
    /// https://{subdomain}.{domain} composed from the configured ingress. Avoids hand-maintaining a separate
    /// per-environment URL parameter. Call AFTER the resource's http endpoint is defined.
    /// </summary>
    public static IResourceBuilder<T> WithPangolinPublicUrl<T>(this IResourceBuilder<T> builder, string environmentVariable)
        where T : IResourceWithEndpoints, IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentVariable);
        var app = builder.ApplicationBuilder;

        // Run mode: derive from the local http endpoint (e.g. http://localhost:3000).
        if (app.ExecutionContext.IsRunMode)
        {
            return builder.WithEnvironment(environmentVariable, builder.GetEndpoint("http"));
        }

        var (ingress, deploymentName) = ResolveIngress(app);
        var subdomain = ResolveSubdomain(builder, deploymentName);

        // The public subdomain MUST match what the stamp step stamps at publish: an explicit WithCustomDomain
        // wins, else the stack-unique {stack}-{resource} name. Without an ingress or a deployment name (e.g. a
        // bare local `aspire publish` with nothing injected) fall back to the local endpoint.
        if (ingress is null || subdomain is null)
        {
            return builder.WithEnvironment(environmentVariable, builder.GetEndpoint("http"));
        }

        return builder.WithEnvironment(environmentVariable, ingress.GetExternalUrl(subdomain));
    }

    /// <summary>
    /// Like <see cref="WithPangolinPublicUrl"/> but wires the bare external HOST (no scheme/port) instead of the
    /// URL — for settings that want a host (e.g. an IdP's external-domain). Run mode → the local endpoint host;
    /// publish mode → <c>{subdomain}.{domain}</c> from the configured ingress. Call AFTER the http endpoint exists.
    /// </summary>
    public static IResourceBuilder<T> WithPangolinPublicHost<T>(this IResourceBuilder<T> builder, string environmentVariable)
        where T : IResourceWithEndpoints, IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentVariable);
        var app = builder.ApplicationBuilder;

        // Run mode: the local endpoint's host (e.g. localhost).
        if (app.ExecutionContext.IsRunMode)
        {
            return builder.WithEnvironment(environmentVariable, builder.GetEndpoint("http").Property(EndpointProperty.Host));
        }

        var (ingress, deploymentName) = ResolveIngress(app);
        var subdomain = ResolveSubdomain(builder, deploymentName);

        // Must match the host the stamp step stamps: explicit WithCustomDomain wins, else stack-unique {stack}-{resource}.
        if (ingress is null || subdomain is null)
        {
            return builder.WithEnvironment(environmentVariable, builder.GetEndpoint("http").Property(EndpointProperty.Host));
        }

        return builder.WithEnvironment(environmentVariable, ingress.GetExternalHost(subdomain));
    }

    /// <summary>
    /// The Pangolin public URL this resource WILL be published at in publish mode (<c>https://{subdomain}.{domain}</c>),
    /// or <c>null</c> in run mode / when no ingress (or deployment name) is configured. Same derivation as
    /// <see cref="WithPangolinPublicUrl"/> but RETURNS the value — e.g. to set a DIFFERENT resource's env to this
    /// one's URL (an IdP issuer a consumer points its OIDC authority at). Call after any <see cref="WithCustomDomain"/>.
    /// </summary>
    public static string? GetPangolinPublicUrl<T>(this IResourceBuilder<T> builder)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        var app = builder.ApplicationBuilder;
        if (app.ExecutionContext.IsRunMode)
        {
            return null;
        }

        var (ingress, deploymentName) = ResolveIngress(app);
        var subdomain = ResolveSubdomain(builder, deploymentName);

        return ingress is null || subdomain is null ? null : ingress.GetExternalUrl(subdomain);
    }

    /// <summary>Read the configured provider + deployment name off the <see cref="PangolinIngressAnnotation"/>
    /// installed by <see cref="WithPangolinIngress"/> (both null when no ingress is configured).</summary>
    private static (PangolinIngress? Ingress, string? DeploymentName) ResolveIngress(IDistributedApplicationBuilder app)
    {
        var annotation = app.Resources
            .SelectMany(r => r.Annotations.OfType<PangolinIngressAnnotation>())
            .FirstOrDefault();
        return (annotation?.Ingress, annotation?.DeploymentName);
    }

    /// <summary>The public subdomain for a resource: an explicit <see cref="WithCustomDomain{T}"/> wins, else
    /// the stack-unique <c>{deployment}-{resource}</c> name (null when no deployment name is configured).</summary>
    private static string? ResolveSubdomain<T>(IResourceBuilder<T> builder, string? deploymentName)
        where T : IResource
    {
        var overrideSubdomain = builder.Resource.Annotations.OfType<PangolinIngressSubdomainAnnotation>().FirstOrDefault()?.Subdomain;
        var resourceName = builder.Resource.Name.ToLowerInvariant();
        return overrideSubdomain
            ?? (string.IsNullOrEmpty(deploymentName) ? null : $"{deploymentName}-{resourceName}");
    }

    /// <summary>
    /// PUBLISH-phase stamp step. Reads the emitted compose, stamps the provider's labels onto each
    /// externally-exposed service, joins each to the provider's required network (and defines that network at
    /// the top level), then writes the file back. Runs BEFORE any deploy target reads the compose — the only
    /// compute↔ingress coupling is these docker labels (Pangolin's pangolin.public-resources.*), so the ingress
    /// plane stays fully decoupled from the deploy target. Compose is edited AS TEXT (line/regex based) because
    /// a downstream deploy target may re-upload the file verbatim and would mangle a YAML re-serialize.
    /// </summary>
    private static async Task StampAsync(PipelineStepContext context, PangolinIngress ingress, string stackName)
    {
        var outDir = context.Services.GetRequiredService<IPipelineOutputService>().GetOutputDirectory();
        var composePath = Path.Combine(outDir, ComposeFile);
        if (!File.Exists(composePath))
        {
            context.Logger.LogWarning("Pangolin: compose not found at {Path}; skipping ingress stamp.", composePath);
            return;
        }
        var compose = await File.ReadAllTextAsync(composePath, context.CancellationToken);

        var targets = context.Model.Resources
            .SelectMany(r => r.Annotations.OfType<EndpointAnnotation>()
                .Where(e => e.IsExternal)
                .Select(e => new IngressTarget(
                    ResourceName: r.Name,
                    ServiceName: r.Name.ToLowerInvariant(),
                    // Stack-unique addressable name → stamped as container_name + used as the ingress backend
                    // hostname, so previews sharing the external ingress network never collide on `web`.
                    Hostname: $"{stackName}-{r.Name.ToLowerInvariant()}",
                    // Public subdomain defaults to that SAME stack-unique name so each deploy gets a host that
                    // is unique AND a single label under a single-level wildcard cert (e.g.
                    // web-pr-42.example.com under *.example.com). WithCustomDomain overrides it.
                    Subdomain: r.Annotations.OfType<PangolinIngressSubdomainAnnotation>().FirstOrDefault()?.Subdomain ?? $"{stackName}-{r.Name.ToLowerInvariant()}",
                    TargetPort: e.TargetPort ?? e.Port ?? 80,
                    Scheme: e.UriScheme ?? "http",
                    // Per-resource SSO override (WithPublicIngress); null = provider's stack-wide default.
                    Sso: r.Annotations.OfType<PangolinIngressSsoAnnotation>().FirstOrDefault()?.Sso,
                    // Per-resource upstream-dial scheme (WithIngressUpstreamMethod, e.g. h2c for gRPC backends).
                    UpstreamMethod: r.Annotations.OfType<PangolinIngressUpstreamMethodAnnotation>().FirstOrDefault()?.Method)))
            .ToList();
        if (targets.Count == 0)
        {
            context.Logger.LogInformation("Pangolin: ingress configured but no external endpoints found.");
            return;
        }
        var network = ingress.RequiredNetwork;
        foreach (var target in targets)
        {
            compose = StampServiceForIngress(compose, target.ServiceName, target.Hostname, ingress.GetLabels(target), network, context);
            context.Logger.LogInformation("Pangolin: ingress — {Resource} → {Url}", target.ResourceName, ingress.GetExternalUrl(target));
        }
        // Define the provider's external network at the top level (it's joined per-service above).
        if (network is not null && !compose.Contains($"\n  {network}:", StringComparison.Ordinal))
        {
            compose = Regex.Replace(compose, @"(?m)^networks:[ \t]*$",
                $"networks:\n  {network}:\n    name: {network}\n    external: true");
        }

        await File.WriteAllTextAsync(composePath, compose, context.CancellationToken);
        context.Logger.LogInformation("Pangolin: stamped {Count} external service(s) in {File}.", targets.Count, ComposeFile);
    }

    /// <summary>Inject a <c>labels:</c> block (and join an external network) into one compose service —
    /// line-based, so we never re-serialize the YAML (which a downstream deploy target would mangle).</summary>
    private static string StampServiceForIngress(
        string compose, string service, string hostname, IReadOnlyDictionary<string, string> labels, string? network, PipelineStepContext context)
    {
        var lines = compose.Split('\n').ToList();
        var prefix = $"  {service}:";
        var header = lines.FindIndex(l => l.StartsWith(prefix, StringComparison.Ordinal) && l[prefix.Length..].Trim().Length == 0);
        if (header < 0)
        {
            context.Logger.LogWarning("Pangolin: ingress — service '{Service}' not found in compose; labels skipped.", service);
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
        // Drop the host-published `ports:` block — for an ingress'd service the ingress IS the entry
        // point (Pangolin/Traefik reach the container over the ingress network), so a published host
        // port is redundant surface area. The container stays reachable network-internally.
        var portsIdx = -1;
        for (var i = header + 1; i < end; i++)
        {
            if (lines[i].TrimEnd() == "    ports:")
            {
                portsIdx = i;
                break;
            }
        }
        if (portsIdx >= 0)
        {
            var removeTo = portsIdx + 1;
            while (removeTo < end && lines[removeTo].Trim().Length != 0 &&
                   lines[removeTo].Length - lines[removeTo].TrimStart(' ').Length > 4)
            {
                removeTo++;
            }
            lines.RemoveRange(portsIdx, removeTo - portsIdx);
            end -= removeTo - portsIdx;
        }

        // Join the external network by appending to the service's existing networks: list (within the block).
        if (network is not null)
        {
            var netIdx = -1;
            for (var i = header + 1; i < end; i++)
            {
                if (lines[i].TrimEnd() == "    networks:")
                {
                    netIdx = i;
                    break;
                }
            }
            if (netIdx >= 0)
            {
                var insertAt = netIdx + 1;
                while (insertAt < end && lines[insertAt].TrimStart().StartsWith("- ", StringComparison.Ordinal))
                {
                    insertAt++;
                }
                lines.Insert(insertAt, $"      - \"{network}\"");
                end++;
            }
            else
            {
                context.Logger.LogWarning("Pangolin: ingress — service '{Service}' has no networks: block; '{Net}' not joined.", service, network);
            }
        }
        // Inject a stack-unique container_name + the labels block right after the service header. The
        // container_name makes the service addressable on a shared external ingress network (where the bare
        // compose alias collides across preview stacks); skip it if the compose already declares one.
        var hasContainerName = false;
        for (var i = header + 1; i < end; i++)
        {
            if (lines[i].TrimStart().StartsWith("container_name:", StringComparison.Ordinal))
            {
                hasContainerName = true;
                break;
            }
        }
        var inject = new List<string>();
        if (!hasContainerName)
        {
            inject.Add($"    container_name: {hostname}");
        }
        inject.Add("    labels:");
        foreach (var kv in labels)
        {
            inject.Add($"      {kv.Key}: \"{kv.Value}\"");
        }
        lines.InsertRange(header + 1, inject);
        return string.Join("\n", lines);
    }

    /// <summary>
    /// DESTROY-phase teardown step. Deletes the Pangolin resource each external endpoint created — label-driven
    /// ingress (Pangolin/newt) does NOT reconcile removals (fosrl/pangolin#1864), so without this they dangle
    /// as routes to a dead backend (502). No-op unless the provider has its Integration API configured. REMOVE
    /// once upstream reconciles itself.
    /// </summary>
    private static async Task TeardownAsync(PipelineStepContext context, PangolinIngress ingress, string stackName)
    {
        var ct = context.CancellationToken;
        using var http = new HttpClient();
        foreach (var r in context.Model.Resources)
        {
            if (!r.Annotations.OfType<EndpointAnnotation>().Any(e => e.IsExternal))
            {
                continue;
            }
            var niceId = $"{stackName}-{r.Name.ToLowerInvariant()}";
            try
            {
                await ingress.TeardownAsync(http, niceId, ct);
                context.Logger.LogInformation("Pangolin: ingress — deleted resource '{NiceId}'.", niceId);
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning("Pangolin: ingress — failed to delete resource '{NiceId}' ({Msg}); leaving it.", niceId, ex.Message);
            }
        }
    }
}

/// <summary>Carries the live Pangolin ingress provider + the deployment/stack name (for the
/// <c>{deployment}-{resource}</c> subdomain default) on the compose environment resource.</summary>
internal sealed class PangolinIngressAnnotation(PangolinIngress ingress, string deploymentName) : IResourceAnnotation
{
    public PangolinIngress Ingress { get; } = ingress;
    public string DeploymentName { get; } = deploymentName;
}

/// <summary>Carries a per-resource ingress subdomain override for the configured ingress provider.</summary>
internal sealed class PangolinIngressSubdomainAnnotation(string subdomain) : IResourceAnnotation
{
    public string Subdomain { get; } = subdomain;
}

/// <summary>Carries a per-resource ingress SSO override (<c>true</c>=gated, <c>false</c>=public); absent = provider default.</summary>
internal sealed class PangolinIngressSsoAnnotation(bool sso) : IResourceAnnotation
{
    public bool Sso { get; } = sso;
}

/// <summary>Carries a per-resource override of the scheme the ingress dials the backend with
/// (Pangolin <c>targets[].method</c>: <c>http</c>, <c>https</c>, or <c>h2c</c>); absent = the endpoint's scheme.</summary>
internal sealed class PangolinIngressUpstreamMethodAnnotation(string method) : IResourceAnnotation
{
    public string Method { get; } = method;
}
