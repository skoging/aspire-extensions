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
- **Stack-unique internal references** — on `aspire publish` each internal service gets a
  stack-unique `container_name` and its bare-alias references (`http://api:8080`) are rewritten to
  match (`http://{stack}-api:8080`), so stacks sharing an external docker network can never
  round-robin onto each other's services. Externally-exposed services are left to the ingress
  provider; no hand-pinned service URLs needed.
- **Down before up** — `Komodo:DestroyBeforeDeploy=true` sets the stack's `destroy_before_deploy`, so each
  deploy runs `compose down` first and containers are created under their final names instead of being
  recreated under a temporary `<id>_<name>` and renamed. Tools that track containers by name (a scale-to-zero
  proxy, say) can miss that rename. The stack is down while it redeploys (and stays down if `up` fails), and
  anonymous volumes don't carry over, so use it only where that's fine. Unset leaves the stack's own setting, so to turn it off again set it to `false` rather than removing it.
- **Services stopped on purpose** — `resource.ExcludeFromKomodoStackState()` leaves a service that
  is stopped by design (scaled to zero while idle) out of the stack's state, as run-once services
  already are, so the stack doesn't read Unhealthy and alert every time it stops.

Pairs with (but does not depend on) `Skoging.Aspire.Hosting.Pangolin` for ingress.
Full docs: https://github.com/skoging/aspire-extensions
