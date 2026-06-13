using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Komodo;

/// <summary>
/// The Komodo deploy's generic <c>*AsExisting</c> family — the non-Azure substitute for Aspire's
/// <c>RunAsExisting</c>/<c>PublishAsExisting</c>/<c>AsExisting</c> (which are hard-locked to
/// <c>where T : IAzureResource</c>). "Existing" means: in the gated mode, DON'T deploy this resource's
/// compose service — point its dependents at an already-running instance instead. Two flavors, by how the
/// dependents discover that instance:
/// <list type="bullet">
///   <item><b>connection-string</b> (<see cref="IResourceWithConnectionString"/>): redirects every dependent's
///   <c>ConnectionStrings__&lt;name&gt;</c> via Aspire's <see cref="ConnectionStringRedirectAnnotation"/>. (postgres, redis, …)</item>
///   <item><b>endpoint / service-discovery</b> (<see cref="IResourceWithEndpoints"/>): records the external URL so
///   the service is skipped and — for consumers that use service discovery — the deploy injects
///   <c>services__&lt;name&gt;__&lt;scheme&gt;__0</c>. Resources that surface an explicit URL property (e.g. an IdP's
///   issuer) only need the skip. (Any HTTP-discovered dependency.)</item>
/// </list>
/// All three differ only in which mode attaches the annotations: <c>AsExisting</c> = both,
/// <c>RunAsExisting</c> = run-only, <c>PublishAsExisting</c> = publish-only — mirroring Azure exactly. RUN mode
/// (when not gated) is untouched: <c>aspire run</c> still spins up a local instance.
/// </summary>
public static class KomodoExistingResourceExtensions
{
    private static bool Gate(DistributedApplicationExecutionContext ctx, ExistingMode mode) => mode switch
    {
        ExistingMode.Both => true,
        ExistingMode.RunOnly => !ctx.IsPublishMode,
        ExistingMode.PublishOnly => ctx.IsPublishMode,
        _ => false,
    };

    private enum ExistingMode { Both, RunOnly, PublishOnly }

    // ─────────────────────────── connection-string flavor ───────────────────────────

    /// <summary>Reference an existing connection-string instance in BOTH run and publish.</summary>
    public static IResourceBuilder<T> AsExisting<T>(this IResourceBuilder<T> builder, ReferenceExpression connectionString, string? network = null)
        where T : class, IResource, IResourceWithConnectionString
        => ConnectionStringExisting(builder, connectionString, network, ExistingMode.Both);

    /// <summary>Reference an existing connection-string instance in RUN only; publish still deploys it.</summary>
    public static IResourceBuilder<T> RunAsExisting<T>(this IResourceBuilder<T> builder, ReferenceExpression connectionString, string? network = null)
        where T : class, IResource, IResourceWithConnectionString
        => ConnectionStringExisting(builder, connectionString, network, ExistingMode.RunOnly);

    /// <summary>Reference an existing connection-string instance in PUBLISH only; run still spins up a local one.</summary>
    public static IResourceBuilder<T> PublishAsExisting<T>(this IResourceBuilder<T> builder, ReferenceExpression connectionString, string? network = null)
        where T : class, IResource, IResourceWithConnectionString
        => ConnectionStringExisting(builder, connectionString, network, ExistingMode.PublishOnly);

