namespace ConnectionPool.Dataverse;

/// <summary>
/// Conservative classifier for connection-establishment failures that will not heal on their own
/// (docs/research/autoscaling.md §9.6 item 4): a revoked/expired client secret, an app registration
/// that no longer exists or is disabled, a disabled/removed Dataverse application user, or a
/// persistent 401. Probing such a member every breaker cooldown is noise, so
/// <see cref="DataverseUserPool"/> quarantines it instead of letting the circuit breaker retry on a
/// short loop.
///
/// <para>
/// <b>Unknown means transient.</b> Only well-known markers match, so a false positive (quarantining a
/// member that would have recovered) stays unlikely; a false negative just falls back to the normal
/// breaker behavior. Matching is on the whole exception chain (inner exceptions and aggregates),
/// because the SDK, MSAL and the pool each wrap the original error.
/// </para>
/// </summary>
public static class DataverseFailureClassifier
{
    // Entra ID (AADSTS) errors that cannot succeed without a human changing configuration.
    private static readonly (string Marker, string Reason)[] MessageMarkers =
    {
        ("AADSTS7000215", "invalid client secret"),
        ("AADSTS7000222", "client secret expired"),
        ("AADSTS700016", "application not found in tenant"),
        ("AADSTS7000112", "application disabled"),
        ("AADSTS7000218", "client credential missing or invalid"),
        ("AADSTS700027", "client assertion/certificate invalid"),
        ("AADSTS90002", "tenant not found"),
        ("invalid_client", "invalid client credentials"),
        ("0x80072560", "application user not a member of the organization"),
        ("not a member of the organization", "application user not a member of the organization"),
        ("the user is disabled", "application user disabled"),
    };

    /// <summary>
    /// True if <paramref name="exception"/> (or anything in its chain) is a known non-self-healing
    /// connection failure; <paramref name="reason"/> is a short human-readable description.
    /// </summary>
    public static bool IsPermanent(Exception? exception, out string reason)
    {
        reason = string.Empty;
        if (exception is null)
        {
            return false;
        }

        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Exception>();
        pending.Push(exception);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            if (current is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
            {
                reason = "HTTP 401 Unauthorized";
                return true;
            }

            var message = current.Message;
            foreach (var (marker, markerReason) in MessageMarkers)
            {
                if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    reason = markerReason;
                    return true;
                }
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }

            if (current.InnerException is { } next)
            {
                pending.Push(next);
            }
        }

        return false;
    }
}
