using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Iar;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Guests;
using GridGate.Launcher.Iar;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace GridGate.Launcher.Pages.Guest;

/// <summary>
/// The resident's inventory backups (<c>/guest/inventory</c>; GuestPolicy by the /Guest folder; only when they may download or
/// upload, see <see cref="IarPagesOnlyAttribute"/>; spec 2026-11-11-iar-backups-design §5): back up and download, with what was
/// left out and why, and bring in an IAR (posted to <c>/guest/inventory/upload</c>, streamed). Each part shows only when it's
/// allowed. While a job runs the page reloads itself.
/// </summary>
[IarPagesOnly]
public sealed class InventoryModel(IarJobs jobs, IarFiles files, IarRights rights, TakedownStore takedowns, Func<LauncherSettings> settings,
    IStringLocalizer<ResidentText> t) : PageModel
{
    public bool DownloadShown { get; private set; }
    public bool UploadShown { get; private set; }
    public bool NotAvailable { get; private set; }
    public IarJobSnapshot? Job { get; private set; }
    public IarReadyFile? Ready { get; private set; }
    public IarSettings Limits { get; private set; } = new();
    public string? Error { get; private set; }
    public string GridName => settings().GridName;

    public void OnGet(string? refused, string? problem)
    {
        Fill();
        if (refused is not null && Enum.TryParse<IarRefusal>(refused, out var r)) Error = Words(r, IarJobKind.Load);
        else if (problem is not null && Enum.TryParse<IarUploadProblem>(problem, out var p)) Error = Words(p);
    }

    public IActionResult OnPostBackup()
    {
        var guest = GuestSignIn.Read(User)!.Value;
        var (first, last) = Names();
        if (jobs.TryStartDownload(guest.Principal, first, last, guest.Stamp, HttpContext.Connection.RemoteIpAddress?.ToString()) is { } refusal)
        {
            Fill();
            Error = Words(refusal, IarJobKind.Save);
            return Page();
        }
        return Redirect("/guest/inventory");
    }

    private (string First, string Last) Names()
    {
        var parts = (User.Identity?.Name ?? "").Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return (parts.ElementAtOrDefault(0) ?? "", parts.ElementAtOrDefault(1) ?? "");
    }

    private void Fill()
    {
        var guest = GuestSignIn.Read(User)!.Value.Principal;
        var s = settings();
        var mine = rights.For(guest);
        DownloadShown = IarPagesOnlyAttribute.MayDownload(s, mine);
        UploadShown = IarPagesOnlyAttribute.MayUpload(s, mine);
        NotAvailable = takedowns.NamedInOpenCase(guest);
        Limits = IarRights.Effective(s.Iar, mine);
        Job = jobs.For(guest);
        Ready = files.Find(guest);
    }

    public string Words(IarRefusal r, IarJobKind kind) => r == IarRefusal.DailyLimit
        ? t["Iar.Refused.DailyLimit", kind == IarJobKind.Save ? Limits.DownloadsPerDay : Limits.UploadsPerDay]
        : t["Iar.Refused." + r];

    public string Words(IarUploadProblem p) => p switch
    {
        IarUploadProblem.TooBig => t["Iar.Upload.TooBig", Limits.MaxUploadMb],
        IarUploadProblem.NotAnIar => t["Iar.Upload.NotAnIar"],
        IarUploadProblem.NoSpace => t["Iar.Refused.LowDisk"],
        _ => t["Iar.Upload.Stopped"],
    };

    public string Reason(LeftOutReason r) => t["Iar.LeftOut." + r];
}
