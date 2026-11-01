using System.Net;
using DreamGrid.Launcher.Core.Guests;
using DreamGrid.Launcher.Core.Settings;

namespace DreamGrid.Launcher.Core.Email;

/// <summary>One email's wording as the owner saved it: a subject and a Markdown body with fill-in words such as <c>{Link}</c>.</summary>
public sealed record EmailTemplateText(string Subject, string Body, DateTimeOffset Changed);

/// <summary>An email ready to send: a one-line subject, the plain-text body and the HTML body made from it.</summary>
public sealed record RenderedEmail(string Subject, string Text, string Html);

/// <summary>
/// The emails the grid sends, each with default wording that works as it is, the fill-in words it understands and the ones it
/// can't do without. The owner's own wording is kept in <see cref="EmailSettings"/>, by language, only where it differs from the
/// default. The body is Markdown with raw HTML off (<see cref="LegalMarkdown"/>); fill-in values are placed after the HTML is
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

    /// <summary>Until languages arrive (worklist 1.5) every template is in this one.</summary>
    public const string DefaultLanguage = LegalPages.DefaultLanguage;

    public static IReadOnlyList<string> All { get; } = [Test, ConfirmEmail, ResetPassword, AccountApproved, PasswordChanged, AlreadyHaveAccount,
        ConfirmNewEmail, EmailChangeNotice, SignUpDenied, SignUpsWaiting];

    private static readonly DateTimeOffset Shipped = DateTimeOffset.UnixEpoch;

    private sealed record Template(string Title, string[] Words, string[] Required, string Subject, string Body);

    private static readonly Dictionary<string, Template> Templates = new()
    {
        [Test] = new("Test email", ["{GridName}", "{AvatarName}"], [],
            "Test email from {GridName}",
            "Hello {AvatarName},\n\nThis is a test email from {GridName}. If you can read it, the grid can send email."),
        [ConfirmEmail] = new("Confirm your email", ["{GridName}", "{AvatarName}", "{Link}", "{ExpiresIn}"], ["{Link}", "{ExpiresIn}"],
            "Confirm your email for {GridName}",
            "Hello {AvatarName},\n\nPlease confirm your email address for {GridName}:\n\n{Link}\n\nThe link works once, for {ExpiresIn}. If you didn't sign up, you can ignore this email."),
        [ResetPassword] = new("Reset your password", ["{GridName}", "{AvatarName}", "{Link}", "{ExpiresIn}"], ["{Link}", "{ExpiresIn}"],
            "Reset your {GridName} password",
            "Hello {AvatarName},\n\nSomeone asked to reset your password on {GridName}. To choose a new one, open this link:\n\n{Link}\n\nThe link works once, for {ExpiresIn}. If it wasn't you, ignore this email: your password stays the same."),
        [AccountApproved] = new("Account approved", ["{GridName}", "{AvatarName}", "{Link}"], [],
            "Welcome to {GridName}",
            "Hello {AvatarName},\n\nYour account on {GridName} is ready. You can sign in now:\n\n{Link}"),
        [PasswordChanged] = new("Password changed", ["{GridName}", "{AvatarName}"], [],
            "Your password on {GridName} was changed",
            "Hello {AvatarName},\n\nYour password on {GridName} was just changed, and you were signed out everywhere.\n\nIf it wasn't you, contact the grid owner straight away."),
        [AlreadyHaveAccount] = new("Already have an account", ["{GridName}", "{AvatarName}", "{Link}"], ["{Link}"],
            "Someone tried to sign up on {GridName}",
            "Hello {AvatarName},\n\nSomeone tried to sign up on {GridName} with this email address. If that was you, you already have an avatar: {AvatarName}.\n\nForgot your password? You can choose a new one here:\n\n{Link}\n\nIf it wasn't you, you can ignore this email."),
        [ConfirmNewEmail] = new("Confirm your new email address", ["{GridName}", "{AvatarName}", "{Link}", "{ExpiresIn}"], ["{Link}", "{ExpiresIn}"],
            "Confirm your new email address",
            "Hello {AvatarName},\n\nConfirm your new email address for {AvatarName} on {GridName}:\n\n{Link}\n\nThe link works once, for {ExpiresIn}. If you didn't ask for this, ignore this email."),
        // Never names the new address: this goes to the old one, which may no longer be the person's.
        [EmailChangeNotice] = new("Email change requested", ["{GridName}", "{AvatarName}", "{Link}"], ["{Link}"],
            "Your email address on {GridName}",
            "Hello {AvatarName},\n\nSomeone asked to change the email address for {AvatarName} on {GridName}. If that was you, there's nothing to do.\n\nIf not, change your password now:\n\n{Link}"),
        // {Note} is the staff member's optional words, empty when they wrote none.
        [SignUpDenied] = new("Sign-up not approved", ["{GridName}", "{AvatarName}", "{Note}"], [],
            "Your sign-up on {GridName}",
            "Hello {AvatarName},\n\nYour sign-up on {GridName} wasn't approved.\n\n{Note}"),
        // To staff: names only, never an applicant's email address.
        [SignUpsWaiting] = new("Sign-ups waiting", ["{GridName}", "{Count}", "{Names}", "{Link}"], ["{Link}"],
            "{Count} waiting for approval on {GridName}",
            "{Count} people are waiting for approval on {GridName}:\n\n{Names}\n\n{Link}"),
    };

    public static string Title(string id) => Get(id).Title;

    /// <summary>Every fill-in word the template understands, like <c>{Link}</c>.</summary>
    public static IReadOnlyList<string> Words(string id) => Get(id).Words;

    /// <summary>The fill-in words the template can't be saved without.</summary>
    public static IReadOnlyList<string> Required(string id) => Get(id).Required;

    public static EmailTemplateText Default(string id) => new(Get(id).Subject, Get(id).Body, Shipped);

    /// <summary>The owner's wording for the template in the default language, or the default.</summary>
    public static EmailTemplateText Current(LauncherSettings s, string id) =>
        s.Email.Templates is { } all && all.TryGetValue(DefaultLanguage, out var texts) && texts.TryGetValue(id, out var t)
            ? t
            : Default(id);

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
