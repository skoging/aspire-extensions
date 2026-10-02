using System.Net;
using System.Text.Json;
using Aspire.Hosting.Komodo;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aspire.Hosting.Komodo.Tests;

/// <summary>Covers the stack's destroy_before_deploy setting, from configuration to the Komodo API.</summary>
public class KomodoDestroyBeforeDeployTests
{
    [Fact]
    public void BindsFromTheKomodoSection()
    {
        var options = new KomodoDeployOptions();
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DestroyBeforeDeploy"] = "true" })
            .Build()
            .Bind(options);

        Assert.True(options.DestroyBeforeDeploy);
        Assert.False(new KomodoDeployOptions().DestroyBeforeDeploy);
    }

    [Fact]
    public void ResourceSyncTomlCarriesItOnlyWhenSet()
    {
        Assert.Contains("destroy_before_deploy = true", KomodoResyncToml.Render("demo", "local", "services: {}\n", destroyBeforeDeploy: true));
        Assert.DoesNotContain("destroy_before_deploy", KomodoResyncToml.Render("demo", "local", "services: {}\n"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendsItInTheStackConfig(bool destroyBeforeDeploy)
    {
        var komodo = new StubKomodo();
        var client = new KomodoApiClient(new HttpClient(komodo), "http://komodo.test", "key", "secret");

        await client.UpsertStackAsync("demo", "server-1", "services: {}\n", default, destroyBeforeDeploy: destroyBeforeDeploy);

        var create = Assert.Single(komodo.Bodies, b => b.GetProperty("type").GetString() == "CreateStack");
        Assert.Equal(destroyBeforeDeploy, create.GetProperty("params").GetProperty("config").GetProperty("destroy_before_deploy").GetBoolean());
    }

    private sealed class StubKomodo : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            Bodies.Add(body);
            var reply = body.GetProperty("type").GetString() == "ListStacks" ? "[]" : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply) };
        }
    }
}
