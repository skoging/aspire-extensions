using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Komodo;

/// <summary>
/// Adds Komodo publish/deploy support to an Aspire Docker Compose environment.
/// </summary>
/// <remarks>
/// <para><c>aspire publish</c> emits the generated compose plus a Komodo Resource-Sync TOML
/// (inspectable, GitOps-friendly) and leaves them on disk.</para>
/// <para><c>aspire deploy</c> imperatively upserts the Stack via the Komodo API and deploys it,
/// then removes the generated files so the deploy leaves nothing behind.</para>
/// </remarks>
public static class KomodoExtensions
{
    /// <summary>Adds Komodo publish/deploy support to a Docker Compose environment.</summary>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithKomodoDeploySupport(
        this IResourceBuilder<DockerComposeEnvironmentResource> builder,
        Action<KomodoDeployOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new KomodoDeployOptions();
        configure?.Invoke(options);
        builder.WithAnnotation(new KomodoDeployAnnotation(options));

        var name = builder.Resource.Name;

        return builder
            // PUBLISH face: emit an inspectable Komodo Resource-Sync TOML next to the compose.
            .WithPipelineStepFactory(
                stepName: $"komodo-emit-resync-{name}",
                callback: (PipelineStepContext context) => KomodoSteps.EmitResourceSyncAsync(context, options, name),
                // Run AFTER the compose environment writes docker-compose.yaml (its publish step is
                // named "publish-{envName}"), but still within the Publish phase.
                dependsOn: [$"publish-{name}"],
                requiredBy: [WellKnownPipelineSteps.Publish],
                tags: [],
                description: "Emits a Komodo Resource-Sync TOML alongside the generated compose.")
            // DEPLOY face: upsert + deploy via the Komodo API, then clean up the generated files.
            .WithPipelineStepFactory(
                stepName: $"komodo-deploy-{name}",
                callback: (PipelineStepContext context) => KomodoSteps.DeployAsync(context, options, name),
                // After the compose env writes docker-compose.yaml ("publish-{name}") and after any
                // image push, but before the Deploy phase completes.
                dependsOn: [$"publish-{name}", WellKnownPipelineSteps.Push],
                requiredBy: [WellKnownPipelineSteps.Deploy],
                tags: [],
                description: "Upserts + deploys the stack to Komodo, then removes generated temp files.")
            // DESTROY face: tear the Komodo stack down (DestroyStack + DeleteStack) on `aspire destroy`.
            .WithPipelineStepFactory(
                stepName: $"komodo-destroy-{name}",
                callback: (PipelineStepContext context) => KomodoSteps.DestroyAsync(context, options, name),
                dependsOn: [],
                requiredBy: [WellKnownPipelineSteps.Destroy],
                tags: [],
                description: "Destroys + deletes the Komodo stack on `aspire destroy`.")
            // Suppress the compose env's local `docker compose up`/`down` so `aspire deploy`/`destroy` act
            // ONLY on Komodo. The v13 pipeline has no public "remove step", so — deliberately hacky — we
            // no-op those steps' Action via reflection during the configuration phase.
            .WithPipelineConfiguration((PipelineConfigurationContext context) =>
            {
                foreach (var stepName in new[] { $"docker-compose-up-{name}", $"docker-compose-down-{name}" })
                {
                    var step = context.Steps.FirstOrDefault(s => string.Equals(s.Name, stepName, StringComparison.Ordinal));
                    if (step is not null)
                    {
                        TryNeutralizeStep(step);
                    }
                }
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Config-driven overload: binds <see cref="KomodoDeployOptions"/>'s scalar settings
    /// (CoreUrl/ApiKey/ApiSecret/ServerName/StackName/RegistryProvider/RegistryAccount) from
    /// <paramref name="section"/>, then applies the optional <paramref name="configure"/> for code overrides.
    /// The interface-typed <c>SecretProvider</c> is not bound — it keeps its default (Komodo Variables).
    /// The HTTP routing/ingress plane lives in a separate, independent package; install it from there.
    /// </summary>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithKomodoDeploySupport(
        this IResourceBuilder<DockerComposeEnvironmentResource> builder,
        IConfigurationSection section,
        Action<KomodoDeployOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(section);

        return builder.WithKomodoDeploySupport(options =>
        {
            section.Bind(options);      // config first
            configure?.Invoke(options); // then code overrides
        });
    }

    /// <summary>Hack: replace a pipeline step's action with a no-op (init setter via reflection).</summary>
    private static bool TryNeutralizeStep(PipelineStep step)
    {
        try
        {
            var actionProperty = typeof(PipelineStep).GetProperty("Action");
            if (actionProperty is null)
            {
                return false;
            }
            Func<PipelineStepContext, Task> noop = _ => Task.CompletedTask;
            actionProperty.SetValue(step, noop);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
