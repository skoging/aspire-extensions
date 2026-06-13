using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.Komodo;

/// <summary>
/// Minimal client for the Komodo Core HTTP API. All requests are
/// <c>POST {core}/{read|write|execute} {"type","params"}</c> with
/// <c>X-Api-Key</c> / <c>X-Api-Secret</c> headers; responses are the raw typed JSON.
/// </summary>
internal sealed class KomodoApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // Komodo expects exact snake_case names (server_id, file_contents, …) — no naming policy.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _core;

    public KomodoApiClient(HttpClient http, string coreUrl, string apiKey, string apiSecret)
    {
        _http = http;
        _core = coreUrl.TrimEnd('/');
        _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        _http.DefaultRequestHeaders.Add("X-Api-Secret", apiSecret);
    }

    private async Task<JsonElement> CallAsync(string route, string type, object @params, CancellationToken ct)
    {
        var payload = new Dictionary<string, object> { ["type"] = type, ["params"] = @params };
        using var resp = await _http.PostAsJsonAsync($"{_core}/{route}", payload, JsonOpts, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Komodo {route}/{type} failed ({(int)resp.StatusCode}): {Truncate(body, 500)}");
        }
        if (string.IsNullOrWhiteSpace(body))
        {
            return default;
        }
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    public Task<JsonElement> ReadAsync(string type, object p, CancellationToken ct) => CallAsync("read", type, p, ct);
    public Task<JsonElement> WriteAsync(string type, object p, CancellationToken ct) => CallAsync("write", type, p, ct);
    public Task<JsonElement> ExecuteAsync(string type, object p, CancellationToken ct) => CallAsync("execute", type, p, ct);

    /// <summary>Resolve a Komodo Server name to its id. Throws if not found.</summary>
    public async Task<string> ResolveServerIdAsync(string serverName, CancellationToken ct)
    {
        var servers = await ReadAsync("ListServers", new { }, ct);
        foreach (var s in servers.EnumerateArray())
        {
            if (string.Equals(GetString(s, "name"), serverName, StringComparison.OrdinalIgnoreCase))
            {
                return GetId(s);
            }
        }
        throw new InvalidOperationException($"Komodo server '{serverName}' not found.");
    }

    /// <summary>Find a Stack id by name, or null if it doesn't exist yet.</summary>
    public async Task<string?> FindStackIdAsync(string name, CancellationToken ct)
    {
        var stacks = await ReadAsync("ListStacks", new { }, ct);
        foreach (var s in stacks.EnumerateArray())
        {
            if (string.Equals(GetString(s, "name"), name, StringComparison.OrdinalIgnoreCase))
            {
                return GetId(s);
            }
        }
        return null;
    }

    /// <summary>Create-or-update a Stack with inline compose content + resolved environment (idempotent).</summary>
    public async Task UpsertStackAsync(string name, string serverId, string composeYaml, CancellationToken ct,
        string? environment = null, string? registryProvider = null, string? registryAccount = null,
        string? composeCmdWrapper = null)
    {
        // `environment` is a .env-style string Komodo hands to `docker compose` for ${VAR} interpolation.
        // registry_provider/registry_account point the Periphery at a Komodo Docker Registry Account so it
        // can authenticate pulls of private images (e.g. ghcr.io/<account>).
        // compose_cmd_wrapper wraps the compose COMMAND (e.g. `env SECRET=[[var]] -- docker compose up`);
        // Komodo interpolates [[var]] in THIS field, not the compose body, so secret injection dodges the
        // compose-flatten bug. _include selects which compose subcommands get wrapped.
        var config = new
        {
            server_id = serverId,
            files_on_host = false,
            file_contents = composeYaml,
            environment = environment ?? "",
            registry_provider = registryProvider ?? "",
            registry_account = registryAccount ?? "",
            compose_cmd_wrapper = composeCmdWrapper ?? "",
            // Wrap up/down/pull/build but NOT `config`: Komodo's wrapped `compose config` stage re-serializes
            // the resolved compose, and for a compose with an external network that re-serialize hits Core's
            // flatten bug — the whole file comes back as a single-line string the Periphery can't parse
            // ("invalid type: string, expected struct ComposeFile"). Secrets are only needed at up/pull/build,
            // so skipping `config` is free. Verified on local Komodo (the secret + external-network combo).
            compose_cmd_wrapper_include = string.IsNullOrEmpty(composeCmdWrapper)
                ? Array.Empty<string>()
                : new[] { "up", "down", "pull", "build" },
        };
        var existingId = await FindStackIdAsync(name, ct);
        if (existingId is null)
        {
            await WriteAsync("CreateStack", new { name, config }, ct);
        }
        else
        {
            await WriteAsync("UpdateStack", new { id = existingId, config }, ct);
        }
    }

    /// <summary>Create-or-update a global Komodo Variable (secrets routed here, referenced as [[name]]).</summary>
    public async Task UpsertVariableAsync(string name, string value, bool isSecret, CancellationToken ct)
    {
        try
        {
            await WriteAsync("CreateVariable", new { name, value, is_secret = isSecret, description = "" }, ct);
        }
        catch (InvalidOperationException)
        {
            // Already exists — update the value (is_secret was set at create time).
            await WriteAsync("UpdateVariableValue", new { name, value }, ct);
        }
    }

    /// <summary>Trigger DeployStack; returns the Update id to poll.</summary>
    public async Task<string> DeployStackAsync(string nameOrId, CancellationToken ct)
    {
        var update = await ExecuteAsync("DeployStack", new { stack = nameOrId }, ct);
        return GetId(update);
    }

    /// <summary>Trigger DestroyStack (bring the stack's containers down); returns the Update id to poll, or null.</summary>
    public async Task<string?> DestroyStackAsync(string nameOrId, CancellationToken ct)
    {
        var update = await ExecuteAsync("DestroyStack", new { stack = nameOrId }, ct);
        try { return GetId(update); } catch { return null; }
    }

    /// <summary>Delete a Stack record by id (after DestroyStack brings it down).</summary>
    public Task DeleteStackAsync(string id, CancellationToken ct) => WriteAsync("DeleteStack", new { id }, ct);

    /// <summary>Poll an Update until it completes; throws on failure or timeout.</summary>
    public async Task WaitForUpdateAsync(string updateId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var u = await ReadAsync("GetUpdate", new { id = updateId }, ct);
            var status = GetString(u, "status");
            if (string.Equals(status, "Complete", StringComparison.OrdinalIgnoreCase))
            {
                var success = u.TryGetProperty("success", out var sv) && sv.ValueKind == JsonValueKind.True;
                if (!success)
                {
                    throw new InvalidOperationException($"Komodo update {updateId} completed but failed: {Truncate(u.ToString(), 500)}");
                }
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new TimeoutException($"Komodo update {updateId} did not complete within {timeout}.");
    }

    // Komodo ids: "id" (string) on list items, or "_id":{"$oid":...} on full documents.
    private static string GetId(JsonElement e)
    {
        if (e.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String)
        {
            return idv.GetString()!;
        }
        if (e.TryGetProperty("_id", out var oid) && oid.TryGetProperty("$oid", out var ov))
        {
            return ov.GetString()!;
        }
        throw new InvalidOperationException($"No id on Komodo object: {Truncate(e.ToString(), 200)}");
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
