using System.Globalization;
using GridGate.Launcher.Core.Config;
using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Email;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Email;

namespace GridGate.Launcher.Iar;

/// <summary>
/// Emails a resident that their backup is ready, in their language, with a link to their own inventory page (never to the file:
/// the download needs them signed in). Nothing is sent when email isn't set up or they have no address.
/// </summary>
public sealed class IarEmailNotifier(EmailSender sender, EmailLanguage language, IPeopleDirectory people, Func<LauncherSettings> settings,
    ILogger<IarEmailNotifier> log) : IIarNotifier
{
    public const string PagePath = "/guest/inventory";

    public async Task<bool> NotifyReadyAsync(Guid principal, string avatarName, DateTimeOffset expires, CancellationToken ct)
    {
        if (!sender.IsReady) return false;
        try
        {
            if (await people.GetAsync(principal, ct) is not { } row || row.Account.Email.Trim() is not { Length: > 0 } to) return false;
            var values = new Dictionary<string, string>
            {
                ["AvatarName"] = avatarName,
                ["Link"] = WebsiteAddress.Base(settings()) + PagePath,
                ["ExpiresAt"] = expires.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
            };
            return sender.Queue(EmailTemplates.IarReady, language.ForResident(principal), to, avatarName, values) == QueueResult.Queued;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "The backup-ready email for {Name} could not be queued.", avatarName);
            return false;
        }
    }
}
