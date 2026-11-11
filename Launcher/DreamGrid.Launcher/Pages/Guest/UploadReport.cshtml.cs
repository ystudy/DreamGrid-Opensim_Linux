using System.Globalization;
using DreamGrid.Launcher.Core.ModelImport;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.ModelImport;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Guest;

/// <summary>One finding as the report shows it.</summary>
public sealed record ReportLine(Finding Finding, FindingLine Text);

/// <summary>
/// The check report for one of the guest's uploads (GuestPolicy by the /Guest folder convention, <see cref="UploadPagesOnlyAttribute"/>),
/// with Check again (a new scale, name or next-owner permissions) and Make the item. Another guest's job, or one that is gone, is 404.
/// Make the item with a name, scale or permissions other than the ones checked checks again first, so what the guest read is what
/// they get. The done page is shown here too.
/// </summary>
[UploadPagesOnly]
public sealed class UploadReportModel(ModelUploads uploads) : PageModel
{
    public const int MinScale = 1;
    public const int MaxScale = 1000;
    public const string NothingCharged = "That didn't work, nothing was charged";
    public const string CheckedAgain = "We checked it again with your changes. Look it over, then make the item.";

    [FromRoute] public string Job { get; set; } = "";

    [BindProperty] public string? ItemName { get; set; }
    [BindProperty] public int? ScalePercent { get; set; }
    [BindProperty] public bool Copy { get; set; }
    [BindProperty] public bool Modify { get; set; }
    [BindProperty] public bool Transfer { get; set; }

    [TempData] public string? Notice { get; set; }

