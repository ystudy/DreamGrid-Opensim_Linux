using System.Text;
using System.Text.Json;
using DreamGrid.Launcher.Core;
using DreamGrid.Launcher.Resident;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>The file an owner downloads to translate a language offline and uploads back (plan 16b, Review Focus 1).</summary>
public sealed class LanguageFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dg-language-file-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly LanguageCatalog _catalog;

    public LanguageFileTests() => _catalog = new LanguageCatalog(new OwnerLanguages(_dir, _time));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static MemoryStream Json(string json) => new(new UTF8Encoding(false).GetBytes(json));

    private (OwnerLanguage? Language, IReadOnlyList<LanguageFileProblem> Problems) Read(string json, string code = "it") =>
        LanguageFile.Read(Json(json), code, _catalog, _time);

    [Fact]
    public void Round_trip()
    {
        var language = new OwnerLanguage("it",
            new Dictionary<string, string> { ["Home.Title"] = "Benvenuto", ["Bots.Question"] = "Quanto fa {0} più {1}?" },
            new Dictionary<string, string> { ["confirm-email.Subject"] = "Conferma la tua email per {GridName}", ["Reason.Other"] = "Altro" },
            _time.GetUtcNow());
        var bytes = LanguageFile.Write(language, _catalog);

        var (read, problems) = LanguageFile.Read(new MemoryStream(bytes), "it", _catalog, _time);
        Assert.Empty(problems);
        Assert.NotNull(read);
        Assert.Equal("it", read.Code);
        Assert.Equal(language.Page.OrderBy(p => p.Key), read.Page.OrderBy(p => p.Key));
        Assert.Equal(language.Email.OrderBy(p => p.Key), read.Email.OrderBy(p => p.Key));
        Assert.Equal(_time.GetUtcNow(), read.Saved);

        // Every catalogue key is in the file, untranslated ones empty, with English beside it.
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        Assert.Equal("it", root.GetProperty("code").GetString());
        Assert.Equal(_catalog.PageKeys(), root.GetProperty("page").EnumerateObject().Select(p => p.Name));
        Assert.Equal(_catalog.EmailKeys(), root.GetProperty("email").EnumerateObject().Select(p => p.Name));
        Assert.Equal("", root.GetProperty("page").GetProperty("Home.Intro").GetString());
        Assert.Equal(_catalog.English("Home.Intro"), root.GetProperty("english").GetProperty("page").GetProperty("Home.Intro").GetString());
        Assert.Equal(_catalog.EnglishEmail("Reason.Other"), root.GetProperty("english").GetProperty("email").GetProperty("Reason.Other").GetString());
        // UTF-8 without a byte-order mark; letters outside ASCII are written as they are.
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("più", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void English_section_is_ignored()
    {
        var (read, problems) = Read("""{ "code": "it", "page": { "Home.Title": "Ciao" }, "english": { "page": { "Nope": 5 }, "junk": [1] } }""");
        Assert.Empty(problems);
        Assert.Equal("Ciao", read!.Page["Home.Title"]);
        Assert.Empty(read.Email);
    }

    [Fact]
    public void Empty_values_are_not_translations()
    {
        var (read, problems) = Read("""{ "code": "it", "page": { "Home.Title": "", "Home.Intro": "  " }, "email": { "Reason.Other": "" } }""");
        Assert.Empty(problems);
        Assert.Empty(read!.Page);
        Assert.Empty(read.Email);
    }

    [Fact]
    public void Line_breaks_are_kept_as_line_feeds()
    {
        var (read, _) = Read("""{ "code": "it", "page": { "Home.Intro": "  Una\r\nDue  " } }""");
        Assert.Equal("Una\nDue", read!.Page["Home.Intro"]);
    }

    [Fact]
    public void Unknown_keys_are_problems()
    {
        var (read, problems) = Read("""{ "code": "it", "page": { "Home.Title": "Ciao", "No.Such.Key": "x" }, "email": { "test.Subject": "x" }, "extra": {} }""");
        Assert.Null(read);
        Assert.Contains(problems, p => p.Key == "No.Such.Key");
        // A staff email is not something an added language translates.
        Assert.Contains(problems, p => p.Key == "test.Subject");
        Assert.Contains(problems, p => p.Key == "extra");
        Assert.DoesNotContain(problems, p => p.Key == "Home.Title");
    }

    [Fact]
    public void A_page_key_in_the_email_section_is_a_problem()
    {
        var (read, problems) = Read("""{ "code": "it", "email": { "Home.Title": "Ciao" } }""");
        Assert.Null(read);
        Assert.Contains(problems, p => p.Key == "Home.Title");
    }

    [Fact]
    public void A_key_given_twice_is_a_problem()
    {
        var (read, problems) = Read("""{ "code": "it", "page": { "Home.Title": "Ciao", "Home.Title": "Salve" } }""");
        Assert.Null(read);
        Assert.Contains(problems, p => p.Key == "Home.Title");
    }

    [Theory]
    [InlineData("5")]
    [InlineData("null")]
    [InlineData("[\"Ciao\"]")]
    [InlineData("{ \"text\": \"Ciao\" }")]
    [InlineData("true")]
    public void Non_string_values_are_problems(string value)
    {
        var (read, problems) = Read($$"""{ "code": "it", "page": { "Home.Title": {{value}} } }""");
        Assert.Null(read);
        Assert.Contains(problems, p => p.Key == "Home.Title");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"page\"")]
    [InlineData("5")]
    public void A_section_that_is_not_an_object_is_a_problem(string section)
    {
        var (read, problems) = Read($$"""{ "code": "it", "page": {{section}} }""");
        Assert.Null(read);
        Assert.Contains(problems, p => p.Key == "page");
    }

    [Fact]
    public void Oversize_is_refused()
    {
        var filler = new string('a', LanguageFile.MaxBytes);
        var (read, problems) = Read($$"""{ "code": "it", "english": { "x": "{{filler}}" } }""");
        Assert.Null(read);
        Assert.Single(problems);
        Assert.Contains("1 MB", problems[0].Problem);

        // Exactly the limit is fine.
        var head = "{ \"code\": \"it\", \"english\": { \"x\": \"";
        var tail = "\" } }";
        var exact = head + new string('a', LanguageFile.MaxBytes - head.Length - tail.Length) + tail;
        Assert.Equal(LanguageFile.MaxBytes, Encoding.UTF8.GetByteCount(exact));
        Assert.Empty(Read(exact).Problems);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{ \"code\": \"it\", ")]
    [InlineData("[1, 2]")]
    [InlineData("\"it\"")]
    [InlineData("{ \"code\": \"it\", /* comment */ \"page\": {} }")]
    [InlineData("{ \"code\": \"it\", \"page\": {}, }")]
    public void Not_JSON_is_refused(string json)
    {
        var (read, problems) = Read(json);
        Assert.Null(read);
        Assert.NotEmpty(problems);
    }

    [Fact]
    public void Not_UTF8_is_refused()
    {
        // "Più" in Latin-1: 0xF9 on its own is not UTF-8.
        var bytes = Encoding.Latin1.GetBytes("""{ "code": "it", "page": { "Home.Title": "Più" } }""");
        var (read, problems) = LanguageFile.Read(new MemoryStream(bytes), "it", _catalog, _time);
        Assert.Null(read);
        Assert.Contains("UTF-8", Assert.Single(problems).Problem);

        // A UTF-16 file is not UTF-8 either.
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("""{ "code": "it" }""")).ToArray();
        Assert.Null(LanguageFile.Read(new MemoryStream(utf16), "it", _catalog, _time).Language);
    }

    [Fact]
    public void A_UTF8_byte_order_mark_is_allowed()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("""{ "code": "it", "page": { "Home.Title": "Più" } }""")).ToArray();
        var (read, problems) = LanguageFile.Read(new MemoryStream(bytes), "it", _catalog, _time);
        Assert.Empty(problems);
        Assert.Equal("Più", read!.Page["Home.Title"]);
    }

    [Theory]
    [InlineData("page", "Bots.Question", "Quanto fa {0}?")]               // {1} missing
    [InlineData("page", "Bots.Question", "Quanto fa {0} più {1} e {2}?")] // {2} extra
    [InlineData("page", "Home.Title", "Benvenuto {0}")]                   // English has none
    [InlineData("page", "Bots.Question", "Quanto fa {0} più {1?")]        // a stray brace breaks the fill-in
    [InlineData("email", "confirm-email.Subject", "Conferma la tua email")]          // {GridName} missing
    [InlineData("email", "confirm-email.Subject", "Conferma {GridName} {Nome}")]     // a word English doesn't have
    [InlineData("email", "Reason.Other", "Altro {GridName}")]
    public void Placeholder_mismatch_is_a_problem(string section, string key, string text)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object> { ["code"] = "it", [section] = new Dictionary<string, string> { [key] = text } });
        var (read, problems) = Read(json);
        Assert.Null(read);
        Assert.Equal(key, Assert.Single(problems).Key);
    }

    [Fact]
    public void Placeholders_in_another_order_are_fine()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["code"] = "it",
            ["page"] = new Dictionary<string, string> { ["Bots.Question"] = "{1} più {0}?", ["SignUp.AcceptTerms"] = "Accetto {1} e {0}." },
        });
        Assert.Empty(Read(json).Problems);
    }

    [Fact]
    public void Missing_required_email_word_is_a_problem()
    {
        // The body is where {Link} and {ExpiresIn} are; a body without them can't be used.
        var english = _catalog.EnglishEmail("confirm-email.Body");
        Assert.Contains("{Link}", english);
        var withoutLink = english.Replace("{Link}", "", StringComparison.Ordinal);
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["code"] = "it",
            ["email"] = new Dictionary<string, string> { ["confirm-email.Body"] = withoutLink },
        });
        var (read, problems) = Read(json);
        Assert.Null(read);
        var problem = Assert.Single(problems);
        Assert.Equal("confirm-email.Body", problem.Key);
        Assert.Contains("{Link}", problem.Problem);
    }

    [Fact]
    public void Markup_is_kept_as_text()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["code"] = "it",
            ["page"] = new Dictionary<string, string> { ["Home.Title"] = "<script>alert(1)</script>" },
        });
        var (read, problems) = Read(json);
        Assert.Empty(problems);
        Assert.Equal("<script>alert(1)</script>", read!.Page["Home.Title"]);
    }

    [Theory]
    [InlineData("\"es\"")]
    [InlineData("\"\"")]
    [InlineData("\"../it\"")]
    [InlineData("5")]
    [InlineData("null")]
    public void Code_in_file_must_match_the_page_code(string code)
    {
        var (read, problems) = Read($$"""{ "code": {{code}}, "page": { "Home.Title": "Ciao" } }""");
        Assert.Null(read);
        Assert.Contains(problems, p => p.Key == "code");
    }

    [Fact]
    public void Code_is_needed_and_its_case_does_not_matter()
    {
        Assert.Contains(Read("""{ "page": { "Home.Title": "Ciao" } }""").Problems, p => p.Key == "code");
        Assert.Empty(Read("""{ "code": "PT-pt", "page": { "Home.Title": "Olá" } }""", "pt-PT").Problems);
    }

    [Fact]
    public void Too_long_a_text_is_a_problem()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["code"] = "it",
            ["page"] = new Dictionary<string, string> { ["Home.Title"] = new string('a', LanguageFile.MaxTextLength + 1) },
        });
        Assert.Equal("Home.Title", Assert.Single(Read(json).Problems).Key);
    }

    [Fact]
    public void Check_tells_one_text_at_a_time()
    {
        Assert.Null(LanguageFile.Check(_catalog, LanguageFile.Section.Page, "Bots.Question", "{1} + {0}", null));
        Assert.NotNull(LanguageFile.Check(_catalog, LanguageFile.Section.Page, "Bots.Question", "{0}", null));
        Assert.NotNull(LanguageFile.Check(_catalog, LanguageFile.Section.Email, "confirm-email.Subject", "Conferma", null));
    }
}
