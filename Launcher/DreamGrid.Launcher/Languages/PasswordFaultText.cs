using DreamGrid.Launcher.Core.Security;
using Microsoft.Extensions.Localization;

namespace DreamGrid.Launcher;

/// <summary>What is wrong with a new password (<see cref="PasswordRules.Check"/>), in the page's language.</summary>
public static class PasswordFaultText
{
    public static string For(IStringLocalizer<ResidentText> t, PasswordRules.Fault fault) => fault switch
    {
        PasswordRules.Fault.Mismatch => t["Password.Mismatch"].Value,
        PasswordRules.Fault.TooShort => t["Password.TooShort", PasswordRules.MinLength].Value,
        PasswordRules.Fault.TooCommon => t["Password.TooCommon"].Value,
        _ => t["Password.IsName"].Value,
    };
}
