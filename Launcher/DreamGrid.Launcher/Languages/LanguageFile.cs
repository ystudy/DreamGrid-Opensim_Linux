using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Core.Email;

namespace DreamGrid.Launcher.Resident;

/// <summary>Something wrong with one text of a language file, or with the file itself (<see cref="Key"/> is then <c>""</c>, <c>"code"</c> or a section name).</summary>
public sealed record LanguageFileProblem(string Key, string Problem);

/// <summary>
/// The file an owner downloads to translate an added language away from the site, and uploads back (plan 16b):
/// <c>{ "code": "it", "page": { key: text }, "email": { key: text }, "english": { "page": {...}, "email": {...} } }</c>, UTF-8.
/// <see cref="Write"/> lists every catalogue key (an untranslated one as <c>""</c>) with the English beside it under <c>"english"</c>;
/// <see cref="Read"/> ignores <c>"english"</c>, takes an empty text as untranslated, and refuses the whole file on any problem: one
/// larger than <see cref="MaxBytes"/>, not UTF-8 or not JSON, a code other than the page's, a key GridGate doesn't know, a value that
/// is not a string, or a text whose fill-ins don't match the English (<see cref="Check"/>). Text is kept exactly as written (markup
/// included); the pages encode it when they show it.
/// </summary>
public static partial class LanguageFile
{
    /// <summary>The largest file accepted: 1 MB.</summary>
    public const int MaxBytes = 1_048_576;

    /// <summary>The most characters one text may have.</summary>
    public const int MaxTextLength = 10_000;

