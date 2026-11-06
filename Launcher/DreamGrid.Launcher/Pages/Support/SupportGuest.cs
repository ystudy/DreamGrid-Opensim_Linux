using DreamGrid.Launcher.Core.OpenSim;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Authentication;

namespace DreamGrid.Launcher.Pages.Support;

/// <summary>The signed-in resident on a support page, if any: these pages are open to anyone, so the guest cookie is read here.</summary>
public static class SupportGuest
{
    public static async Task<RobustAccount?> AccountAsync(HttpContext ctx, IRobustClient robust, CancellationToken ct)
    {
        var result = await ctx.AuthenticateAsync(AuthSchemes.Guest);
        if (!result.Succeeded || GuestSignIn.Read(result.Principal!) is not { } guest) return null;
        try
        {
            return await robust.FindAccountByIdAsync(guest.Principal, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null; // the grid service is down: treat them as not signed in
        }
    }
}
