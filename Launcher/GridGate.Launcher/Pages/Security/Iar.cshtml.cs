using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.Security;

/// <summary>
/// The owner's page for inventory backups (OwnerPolicy; <c>/security/iar</c>; 404 where there is no guest side): the grid's two
/// switches and every limit (spec 2026-11-11-iar-backups-design §8). Save goes through IGridController.UpdateSettings (no config
/// files change). Each changed field is audited as <c>iar-settings</c> with the target <c>"Field old -> new"</c>; a save that
/// changes nothing is not audited.
/// </summary>
public sealed class IarModel(IGridController grid, Func<LauncherSettings> settings, AuditLog audit) : PageModel
{
    [BindProperty] public bool DownloadsOn { get; set; }
    [BindProperty] public bool UploadsOn { get; set; }
    [BindProperty] public int MaxUploadMb { get; set; }
    [BindProperty] public int UnpackFactor { get; set; }
    [BindProperty] public int MaxUploadItems { get; set; }
    [BindProperty] public int MaxDownloadMb { get; set; }
    [BindProperty] public int DownloadsPerDay { get; set; }
    [BindProperty] public int UploadsPerDay { get; set; }
    [BindProperty] public int JobsAtOnce { get; set; }
    [BindProperty] public int DownloadKbPerSecond { get; set; }
    [BindProperty] public int LinkHours { get; set; }
    [BindProperty] public int MinFreeDiskGb { get; set; }

    [TempData] public string? Notice { get; set; }

    public string? Error { get; private set; }

    public void OnGet() => Fill(settings().Iar);

    public IActionResult OnPostSave()
    {
        var wanted = new IarSettings(DownloadsOn, UploadsOn, MaxUploadMb, UnpackFactor, MaxUploadItems, MaxDownloadMb,
            DownloadsPerDay, UploadsPerDay, JobsAtOnce, DownloadKbPerSecond, LinkHours, MinFreeDiskGb);
        var check = SettingsValidator.Iar(wanted);
        if (!check.Ok)
        {
            Error = string.Join(" ", check.Errors.Values);
            return Page();
        }

        var changes = new List<string>();
        grid.UpdateSettings(s =>
        {
            changes.Clear(); // the update may run again against a newer copy
            changes.AddRange(Changes(s.Iar, wanted));
            return changes.Count == 0 ? s : s with { Iar = wanted };
        }, writeConfig: false);

        var (name, id, role) = AuditActor.Of(User);
        foreach (var target in changes)
            audit.Write(new AuditEntry(name, id, role, HttpContext.Connection.RemoteIpAddress?.ToString(), "iar-settings", target, "ok"));
        Notice = changes.Count == 0 ? "Nothing changed." : "Saved. The new limits apply to the next job.";
        return Redirect("/security/iar");
    }

    /// <summary>One <c>"Field old -> new"</c> per field that differs; switches read as on and off.</summary>
    public static IEnumerable<string> Changes(IarSettings before, IarSettings after)
    {
        if (before.DownloadsOn != after.DownloadsOn) yield return $"DownloadsOn {OnOff(before.DownloadsOn)} -> {OnOff(after.DownloadsOn)}";
        if (before.UploadsOn != after.UploadsOn) yield return $"UploadsOn {OnOff(before.UploadsOn)} -> {OnOff(after.UploadsOn)}";
        foreach (var (field, was, now) in new[]
        {
            (nameof(IarSettings.MaxUploadMb), before.MaxUploadMb, after.MaxUploadMb),
            (nameof(IarSettings.UnpackFactor), before.UnpackFactor, after.UnpackFactor),
            (nameof(IarSettings.MaxUploadItems), before.MaxUploadItems, after.MaxUploadItems),
            (nameof(IarSettings.MaxDownloadMb), before.MaxDownloadMb, after.MaxDownloadMb),
            (nameof(IarSettings.DownloadsPerDay), before.DownloadsPerDay, after.DownloadsPerDay),
            (nameof(IarSettings.UploadsPerDay), before.UploadsPerDay, after.UploadsPerDay),
            (nameof(IarSettings.JobsAtOnce), before.JobsAtOnce, after.JobsAtOnce),
            (nameof(IarSettings.DownloadKbPerSecond), before.DownloadKbPerSecond, after.DownloadKbPerSecond),
            (nameof(IarSettings.LinkHours), before.LinkHours, after.LinkHours),
            (nameof(IarSettings.MinFreeDiskGb), before.MinFreeDiskGb, after.MinFreeDiskGb),
        })
            if (was != now) yield return $"{field} {was} -> {now}";

        static string OnOff(bool on) => on ? "on" : "off";
    }

    private void Fill(IarSettings i) =>
        (DownloadsOn, UploadsOn, MaxUploadMb, UnpackFactor, MaxUploadItems, MaxDownloadMb, DownloadsPerDay, UploadsPerDay, JobsAtOnce,
            DownloadKbPerSecond, LinkHours, MinFreeDiskGb) =
        (i.DownloadsOn, i.UploadsOn, i.MaxUploadMb, i.UnpackFactor, i.MaxUploadItems, i.MaxDownloadMb, i.DownloadsPerDay, i.UploadsPerDay,
            i.JobsAtOnce, i.DownloadKbPerSecond, i.LinkHours, i.MinFreeDiskGb);
}
