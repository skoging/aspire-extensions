using Aspire.Hosting.Komodo;
using Xunit;

namespace Aspire.Hosting.Komodo.Tests;

/// <summary>
/// Covers the pure text transform behind the <c>komodo-stamp-internal</c> step: stack-unique container_name
/// stamping for internal services + scheme://host reference rewriting, with the hardening guarantees
/// (idempotency, false-positive avoidance, *AsExisting exclusion, ingress coordination).
/// </summary>
public class KomodoInternalStampTests
{
    private static ISet<string> Set(params string[] names) => new HashSet<string>(names, StringComparer.Ordinal);

    [Fact]
    public void StampsInternalServicesAndRewritesReferences()
    {
        var compose =
            """
            services:
              api:
                image: "nginx:latest"
                expose:
                  - "8080"
                networks:
                  - "aspire"
              web:
                image: "nginx:latest"
                environment:
                  API_URL: "http://api:8080"
                  services__api__http__0: "http://api:8080"
                networks:
                  - "aspire"
            networks:
              aspire:
                driver: "bridge"
            """;

        var result = KomodoInternalStamp.ApplyStackUniqueNaming(
            compose, Set("api", "web"), Set(), "wishlist-prod", out var stamped, out var rewrites);

        Assert.Equal(2, stamped);
        Assert.Equal(2, rewrites);
        Assert.Contains("    container_name: wishlist-prod-api", result);
        Assert.Contains("    container_name: wishlist-prod-web", result);
        Assert.Contains("API_URL: \"http://wishlist-prod-api:8080\"", result);
        // The service-discovery KEY logical name is untouched; only its value host is rewritten.
        Assert.Contains("services__api__http__0: \"http://wishlist-prod-api:8080\"", result);
        Assert.DoesNotContain("\"http://api:8080\"", result);
    }

    [Fact]
    public void IsIdempotent()
    {
        var compose =
            """
            services:
              api:
                image: "nginx:latest"
                networks:
                  - "aspire"
              web:
                image: "nginx:latest"
                environment:
                  API_URL: "http://api:8080"
                networks:
                  - "aspire"
            networks:
              aspire:
                driver: "bridge"
            """;

        var once = KomodoInternalStamp.ApplyStackUniqueNaming(compose, Set("api", "web"), Set(), "wishlist-prod", out _, out _);
        var twice = KomodoInternalStamp.ApplyStackUniqueNaming(once, Set("api", "web"), Set(), "wishlist-prod", out var stamped2, out var rewrites2);

        Assert.Equal(once, twice);
        Assert.Equal(0, stamped2);
        Assert.Equal(0, rewrites2);
    }

    [Fact]
    public void DoesNotRewriteFqdnsPrefixesOrSchemelessHosts()
    {
        var compose =
            """
            services:
              api:
                image: "nginx:latest"
                networks:
                  - "aspire"
              web:
                image: "nginx:latest"
                environment:
                  EXT_URL: "http://api.example.com/x"
                  OTHER: "http://apiserver:9000"
                  DB: "Host=api;Port=5432"
                networks:
                  - "aspire"
            networks:
              aspire:
                driver: "bridge"
            """;

        var result = KomodoInternalStamp.ApplyStackUniqueNaming(compose, Set("api", "web"), Set(), "wishlist-prod", out _, out var rewrites);

        Assert.Equal(0, rewrites);
        Assert.Contains("http://api.example.com/x", result);
        Assert.Contains("http://apiserver:9000", result);
        Assert.Contains("Host=api;Port=5432", result);
    }

    [Fact]
    public void DoesNotStampOrRewriteExistingResources()
    {
        // postgres is referenced via *AsExisting → excluded from the emitted-service set entirely.
        var compose =
            """
            services:
              postgres:
                image: "postgres:18"
                networks:
                  - "postgres_shared"
              api:
                image: "nginx:latest"
                environment:
                  DB_URL: "tcp://postgres:5432"
                networks:
                  - "aspire"
            networks:
              aspire:
                driver: "bridge"
            """;

        var result = KomodoInternalStamp.ApplyStackUniqueNaming(compose, Set("api"), Set(), "wishlist-prod", out var stamped, out var rewrites);

        Assert.Equal(1, stamped);  // only api
        Assert.Equal(0, rewrites); // postgres ref untouched (not an emitted service)
        Assert.Contains("    container_name: wishlist-prod-api", result);
        Assert.DoesNotContain("container_name: wishlist-prod-postgres", result);
        Assert.Contains("tcp://postgres:5432", result);
    }

    [Fact]
    public void LeavesIngressStampedExternalUntouchedAndRewritesToItsReadBackName()
    {
        // web is externally-exposed AND already carries an ingress-stamped container_name with a DIFFERENT
        // stack name (pr-42) than this Komodo stack (wishlist-prod). We must not touch it, and references to
        // it must rewrite to the value on disk (pr-42-web), proving no recomputation / no 502.
        var compose =
            """
            services:
              web:
                container_name: pr-42-web
                image: "nginx:latest"
                networks:
                  - "ingress_shared"
              api:
                image: "nginx:latest"
                networks:
                  - "aspire"
              worker:
                image: "nginx:latest"
                environment:
                  services__web__http__0: "http://web:80"
                  services__api__http__0: "http://api:8080"
                networks:
                  - "aspire"
            networks:
              aspire:
                driver: "bridge"
            """;

        var result = KomodoInternalStamp.ApplyStackUniqueNaming(
            compose, Set("web", "api", "worker"), Set("web"), "wishlist-prod", out var stamped, out var rewrites);

        Assert.Equal(2, stamped); // api + worker (web is external → skipped)
        Assert.Contains("container_name: pr-42-web", result);
        Assert.DoesNotContain("container_name: wishlist-prod-web", result);
        Assert.Contains("    container_name: wishlist-prod-api", result);
        Assert.Contains("services__web__http__0: \"http://pr-42-web:80\"", result);
        Assert.Contains("services__api__http__0: \"http://wishlist-prod-api:8080\"", result);
        Assert.Equal(2, rewrites);
    }

