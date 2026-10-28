namespace DreamGrid.Launcher.Security;

/// <summary>
/// Endpoint metadata for an API outside <c>/api</c>: a caller that isn't signed in gets 401 (or 403), never a redirect
/// to the login page.
/// </summary>
public sealed class ApiEndpoint
{
    public static ApiEndpoint Marker { get; } = new();

    private ApiEndpoint() { }

    /// <summary>True for a request under <c>/api</c> or to an endpoint marked with <see cref="Marker"/>.</summary>
    public static bool Is(HttpContext ctx) =>
        ctx.Request.Path.StartsWithSegments("/api") || ctx.GetEndpoint()?.Metadata.GetMetadata<ApiEndpoint>() is not null;
}
