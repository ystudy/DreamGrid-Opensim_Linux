using DreamGrid.Launcher.Core.Grid;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Core.Settings;
using DreamGrid.Launcher.Guests;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Security;

/// <summary>
/// The owner's page for the bot protection (OwnerPolicy; <c>/security/bots</c>; 404 where there is no guest side): one switch for
/// each layer (honeypot, per-address limit with its number, proof-of-work with its difficulty, image CAPTCHA) and for admin approval,
/// and a "Try it" form that runs the layers that are on so the owner can see them work in their own browser.
/// <list type="bullet">
/// <item>Save goes through IGridController.UpdateSettings (no config files change). Each layer turned on or off is audited as
/// <c>bot-layer-on</c> or <c>bot-layer-off</c> with the layer id as the target (<see cref="BotLayers"/>); a change to the limit, the
/// difficulty or admin approval as <c>bot-settings</c>. A save that changes nothing is not audited.</item>
/// <item>Try it posts to <see cref="OnPostTry"/>, which asks <see cref="BotGuard"/> as the form "try" and says whether the post passed
/// or which layer stopped it. It saves nothing, but it counts against the rate limit like any other post.</item>
/// </list>
/// </summary>
public sealed class BotsModel(IGridController grid, Func<LauncherSettings> settings, BotGuard guard, AuditLog audit) : PageModel
{
    [BindProperty] public bool Honeypot { get; set; }
    [BindProperty] public bool RateLimit { get; set; }
    [BindProperty] public int PerHourPerIp { get; set; }
    [BindProperty] public bool ProofOfWork { get; set; }
    [BindProperty] public int Difficulty { get; set; }
    [BindProperty] public bool ImageCaptcha { get; set; }
    [BindProperty] public bool AdminApproval { get; set; }

    [TempData] public string? Notice { get; set; }

    public string? Error { get; private set; }

    /// <summary>What the Try it form reported, or null before it was used.</summary>
    public string? TryResult { get; private set; }

    /// <summary>True when every protection layer is off.</summary>
    public bool AllOff => !Honeypot && !RateLimit && !ProofOfWork && !ImageCaptcha;

    public void OnGet() => Fill();

    public IActionResult OnPostSave()
    {
        var wanted = new BotSettings(Honeypot, RateLimit, PerHourPerIp, ProofOfWork, Difficulty, ImageCaptcha, AdminApproval);
        var check = SettingsValidator.Bots(wanted);
        if (!check.Ok)
        {
            Error = string.Join(" ", check.Errors.Values);
            return Page();
        }

        var changes = new List<(string Action, string Target)>();
        grid.UpdateSettings(s =>
        {
            changes.Clear(); // the update may run again against a newer copy
            var before = s.Bots;
            Layer(BotLayers.Honeypot, before.Honeypot, wanted.Honeypot);
            Layer(BotLayers.RateLimit, before.RateLimit, wanted.RateLimit);
            Layer(BotLayers.ProofOfWork, before.ProofOfWork, wanted.ProofOfWork);
            Layer(BotLayers.ImageCaptcha, before.ImageCaptcha, wanted.ImageCaptcha);
            if (before.PerHourPerIp != wanted.PerHourPerIp) changes.Add(("bot-settings", $"rate-limit {wanted.PerHourPerIp} per hour"));
            if (before.Difficulty != wanted.Difficulty) changes.Add(("bot-settings", $"difficulty {wanted.Difficulty}"));
            if (before.AdminApproval != wanted.AdminApproval) changes.Add(("bot-settings", "admin-approval " + (wanted.AdminApproval ? "on" : "off")));
            return changes.Count == 0 ? s : s with { Bots = wanted };
        }, writeConfig: false);

        foreach (var (action, target) in changes)
            Write(action, target);
        Notice = changes.Count == 0 ? "Nothing changed." : "Saved. The protection is live now.";
        return Redirect("/security/bots");

        void Layer(string id, bool was, bool now)
        {
            if (was != now) changes.Add((now ? "bot-layer-on" : "bot-layer-off", id));
        }
    }

    public IActionResult OnPostTry()
    {
        Fill();
        var result = guard.Check(HttpContext, "try");
        TryResult = result.Passed ? "Passed" : "Stopped by " + BotLayers.Title(result.Layer!);
        return Page();
    }

    private void Fill()
    {
        var b = settings().Bots;
        (Honeypot, RateLimit, PerHourPerIp, ProofOfWork, Difficulty, ImageCaptcha, AdminApproval) =
            (b.Honeypot, b.RateLimit, b.PerHourPerIp, b.ProofOfWork, b.Difficulty, b.ImageCaptcha, b.AdminApproval);
    }

    private void Write(string action, string target)
    {
        var (name, id, role) = AuditActor.Of(User);
        audit.Write(new AuditEntry(name, id, role, HttpContext.Connection.RemoteIpAddress?.ToString(), action, target, "ok"));
    }
}