    /// <summary>Which part of the language a key belongs to.</summary>
    public enum Section { Page, Email }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Letters of every language as they are, so a translator can read the file; HTML-significant characters stay escaped.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The language in <paramref name="json"/> for the page's <paramref name="code"/>, saved now, or null and every problem found.
    /// At most <see cref="MaxBytes"/> + 1 bytes are read from the stream.
    /// </summary>
    public static (OwnerLanguage? Language, IReadOnlyList<LanguageFileProblem> Problems) Read(Stream json, string code, LanguageCatalog catalog, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(time);

        var bytes = ReadAtMost(json, MaxBytes + 1);
        if (bytes.Length > MaxBytes)
            return Refused("", "The file is larger than 1 MB.");

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Refused("", "The file is not UTF-8 text. Save it as UTF-8 and upload it again.");
        }
        // A UTF-8 byte-order mark (U+FEFF) at the start is allowed: some editors write one.
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            return Refused("", "The file is not a language file: it is not valid JSON.");
        }

        using (doc)
        {
            try
            {
                return ReadDocument(doc.RootElement, code, catalog, time);
            }
            catch (InvalidOperationException)
            {
                // A key or text holding half of a character pair (such as "\uD800") parses as JSON but can't be read as text.
                return Refused("", "The file has a key or text that is not valid text (a broken \\u escape). Fix it and upload it again.");
            }
        }
    }

    private static (OwnerLanguage?, IReadOnlyList<LanguageFileProblem>) ReadDocument(JsonElement root, string code, LanguageCatalog catalog, TimeProvider time)
    {
        {
            if (root.ValueKind != JsonValueKind.Object)
                return Refused("", "The file is not a language file: it should be one JSON object with \"code\", \"page\" and \"email\".");

            List<LanguageFileProblem> problems = [];
            Dictionary<string, string> page = new(StringComparer.Ordinal);
            Dictionary<string, string> email = new(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var hasCode = false;
            foreach (var section in root.EnumerateObject())
            {
                if (!seen.Add(section.Name))
                {
                    problems.Add(new(section.Name, "This appears more than once in the file."));
                    continue;
                }
                switch (section.Name)
                {
                    case "code":
                        hasCode = true;
                        if (section.Value.ValueKind != JsonValueKind.String || OwnerLanguages.CodeFor(section.Value.GetString()) != code)
                            problems.Add(new("code", $"This file is not for {Named(code)}: its \"code\" should be \"{code}\"."));
                        break;
                    case "page":
                        ReadSection(section.Value, "page", new HashSet<string>(catalog.PageKeys(), StringComparer.Ordinal), page, problems);
                        break;
                    case "email":
                        ReadSection(section.Value, "email", new HashSet<string>(catalog.EmailKeys(), StringComparer.Ordinal), email, problems);
                        break;
                    case "english":
                        break;
                    default:
                        problems.Add(new(section.Name, "A language file has only \"code\", \"page\", \"email\" and \"english\"."));
                        break;
                }
            }
            if (!hasCode)
                problems.Add(new("code", $"The file has no \"code\". It should be \"{code}\"."));

            foreach (var (key, value) in page)
                if (Check(catalog, Section.Page, key, value, null) is { } p)
                    problems.Add(new(key, p));
            foreach (var (key, value) in email)
                if (Check(catalog, Section.Email, key, value, Pair(key) is { } other ? email.GetValueOrDefault(other) : null) is { } p)
                    problems.Add(new(key, p));

            return problems.Count > 0
                ? (null, Capped(problems))
                : (new OwnerLanguage(code, page, email, time.GetUtcNow()), problems);
        }
    }

    /// <summary>The file for <paramref name="language"/>: every catalogue key, its text or <c>""</c>, and the English beside it; UTF-8 without a byte-order mark.</summary>
    public static byte[] Write(OwnerLanguage language, LanguageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(catalog);
        var file = new Dictionary<string, object>
        {
            ["code"] = language.Code,
            ["page"] = catalog.PageKeys().ToDictionary(k => k, k => language.Page.GetValueOrDefault(k) ?? ""),
            ["email"] = catalog.EmailKeys().ToDictionary(k => k, k => language.Email.GetValueOrDefault(k) ?? ""),
            ["english"] = new Dictionary<string, object>
            {
                ["page"] = catalog.PageKeys().ToDictionary(k => k, catalog.English),
                ["email"] = catalog.EmailKeys().ToDictionary(k => k, catalog.EnglishEmail),
            },
        };
        return JsonSerializer.SerializeToUtf8Bytes(file, WriteOptions);
    }

    /// <summary>
    /// What is wrong with <paramref name="text"/> (already trimmed, not empty) as the translation of <paramref name="key"/>, or null when
    /// nothing is. A page text has the same <c>{0}</c>-style fill-ins as the English and works as a format string; an email text has
    /// the same <c>{Word}</c> fill-ins; and an email's subject and body together keep its required words
    /// (<see cref="EmailTemplates.MissingWords"/>), <paramref name="pairedText"/> being the other half (the English when null).
    /// </summary>
    public static string? Check(LanguageCatalog catalog, Section section, string key, string text, string? pairedText)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (text.Length > MaxTextLength)
            return $"This is too long. Keep it to {MaxTextLength:N0} characters or fewer.";
        if (section == Section.Page)
        {
            if (StrayFillIn(text) is { } stray)
                return $"{stray} is not a fill-in GridGate can use. Write fill-ins as {{0}}, {{1}} and so on.";
            var english = catalog.English(key);
            var want = Indexes(english);
            var have = Indexes(text);
            if (!want.SetEquals(have))
                return Mismatch(want.Order().Select(i => $"{{{i}}}"), have.Order().Select(i => $"{{{i}}}"));
            if (want.Count > 0)
            {
                try
                {
                    _ = string.Format(CultureInfo.InvariantCulture, text, [.. Enumerable.Repeat<object?>("x", want.Max() + 1)]);
                }
                catch (FormatException)
                {
                    return "A { or } here is not part of a fill-in such as {0}. Take it out.";
                }
            }
            return null;
        }

        var englishEmail = catalog.EnglishEmail(key);
        var words = Words(englishEmail);
        var got = Words(text);
        if (!words.SetEquals(got))
            return Mismatch(words.Order(StringComparer.Ordinal), got.Order(StringComparer.Ordinal));
        if (Pair(key) is { } other)
        {
            var id = key[..key.LastIndexOf('.')];
            var otherText = string.IsNullOrWhiteSpace(pairedText) ? catalog.EnglishEmail(other) : pairedText;
            var (subject, body) = key.EndsWith(".Subject", StringComparison.Ordinal) ? (text, otherText) : (otherText, text);
            var missing = EmailTemplates.MissingWords(id, subject, body);
            if (missing.Count > 0)
                return $"The email can't be sent without {string.Join(" and ", missing)}. Put it in the subject or the body.";
        }
        return null;
    }

    /// <summary>The other half of an email's subject and body (<c>x.Subject</c> for <c>x.Body</c> and back), or null for a word such as <c>Reason.Other</c>.</summary>
    public static string? Pair(string key) =>
        key.EndsWith(".Subject", StringComparison.Ordinal) ? key[..^".Subject".Length] + ".Body"
        : key.EndsWith(".Body", StringComparison.Ordinal) ? key[..^".Body".Length] + ".Subject"
        : null;

    /// <summary>A text as stored: line breaks as line feeds, no spaces around it.</summary>
    public static string Tidy(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    /// <summary>The most problems listed; past it, one more line says how many others there are.</summary>
    public const int MaxProblems = 50;

    private static IReadOnlyList<LanguageFileProblem> Capped(List<LanguageFileProblem> problems) =>
        problems.Count <= MaxProblems ? problems
        : [.. problems.Take(MaxProblems), new("", $"... and {problems.Count - MaxProblems:N0} more.")];

    private static void ReadSection(JsonElement value, string name, HashSet<string> keys, Dictionary<string, string> into, List<LanguageFileProblem> problems)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            problems.Add(new(name, $"\"{name}\" should be a list of texts by key."));
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject())
        {
            if (!seen.Add(item.Name))
            {
                problems.Add(new(item.Name, "This key appears more than once."));
                continue;
            }
            if (!keys.Contains(item.Name))
            {
                problems.Add(new(item.Name, $"GridGate has no {name} text with this key."));
                continue;
            }
            if (item.Value.ValueKind != JsonValueKind.String)
            {
                problems.Add(new(item.Name, "The text should be in quotes."));
                continue;
            }
            var text = Tidy(item.Value.GetString()!);
            if (text.Length > 0)
                into[item.Name] = text;
        }
    }

    private static string Mismatch(IEnumerable<string> want, IEnumerable<string> have)
    {
        static string List(IEnumerable<string> items) => items.Any() ? string.Join(", ", items) : "none";
        return $"Use the same fill-ins as the English ({List(want)}); this text has {List(have)}.";
    }

    /// <summary>The text without escaped braces (<c>{{</c> and <c>}}</c>), which are not fill-ins.</summary>
    private static string Unescaped(string text) => text.Replace("{{", "", StringComparison.Ordinal).Replace("}}", "", StringComparison.Ordinal);

    /// <summary>A brace pair that starts with a digit but isn't an index fill-in GridGate accepts (such as <c>{0,999999}</c>), or null.</summary>
    private static string? StrayFillIn(string text) =>
        DigitBracePattern().Matches(Unescaped(text)).Select(m => m.Value).FirstOrDefault(t => !IndexPattern().IsMatch(t) || IndexPattern().Match(t).Length != t.Length);

    private static HashSet<int> Indexes(string text) =>
        [.. IndexPattern().Matches(Unescaped(text)).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))];

    private static HashSet<string> Words(string text) =>
        new(WordPattern().Matches(Unescaped(text)).Select(m => m.Value), StringComparer.Ordinal);

    private static string Named(string code) => Languages.HasCulture(code) ? Languages.NativeName(code) : code;

    private static (OwnerLanguage?, IReadOnlyList<LanguageFileProblem>) Refused(string key, string problem) => (null, [new(key, problem)]);

    private static byte[] ReadAtMost(Stream stream, int limit)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while (buffer.Length < limit && (read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, limit - buffer.Length))) > 0)
            buffer.Write(chunk, 0, read);
        return buffer.ToArray();
    }

    // An index fill-in: {0}, {0:N0}, {0,-10}. The alignment has at most three digits, so {0,999999} is not one.
    [GeneratedRegex(@"\{(\d{1,3})(?:,-?\d{1,3})?(?::[^{}]*)?\}")]
    private static partial Regex IndexPattern();

    // Anything in braces that starts with a digit: an index fill-in or a mistaken one.
    [GeneratedRegex(@"\{\s*\d[^{}]*\}")]
    private static partial Regex DigitBracePattern();

    [GeneratedRegex(@"\{[A-Za-z]+\}")]
    private static partial Regex WordPattern();
}
