using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Komodo;

/// <summary>
/// Configuration for deploying an Aspire Docker Compose environment to a Komodo server.
/// </summary>
public sealed class KomodoDeployOptions
{
    /// <summary>Base URL of the Komodo Core API (e.g. <c>http://localhost:9120</c>).</summary>
    public string? CoreUrl { get; set; }

    /// <summary>Komodo API key, sent as the <c>X-Api-Key</c> header.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Komodo API secret, sent as the <c>X-Api-Secret</c> header.</summary>
    public string? ApiSecret { get; set; }

    /// <summary>Name of the Komodo Server (Periphery host) the stack is deployed onto.</summary>
    public string? ServerName { get; set; }

    /// <summary>Stack name in Komodo. Defaults to the compose environment resource name.</summary>
    public string? StackName { get; set; }

    /// <summary>
    /// When true (default), <c>aspire publish</c> emits a Komodo Resource-Sync TOML next to the
    /// generated compose for inspection / GitOps. <c>aspire deploy</c> never leaves it behind.
    /// </summary>
    public bool EmitResourceSyncToml { get; set; } = true;

    /// <summary>
    /// Registry provider (domain, e.g. <c>ghcr.io</c>) set on the Komodo Stack so the Periphery
    /// authenticates pulls of private images. Pairs with <see cref="RegistryAccount"/>. The matching
    /// Komodo Docker Registry Account must already exist (registered out-of-band).
    /// </summary>
    public string? RegistryProvider { get; set; }

    /// <summary>
    /// Registry account name (the Komodo Docker Registry Account username, e.g. <c>skoging</c>) used to
    /// pull private images. Pairs with <see cref="RegistryProvider"/>.
    /// </summary>
    public string? RegistryAccount { get; set; }

    /// <summary>
    /// Secret provider — produces the Komodo <c>compose_cmd_wrapper</c> that injects secret values into the
    /// compose command's environment so they never enter the stored compose. Defaults to
    /// <see cref="KomodoVariablesSecretProvider"/> (Komodo Variables); implement <see cref="ISecretProvider"/>
    /// to back secrets with an external vault. A pluggable plane, decoupled from Komodo's deploy mechanics.
    /// </summary>
    public ISecretProvider SecretProvider { get; set; } = new KomodoVariablesSecretProvider();
}

/// <summary>Carries <see cref="KomodoDeployOptions"/> on the compose environment resource.</summary>
internal sealed class KomodoDeployAnnotation(KomodoDeployOptions options) : IResourceAnnotation
{
    public KomodoDeployOptions Options { get; } = options;
}
