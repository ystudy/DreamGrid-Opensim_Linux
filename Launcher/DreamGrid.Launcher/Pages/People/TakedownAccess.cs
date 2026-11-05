using System.Globalization;
using System.Security.Claims;
using DreamGrid.Launcher.Core.Security;
using DreamGrid.Launcher.Security;

namespace DreamGrid.Launcher.Pages.People;

/// <summary>Who may use the inventory and takedown pages (the owner and admins), and how they show dates.</summary>
public static class TakedownAccess
{
    public static bool MayUse(ClaimsPrincipal user) => StaffSignIn.RoleOf(user) is StaffRole.Owner or StaffRole.Admin;

    public static string Day(DateOnly d) => d.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    public static string Day(int unixSeconds) =>
        unixSeconds <= 0 ? "" : DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    public static string Kind(PersonOnCase k) => k == PersonOnCase.Named ? "named" : "holds a copy";
}
