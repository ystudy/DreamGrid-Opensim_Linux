using System.Globalization;
using System.Net;
using System.Resources;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Email;

/// <summary>One email's wording as the owner saved it: a subject and a Markdown body with fill-in words such as <c>{Link}</c>.</summary>
public sealed record EmailTemplateText(string Subject, string Body, DateTimeOffset Changed);

/// <summary>An email ready to send: a one-line subject, the plain-text body and the HTML body made from it.</summary>
public sealed record RenderedEmail(string Subject, string Text, string Html);

/// <summary>
/// The emails the grid sends, each with default wording that works as it is, the fill-in words it understands and the ones it
/// can't do without. The 17 emails to residents have their subject and body in <c>Resources/EmailText.resx</c> and its language
/// files, so each goes out in the resident's language; the 4 to staff (<see cref="IsStaffEmail"/>) are English only and live here.
/// The owner's own wording is kept in <see cref="EmailSettings"/>, by language, only where it differs from the default in that
/// language, and never stands in for another language. The body is Markdown with raw HTML off (<see cref="LegalMarkdown"/>); fill-in values are placed after the HTML is
/// made, HTML-escaped, so a value can neither add markup nor be read as Markdown.
/// </summary>
public static class EmailTemplates
{
    public const string Test = "test";
    public const string ConfirmEmail = "confirm-email";
    public const string ResetPassword = "reset-password";
    public const string AccountApproved = "account-approved";
    public const string PasswordChanged = "password-changed";
    public const string AlreadyHaveAccount = "already-have-account";
    public const string ConfirmNewEmail = "confirm-new-email";
    public const string EmailChangeNotice = "email-change-notice";
    public const string SignUpDenied = "signup-denied";
    public const string SignUpsWaiting = "signups-waiting";
    public const string NameChanged = "name-changed";
    public const string EmailChangedByStaff = "email-changed-by-staff";
    public const string EmailSetByStaff = "email-set-by-staff";
    public const string StaffResetPassword = "staff-reset-password";
    public const string TakedownRemoved = "takedown-removed";
    public const string TakedownRestored = "takedown-restored";
    public const string TicketConfirm = "ticket-confirm";
    public const string TicketAccess = "ticket-access";
    public const string TicketStaffReply = "ticket-staff-reply";
    public const string TicketNew = "ticket-new";
    public const string TicketReplyGuest = "ticket-reply-guest";

    /// <summary>The language the wording page edits; the owner's wording in other languages arrives with plan 16b.</summary>
    public const string DefaultLanguage = LegalPages.DefaultLanguage;

    private static readonly ResourceManager Text = new("DreamGrid.Launcher.Core.Resources.EmailText", typeof(EmailText).Assembly);

    public static IReadOnlyList<string> All { get; } = [Test, ConfirmEmail, ResetPassword, AccountApproved, PasswordChanged, AlreadyHaveAccount,
        ConfirmNewEmail, EmailChangeNotice, SignUpDenied, SignUpsWaiting, NameChanged, EmailChangedByStaff, EmailSetByStaff, StaffResetPassword,
        TakedownRemoved, TakedownRestored, TicketConfirm, TicketAccess, TicketStaffReply, TicketNew, TicketReplyGuest];

    private static readonly DateTimeOffset Shipped = DateTimeOffset.UnixEpoch;

    /// <summary>Subject and Body are empty for an email to a resident: those are read from <c>EmailText</c> in the resident's language.</summary>
    private sealed record Template(string Title, string[] Words, string[] Required, string Subject, string Body);

