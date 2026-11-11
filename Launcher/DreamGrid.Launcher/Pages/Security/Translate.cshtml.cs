using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Logs;
using DreamGrid.Launcher.Resident;
using DreamGrid.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages.Security;

/// <summary>
/// The page that translates one language the owner added (OwnerPolicy; <c>/security/languages/{code}</c>; 404 for a built-in, unknown
/// or not-added code, and where there is no guest side). One row per catalogue key, English beside a box, page texts and email texts
/// apart. Save keeps what was typed: each text must keep the English's fill-ins (<see cref="LanguageFile.Check"/>); one that doesn't is
/// shown with its problem and not saved, and the others are; an emptied box takes its text away. The file can be downloaded
/// (<see cref="LanguageFile.Write"/>) and uploaded back (<see cref="LanguageFile.Read"/>, at most <see cref="LanguageFile.MaxBytes"/>):
/// an upload replaces the texts it gives and keeps the others, and saves nothing when it has any problem. Audited as
/// <c>language-translated</c>, <c>language-uploaded</c> and <c>language-downloaded</c> with the target <c>code</c>, never the text.
/// </summary>
// The upload is one file of at most 1 MB; the Save form carries every text, so the whole request may be larger.
[RequestFormLimits(MultipartBodyLengthLimit = LanguageFile.MaxBytes + UploadSlack)]
[RequestSizeLimit(MaxRequestBytes)]
public sealed class TranslateModel(OwnerLanguages added, LanguageCatalog catalog, AuditLog audit, TimeProvider time) : PageModel
{
    /// <summary>Room for the multipart headers around the file and the antiforgery token.</summary>
    private const int UploadSlack = 64 * 1024;

    /// <summary>The largest request: every text at its longest would be more, but no translation comes near it.</summary>
    private const long MaxRequestBytes = 16L * 1024 * 1024;

    /// <summary>The prefixes of the Save form's fields: <c>page.&lt;key&gt;</c> and <c>email.&lt;key&gt;</c>.</summary>
    public const string PageField = "page.", EmailField = "email.";

    /// <summary>One text's row: its key, the English, the translation (or what was typed) and any problem.</summary>
    public sealed record Row(string Key, string English, string Text, string? Error);

    [TempData] public string? Notice { get; set; }

    public string Code { get; private set; } = "";

    public string Name { get; private set; } = "";

    public int Missing { get; private set; }

    public IReadOnlyList<Row> PageRows { get; private set; } = [];

    public IReadOnlyList<Row> EmailRows { get; private set; } = [];

    /// <summary>Why the uploaded file was not used, or empty.</summary>
    public IReadOnlyList<LanguageFileProblem> UploadProblems { get; private set; } = [];

    /// <summary>Set when a Save left rows unsaved.</summary>
    public string? Error { get; private set; }

    public IActionResult OnGet(string? code)
    {
        if (Find(code) is not { } language) return NotFound();
        Fill(language, null);
        return Page();
    }

    public IActionResult OnGetDownload(string? code)
    {
        if (Find(code) is not { } language) return NotFound();
        Write("language-downloaded", language.Code);
        // The code is one CodeFor gave (letters, digits and '-'), so it is safe in the header as it is.
        Response.Headers.ContentDisposition = $"attachment; filename=\"{language.Code}.json\"";
        return File(LanguageFile.Write(language, catalog), "application/json");
    }

