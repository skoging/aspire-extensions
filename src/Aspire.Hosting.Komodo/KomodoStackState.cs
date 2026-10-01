using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Komodo;

/// <summary>Controls which compose services count toward a Komodo stack's state.</summary>
public static class KomodoStackStateExtensions
{
    /// <summary>
    /// Leaves this resource's compose service out of the Komodo stack's state (the stack's <c>ignore_services</c>).
    /// For a service that is stopped on purpose while the stack is healthy, such as one scaled to zero: Komodo
    /// derives stack state from container state, so without this the stack reads Unhealthy whenever the service is
    /// stopped and raises a state-change alert on every stop and start.
    /// </summary>
    /// <remarks>Run-once services (see the package README) are already left out; this is for everything else.</remarks>
    public static IResourceBuilder<T> ExcludeFromKomodoStackState<T>(this IResourceBuilder<T> builder)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!builder.Resource.Annotations.OfType<KomodoStackStateExcludedAnnotation>().Any())
        {
            builder.Resource.Annotations.Add(new KomodoStackStateExcludedAnnotation());
        }
        return builder;
    }
}

/// <summary>Marks a resource as excluded from its Komodo stack's state.</summary>
internal sealed class KomodoStackStateExcludedAnnotation : IResourceAnnotation;

internal static class KomodoIgnoredServices
{
    /// <summary>
    /// The stack's <c>ignore_services</c>: the run-once services found in <paramref name="compose"/>, then every
    /// resource excluded with <see cref="KomodoStackStateExtensions.ExcludeFromKomodoStackState{T}"/>.
    /// </summary>
    public static IReadOnlyList<string> Resolve(DistributedApplicationModel model, string compose) =>
        Merge(
            KomodoRunOnce.FindServices(compose),
            model.Resources
                .Where(r => r.Annotations.OfType<KomodoStackStateExcludedAnnotation>().Any())
                .Select(r => r.Name.ToLowerInvariant()));

    public static IReadOnlyList<string> Merge(IReadOnlyList<string> runOnce, IEnumerable<string> excluded) =>
        runOnce.Concat(excluded).Distinct(StringComparer.Ordinal).ToList();
}