    private static readonly Dictionary<string, Template> Templates = new()
    {
        [Test] = new("Test email", ["{GridName}", "{AvatarName}"], [],
            "Test email from {GridName}",
            "Hello {AvatarName},\n\nThis is a test email from {GridName}. If you can read it, the grid can send email."),
        [ConfirmEmail] = new("Confirm your email", ["{GridName}", "{AvatarName}", "{Link}", "{ExpiresIn}"], ["{Link}", "{ExpiresIn}"], "", ""),
        [ResetPassword] = new("Reset your password", ["{GridName}", "{AvatarName}", "{Link}", "{ExpiresIn}"], ["{Link}", "{ExpiresIn}"], "", ""),
        [AccountApproved] = new("Account approved", ["{GridName}", "{AvatarName}", "{Link}"], [], "", ""),
        [PasswordChanged] = new("Password changed", ["{GridName}", "{AvatarName}"], [], "", ""),
        [AlreadyHaveAccount] = new("Already have an account", ["{GridName}", "{AvatarName}", "{Link}"], ["{Link}"], "", ""),
        [ConfirmNewEmail] = new("Confirm your new email address", ["{GridName}", "{AvatarName}", "{Link}", "{ExpiresIn}"], ["{Link}", "{ExpiresIn}"], "", ""),
        // Never names the new address: this goes to the old one, which may no longer be the person's.
        [EmailChangeNotice] = new("Email change requested", ["{GridName}", "{AvatarName}", "{Link}"], ["{Link}"], "", ""),
        // {Note} is the staff member's optional words, empty when they wrote none.
        [SignUpDenied] = new("Sign-up not approved", ["{GridName}", "{AvatarName}", "{Note}"], [], "", ""),
        [TakedownRemoved] = new("Item removed from your inventory", ["{GridName}", "{AvatarName}", "{CaseNumber}", "{Items}", "{Reason}"],
            ["{CaseNumber}", "{Items}"], "", ""),
        [TicketConfirm] = new("Confirm a support ticket", ["{GridName}", "{Name}", "{TicketNumber}", "{Subject}", "{Link}"], ["{Link}"], "", ""),
        [TicketAccess] = new("Your support ticket", ["{GridName}", "{Name}", "{TicketNumber}", "{Subject}", "{Link}"], ["{Link}"], "", ""),
        [TicketStaffReply] = new("Staff answered a support ticket", ["{GridName}", "{Name}", "{TicketNumber}", "{Subject}", "{Link}"], ["{Link}"], "", ""),
        [TakedownRestored] = new("Item put back in your inventory", ["{GridName}", "{AvatarName}", "{CaseNumber}", "{Items}", "{Reason}"],
            ["{CaseNumber}", "{Items}"], "", ""),
        [NameChanged] = new("Name changed by staff", ["{GridName}", "{OldName}", "{AvatarName}"], [], "", ""),
        // To the old address: never names the new one.
        [EmailChangedByStaff] = new("Email changed by staff", ["{GridName}", "{AvatarName}", "{Link}"], [], "", ""),
        [EmailSetByStaff] = new("Email set by staff", ["{GridName}", "{AvatarName}"], [], "", ""),
        [StaffResetPassword] = new("Password reset from staff", ["{GridName}", "{AvatarName}", "{Link}", "{ExpiresIn}"], ["{Link}", "{ExpiresIn}"], "", ""),
        // To staff: names only, never an applicant's email address.
        [SignUpsWaiting] = new("Sign-ups waiting", ["{GridName}", "{Count}", "{Names}", "{Link}"], ["{Link}"],
            "{Count} waiting for approval on {GridName}",
            "{Count} people are waiting for approval on {GridName}:\n\n{Names}\n\n{Link}"),
        // [S] To staff: numbers, categories and subjects only, never a message or the guest's email.
        [TicketNew] = new("New support tickets", ["{GridName}", "{Count}", "{Numbers}", "{Tickets}", "{Link}"], ["{Link}"],
            "New support tickets on {GridName}: {Numbers}",
            "New support tickets on {GridName} ({Count}):\n\n{Tickets}\n\nRead and answer them here:\n\n{Link}\n\nYou get this because you switched on \"New support tickets\" under Email notices."),
        [TicketReplyGuest] = new("A guest replied on a support ticket", ["{GridName}", "{Count}", "{Numbers}", "{Tickets}", "{Link}"], ["{Link}"],
            "Guests replied on {GridName} tickets: {Numbers}",
            "Guests replied on these support tickets on {GridName} ({Count}):\n\n{Tickets}\n\nRead and answer them here:\n\n{Link}\n\nYou get this because you switched on \"New support tickets\" under Email notices."),
    };

    public static string Title(string id) => Get(id).Title;

    /// <summary>Every fill-in word the template understands, like <c>{Link}</c>.</summary>
    public static IReadOnlyList<string> Words(string id) => Get(id).Words;

    /// <summary>The fill-in words the template can't be saved without.</summary>
    public static IReadOnlyList<string> Required(string id) => Get(id).Required;

    /// <summary>Whether the email goes to staff, so it is in English whatever the language: <c>test</c>, <c>signups-waiting</c>, <c>ticket-new</c> and <c>ticket-reply-guest</c>.</summary>
    public static bool IsStaffEmail(string id) => id is Test or SignUpsWaiting or TicketNew or TicketReplyGuest;

    /// <summary>The built-in wording in English.</summary>
    public static EmailTemplateText Default(string id) => Default(id, Languages.English);

    /// <summary>
    /// The built-in wording in <paramref name="language"/>: an email to a resident reads <c>EmailText</c> (a missing word or language
    /// falls back to English); an email to staff is English whatever the language.
    /// </summary>
    public static EmailTemplateText Default(string id, string language)
    {
        var t = Get(id);
        if (IsStaffEmail(id)) return new(t.Subject, t.Body, Shipped);
        var culture = CultureFor(language);
        return new(Resource(id + ".Subject", culture), Resource(id + ".Body", culture), Shipped);
    }

    /// <summary>
    /// A fill-in value in <paramref name="language"/>: <c>ExpiresIn.30m</c>, <c>ExpiresIn.24h</c> or <c>Reason.Copyright</c>,
    /// <c>Reason.Illegal</c>, <c>Reason.Rules</c>, <c>Reason.Other</c>.
    /// </summary>
    public static string Words(string key, string language) =>
        Text.GetString(key, CultureFor(language)) ?? throw new ArgumentException($"No email word \"{key}\".", nameof(key));

