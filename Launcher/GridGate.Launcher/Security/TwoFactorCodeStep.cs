using System.Net;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Security;

namespace GridGate.Launcher.Security;

/// <summary>How the second sign-in step ended; each door maps it to its own wording and redirect.</summary>
public enum CodeStepResult { SignedIn, Locked, TwoFactorOff, TwoFactorUnknown, SaveFailed, WrongCode }

/// <summary>Who is at the code step: the pending sign-in's account id, name, first name, and the role the audit records.</summary>
public sealed record CodeStepCaller(Guid Principal, string Name, string First, string AuditRole);

/// <summary>
/// The two-factor code step the staff and guest doors share: locked, then the stored state, then the code; a wrong code counts in
/// the throttle, a right one signs in. Codes are never logged.
/// </summary>
public static class TwoFactorCodeStep
{
    public static async Task<CodeStepResult> VerifyAsync(TwoFactorStore store, LoginThrottle throttle, AuditLog audit, ILogger log,
        string logArea, SignInDoor door, CodeStepCaller who, string? code, IPAddress? remote, Func<Task> signIn)
    {
        var key = AvatarName.Parse(who.Name).Key;
        if (throttle.IsLocked(door, key, remote)) return CodeStepResult.Locked;

        switch (store.StateOf(who.Principal))
        {
            case TwoFactorState.Off:
                return CodeStepResult.TwoFactorOff;
            case TwoFactorState.Unknown:
                return CodeStepResult.TwoFactorUnknown;
        }

        bool right, usedBackup;
        try
        {
            right = store.VerifyCode(who.Principal, code ?? "", out usedBackup);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not a wrong code, so not counted; nothing was used up.
            log.LogWarning("{Area}: a two-factor code for first name {First} couldn't be marked used: {Message}",
                logArea, LogText.Clean(who.First, 32), e.Message);
            return CodeStepResult.SaveFailed;
        }

        var ip = remote?.ToString();
        if (!right)
        {
            throttle.RecordFailure(door, key, remote);
            log.LogInformation("{Area}: a two-factor code for first name {First} was refused", logArea, LogText.Clean(who.First, 32));
            audit.Write(new AuditEntry(who.Name, who.Principal, who.AuditRole, ip, "sign-in-refused", "two-factor code", "refused"));
            return CodeStepResult.WrongCode;
        }

        throttle.RecordSuccess(door, key);
        await signIn();
        audit.Write(new AuditEntry(who.Name, who.Principal, who.AuditRole, ip, "sign-in", null, "ok"));
        if (usedBackup)
            audit.Write(new AuditEntry(who.Name, who.Principal, who.AuditRole, ip, "backup-code-used", null, "ok"));
        return CodeStepResult.SignedIn;
    }
}
