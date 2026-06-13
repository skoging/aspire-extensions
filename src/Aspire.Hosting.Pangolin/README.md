# Skoging.Aspire.Hosting.Pangolin

A label-driven [Pangolin](https://github.com/fosrl/pangolin) ingress provider for
[.NET Aspire](https://aspire.dev) Docker Compose deployments: externally-exposed services get
`pangolin.public-resources.*` docker labels stamped onto the emitted compose, which a
[newt](https://github.com/fosrl/newt) watcher turns into routed, TLS'd, optionally SSO-gated
public endpoints. A destroy-phase step removes the routes on `aspire destroy`.

> ⚠️ Experimental / alpha. Built on Aspire 13.x `[Experimental]` pipeline APIs; pinned to
> Aspire 13.4, targets net10.0. API will change before 1.0.

```csharp
builder.AddDockerComposeEnvironment("compose")
    .WithPangolinIngress(builder.Configuration.GetSection("Ingress"));

var web = builder.AddNpmApp("web", "../web")
    .WithHttpEndpoint(port: 3000)
    .WithExternalHttpEndpoints();          // external endpoints get ingress

web.WithPangolinPublicUrl("AUTH_URL");     // inject the public URL as env
```

Configure `Ingress:Domain` (absent = no-op, e.g. local runs), plus optional `StackName`
(subdomain prefix), `Sso`/`SsoIdp`/`WhitelistUsers`, and `ApiUrl`/`ApiKey`/`Org` for teardown.

Per-resource: `WithCustomDomain`, `WithPublicIngress()` (SSO off for one resource),
`WithIngressUpstreamMethod("h2c")` (gRPC backends), `WithPangolinPublicUrl/Host`.

Deploy-target-agnostic — depends only on `Aspire.Hosting` + `Aspire.Hosting.Docker`; pairs with
(but does not depend on) `Skoging.Aspire.Hosting.Komodo`.
Full docs: https://github.com/skoging/aspire-extensions
