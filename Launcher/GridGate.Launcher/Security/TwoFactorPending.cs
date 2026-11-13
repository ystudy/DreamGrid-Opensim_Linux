using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Security;

namespace GridGate.Launcher.Security;

/// <summary>Who passed the password step and is still to give their two-factor code.</summary>
/// <param name="Expires">Unix seconds; checked here from the protected payload, not left to the cookie's own expiry.</param>
public sealed record TwoFactorPendingSignIn(Guid Principal, string First, string Last, int Level, StaffRole Role, string? ReturnUrl, long Expires) : IPendingSignIn
{
    public string Name => $"{First} {Last}";

    public RobustAccount Account => new(Principal, First, Last, Level, "");
}

/// <summary>
/// The <c>gg-2fa</c> cookie between the password and the code steps: data-protected (purpose <c>GridGate.TwoFactorPending.v1</c>),
/// HttpOnly, SameSite=Strict, only sent to <c>/login</c> and below, and good for <see cref="Lifetime"/> by the time inside it.
/// It carries the account (principal, name, level), the role and the return address, so the code step needs no second Robust
/// lookup. It is no sign-in: nothing but <c>/login/code</c> reads it.
/// </summary>
public static class TwoFactorPending
{
    public const string CookieName = "gg-2fa";
    public const string CookiePath = "/login";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private static readonly PendingSignInCookie<TwoFactorPendingSignIn> Cookie =
        new(CookieName, CookiePath, "GridGate.TwoFactorPending.v1", Lifetime, p => p.Role != StaffRole.None);

    public static void Issue(HttpContext ctx, RobustAccount account, StaffRole role, string? returnUrl) =>
        Cookie.Issue(ctx, expires => new TwoFactorPendingSignIn(account.PrincipalId, account.FirstName, account.LastName, account.UserLevel,
            role, returnUrl, expires));

    /// <summary>The pending sign-in, or null when there is none, it can't be read, or its five minutes are over.</summary>
    public static TwoFactorPendingSignIn? Read(HttpContext ctx) => Cookie.Read(ctx);

    public static void Delete(HttpContext ctx) => Cookie.Delete(ctx);
}
