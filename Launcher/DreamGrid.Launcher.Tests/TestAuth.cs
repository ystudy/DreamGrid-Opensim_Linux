using System.Security.Claims;
using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DreamGrid.Launcher.Tests;

/// <summary>Test-only sign-in helpers that go through the real "dg" cookie scheme.</summary>
internal static class TestAuth
{
    public static readonly Guid OwnerId = Guid.Parse("0e000000-0000-0000-0000-00000000000e");
    public static readonly Guid AdminId = Guid.Parse("0a000000-0000-0000-0000-00000000000a");
    public static readonly Guid SupervisorId = Guid.Parse("05000000-0000-0000-0000-000000000005");

    /// <summary>The account each role signs in as (level 200 for the owner and admin, 100 for the supervisor).</summary>
    public static RobustAccount Account(StaffRole role) => role switch
    {
        StaffRole.Owner => new RobustAccount(OwnerId, "Test", "Owner", 200, ""),
        StaffRole.Admin => new RobustAccount(AdminId, "Test", "Admin", 200, ""),
        StaffRole.Supervisor => new RobustAccount(SupervisorId, "Test", "Supervisor", 100, ""),
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    /// <summary>Adds the three test accounts to <paramref name="robust"/>, each with password "pass-word1".</summary>
    public static FakeRobustAccounts AddStaff(FakeRobustAccounts robust)
    {
        foreach (var role in new[] { StaffRole.Owner, StaffRole.Admin, StaffRole.Supervisor })
        {
            var a = Account(role);
            robust.Add(a.FirstName, a.LastName, a.UserLevel, "pass-word1", a.PrincipalId);
        }
        return robust;
    }

    /// <summary>
    /// For <see cref="LauncherFactory.Before"/>: GET /__test/signin?role=owner|admin|supervisor (default admin) signs the
    /// client in through <see cref="StaffSignIn"/>, with a real session; GET /__test/signin-old mints an old-style cookie
    /// (name and admin role only, no session); a path ending in /__test/challenge issues the scheme's challenge.
    /// </summary>
    public static void Apply(IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path == "/__test/signin")
        {
            var role = ctx.Request.Query["role"].ToString() switch
            {
                "" or "admin" => StaffRole.Admin,
                "owner" => StaffRole.Owner,
                "supervisor" => StaffRole.Supervisor,
                var other => throw new ArgumentException($"Unknown test role {other}."),
            };
            await StaffSignIn.SignInAsync(ctx, Account(role), role);
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        else if (ctx.Request.Path == "/__test/signin-old")
        {
            var id = new ClaimsIdentity([new Claim(ClaimTypes.Name, "Test Admin"), new Claim(ClaimTypes.Role, "admin")], "dg");
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