    [Fact]
    public void DoesNotStampUnstampedExternalNorRewriteRefsToIt()
    {
        // api is externally-exposed but has no container_name (no ingress provider installed). We leave it
        // unstamped, and since we have no stack-unique name for it, references to it are left alone.
        var compose =
            """
            services:
              api:
                image: "nginx:latest"
                networks:
                  - "aspire"
              worker:
                image: "nginx:latest"
                environment:
                  services__api__http__0: "http://api:8080"
                networks:
                  - "aspire"
            networks:
              aspire:
                driver: "bridge"
            """;

        var result = KomodoInternalStamp.ApplyStackUniqueNaming(
            compose, Set("api", "worker"), Set("api"), "wishlist-prod", out var stamped, out var rewrites);

        Assert.Equal(1, stamped);  // worker only
        Assert.Equal(0, rewrites); // api has no effective container_name → ref left bare
        Assert.DoesNotContain("container_name: wishlist-prod-api", result);
        Assert.Contains("    container_name: wishlist-prod-worker", result);
        Assert.Contains("services__api__http__0: \"http://api:8080\"", result);
    }

    [Fact]
    public void RewritesMultipleHostsInOneValueAndSelfReference()
    {
        var compose =
            """
            services:
              api:
                image: "nginx:latest"
                environment:
                  SELF: "http://api:8080"
                networks:
                  - "aspire"
              web:
                image: "nginx:latest"
                environment:
                  BOTH: "http://api:8080,http://web:80"
                networks:
                  - "aspire"
            networks:
              aspire:
                driver: "bridge"
            """;

        var result = KomodoInternalStamp.ApplyStackUniqueNaming(compose, Set("api", "web"), Set(), "wishlist-prod", out _, out var rewrites);

        Assert.Contains("SELF: \"http://wishlist-prod-api:8080\"", result);
        Assert.Contains("BOTH: \"http://wishlist-prod-api:8080,http://wishlist-prod-web:80\"", result);
        Assert.Equal(3, rewrites);
    }

    [Fact]
    public void RewritesHostAfterUserinfo()
    {
        // scheme://user:pass@host — the host group must bind to the real host, not the userinfo.
        var compose =
            """
            services:
              api:
                image: "nginx:latest"
                networks:
                  - "aspire"
              worker:
                image: "nginx:latest"
                environment:
                  BROKER: "amqp://user:pass@api:5672"
                networks:
                  - "aspire"
            networks:
              aspire:
                driver: "bridge"
            """;

        var result = KomodoInternalStamp.ApplyStackUniqueNaming(compose, Set("api", "worker"), Set(), "wishlist-prod", out _, out var rewrites);

        Assert.Contains("BROKER: \"amqp://user:pass@wishlist-prod-api:5672\"", result);
        Assert.Equal(1, rewrites);
    }

    [Fact]
    public void HandlesRealisticComposeStructure()
    {
        // Mirrors the shape Aspire's Docker publisher emits: quoted values, a healthcheck block, a depends_on
        // MAP, restart, a blank line between services, a pre-stamped external service (web), and top-level
        // external networks — none of which must derail the env-only rewrite or the internal-only stamping.
        var compose =
            """
            services:
              api:
                image: "ghcr.io/x/api:latest"
                expose:
                  - "8080"
                environment:
                  ConnectionStrings__db: "Host=postgres;Username=app"
                  OIDC: "https://id.example.com"
                depends_on:
                  postgres:
                    condition: service_started
                healthcheck:
                  test: ["CMD", "curl", "http://localhost:8080/health"]
                restart: "unless-stopped"
                networks:
                  - "aspire"
                  - "observability"

              web:
                container_name: wishlist-prod-web
                image: "ghcr.io/x/web:latest"
                environment:
                  API_HTTP: "http://api:8080"
                  AUTH_URL: "https://onskeliste.example.com"
                networks:
                  - "aspire"
                  - "ingress_shared"
            networks:
              aspire:
                driver: "bridge"
              observability:
                external: true
              ingress_shared:
                name: ingress_shared
                external: true
            """;

        var result = KomodoInternalStamp.ApplyStackUniqueNaming(
            compose, Set("api", "web"), Set("web"), "wishlist-prod", out var stamped, out var rewrites);

        // api (internal) stamped; web (external) left exactly as the ingress provider set it.
        Assert.Equal(1, stamped);
        Assert.Contains("    container_name: wishlist-prod-api", result);
        Assert.Contains("    container_name: wishlist-prod-web", result);
        // Only the BFF's api reference is rewritten; conn-string host, FQDNs and the healthcheck URL are not.
        Assert.Equal(1, rewrites);
        Assert.Contains("API_HTTP: \"http://wishlist-prod-api:8080\"", result);
        Assert.Contains("Host=postgres;Username=app", result);
        Assert.Contains("OIDC: \"https://id.example.com\"", result);
        Assert.Contains("AUTH_URL: \"https://onskeliste.example.com\"", result);
        Assert.Contains("http://localhost:8080/health", result);
        // unrelated structure preserved
        Assert.Contains("condition: service_started", result);
        Assert.Contains("restart: \"unless-stopped\"", result);
    }
}
