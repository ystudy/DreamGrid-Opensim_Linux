using DreamGrid.Launcher.Core.Security;
using Microsoft.Extensions.Localization;

namespace DreamGrid.Launcher;

/// <summary>What is wrong with a new password (<see cref="PasswordRules.Check"/>), in the page's language.</summary>
public static class PasswordFaultText
{
    public static string For(IStringLocalizer<ResidentText> t, PasswordRules.Fault fault) => Message(fault).In(t);

    /// <summary>The same words as a message a service can hand to a page.</summary>
    public static ResidentMessage Message(PasswordRules.Fault fault) => fault switch
    {
        PasswordRules.Fault.Mismatch => new("Password.Mismatch"),
        PasswordRules.Fault.TooShort => new("Password.TooShort", PasswordRules.MinLength),
        PasswordRules.Fault.TooCommon => new("Password.TooCommon"),
        _ => new("Password.IsName"),
    };
}
