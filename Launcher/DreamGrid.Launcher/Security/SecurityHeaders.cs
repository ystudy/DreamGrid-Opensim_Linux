namespace DreamGrid.Launcher.Security;

/// <summary>
/// Browser security headers on every response: pages, API answers, static files and the plain-text replies of the
/// port-8001 contract. The policy has no 'unsafe-inline', so every script is a file under wwwroot/js and every style
/// lives in site.css (a scan test keeps the page sources that way). It is first in the pipeline so nothing is missed.
/// </summary>
public sealed class SecurityHeaders(RequestDelegate next)
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
        "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "same-origin";
        headers["X-Frame-Options"] = "DENY";
        return next(context);
    }
}