    public UploadJob? Current { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Make the item went wrong after the checks; the job is gone.</summary>
    public bool Failed { get; private set; }
    /// <summary>Set once the item is made: the done page.</summary>
    public string? DoneItem { get; private set; }
    /// <summary>Another request already marked this job finished.</summary>
    public bool AlreadyFinished { get; private set; }

    public ImportReport? Report { get; private set; }
    public IReadOnlyList<ReportLine> Safe { get; private set; } = [];
    public IReadOnlyList<ReportLine> Risky { get; private set; } = [];
    public IReadOnlyList<ReportLine> Blocking { get; private set; } = [];

    /// <summary>The item's name as the report speaks of it.</summary>
    public string Item => DoneItem ?? Current?.Options.ItemName ?? "";

    private Guid Guest => GuestSignIn.Read(User)!.Value.Principal;

    public IActionResult OnGet()
    {
        if (uploads.Read(Guest, Job) is { } job) return Show(job);
        if (uploads.Made(Guest, Job) is { } made) return Done(made.ItemName, made.Report);
        return NotFound();
    }

    public async Task<IActionResult> OnPostRescaleAsync(CancellationToken ct)
    {
        if (uploads.Read(Guest, Job) is not { } job) return NotFound();
        if (job.Finished) return Show(job);
        return Posted(job) is { } options ? await CheckAgainAsync(job, options, null, ct) : Show(job);
    }

    public async Task<IActionResult> OnPostMakeAsync(CancellationToken ct)
    {
        if (uploads.Read(Guest, Job) is { Finished: false } job)
        {
            if (Posted(job) is not { } options) return Show(job);
            if (options != job.Options) return await CheckAgainAsync(job, options, CheckedAgain, ct);
        }

        var who = new Uploader(Guest, User.Identity?.Name ?? "", HttpContext.Connection.RemoteIpAddress?.ToString());
        var result = await uploads.MakeAsync(who, Job, ct);
        switch (result.Status)
        {
            case MakeStatus.Made:
            case MakeStatus.AlreadyFinished when result.ItemName is not null:
                return Done(result.ItemName!, result.Report!);
            case MakeStatus.AlreadyFinished:
                AlreadyFinished = true;
                return Page();
            case MakeStatus.Refused:
            case MakeStatus.Blocked:
                if (uploads.Read(Guest, Job) is not { } again) return NotFound();
                Error = result.Message;
                return Show(again);
            case MakeStatus.Failed:
                Failed = true;
                return Page();
            default:
                return NotFound();
        }
    }

    /// <summary>The job's options with the form's name, scale and permissions, or null (with <see cref="Error"/>) for a bad scale.</summary>
    private ImportOptions? Posted(UploadJob job)
    {
        var scale = ScalePercent ?? job.Options.ScalePercent;
        if (scale is < MinScale or > MaxScale)
        {
            Error = string.Create(CultureInfo.InvariantCulture, $"Choose a size from {MinScale} to {MaxScale} percent.");
            return null;
        }
        var name = ModelUploads.CleanName(ItemName) is { Length: > 0 } n ? n : UploadModel.DefaultItemName(job.Options.FileName);
        return job.Options with { ItemName = name, ScalePercent = scale, NextOwner = new NextOwner(Copy, Modify, Transfer) };
    }

    private async Task<IActionResult> CheckAgainAsync(UploadJob job, ImportOptions options, string? notice, CancellationToken ct)
    {
        var result = await uploads.CheckAgainAsync(Guest, job.Id, options, ct);
        if (result.Status == CheckStatus.Checked)
        {
            Notice = notice;
            return Redirect("/guest/upload/" + result.JobId);
        }
        Error = result.Status == CheckStatus.Busy ? UploadModel.Busy : UploadModel.Unreadable;
        return uploads.Read(Guest, job.Id) is { } still ? Show(still) : Page();
    }

    private PageResult Show(UploadJob job)
    {
        Current = job;
        if (job.Finished) AlreadyFinished = true;
        // The form shows what was checked; a refused post shows it again as it was checked, not as it was typed.
        ItemName = job.Options.ItemName;
        ScalePercent = job.Options.ScalePercent;
        (Copy, Modify, Transfer) = (job.Options.NextOwner.Copy, job.Options.NextOwner.Modify, job.Options.NextOwner.Transfer);
        Lines(job.Report, job.Options.ItemName);
        return Page();
    }

    private PageResult Done(string itemName, ImportReport report)
    {
        DoneItem = itemName;
        Lines(report, itemName);
        return Page();
    }

    private void Lines(ImportReport report, string itemName)
    {
        Report = report;
        var lines = (report.Findings ?? []).Select(f => new ReportLine(f, FindingText.Of(f, itemName))).ToList();
        Safe = lines.Where(l => l.Finding.Kind == FindingKind.Safe).ToList();
        Risky = lines.Where(l => l.Finding.Kind == FindingKind.Risky).ToList();
        Blocking = lines.Where(l => l.Finding.Kind == FindingKind.Blocking).ToList();
    }

    /// <summary>The scale the size field offers: the largest that fits, or the one checked.</summary>
    public int OfferedScale(Finding tooBig) =>
        tooBig.Values is { } v && v.TryGetValue("fitPercent", out var s) && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var fit) ? fit
        : Report?.Summary?.LargestFitPercent ?? Current?.Options.ScalePercent ?? 100;

    /// <summary>"castle.glb was checked: 1 thing may look wrong, 2 things will be fixed for you."</summary>
    public string Heading()
    {
        static string Things(int n) => n == 1 ? "1 thing" : n.ToString(CultureInfo.InvariantCulture) + " things";
        var parts = new List<string>();
        if (Blocking.Count > 0) parts.Add($"{Things(Blocking.Count)} must be fixed before the item can be made");
        if (Risky.Count > 0) parts.Add($"{Things(Risky.Count)} may look wrong");
        if (Safe.Count > 0) parts.Add($"{Things(Safe.Count)} will be fixed for you");
        var file = Current?.Options.FileName ?? Item;
        return parts.Count == 0 ? $"{file} was checked: nothing needs changing." : $"{file} was checked: {string.Join(", ", parts)}.";
    }
}
