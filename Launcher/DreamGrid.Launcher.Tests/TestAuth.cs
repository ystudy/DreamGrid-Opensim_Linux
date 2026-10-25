using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DreamGrid.Launcher.Tests;

/// <summary>Test-only sign-in helpers that go through the real "dg" cookie scheme.</summary>
internal static class TestAuth
{
    /// <summary>
    /// For <see cref="LauncherFactory.Before"/>: GET /__test/signin signs the client in as an admin;
    /// a path ending in /__test/challenge issues the scheme's challenge.
    /// </summary>
    public static void Apply(IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path == "/__test/signin")
        {
            var id = new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner"), new Claim(ClaimTypes.Role, "admin")], "dg");
            await ctx.SignInAsync("dg", new ClaimsPrincipal(id));
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        else if (ctx.Request.Path.Value!.EndsWith("/__test/challenge", StringComparison.Ordinal))
        {
            await ctx.ChallengeAsync("dg");
        }
        else
        {
            await next(ctx);
        }
    });
}
