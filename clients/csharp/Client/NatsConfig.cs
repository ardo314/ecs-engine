using NATS.Client.Core;

namespace Client;

/// <summary>
/// Builds NATS connection options from explicit values, falling back to the
/// NATS_URL, NATS_USER and NATS_TOKEN environment variables.
/// </summary>
public static class NatsConfig
{
    public const string DefaultUrl = "nats://localhost:4222";

    public static string ResolveUrl(string? url = null) =>
        NullIfEmpty(url) ?? NullIfEmpty(Environment.GetEnvironmentVariable("NATS_URL")) ?? DefaultUrl;

    public static string? ResolveUser(string? user = null) =>
        NullIfEmpty(user) ?? NullIfEmpty(Environment.GetEnvironmentVariable("NATS_USER"));

    public static string? ResolveToken(string? token = null) =>
        NullIfEmpty(token) ?? NullIfEmpty(Environment.GetEnvironmentVariable("NATS_TOKEN"));

    /// <summary>
    /// Creates connection options for the given URL and credentials.
    /// </summary>
    public static NatsOpts CreateOpts(string? url = null, string? user = null, string? token = null)
    {
        var opts = new NatsOpts { Url = ResolveUrl(url) };

        var resolvedUser = ResolveUser(user);
        var resolvedToken = ResolveToken(token);
        if (resolvedUser is null && resolvedToken is null)
            return opts;

        // The token is sent as both password and auth token so it works against a
        // server configured for user/password auth as well as token auth.
        return opts with
        {
            AuthOpts = new NatsAuthOpts
            {
                Username = resolvedUser,
                Password = resolvedToken,
                Token = resolvedToken
            }
        };
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
