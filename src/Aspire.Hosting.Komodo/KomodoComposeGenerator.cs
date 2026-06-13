using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker.Resources;
using Aspire.Hosting.Docker.Resources.ComposeNodes;

namespace Aspire.Hosting.Komodo;

/// <summary>
/// Generates a docker-compose document from the app model's container resources, using Aspire's
/// own (public) compose object model (<see cref="ComposeFile"/> / <see cref="Service"/>) and its
/// public <c>ToYaml()</c> serializer — so the output format matches Aspire exactly without
/// vendoring the (internal) generator.
/// </summary>
/// <remarks>
/// Covers container resources: image, exposed target ports, environment variables (resolved via
/// <see cref="IValueProvider"/>), command/args. Volumes, networks, and project-build resources
/// (image push, container-port placeholders) are not yet handled — those live in Aspire's internal
/// <c>DockerComposePublishingContext</c>; the clean long-term fix is for Aspire to make its
/// generator public.
/// </remarks>
internal static class KomodoComposeGenerator
{
    public static async Task<string> GenerateAsync(
        DistributedApplicationModel model, DistributedApplicationExecutionContext executionContext, CancellationToken ct)
    {
        var containers = model.Resources.OfType<ContainerResource>().ToList();
        if (containers.Count == 0)
        {
            throw new InvalidOperationException(
                "Komodo: no container resources to deploy. This generator currently supports container resources.");
        }

        var compose = new ComposeFile();
        foreach (var c in containers)
        {
            var service = new Service { Name = c.Name };

            if (c.TryGetContainerImageName(out var image) && !string.IsNullOrEmpty(image))
            {
                service.Image = image;
            }

            if (c.TryGetEndpoints(out var endpoints))
            {
                foreach (var port in endpoints.Where(e => e.TargetPort is not null)
                                              .Select(e => e.TargetPort!.Value)
                                              .Distinct())
                {
                    service.Expose.Add(port.ToString(CultureInfo.InvariantCulture));
                }
            }

            foreach (var (key, value) in await ResolveEnvironmentAsync(c, executionContext, ct))
            {
                service.Environment[key] = value;
            }

            foreach (var arg in await ResolveArgsAsync(c, executionContext, ct))
            {
                service.Command.Add(arg);
            }

            service.Restart = "unless-stopped";
            compose.AddService(service);
        }

        return compose.ToYaml();
    }

    private static async Task<Dictionary<string, string>> ResolveEnvironmentAsync(
        IResource resource, DistributedApplicationExecutionContext executionContext, CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        if (!resource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var callbacks))
        {
            return result;
        }

        var context = new EnvironmentCallbackContext(executionContext, resource, new Dictionary<string, object>(), ct);
        foreach (var callback in callbacks)
        {
            await callback.Callback(context);
        }

        foreach (var (key, value) in context.EnvironmentVariables)
        {
            var resolved = await ResolveValueAsync(value, ct);
            if (resolved is not null)
            {
                result[key] = resolved;
            }
        }
        return result;
    }

    private static async Task<List<string>> ResolveArgsAsync(
        IResource resource, DistributedApplicationExecutionContext executionContext, CancellationToken ct)
    {
        var args = new List<string>();
        if (!resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var callbacks))
        {
            return args;
        }

        var context = new CommandLineArgsCallbackContext(new List<object>(), resource, ct)
        {
            ExecutionContext = executionContext,
        };
        foreach (var callback in callbacks)
        {
            await callback.Callback(context);
        }

        foreach (var arg in context.Args)
        {
            var resolved = await ResolveValueAsync(arg, ct);
            if (resolved is not null)
            {
                args.Add(resolved);
            }
        }
        return args;
    }

    private static async Task<string?> ResolveValueAsync(object? value, CancellationToken ct) => value switch
    {
        null => null,
        string s => s,
        IValueProvider provider => await provider.GetValueAsync(ct),
        _ => value.ToString(),
    };
}
