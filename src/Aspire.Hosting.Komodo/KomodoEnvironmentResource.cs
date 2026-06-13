using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Komodo;

/// <summary>
/// A compute environment that deploys the app to a Komodo server.
/// </summary>
/// <remarks>
/// Unlike extending the Docker Compose environment (<see cref="KomodoExtensions.WithKomodoDeploySupport"/>),
/// this generates the compose itself and ships it to Komodo — it never runs a local
/// <c>docker compose up</c>, so nothing is deployed to the local host.
/// </remarks>
public sealed class KomodoEnvironmentResource : Resource, IComputeEnvironmentResource
{
    public KomodoEnvironmentResource(string name, KomodoDeployOptions options) : base(name)
        => Options = options;

    public KomodoDeployOptions Options { get; }

    /// <summary>Inside the generated compose, services reach each other by service (resource) name.</summary>
    public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference)
        => ReferenceExpression.Create($"{endpointReference.Resource.Name}");
}