    public async Task<IActionResult> OnPostSaveAsync(string? code, CancellationToken ct)
    {
        if (Find(code) is not { } language) return NotFound();
        var form = await Request.ReadFormAsync(ct);
        var page = new Dictionary<string, string>(language.Page, StringComparer.Ordinal);
        var email = new Dictionary<string, string>(language.Email, StringComparer.Ordinal);
        var problems = new Dictionary<string, (string Text, string Problem)>(StringComparer.Ordinal);

        foreach (var key in catalog.PageKeys())
            if (form.TryGetValue(PageField + key, out var typed))
                Apply(page, key, LanguageFile.Tidy(typed.ToString()), LanguageFile.Section.Page, null, problems);
        // An email's subject and body are checked together, each against the other half as it will be saved.
        var postedEmail = catalog.EmailKeys()
            .Where(k => form.ContainsKey(EmailField + k))
            .ToDictionary(k => k, k => LanguageFile.Tidy(form[EmailField + k].ToString()), StringComparer.Ordinal);
        foreach (var (key, text) in postedEmail)
        {
            var pair = LanguageFile.Pair(key);
            var other = pair is null ? null
                : postedEmail.TryGetValue(pair, out var posted) ? posted
                : email.GetValueOrDefault(pair);
            Apply(email, key, text, LanguageFile.Section.Email, other, problems);
        }

        var saved = language with { Page = page, Email = email, Saved = time.GetUtcNow() };
        added.Save(saved);
        Write("language-translated", language.Code);
        if (problems.Count > 0)
        {
            Fill(added.Get(language.Code) ?? saved, problems);
            Error = problems.Count == 1
                ? "One text was not saved: see the box marked below. The others were saved."
                : $"{problems.Count} texts were not saved: see the boxes marked below. The others were saved.";
            return Page();
        }
        Notice = "Saved.";
        return Redirect($"/security/languages/{Uri.EscapeDataString(language.Code)}");
    }

    public async Task<IActionResult> OnPostUploadAsync(string? code, IFormFile? file, CancellationToken ct)
    {
        if (Find(code) is not { } language) return NotFound();
        IReadOnlyList<LanguageFileProblem> problems;
        if (file is null || file.Length == 0)
            problems = [new("", "Choose a file to upload.")];
        else if (file.Length > LanguageFile.MaxBytes)
            problems = [new("", "The file is larger than 1 MB.")];
        else
        {
            await using var stream = file.OpenReadStream();
            var (read, found) = LanguageFile.Read(stream, language.Code, catalog, time);
            problems = found;
            if (read is not null && problems.Count == 0)
            {
                // The texts the file gives replace those saved; the rest are kept.
                var merged = language with
                {
                    Page = Merge(language.Page, read.Page),
                    Email = Merge(language.Email, read.Email),
                    Saved = read.Saved,
                };
                added.Save(merged);
                Write("language-uploaded", language.Code);
                var count = read.Page.Count + read.Email.Count;
                Notice = count == 1 ? "Uploaded 1 text." : $"Uploaded {count} texts.";
                return Redirect($"/security/languages/{Uri.EscapeDataString(language.Code)}");
            }
        }
        Fill(language, null);
        UploadProblems = problems;
        return Page();
    }

    private void Apply(Dictionary<string, string> texts, string key, string text, LanguageFile.Section section, string? other,
        Dictionary<string, (string, string)> problems)
    {
        if (text.Length == 0)
        {
            texts.Remove(key);
            return;
        }
        if (LanguageFile.Check(catalog, section, key, text, other) is { } problem)
        {
            problems[key] = (text, problem);
            return;
        }
        texts[key] = text;
    }

    private static IReadOnlyDictionary<string, string> Merge(IReadOnlyDictionary<string, string> kept, IReadOnlyDictionary<string, string> given)
    {
        var all = new Dictionary<string, string>(kept, StringComparer.Ordinal);
        foreach (var (key, text) in given)
            all[key] = text;
        return all;
    }

    /// <summary>The added language for the route's code, or null for one that is built in, unknown or not added.</summary>
    private OwnerLanguage? Find(string? code) => OwnerLanguages.CodeFor(code) is { } name ? added.Get(name) : null;

    private void Fill(OwnerLanguage language, IReadOnlyDictionary<string, (string Text, string Problem)>? problems)
    {
        Code = language.Code;
        Name = Languages.NativeName(language.Code);
        Missing = catalog.Missing(language.Code);
        Row RowOf(string key, string english, IReadOnlyDictionary<string, string> texts) =>
            problems is not null && problems.TryGetValue(key, out var p)
                ? new Row(key, english, p.Text, p.Problem)
                : new Row(key, english, texts.GetValueOrDefault(key) ?? "", null);
        PageRows = [.. catalog.PageKeys().Select(k => RowOf(k, catalog.English(k), language.Page))];
        EmailRows = [.. catalog.EmailKeys().Select(k => RowOf(k, catalog.EnglishEmail(k), language.Email))];
    }

    private void Write(string action, string target)
    {
        var (name, actorId, role) = AuditActor.Of(User);
        audit.Write(new AuditEntry(name, actorId, role, HttpContext.Connection.RemoteIpAddress?.ToString(), action, target, "ok"));
    }
}
