using System.Net;
using System.Text.Json;
using Aspire.Hosting.Pangolin;
using Xunit;

namespace Aspire.Hosting.Komodo.Tests;

/// <summary>Covers finding and deleting a resource through Pangolin's paged list endpoint.</summary>
public class PangolinTeardownTests
{
    private static PangolinIngress Ingress() =>
        new("example.com", apiUrl: "https://pangolin.test", apiKey: "key", org: "org");

    [Fact]
    public async Task FindsAResourceBeyondTheFirstPage()
    {
        // Ignores `query` and serves 20 per page, as the endpoint does by default.
        var api = new StubPangolin(Enumerable.Range(1, 45).Select(i => ($"other-{i:D2}", i)).Append(("demo-web", 99)), pageSize: 20);

        Assert.True(await Ingress().TeardownAsync(new HttpClient(api), "demo-web", default));
        Assert.Equal("/v1/resource/99", api.Deleted);
    }

    [Fact]
    public async Task MatchesTheNiceIdExactly()
    {
        var api = new StubPangolin([("demo-web-old", 1), ("demo-website", 2)], pageSize: 20);

        Assert.False(await Ingress().TeardownAsync(new HttpClient(api), "demo-web", default));
        Assert.Null(api.Deleted);
    }

    [Fact]
    public async Task DoesNothingWithoutTheIntegrationApi()
    {
        var api = new StubPangolin([("demo-web", 1)], pageSize: 20);

        Assert.False(await new PangolinIngress("example.com").TeardownAsync(new HttpClient(api), "demo-web", default));
        Assert.Equal(0, api.Requests);
    }

    private sealed class StubPangolin(IEnumerable<(string NiceId, int Id)> resources, int pageSize) : HttpMessageHandler
    {
        private readonly List<(string NiceId, int Id)> _resources = resources.ToList();

        public string? Deleted { get; private set; }
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (request.Method == HttpMethod.Delete)
            {
                Deleted = request.RequestUri!.AbsolutePath;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
            var page = int.Parse(
                request.RequestUri!.Query.TrimStart('?').Split('&').First(p => p.StartsWith("page=", StringComparison.Ordinal))[5..]);
            var body = JsonSerializer.Serialize(new
            {
                data = new
                {
                    resources = _resources.Skip((page - 1) * pageSize).Take(pageSize)
                        .Select(r => new { niceId = r.NiceId, resourceId = r.Id }),
                    pagination = new { total = _resources.Count, pageSize, page },
                },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
