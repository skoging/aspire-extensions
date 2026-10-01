using Aspire.Hosting.Pangolin;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.Komodo.Tests;

/// <summary>Covers routing a service's ingress through a front proxy (<c>WithIngressVia</c>).</summary>
public class PangolinIngressViaTests
{
    private static readonly IngressTarget Web = new(
        ResourceName: "web",
        ServiceName: "web",
        Hostname: "demo-web",
        Subdomain: "demo-web",
        TargetPort: 3000,
        Via: new IngressVia("gate", "demo-gate", 8080));

    [Fact]
    public void KeepsTheResourceKeyedOnTheServiceButTargetsTheFrontProxy()
    {
        var labels = new PangolinIngress("example.com").GetLabels(Web);

        Assert.Equal("demo-web", labels["pangolin.public-resources.demo-web.name"]);
        Assert.Equal("demo-web.example.com", labels["pangolin.public-resources.demo-web.full-domain"]);
        Assert.Equal("demo-gate", labels["pangolin.public-resources.demo-web.targets[0].hostname"]);
        Assert.Equal("8080", labels["pangolin.public-resources.demo-web.targets[0].port"]);
    }

    [Fact]
    public void TargetsTheServiceItselfWithoutAFrontProxy()
    {
        var labels = new PangolinIngress("example.com").GetLabels(Web with { Via = null });

        Assert.Equal("demo-web", labels["pangolin.public-resources.demo-web.targets[0].hostname"]);
        Assert.Equal("3000", labels["pangolin.public-resources.demo-web.targets[0].port"]);
    }

    [Fact]
    public void MergesIntoLabelsTheServiceAlreadyDeclares()
    {
        var compose =
            """
            services:
              gate:
                image: "gate:1"
                labels:
                  existing: "1"
                networks:
                  - "aspire"
            """;

        var stamped = PangolinIngressExtensions.StampService(
            compose, "gate", "demo-gate", new Dictionary<string, string> { ["added"] = "2" }, "ingress_shared",
            NullLogger.Instance);

        Assert.Equal(
            """
            services:
              gate:
                container_name: demo-gate
                image: "gate:1"
                labels:
                  existing: "1"
                  added: "2"
                networks:
                  - "aspire"
                  - "ingress_shared"
            """,
            stamped);
    }

    [Fact]
    public void NamesTheServiceWithoutLabelsOrNetworkWhenItsIngressGoesViaAFrontProxy()
    {
        var compose =
            """
            services:
              web:
                image: "web:1"
                ports:
                  - "3000"
                networks:
                  - "aspire"
            """;

        var stamped = PangolinIngressExtensions.StampService(compose, "web", "demo-web", null, null, NullLogger.Instance);

        Assert.Equal(
            """
            services:
              web:
                container_name: demo-web
                image: "web:1"
                networks:
                  - "aspire"
            """,
            stamped);
    }
}
