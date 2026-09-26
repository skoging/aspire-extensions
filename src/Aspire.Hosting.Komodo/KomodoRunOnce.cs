namespace Aspire.Hosting.Komodo;

/// <summary>
/// Run-once services: ones another service waits on with <c>condition: service_completed_successfully</c>
/// (a migrator, a seeder). They exit by design, so the deploy runs each one before the stack is redeployed
/// and Komodo is told to leave them out of the stack's status.
/// </summary>
internal static class KomodoRunOnce
{
    private const string Condition = "service_completed_successfully";

    /// <summary>
    /// Names of the services some other service depends on with <see cref="Condition"/>, in first-seen order.
    /// Reads the compose the Aspire Docker publisher emits (2-space indent, <c>depends_on</c> as a map).
    /// </summary>
    public static IReadOnlyList<string> FindServices(string compose)
    {
        var found = new List<string>();
        var inServices = false;
        var inDependsOn = false;
        string? dependency = null;
        foreach (var raw in compose.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var indent = line.Length - line.TrimStart(' ').Length;
            if (indent == 0)
            {
                inServices = trimmed == "services:";
                inDependsOn = false;
                dependency = null;
            }
            else if (!inServices)
            {
                continue;
            }
            else if (indent <= 4)
            {
                inDependsOn = indent == 4 && trimmed == "depends_on:";
                dependency = null;
            }
            else if (inDependsOn && indent == 6 && trimmed.EndsWith(':'))
            {
                dependency = trimmed[..^1].Trim('"');
            }
            else if (inDependsOn && indent == 8 && dependency is not null
                     && trimmed.StartsWith("condition:", StringComparison.Ordinal)
                     && trimmed["condition:".Length..].Trim().Trim('"') == Condition
                     && !found.Contains(dependency))
            {
                found.Add(dependency);
            }
        }
        return found;
    }
}
