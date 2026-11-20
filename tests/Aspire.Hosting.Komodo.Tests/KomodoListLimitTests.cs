using System.Net;
using System.Text.Json;
using Aspire.Hosting.Komodo;
using Xunit;

namespace Aspire.Hosting.Komodo.Tests;

/// <summary>
/// Komodo 2.3 pages its List APIs by default (50 items, configurable) unless the request passes
/// <c>limit: 0</c>, so every List call asks for all of them: past one page, a deploy would miss its
/// existing stack and create a duplicate.
/// </summary>
public class KomodoListLimitTests
{
    [Fact]
    public async Task EveryListCallAsksForAllItems()
    {
        var komodo = new StubKomodo();
        var client = new KomodoApiClient(new HttpClient(komodo), "http://komodo.test", "key", "secret");

        await client.ResolveServerIdAsync("server", default);
        await client.FindStackIdAsync("demo", default);
        await client.ListVariableNamesAsync(default);

        Assert.Equal(["ListServers", "ListStacks", "ListVariables"],
            komodo.Bodies.Select(b => b.GetProperty("type").GetString()));
        Assert.All(komodo.Bodies,
            b => Assert.Equal(0, b.GetProperty("params").GetProperty("limit").GetInt32()));
    }

    [Fact]
    public async Task FindsAStackPastTheFiftiethItem()
    {
        var komodo = new StubKomodo();
        var client = new KomodoApiClient(new HttpClient(komodo), "http://komodo.test", "key", "secret");

        Assert.Equal("s60", await client.FindStackIdAsync("stack-60", default));
    }

    /// <summary>
    /// Answers like Komodo 2.3 with its default page of 50: 60 servers and stacks, only the first 50 unless
    /// the request passes limit 0. ListVariables doesn't page, so it always answers all 60.
    /// </summary>
    private sealed class StubKomodo : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            Bodies.Add(body);
            var type = body.GetProperty("type").GetString();
            var all = type == "ListVariables"
                || (body.GetProperty("params").TryGetProperty("limit", out var limit) && limit.GetInt32() == 0);
            var prefix = type switch
            {
                "ListServers" => "server",
                "ListStacks" => "stack",
                _ => "variable",
            };
            var items = Enumerable.Range(1, all ? 60 : 50)
                .Select(i => new { name = i == 1 && prefix == "server" ? "server" : $"{prefix}-{i}", id = $"s{i}" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(items)),
            };
        }
    }
}
