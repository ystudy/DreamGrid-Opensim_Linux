using System.Globalization;
using Microsoft.Extensions.Localization;

namespace GridGate.Launcher;

/// <summary>
/// Counted text: a sentence whose words change with a number ("1 thing", "2 things"; Russian has three forms). A counted key has
/// three entries, <c>Key.One</c>, <c>Key.Few</c> and <c>Key.Other</c>, in every language; a language with two forms gives Few the
/// same words as Other. The number is <c>{0}</c>, already written in the page's language.
/// </summary>
public static class ResidentPlural
{
    public enum Form { One, Few, Other }

    /// <summary>The form a language uses for <paramref name="n"/> (the CLDR rules for whole numbers in the built-in languages).
    /// A language added by the owner gets One for 1 and Other for the rest.</summary>
    public static Form Of(long n, CultureInfo culture)
    {
        var a = Math.Abs(n);
        switch (culture.TwoLetterISOLanguageName)
        {
            case "ru":
                if (a % 10 == 1 && a % 100 != 11) return Form.One;
                if (a % 10 is >= 2 and <= 4 && a % 100 is < 12 or > 14) return Form.Few;
                return Form.Other;
            case "fr":
            case "pt":
                return a <= 1 ? Form.One : Form.Other;
            default:
                return a == 1 ? Form.One : Form.Other;
        }
    }

    /// <summary>The counted sentence <paramref name="key"/> for <paramref name="n"/>, then any further values as {1}, {2}...</summary>
    public static string Count(this IStringLocalizer<ResidentText> t, string key, long n, params object[] more) =>
        t[key + "." + Of(n, CultureInfo.CurrentUICulture), [n.ToString("N0", CultureInfo.CurrentCulture), .. more]].Value;
}
