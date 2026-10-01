using System.Text.Json;
using Aspire.Hosting.Komodo;
using Xunit;

namespace Aspire.Hosting.Komodo.Tests;

/// <summary>Covers the error a failed Komodo update is reported with.</summary>
public class KomodoUpdateFailureTests
{
    [Fact]
    public void ReportsTheFailedStageAsPlainText()
    {
        var update = JsonDocument.Parse(
            """
            {
              "operation": "DeployStack",
              "success": false,
              "logs": [
                { "stage": "Interpolate Secrets", "command": "", "success": true,
                  "stdout": "<span class=\"text-muted-foreground\">replaced:</span> demo_secret" },
                { "stage": "Compose Up", "command": "docker compose -p demo up -d", "success": false,
                  "stdout": "pulling", "stderr": "network <b>edge</b> declared as external, but could not be found &amp; so on" }
              ]
            }
            """).RootElement;

        Assert.Equal(
            "Komodo DeployStack u1 failed at stage 'Compose Up' ($ docker compose -p demo up -d):\n"
            + "network edge declared as external, but could not be found & so on",
            KomodoApiClient.DescribeFailure("u1", update));
    }

    [Fact]
    public void FallsBackToStdoutAndKeepsTheEndOfLongOutput()
    {
        var output = new string('x', 5000) + "the error";
        var update = JsonDocument.Parse(
            $$"""{ "operation": "RunStackService", "logs": [ { "stage": "Run", "success": false, "stdout": "{{output}}" } ] }""")
            .RootElement;

        var message = KomodoApiClient.DescribeFailure("u2", update);

        Assert.StartsWith("Komodo RunStackService u2 failed at stage 'Run':\n…", message, StringComparison.Ordinal);
        Assert.EndsWith("the error", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FallsBackToTheDocumentWithoutAFailedStage()
    {
        var update = JsonDocument.Parse("""{ "operation": "DeployStack", "success": false, "logs": [] }""").RootElement;

        Assert.StartsWith("Komodo DeployStack u3 failed: {", KomodoApiClient.DescribeFailure("u3", update), StringComparison.Ordinal);
    }
}
