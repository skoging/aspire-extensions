# Skoging.Aspire.Hosting.Komodo

A [Komodo](https://komo.do) deploy target for [.NET Aspire](https://aspire.dev):
`aspire deploy` ships your app's emitted Docker Compose stack to a Komodo server via its API,
`aspire publish` emits a GitOps-friendly Resource-Sync TOML, and `aspire destroy` tears the stack
down again. Nothing runs on your local machine.

> ⚠️ Experimental / alpha. Built on Aspire 13.x `[Experimental]` pipeline APIs; pinned to
> Aspire 13.4, targets net10.0. API will change before 1.0.

```csharp
builder.AddDockerComposeEnvironment("compose")
    .WithKomodoDeploySupport(builder.Configuration.GetSection("Komodo"));
```

Configure `Komodo:CoreUrl`, `Komodo:ApiKey`, `Komodo:ApiSecret`, `Komodo:ServerName`
(+ optional `StackName`, `RegistryProvider`/`RegistryAccount` for private images).

Highlights:

- **Resource-type-agnostic** — deploys whatever compose Aspire emits; depends only on
  `Aspire.Hosting` + `Aspire.Hosting.Docker`.
- **Secrets stay out of the stored compose** — `.Secret` parameters are vaulted as Komodo
  Variables and injected via `compose_cmd_wrapper` (pluggable `ISecretProvider` — implement it to
  back secrets with an external vault).
- **Existing-resource family** — `PublishAsExisting` / `RunAsExisting` / `AsExisting` redirect
  dependents to an already-running instance (e.g. a shared postgres) instead of deploying one.
- **Join shared external networks** — `resource.WithExternalNetwork("name")` attaches the emitted
  compose service to a pre-existing external docker network (a shared collector or database network)
  *and* declares it `external: true` at the top level — no hand-rolled `ConfigureComposeFile` /
  `PublishAsDockerComposeService` escape hatches. Publish-only; idempotent across many joiners.

Pairs with (but does not depend on) `Skoging.Aspire.Hosting.Pangolin` for ingress.
Full docs: https://github.com/skoging/aspire-extensions
