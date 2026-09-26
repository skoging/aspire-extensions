using Aspire.Hosting.Komodo;
using Xunit;

namespace Aspire.Hosting.Komodo.Tests;

/// <summary>
/// Covers run-once detection (a service another one waits on with service_completed_successfully) and its
/// Resource-Sync output.
/// </summary>
public class KomodoRunOnceTests
{
    private const string Compose =
        """
        services:
          postgres:
            image: "postgres:18"
          app:
            image: "example/app:1"
            depends_on:
              postgres:
                condition: "service_started"
              migrate:
                condition: "service_completed_successfully"
            restart: "unless-stopped"
          migrate:
            image: "example/app:1"
            command:
              - "migrate"
            depends_on:
              postgres:
                condition: "service_started"
            restart: "no"
          web:
            image: "example/web:1"
            depends_on:
              app:
                condition: service_started
        networks:
          aspire:
            driver: "bridge"
        """;

    [Fact]
    public void FindsServicesOthersWaitToComplete()
    {
        Assert.Equal(["migrate"], KomodoRunOnce.FindServices(Compose));
    }

    [Fact]
    public void ListsEachServiceOnceInFirstSeenOrder()
    {
        var compose =
            """
            services:
              a:
                depends_on:
                  seed:
                    condition: service_completed_successfully
                  migrate:
                    condition: service_completed_successfully
              b:
                depends_on:
                  migrate:
                    condition: "service_completed_successfully"
            """;

        Assert.Equal(["seed", "migrate"], KomodoRunOnce.FindServices(compose));
    }

    [Fact]
    public void IgnoresOtherConditionsAndSectionsOutsideServices()
    {
        var compose =
            """
            services:
              app:
                depends_on:
                  postgres:
                    condition: service_healthy
            x-notes:
              app:
                depends_on:
                  fake:
                    condition: service_completed_successfully
            """;

        Assert.Empty(KomodoRunOnce.FindServices(compose));
    }

    [Fact]
    public void ResourceSyncTomlIgnoresRunOnceServices()
    {
        var toml = KomodoResyncToml.Render("demo", "local", Compose, KomodoRunOnce.FindServices(Compose));

        Assert.Contains("ignore_services = [\"migrate\"]", toml, StringComparison.Ordinal);
    }

    [Fact]
    public void ResourceSyncTomlOmitsIgnoreServicesWhenThereAreNone()
    {
        var toml = KomodoResyncToml.Render("demo", "local", "services:\n  web:\n    image: x\n");

        Assert.DoesNotContain("ignore_services", toml, StringComparison.Ordinal);
    }
}
