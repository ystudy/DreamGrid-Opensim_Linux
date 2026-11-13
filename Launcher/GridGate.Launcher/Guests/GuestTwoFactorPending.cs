using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Security;

namespace GridGate.Launcher.Guests;

/// <summary>A resident who passed the password step at the guest door and is still to give their two-factor code.</summary>
/// <param name="Expires">Unix seconds; checked here from the protected payload, not left to the cookie's own expiry.</param>
public sealed record GuestTwoFactorPendingSignIn(Guid Principal, string First, string Last, int Level, long Expires) : IPendingSignIn
{
    public string Name => $"{First} {Last}";

    public RobustAccount Account => new(Principal, First, Last, Level, "");
}

/// <summary>
/// The <c>gg-guest-2fa</c> cookie between the guest door's password and code steps: data-protected (purpose
/// <c>GridGate.GuestTwoFactorPending.v1</c>), HttpOnly, SameSite=Strict, only sent to <c>/guest</c> and below, good for
/// <see cref="Lifetime"/> by the time inside it. It is no sign-in: nothing but <c>/guest/sign-in/code</c> reads it.
/// </summary>
public static class GuestTwoFactorPending
{
    public const string CookieName = "gg-guest-2fa";
    public const string CookiePath = "/guest";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private static readonly PendingSignInCookie<GuestTwoFactorPendingSignIn> Cookie =
        new(CookieName, CookiePath, "GridGate.GuestTwoFactorPending.v1", Lifetime);

    public static void Issue(HttpContext ctx, RobustAccount account) =>
        Cookie.Issue(ctx, expires => new GuestTwoFactorPendingSignIn(account.PrincipalId, account.FirstName, account.LastName,
            account.UserLevel, expires));

    /// <summary>The pending sign-in, or null when there is none, it can't be read, or its five minutes are over.</summary>
    public static GuestTwoFactorPendingSignIn? Read(HttpContext ctx) => Cookie.Read(ctx);

    public static void Delete(HttpContext ctx) => Cookie.Delete(ctx);
}
