namespace ConnectionPool.Dataverse.Tests;

/// <summary>
/// Single source of truth for turning this repo's <c>DVPOOL_IT_*</c> environment variables into
/// one or more Dataverse connection strings for the opt-in live tests, so adding a third (or
/// tenth) real application user never again means hand-rolling a new <c>_C</c>/<c>_D</c> suffix
/// and a matching <c>ConnectionStringC</c>/<c>ConnectionStringD</c> property in every test class
/// that wants it.
/// </summary>
/// <remarks>
/// <para>
/// Resolution order, most to least specific:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <c>DVPOOL_IT_CONNECTION_STRINGS</c> - a comma-separated list of complete, already-built
/// connection strings. The full escape hatch: use this if any identity needs something the other
/// forms below can't express (a different tenant, a different auth type, etc.).
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>DVPOOL_IT_CLIENT_IDS</c> + <c>DVPOOL_IT_CLIENT_SECRETS</c> - comma-separated, paired by
/// position (the Nth secret belongs to the Nth client ID). <c>DVPOOL_IT_URL</c> and
/// <c>DVPOOL_IT_TENANT_ID</c> stay singular and are shared by every identity in the list - in
/// practice these are different application users registered in the one same tenant/environment
/// under test, not different tenants, so there is no need to repeat the URL/tenant per identity.
/// </description>
/// </item>
/// <item>
/// <description>
/// Legacy fallback, kept for existing <c>.env</c> files and CI secrets: <c>DVPOOL_IT_CONNECTION_STRING</c>
/// (or <c>DVPOOL_IT_URL</c>/<c>DVPOOL_IT_TENANT_ID</c>/<c>DVPOOL_IT_CLIENT_ID</c>/<c>DVPOOL_IT_CLIENT_SECRET</c>)
/// as identity 0, and the same with a <c>_B</c> suffix as identity 1, if present.
/// </description>
/// </item>
/// </list>
/// <para>
/// Returns an empty list (never throws) when nothing is configured, so every call site's existing
/// <c>Skip.If(string.IsNullOrEmpty(...))</c> pattern keeps working unchanged.
/// </para>
/// </remarks>
internal static class LiveDataverseCredentials
{
    /// <summary>
    /// Returns the connection string for application user <paramref name="index"/> (0-based), or
    /// <see langword="null"/> if fewer than <paramref name="index"/> + 1 identities are configured.
    /// </summary>
    public static string? GetConnectionString(int index)
    {
        var connectionStrings = GetConnectionStrings();
        return index >= 0 && index < connectionStrings.Count ? connectionStrings[index] : null;
    }

    /// <summary>
    /// Returns every configured application user's connection string, in order. See the type-level
    /// remarks for the full resolution order across the three supported environment-variable shapes.
    /// </summary>
    public static IReadOnlyList<string> GetConnectionStrings()
    {
        var explicitList = SplitList(Environment.GetEnvironmentVariable("DVPOOL_IT_CONNECTION_STRINGS"));
        if (explicitList.Count > 0)
        {
            return explicitList;
        }

        var clientIds = SplitList(Environment.GetEnvironmentVariable("DVPOOL_IT_CLIENT_IDS"));
        if (clientIds.Count > 0)
        {
            var clientSecrets = SplitList(Environment.GetEnvironmentVariable("DVPOOL_IT_CLIENT_SECRETS"));
            if (clientSecrets.Count != clientIds.Count)
            {
                throw new InvalidOperationException(
                    $"DVPOOL_IT_CLIENT_IDS has {clientIds.Count} entries but DVPOOL_IT_CLIENT_SECRETS has " +
                    $"{clientSecrets.Count} - they must list the same number of application users, in the " +
                    "same order.");
            }

            var url = Environment.GetEnvironmentVariable("DVPOOL_IT_URL");
            var tenantId = Environment.GetEnvironmentVariable("DVPOOL_IT_TENANT_ID");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(tenantId))
            {
                throw new InvalidOperationException(
                    "DVPOOL_IT_CLIENT_IDS/DVPOOL_IT_CLIENT_SECRETS are set but DVPOOL_IT_URL and/or " +
                    "DVPOOL_IT_TENANT_ID are not - every application user listed shares one tenant/environment, " +
                    "so both must be set once.");
            }

            return clientIds
                .Zip(clientSecrets, (clientId, clientSecret) =>
                    $"AuthType=ClientSecret;Url={url};TenantId={tenantId};ClientId={clientId};ClientSecret={clientSecret};")
                .ToList();
        }

        // Legacy fallback: identity 0 from the unsuffixed vars, identity 1 from the _B-suffixed vars.
        var legacy = new List<string>();
        foreach (var suffix in new[] { "", "_B" })
        {
            var legacyConnectionString = BuildLegacyConnectionString(suffix);
            if (legacyConnectionString is null)
            {
                break; // Suffixes are only ever used contiguously (no _B without identity 0, etc.).
            }

            legacy.Add(legacyConnectionString);
        }

        return legacy;
    }

    private static string? BuildLegacyConnectionString(string suffix)
    {
        var connectionString = Environment.GetEnvironmentVariable($"DVPOOL_IT_CONNECTION_STRING{suffix}");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        var url = Environment.GetEnvironmentVariable("DVPOOL_IT_URL");
        var tenantId = Environment.GetEnvironmentVariable($"DVPOOL_IT_TENANT_ID{suffix}");
        var clientId = Environment.GetEnvironmentVariable($"DVPOOL_IT_CLIENT_ID{suffix}");
        var clientSecret = Environment.GetEnvironmentVariable($"DVPOOL_IT_CLIENT_SECRET{suffix}");

        return new[] { url, tenantId, clientId, clientSecret }.All(value => !string.IsNullOrWhiteSpace(value))
            ? $"AuthType=ClientSecret;Url={url};TenantId={tenantId};ClientId={clientId};ClientSecret={clientSecret};"
            : null;
    }

    private static IReadOnlyList<string> SplitList(string? rawValue) =>
        string.IsNullOrWhiteSpace(rawValue)
            ? Array.Empty<string>()
            : rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