    /// <summary>
    /// The wording an email goes out with in <paramref name="language"/>: the owner's wording in that language, else the built-in
    /// wording in that language (English where it has none). The owner's wording in another language is never used.
    /// </summary>
    public static EmailTemplateText Current(LauncherSettings s, string id, string language)
    {
        if (IsStaffEmail(id)) language = Languages.English;
        return s.Email.Templates is { } all && all.TryGetValue(language, out var texts) && texts.TryGetValue(id, out var t)
            ? t
            : Default(id, language);
    }

    /// <summary>
    /// Every key of the resident emails' wording, in order: <c>&lt;id&gt;.Subject</c> and <c>&lt;id&gt;.Body</c> for each email to a
    /// resident, then <c>ExpiresIn.*</c> and <c>Reason.*</c> (what an owner-added language has to translate). Read once from the
    /// English resource; never a staff email.
    /// </summary>
    public static IReadOnlyList<string> ResidentTextKeys() => ResidentKeys.Value;

    /// <summary>The English wording of <paramref name="key"/> (one of <see cref="ResidentTextKeys"/>).</summary>
    public static string EnglishText(string key) =>
        Text.GetString(key, CultureInfo.InvariantCulture) ?? throw new ArgumentException($"No email word \"{key}\".", nameof(key));

    private static readonly Lazy<IReadOnlyList<string>> ResidentKeys = new(() =>
    {
        var set = Text.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false);
        List<string> keys = [];
        if (set is not null)
            foreach (System.Collections.DictionaryEntry e in set)
                if (e.Key is string k && e.Value is string && (k.StartsWith("ExpiresIn.", StringComparison.Ordinal)
                    || k.StartsWith("Reason.", StringComparison.Ordinal) || !IsStaffEmail(k[..Math.Max(k.LastIndexOf('.'), 0)])))
                    keys.Add(k);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    });

    /// <summary>A text of <c>EmailText</c> in <paramref name="culture"/>; a key it lacks is a bug, never an empty email.</summary>
    internal static string Resource(string key, CultureInfo culture) =>
        Text.GetString(key, culture) ?? throw new InvalidOperationException($"EmailText has no \"{key}\".");

    private static CultureInfo CultureFor(string language) =>
        Languages.HasCulture(language) ? Languages.Culture(language) : Languages.Culture(Languages.English);

    /// <summary>The required words found in neither the subject nor the body, in the template's order.</summary>
    public static IReadOnlyList<string> MissingWords(string id, string subject, string body) =>
        [.. Get(id).Required.Where(w => !(subject ?? "").Contains(w, StringComparison.Ordinal) && !(body ?? "").Contains(w, StringComparison.Ordinal))];

    /// <summary>
    /// The email with <paramref name="values"/> (word name without braces to value) filled in. A word with no value is left as
    /// typed. The subject is one line (line breaks become spaces) so no value can start a new header.
    /// </summary>
    public static RenderedEmail Render(string id, EmailTemplateText text, IReadOnlyDictionary<string, string> values)
    {
        _ = Get(id);
        var subject = Fill(text.Subject, values).Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        var plain = Fill(text.Body.Replace("\r\n", "\n"), values);
        // The words go through Markdown untouched (braces mean nothing to it); in a link address Markdig writes them
        // percent-encoded, so both spellings are filled in.
        var html = LegalMarkdown.ToHtml(text.Body);
        foreach (var (word, value) in values)
        {
            var encoded = WebUtility.HtmlEncode(value);
            html = html.Replace("{" + word + "}", encoded, StringComparison.Ordinal)
                .Replace("%7B" + word + "%7D", encoded, StringComparison.Ordinal);
        }
        return new RenderedEmail(subject, plain, html);
    }

    /// <summary>Made-up values for every word, for the preview and the test send.</summary>
    public static IReadOnlyDictionary<string, string> Sample(string id, string gridName)
    {
        var all = new Dictionary<string, string>
        {
            ["GridName"] = gridName,
            ["AvatarName"] = "Sample Avatar",
            ["Link"] = "https://example.com/sample-link",
            ["ExpiresIn"] = "30 minutes",
            ["Note"] = "Sample note from staff.",
            ["Count"] = "2",
            ["Names"] = "Sample Avatar\nOther Avatar",
            ["OldName"] = "Former Name",
            ["CaseNumber"] = "TD-2026-001",
            ["Items"] = "Sample sword, Sample shield",
            ["Reason"] = "Copyright (DMCA)",
            ["Name"] = "Sample Name",
            ["TicketNumber"] = "T-0001",
            ["Subject"] = "Sample ticket subject",
            ["Numbers"] = "T-0001, T-0002",
            ["Tickets"] = "T-0001 (Technical problem): Sample ticket subject\nT-0002 (Something else): Other subject",
        };
        return Get(id).Words.Select(w => w.Trim('{', '}')).ToDictionary(w => w, w => all[w]);
    }

    private static string Fill(string text, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (word, value) in values)
            text = text.Replace("{" + word + "}", value, StringComparison.Ordinal);
        return text;
    }

    private static Template Get(string id) =>
        Templates.TryGetValue(id, out var t) ? t : throw new ArgumentException($"No email template \"{id}\".", nameof(id));
}
