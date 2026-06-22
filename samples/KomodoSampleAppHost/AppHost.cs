using Aspire.Hosting.Komodo;
using Aspire.Hosting.Pangolin;

var builder = DistributedApplication.CreateBuilder(args);

// The compose-env extension: Aspire's FULL compose generation, with the local docker-compose-up
// step suppressed so `aspire deploy` ships ONLY to Komodo.
// Komodo config is read from configuration (user-secrets / env), e.g.:
//   Komodo:CoreUrl, Komodo:ApiKey, Komodo:ApiSecret, Komodo:ServerName
builder.AddDockerComposeEnvironment("komodo")
    .WithDashboard(false)
    .WithKomodoDeploySupport(options =>
    {
        options.CoreUrl = builder.Configuration["Komodo:CoreUrl"];
        options.ApiKey = builder.Configuration["Komodo:ApiKey"];
        options.ApiSecret = builder.Configuration["Komodo:ApiSecret"];
        options.ServerName = builder.Configuration["Komodo:ServerName"];
        options.StackName = builder.Configuration["Komodo:StackName"];
        // Pull private images via a Komodo Docker Registry Account (e.g. ghcr.io / skoging).
        options.RegistryProvider = builder.Configuration["Komodo:RegistryProvider"];
        options.RegistryAccount = builder.Configuration["Komodo:RegistryAccount"];
    })
    // Optional pluggable ingress (a SEPARATE package/plane from the Komodo deploy target). Value-driven:
    // a no-op unless the `Ingress` config section has a `Domain` (e.g. example.com). When set, a publish-
    // phase step stamps Pangolin labels on external services + a destroy-phase step tears the routes down.
    // The subdomain default reads `Ingress:StackName` (falling back to the env resource name).
    .WithPangolinIngress(builder.Configuration.GetSection("Ingress"));

// A secret parameter — demonstrates the SecretProvider. The value is vaulted as an is_secret Komodo
// Variable and injected via the compose_cmd_wrapper, so it never lands in the stored compose. The demo
// value deliberately contains YAML flow chars + a space to prove secrets bypass inline resolution
// (which would break on {}[]) and travel through the wrapper instead.
var apiToken = builder.AddParameter("apitoken", "p@ss {w0rd} [x]", secret: true);

// The thinnest possible workload: a single nginx container, consuming the secret as an env var.
// A buildable image (not a public one) so `aspire do push` / `aspire deploy` exercises the GHCR push.
builder.AddDockerfile("aspire-spike", ".", "Dockerfile")
    .WithHttpEndpoint(targetPort: 80)
    .WithExternalHttpEndpoints()
    .WithEnvironment("API_TOKEN", apiToken);

// An internal service + a consumer that references it by name. Demonstrates stack-unique internal reference
// rewriting: the consumer's `services__api__http__0` value `http://api:8080` is rewritten to
// `http://{stack}-api:8080`, and each service gets a stack-unique `container_name` — so on a shared external
// network this stack can never round-robin onto another stack's `api`. No-op in `aspire run`.
var internalApi = builder.AddContainer("api", "nginx").WithHttpEndpoint(targetPort: 8080, name: "http");
builder.AddContainer("worker", "nginx").WithReference(internalApi.GetEndpoint("http"));

// Push built images to your registry (here GHCR); Komodo pulls them via the configured registry account.
#pragma warning disable ASPIRECOMPUTE003
builder.AddContainerRegistry("ghcr", "ghcr.io", "skoging");
#pragma warning restore ASPIRECOMPUTE003

builder.Build().Run();
