using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources.ComposeNodes;

namespace Aspire.Hosting.Komodo;

/// <summary>
/// Attaches a deployed compose service to a pre-existing (external) docker network.
/// </summary>
/// <remarks>
/// Aspire's compose publisher has no first-class "attach this resource to an existing external network" API.
/// You can append a network to a service via <c>PublishAsDockerComposeService</c>, but nothing declares that
/// network at the compose top level as <c>external: true</c> — so compose would create a fresh, isolated
/// per-stack network instead of attaching to the shared one that already exists on the host. This packages
/// both halves (per-service join + top-level external declaration) into a single call so consumers stop
/// hand-rolling the compose-model escape hatches (a <c>PublishAsDockerComposeService</c> network append plus a
/// matching <c>ConfigureComposeFile</c> top-level declaration).
/// <para>
/// Publish/deploy-only: in <c>aspire run</c> Aspire owns local networking, so this no-ops and never touches
/// local dev. It is meaningful only with Aspire's built-in <see cref="DockerComposeEnvironmentResource"/> (the
/// environment the Komodo deploy target layers onto via <see cref="KomodoExtensions.WithKomodoDeploySupport"/>);
/// it has no effect on the dedicated <see cref="KomodoEnvironmentExtensions.AddKomodoEnvironment"/> generator,
/// which does not emit networks.
/// </para>
/// </remarks>
public static class ExternalNetworkExtensions
{
    /// <summary>
    /// Attaches this resource's emitted compose service to the existing external docker network
    /// <paramref name="networkName"/> and declares that network at the compose top level as
    /// <c>external: true</c>. Additive to the networks Aspire already assigns (the per-stack default and any
    /// others). Idempotent: the per-service join is applied once, and the top-level declaration is emitted
    /// once per network no matter how many resources join it.
    /// </summary>
    /// <param name="builder">The compute resource (e.g. a container) whose compose service joins the network.</param>
    /// <param name="networkName">The name of an external docker network that already exists on the deploy host
    /// — e.g. a shared observability/collector network or a shared database network.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IResourceBuilder<T> WithExternalNetwork<T>(this IResourceBuilder<T> builder, string networkName)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(networkName);

        var app = builder.ApplicationBuilder;

        // Networks are a deploy/compose concern; `aspire run` manages local networking itself, so do nothing
        // there (mirrors how the publish-time compose hooks below would no-op in run mode anyway).
        if (!app.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        // Declare the external network at the compose top level — ONCE per (environment, network), regardless
        // of how many resources join it. `external: true` makes compose attach to the network that already
        // exists on the host rather than creating a fresh, isolated per-stack one.
        foreach (var env in app.Resources.OfType<DockerComposeEnvironmentResource>())
        {
            if (env.Annotations.OfType<ExternalNetworkAnnotation>().Any(a => a.NetworkName == networkName))
            {
                continue;
            }
            env.Annotations.Add(new ExternalNetworkAnnotation(networkName));
            app.CreateResourceBuilder(env).ConfigureComposeFile(file =>
                file.AddNetwork(new Network { Name = networkName, External = true }));
        }

        // Join THIS resource's emitted compose service to the network (additive + idempotent).
        builder.PublishAsDockerComposeService((_, service) =>
        {
            if (!service.Networks.Contains(networkName))
            {
                service.Networks.Add(networkName);
            }
        });

        return builder;
    }
}

/// <summary>Records that an external network has already been declared at the compose top level on a compose
/// environment, so <see cref="ExternalNetworkExtensions.WithExternalNetwork{T}"/> declares each network exactly
/// once however many resources join it.</summary>
internal sealed class ExternalNetworkAnnotation(string networkName) : IResourceAnnotation
{
    public string NetworkName { get; } = networkName;
}
