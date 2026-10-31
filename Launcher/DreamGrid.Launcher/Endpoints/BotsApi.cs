using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Endpoints;

/// <summary>The bot-protection endpoint the sign-up form's script calls.</summary>
public static class BotsApi
{
    public static void Map(WebApplication app) =>
        // Anyone may ask (a visitor is not signed in); where there is no guest side the endpoint does not exist.
        app.MapGet("/api/bots/challenge", (HttpContext ctx, ProofOfWork work, Func<LauncherSettings> settings) =>
        {
            var s = settings();
            if (!AdminRule.HasStaffPages(s)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-store";
            var c = work.Create(s.Bots.Difficulty);
            // ALTCHA's names: the challenge's number limit is "maxnumber", the challenge is "challenge".
            return Results.Json(new Dictionary<string, object>
            {
                ["algorithm"] = c.Algorithm,
                ["challenge"] = c.ChallengeHex,
                ["maxnumber"] = c.MaxNumber,
                ["salt"] = c.Salt,
                ["signature"] = c.Signature,
            });
        }).AllowAnonymous();
}
