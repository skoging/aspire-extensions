# aspire-extensions

Self-host-flavored extensions for [.NET Aspire](https://aspire.dev): deploy an Aspire app to a
[Komodo](https://komo.do) server, and publish services through a
[Pangolin](https://github.com/fosrl/pangolin) ingress — as two **independent** packages.

| Package | What it does |
| --- | --- |
| [`Skoging.Aspire.Hosting.Komodo`](src/Aspire.Hosting.Komodo) | A Komodo **deploy target**: `aspire deploy` ships the emitted Docker Compose stack to a Komodo server via its API (`aspire publish` emits a GitOps-friendly Resource-Sync TOML instead). |
| [`Skoging.Aspire.Hosting.Pangolin`](src/Aspire.Hosting.Pangolin) | A label-driven Pangolin **ingress provider**: stamps `pangolin.public-resources.*` docker labels onto externally-exposed services so a [newt](https://github.com/fosrl/newt) watcher turns them into routed, TLS'd, optionally SSO-gated public endpoints. |

> ⚠️ **Experimental / alpha.** Built on Aspire 13.x pipeline + compute-environment APIs that are
> themselves `[Experimental]` and may change between Aspire minors. Targets `net10.0` and is pinned
> to Aspire 13.4. The public API will churn before 1.0. Used in production on the author's
> homelab; not battle-tested anywhere else (yet).

## Design: two packages, zero coupling

The deploy plane and the ingress plane are deliberately independent — each depends on only
`Aspire.Hosting` + `Aspire.Hosting.Docker`, and **neither references the other**:

- **Komodo** deploys whatever compose Aspire emits. It is resource-type-agnostic: no references to
  postgres/redis/anything — any current or future Aspire integration deploys without changes here.
- **Pangolin** runs as its own publish-phase pipeline step that decorates the emitted compose
  (labels, ingress network, container names) before *any* deploy step reads it, plus its own
  destroy-phase step that deletes the routes.
- The only contract between them is the **docker-compose file itself**. Use either alone, or both
  together; combine Pangolin with a different deploy target, or Komodo with a different ingress.

(Namespaces are `Aspire.Hosting.Komodo` / `Aspire.Hosting.Pangolin` for discoverability — the
Aspire community convention. Package IDs carry the `Skoging.` prefix because `Aspire.Hosting.*`
is a reserved package prefix on nuget.org.)

## Quickstart — deploy to Komodo

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("compose")
    .WithKomodoDeploySupport(builder.Configuration.GetSection("Komodo"));

// ... AddProject / AddContainer / AddNodeApp as usual ...

builder.Build().Run();
```

Config (user-secrets locally, `Komodo__*` env in CI):

| Key | Meaning |
| --- | --- |
| `Komodo:CoreUrl` | Komodo Core, e.g. `https://komodo.example.com` |
| `Komodo:ApiKey` / `Komodo:ApiSecret` | an API key minted in Komodo |
| `Komodo:ServerName` | the Komodo Server (Periphery) to deploy onto |
| `Komodo:StackName` | the stack name (defaults to the compose environment name) |
| `Komodo:RegistryProvider` / `Komodo:RegistryAccount` | optional — pull private images via a Komodo registry account |

- **`aspire publish`** → emits the compose **plus** a `komodo-<stack>.toml` Resource-Sync file
  (point a Komodo Resource Sync at it for GitOps).
- **`aspire deploy`** → upserts the Stack via the API, deploys it, waits for completion, and cleans
  up the generated files. The compose environment's local `docker compose up` is suppressed —
  nothing runs on your machine.
- **`aspire destroy`** → `DestroyStack` + `DeleteStack`. Reversible deploys.
- **Secrets**: `.Secret` parameters never enter the stored compose — they are vaulted as Komodo
  Variables and injected at compose-time via `compose_cmd_wrapper`. The vault is pluggable
  (`KomodoDeployOptions.SecretProvider` / `ISecretProvider`) — implement it to back secrets with an
  external vault instead.
- **Existing resources**: `postgres.PublishAsExisting(connectionString, network)` (and
  `RunAsExisting`/`AsExisting`, mirroring the Azure trio) redirect dependents to an
  already-running instance instead of deploying a fresh container.

## Quickstart — Pangolin ingress

```csharp
builder.AddDockerComposeEnvironment("compose")
    .WithPangolinIngress(builder.Configuration.GetSection("Ingress"));

var web = builder.AddNpmApp("web", "../web")
    .WithHttpEndpoint(port: 3000)
    .WithExternalHttpEndpoints();   // ← external endpoints get ingress

web.WithPangolinPublicUrl("AUTH_URL");  // inject the resulting public URL as env
```

Config (`Ingress` section): `Domain` (required — absent = the whole feature no-ops, e.g. local
runs), `StackName` (subdomain prefix; defaults to the environment name), `Sso`, `SsoIdp`,
`WhitelistUsers`, and optional `ApiUrl`/`ApiKey`/`Org` for route teardown on `aspire destroy`
(newt does not reconcile removals — fosrl/pangolin#1864).

Per-resource: `WithCustomDomain("sub")`, `WithPublicIngress()` (SSO off for one resource — e.g. an
IdP that must stay reachable un-gated), `WithIngressUpstreamMethod("h2c")` (gRPC backends),
`WithPangolinPublicUrl/Host(envVar)`.

Each externally-exposed service gets: the `pangolin.public-resources.<stack>-<name>.*` labels, a
stack-unique `container_name`, membership in the ingress network, and its host-published ports
dropped (the ingress is the entry point). Your newt instance picks the labels up from the docker
socket and registers the Pangolin resources.

## Local testing

`local-komodo/` runs a hermetic Komodo (Core + Mongo + Periphery driving your local docker):

```bash
docker compose -f local-komodo/docker-compose.yml up -d   # admin/admin on :9120
# mint an API key in the UI (or via the API), set Komodo:* config, then:
aspire deploy --project samples/KomodoSampleAppHost/KomodoSampleAppHost.csproj
docker compose -f local-komodo/docker-compose.yml down -v
```

`samples/KomodoSampleAppHost` is a minimal end-to-end consumer of both packages.

## License

[MIT](LICENSE)
