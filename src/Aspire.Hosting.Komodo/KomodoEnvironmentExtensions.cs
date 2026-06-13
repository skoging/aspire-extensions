using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Komodo;

/// <summary>
/// A dedicated Komodo compute environment: <c>aspire deploy</c> ships the app to Komodo and
/// does <b>not</b> run a local <c>docker compose up</c>.
/// </summary>
public static class KomodoEnvironmentExtensions
{
    /// <summary>Adds a Komodo compute environment. Container resources are generated into a compose
    /// document and deployed as a Komodo Stack.</summary>
    public static IResourceBuilder<KomodoEnvironmentResource> AddKomodoEnvironment(
        this IDistributedApplicationBuilder builder, string name, Action<KomodoDeployOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new KomodoDeployOptions();
        configure?.Invoke(options);
        var resource = new KomodoEnvironmentResource(name, options);
        var rb = builder.AddResource(resource);

        var genStep = $"komodo-generate-{name}";
        return rb
            // PUBLISH: generate the compose from the model (no Docker Compose environment involved).
            .WithPipelineStepFactory(
                stepName: genStep,
                callback: (PipelineStepContext ctx) => KomodoEnvSteps.GenerateComposeAsync(ctx, name),
                dependsOn: [],
                requiredBy: [WellKnownPipelineSteps.Publish],
                tags: [],
                description: "Generates docker-compose.yaml from the app model for Komodo.")
            // PUBLISH: emit the inspectable resync TOML next to it.
            .WithPipelineStepFactory(
                stepName: $"komodo-emit-resync-{name}",
                callback: (PipelineStepContext ctx) => KomodoSteps.EmitResourceSyncAsync(ctx, options, name),
                dependsOn: [genStep],
                requiredBy: [WellKnownPipelineSteps.Publish],
                tags: [],
                description: "Emits a Komodo Resource-Sync TOML alongside the generated compose.")
            // DEPLOY: upsert + deploy to Komodo, then clean up.
            .WithPipelineStepFactory(
                stepName: $"komodo-deploy-{name}",
                callback: (PipelineStepContext ctx) => KomodoSteps.DeployAsync(ctx, options, name),
                dependsOn: [genStep, WellKnownPipelineSteps.Push],
                requiredBy: [WellKnownPipelineSteps.Deploy],
                tags: [],
                description: "Upserts + deploys the stack to Komodo, then removes generated temp files.");
    }
}

/// <summary>The generate-compose step for the dedicated Komodo environment.</summary>
internal static class KomodoEnvSteps
{
    public static async Task GenerateComposeAsync(PipelineStepContext context, string envName)
    {
        var outDir = context.Services.GetRequiredService<IPipelineOutputService>().GetOutputDirectory();
        Directory.CreateDirectory(outDir);
        var compose = await KomodoComposeGenerator.GenerateAsync(context.Model, context.ExecutionContext, context.CancellationToken);
        var path = Path.Combine(outDir, "docker-compose.yaml");
        await File.WriteAllTextAsync(path, compose, context.CancellationToken);
        context.Logger.LogInformation("Komodo: generated compose ({Bytes} bytes) -> {Path}", compose.Length, path);
    }
}
