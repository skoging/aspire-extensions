using System.Net;
using System.Text.Json;
using Aspire.Hosting.Komodo;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aspire.Hosting.Komodo.Tests;

/// <summary>Covers the stack's destroy_before_deploy setting, from configuration to the Komodo API.</summary>
public class KomodoDestroyBeforeDeployTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(null, null)]
    public void BindsFromTheKomodoSectionThroughWithKomodoDeploySupport(string? configured, bool? expected)
    {
        var builder = DistributedApplication.CreateBuilder();
        if (configured is not null)
        {
            builder.Configuration["Komodo:DestroyBeforeDeploy"] = configured;
        }

        var compose = builder.AddDockerComposeEnvironment("compose")
            .WithKomodoDeploySupport(builder.Configuration.GetSection("Komodo"));

        var options = compose.Resource.Annotations.OfType<KomodoDeployAnnotation>().Single().Options;
        Assert.Equal(expected, options.DestroyBeforeDeploy);
    }

    [Fact]
    public void ResourceSyncTomlCarriesItOnlyWhenConfigured()
    {
        Assert.Contains("destroy_before_deploy = true", KomodoResyncToml.Render("demo", "local", "services: {}\n", destroyBeforeDeploy: true));
        Assert.Contains("destroy_before_deploy = false", KomodoResyncToml.Render("demo", "local", "services: {}\n", destroyBeforeDeploy: false));
        Assert.DoesNotContain("destroy_before_deploy", KomodoResyncToml.Render("demo", "local", "services: {}\n"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(false, null)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(true, null)]
    public async Task SendsItOnCreateAndUpdateOnlyWhenConfigured(bool stackExists, bool? destroyBeforeDeploy)
    {
        var komodo = new StubKomodo(stackExists);
        var client = new KomodoApiClient(new HttpClient(komodo), "http://komodo.test", "key", "secret");

        await client.UpsertStackAsync("demo", "server-1", "services: {}\n", default, destroyBeforeDeploy: destroyBeforeDeploy);

        var write = Assert.Single(komodo.Bodies, b => b.GetProperty("type").GetString() is "CreateStack" or "UpdateStack");
        Assert.Equal(stackExists ? "UpdateStack" : "CreateStack", write.GetProperty("type").GetString());
        var config = write.GetProperty("params").GetProperty("config");
        Assert.Equal("services: {}\n", config.GetProperty("file_contents").GetString());
        if (destroyBeforeDeploy is { } expected)
        {
            Assert.Equal(expected, config.GetProperty("destroy_before_deploy").GetBoolean());
        }
        else
        {
            Assert.False(config.TryGetProperty("destroy_before_deploy", out _));
        }
    }

    private sealed class StubKomodo(bool stackExists) : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            Bodies.Add(body);
            var reply = body.GetProperty("type").GetString() == "ListStacks"
                ? stackExists ? """[{"name":"demo","id":"s1"}]""" : "[]"
                : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply) };
        }
    }
}
