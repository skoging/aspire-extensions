using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Komodo;
using Xunit;

namespace Aspire.Hosting.Komodo.Tests;

/// <summary>Covers which services a stack's <c>ignore_services</c> lists.</summary>
public class KomodoStackStateTests
{
    private const string Compose =
        """
        services:
          app:
            depends_on:
              migrate:
                condition: "service_completed_successfully"
          migrate:
            image: "example/app:1"
        """;

    [Fact]
    public void ListsRunOnceServicesThenExcludedResources()
    {
        var excluded = new ContainerResource("App");
        excluded.Annotations.Add(new KomodoStackStateExcludedAnnotation());
        var model = new DistributedApplicationModel(new IResource[] { excluded, new ContainerResource("web") });

        Assert.Equal(["migrate", "app"], KomodoIgnoredServices.Resolve(model, Compose));
    }

    [Fact]
    public void ListsAServiceThatIsBothOnlyOnce()
    {
        Assert.Equal(["migrate", "app"], KomodoIgnoredServices.Merge(["migrate"], ["migrate", "app"]));
    }
}