    /// <summary>Convenience: literal connection string (no <c>${VAR}</c> placeholders). Prefer the
    /// <see cref="ReferenceExpression"/> overload when interpolating a secret parameter so the value never inlines.</summary>
    public static IResourceBuilder<T> PublishAsExisting<T>(this IResourceBuilder<T> builder, string connectionString, string? network = null)
        where T : class, IResource, IResourceWithConnectionString
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return ConnectionStringExisting(builder, ReferenceExpression.Create($"{connectionString}"), network, ExistingMode.PublishOnly);
    }

    private static IResourceBuilder<T> ConnectionStringExisting<T>(IResourceBuilder<T> builder, ReferenceExpression connectionString, string? network, ExistingMode mode)
        where T : class, IResource, IResourceWithConnectionString
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connectionString);

        if (Gate(builder.ApplicationBuilder.ExecutionContext, mode))
        {
            var existing = new ExistingConnectionResource($"{builder.Resource.Name}-existing", connectionString);
            builder.Resource.Annotations.Add(new ConnectionStringRedirectAnnotation(existing));
            builder.Resource.Annotations.Add(new KomodoSkipServiceAnnotation(network));
        }

        return builder;
    }

    // ─────────────────────────── endpoint / service-discovery flavor ───────────────────────────

    /// <summary>Reference an existing endpoint instance in BOTH run and publish. <paramref name="url"/> is the
    /// external base URL (e.g. an IdP issuer); the deploy skips this resource's service and — for service-discovery
    /// consumers — injects it as <c>services__&lt;name&gt;__&lt;scheme&gt;__0</c>.</summary>
    public static IResourceBuilder<T> AsExisting<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> url, string? network = null)
        where T : class, IResource, IResourceWithEndpoints
        => EndpointExisting(builder, url, network, ExistingMode.Both);

    /// <summary>Reference an existing endpoint instance in RUN only; publish still deploys it.</summary>
    public static IResourceBuilder<T> RunAsExisting<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> url, string? network = null)
        where T : class, IResource, IResourceWithEndpoints
        => EndpointExisting(builder, url, network, ExistingMode.RunOnly);

    /// <summary>Reference an existing endpoint instance in PUBLISH only; run still spins up a local one.</summary>
    public static IResourceBuilder<T> PublishAsExisting<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> url, string? network = null)
        where T : class, IResource, IResourceWithEndpoints
        => EndpointExisting(builder, url, network, ExistingMode.PublishOnly);

    private static IResourceBuilder<T> EndpointExisting<T>(IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> url, string? network, ExistingMode mode)
        where T : class, IResource, IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(url);

        if (Gate(builder.ApplicationBuilder.ExecutionContext, mode))
        {
            builder.Resource.Annotations.Add(new KomodoExternalEndpointAnnotation(url.Resource));
            builder.Resource.Annotations.Add(new KomodoSkipServiceAnnotation(network));
        }

        return builder;
    }
}

/// <summary>
/// Marks a resource whose compose service must NOT be emitted — it's referenced as existing.
/// <see cref="Network"/>, when set, is the external docker network the existing instance lives on;
/// dependents are joined to it at deploy so they can reach it.
/// </summary>
internal sealed class KomodoSkipServiceAnnotation(string? network = null) : IResourceAnnotation
{
    /// <summary>External network the existing instance is reachable on, or null if on the stack's own network.</summary>
    public string? Network { get; } = network;
}

/// <summary>Carries the external base URL of an existing endpoint-flavored resource (an IdP issuer, an HTTP
/// service). The deploy uses it to inject <c>services__&lt;name&gt;__&lt;scheme&gt;__0</c> for service-discovery
/// consumers (the endpoint analog of <see cref="ConnectionStringRedirectAnnotation"/>, for which Aspire has no
/// built-in). Resources consumed via an explicit URL property surface that property themselves.</summary>
internal sealed class KomodoExternalEndpointAnnotation(ParameterResource url) : IResourceAnnotation
{
    public ParameterResource Url { get; } = url;
}

/// <summary>A bare connection-string resource used as the redirect target for the connection-string flavor.</summary>
internal sealed class ExistingConnectionResource : Resource, IResourceWithConnectionString
{
    private readonly ReferenceExpression _connectionString;

    public ExistingConnectionResource(string name, string connectionString) : base(name)
        => _connectionString = ReferenceExpression.Create($"{connectionString}");

    public ExistingConnectionResource(string name, ReferenceExpression connectionString) : base(name)
        => _connectionString = connectionString;

    public ReferenceExpression ConnectionStringExpression => _connectionString;
}
