# Plan 16a — Resident pages in six languages Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every resident page and every email a resident gets appears in English, German, Spanish, French, Portuguese or Russian, picked per request. Staff pages stay English.

**Architecture:** ASP.NET Core localisation with one shared resource class for resident text (`ResidentText`) and one for resident email defaults (`EmailText`), each with a `.resx` per language. A page filter on resident pages only sets `CurrentCulture`/`CurrentUICulture` from the pick order in the design; staff pages never get the filter, so they stay English. A resident's saved language lives in a launcher-owned JSON file; emails pick their language from it.

**Tech Stack:** .NET 10, Razor Pages, `Microsoft.Extensions.Localization` (in the shared framework), `.resx`, xunit v3, `Microsoft.AspNetCore.Mvc.Testing` (`LauncherFactory`), AngleSharp.

**Spec:** `docs/superpowers/specs/2026-11-10-languages-design.md`. Plan 16b (owner wording, Languages page, owner-added languages) follows and builds on the names below.

## Global Constraints

- Built-in languages, in this order: `en, de, es, fr, pt, ru`. `en` is the source and the last fallback for any missing text.
- `pt` is Brazilian Portuguese (*você*). German *Sie*, French *vous*, Spanish *usted*, Russian *вы*.
- In-world terms (avatar, region, estate, inventory, teleport, hypergrid, landmark, group) use the Firestorm viewer's own translation for that language.
- Each non-English `.resx` begins with an XML comment: drafted by AI; corrections from native speakers welcome; see `docs/languages.md`.
- Staff and owner pages, and staff emails (`test`, `signups-waiting`, `ticket-new`, `ticket-reply-guest`), stay English and ignore the language cookie.
- Language values from a cookie, query, header or form are matched against the offered list before use; never used as a path, never echoed unchecked.
- No inline script or style (`InlineScriptScanTests`); a new cookie is listed in `SiteFacts.Cookies`, new stored data in `SiteFacts.Stored`.
- `InvariantGlobalization` stays off. Anything written to files, logs or OpenSim keeps `CultureInfo.InvariantCulture`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Work only in `C:\Users\ystud\Desktop\CODE\DreamGrid-languages` on `feature/gridgate-languages`.

## Review Focus

1. **A junk `dg-lang` cookie, `lang=` value or `Accept-Language`** (`xx`, `../../etc`, 5 KB long, `*`, `de;q=abc`) → ignored, next step in the pick order, never an exception. Test in Task 3.
2. **`/welcome` is cached publicly for 60 s** → its language may come only from `lang=` and the grid default, never the cookie or `Accept-Language`, or one visitor's German page is served to the next. Test in Task 7.
3. **The picker's `returnUrl`** set to `https://evil.example/` or `//evil.example` → redirect to `/` instead. Test in Task 4.
4. **A staff member with `dg-lang=de`** opens a staff page → English; opens `/guest/...` → German. Test in Task 3.
5. **A translation that drops a required fill-in word** (German confirm-email body without `{Link}`) → caught by a test, not a resident's broken email. Test in Task 9.

---

### Task 1: Language list and settings

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Languages/Languages.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/LauncherSettings.cs` (add `LanguageSettings Languages { get; init; } = new();`)
- Test: `Launcher/DreamGrid.Launcher.Tests/LanguagesTests.cs`

**Interfaces:**
- Produces:
  - `public static class Languages` with `const string English = "en"`, `IReadOnlyList<string> BuiltIn` (`en, de, es, fr, pt, ru`), `string? Match(string? tag, IReadOnlyCollection<string> offered)`, `CultureInfo Culture(string code)`, `bool IsRightToLeft(string code)`, `string NativeName(string code)` (the language's own name, capitalised: "Deutsch", "Русский").
  - `public sealed record LanguageSettings { IReadOnlyList<string> Offered { get; init; } = Languages.BuiltIn; string Default { get; init; } = Languages.English; }`, with `IReadOnlyList<string> OfferedOrDefault()` that returns `Offered` filtered to known codes, always containing `Default`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("de", "de")] [InlineData("DE", "de")] [InlineData("de-AT", "de")] [InlineData("pt-BR", "pt")] [InlineData("pt-PT", "pt")]
[InlineData("ru-RU", "ru")] [InlineData("it", null)] [InlineData("", null)] [InlineData(null, null)]
[InlineData("../../etc", null)] [InlineData("*", null)]
public void Match_finds_an_offered_language(string? tag, string? expected) =>
    Assert.Equal(expected, Languages.Match(tag, Languages.BuiltIn));

[Fact] public void Match_ignores_a_language_that_is_not_offered() => Assert.Null(Languages.Match("de", ["en", "fr"]));
[Fact] public void Match_refuses_a_tag_longer_than_35_chars() => Assert.Null(Languages.Match("de-" + new string('a', 40), Languages.BuiltIn));
[Fact] public void NativeName_is_the_languages_own_name() { Assert.Equal("Deutsch", Languages.NativeName("de")); Assert.Equal("Русский", Languages.NativeName("ru")); }
[Fact] public void None_of_the_built_in_languages_is_right_to_left() => Assert.All(Languages.BuiltIn, c => Assert.False(Languages.IsRightToLeft(c)));
[Fact] public void Arabic_is_right_to_left() => Assert.True(Languages.IsRightToLeft("ar"));
[Fact] public void Default_settings_offer_all_six_with_English_default()
{ var s = new LanguageSettings(); Assert.Equal(Languages.BuiltIn, s.OfferedOrDefault()); Assert.Equal("en", s.Default); }
[Fact] public void OfferedOrDefault_always_contains_the_default() =>
    Assert.Contains("fr", new LanguageSettings { Offered = ["de"], Default = "fr" }.OfferedOrDefault());
[Fact] public void Settings_without_a_Languages_section_still_load() // an existing settings.json
    => /* load settings.json text from before this plan through the existing settings store; Assert.Equal("en", s.Languages.Default) */;
```

- [ ] **Step 2: Run** `dotnet test Launcher/DreamGrid.Launcher.Tests --filter LanguagesTests` — expect build failure (`Languages` not defined).
- [ ] **Step 3: Implement.** `Match`: trim, reject length > 35 or any character outside `[A-Za-z0-9-]`, lower-case, try the whole tag then the part before the first `-`, return it only if in `offered`. `Culture` uses `CultureInfo.GetCultureInfo(code)`; `pt` → `pt-BR` so dates and numbers are Brazilian. `NativeName` = `TextInfo.ToTitleCase` of `Culture(code).NativeName` up to the first ` (`.
- [ ] **Step 4: Run** the filter again — PASS.
- [ ] **Step 5: Commit** `feat(languages): the built-in language list and language settings`.

### Task 2: Residents' saved languages

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Languages/ResidentLanguages.cs`
- Modify: `Launcher/DreamGrid.Launcher/Program.cs` (register singleton with `Path.Combine(paths.Guests, "languages.json")`), `Launcher/DreamGrid.Launcher/Guests/SiteFacts.cs` (`Stored`: "The language each resident chose" → "the data folder, guests/languages.json")
- Test: `Launcher/DreamGrid.Launcher.Tests/ResidentLanguagesTests.cs`

**Interfaces:**
- Consumes: `Languages` (Task 1).
- Produces: `public sealed class ResidentLanguages(string file, ILogger<ResidentLanguages>? log = null)` with `string? Get(Guid principal)`, `void Set(Guid principal, string code)`, `void Remove(Guid principal)`, `int CountUsing(string code)` (16b's "residents using this"). Built on `JsonStateFile<Dictionary<Guid,string>>`, locked for thread safety like `PendingEmailChanges`.

- [ ] **Step 1: Tests:** `Set_then_Get_returns_it`; `Get_unknown_is_null`; `Set_twice_keeps_the_last`; `Remove_forgets`; `Survives_a_new_instance_on_the_same_file`; `CountUsing_counts_only_that_code`; `Set_refuses_a_code_that_is_not_a_language` (`Assert.Throws<ArgumentException>(() => store.Set(id, "../x"))`); `A_damaged_file_reads_as_empty_and_is_moved_aside_on_the_next_write` (as `PendingEmailChanges` does).
- [ ] **Step 2: Run** `--filter ResidentLanguagesTests` — FAIL.
- [ ] **Step 3: Implement** as described; `Set` accepts a code only if `CultureInfo.GetCultureInfo` knows it and it passes `Match`'s character rule (16b's owner-added codes must also be storable, so not limited to `BuiltIn`).
- [ ] **Step 4: Run** — PASS. **Delete hook:** add `Remove(principal)` to the account delete path in `AccountRemover`'s caller (`PeopleActions` delete) so a deleted person's choice goes too; one test in `PeopleAdminTests` (or the existing delete test file) asserting the entry is gone after delete.
- [ ] **Step 5: Commit** `feat(languages): residents' saved language, kept in the data folder`.

### Task 3: Picking the language for a resident page

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Languages/ResidentLanguageFilter.cs`, `Launcher/DreamGrid.Launcher/Languages/LanguagePick.cs`
- Modify: `Launcher/DreamGrid.Launcher/Program.cs` (`builder.Services.AddLocalization()`; add the filter to the resident pages with the same conventions used for `GuestSideOnly`: folders `/Guest`, `/Support`; pages `/Home`, `/Legal`, `/SignUp`, `/Welcome`)
- Test: `Launcher/DreamGrid.Launcher.Tests/LanguagePickTests.cs`

**Interfaces:**
- Consumes: `Languages`, `LanguageSettings`, `ResidentLanguages`, `GuestSignIn.Read(ClaimsPrincipal)`.
- Produces:
  - `public static class LanguagePick { const string Cookie = "dg-lang"; string For(HttpContext ctx, LanguageSettings s, ResidentLanguages saved, bool welcome); }` — order: `lang=` query (only when `welcome`), then for non-welcome pages only: cookie, signed-in guest's saved language, `Accept-Language` (by q-value, highest first, malformed entries skipped); finally `s.Default`. Every candidate goes through `Languages.Match(_, s.OfferedOrDefault())`.
  - `public sealed class ResidentLanguageFilter : IAsyncPageFilter` — sets `CultureInfo.CurrentCulture` and `CurrentUICulture` to `Languages.Culture(code)` and puts the code in `HttpContext.Items["dg-lang"]`; `public static string Current(HttpContext ctx)` reads it back (`Languages.English` when absent). Constructor takes `bool welcome`.

- [ ] **Step 1: Tests** (unit tests on `LanguagePick.For` with `DefaultHttpContext`, plus two through `LauncherFactory`):
  - `Cookie_wins_over_Accept_Language`; `Saved_language_wins_over_Accept_Language`; `Cookie_wins_over_saved_language`; `Accept_Language_by_q_value` (`"fr;q=0.5, de;q=0.9"` → `de`); `Malformed_Accept_Language_entries_are_skipped` (`"de;q=abc, fr"` → `fr`); `Nothing_matches_gives_the_default` (`Default = "es"` → `es`).
  - `Junk_cookie_is_ignored` for `xx`, `../../etc`, a 5 000-char value → falls to the next step, no exception.
  - `Welcome_uses_only_lang_query_and_default` (cookie `de`, `Accept-Language: fr`, no `lang=` → default `en`; `?lang=ru` → `ru`).
  - `Language_not_offered_is_skipped` (`Offered = ["en","de"]`, cookie `fr` → next step).
  - Factory: `Staff_page_stays_English_with_a_German_cookie` — owner signed in, cookie `dg-lang=de`, GET `/` (dashboard): `<html lang="en">` and `CultureInfo` untouched (a date on the dashboard keeps its current format). The matching guest-side test (`/guest/sign-in` → `<html lang="de">`) is in Task 4, which makes the layout print the language.
- [ ] **Step 2: Run** `--filter LanguagePickTests` — FAIL.
- [ ] **Step 3: Implement** both classes and the registration. Parse `Accept-Language` with `StringWithQualityHeaderValue.TryParseList`, capped at the first 10 entries.
- [ ] **Step 4: Run** — PASS (the guest-page factory test after Task 4).
- [ ] **Step 5: Commit** `feat(languages): pick a language for each resident page`.

### Task 4: Guest layout, picker and right-to-left

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Languages/ResidentText.cs` (empty marker class `public sealed class ResidentText;`), `Launcher/DreamGrid.Launcher/Resources/ResidentText.resx` (English), `Launcher/DreamGrid.Launcher/Pages/Language.cshtml(.cs)` (POST-only `/language`), `Launcher/DreamGrid.Launcher/Pages/Shared/_LanguagePicker.cshtml`, `Launcher/DreamGrid.Launcher/wwwroot/js/language.js`, `Launcher/DreamGrid.Launcher/wwwroot/lib/bootstrap/bootstrap.rtl.min.css` (Bootstrap **5.3.3**, the same version as `bootstrap.min.css`, from the official `bootstrap@5.3.3` package's `dist/css/`)
- Modify: `Pages/Shared/_GuestLayout.cshtml`, `Pages/_ViewImports.cshtml` (`@inject IStringLocalizer<ResidentText> T`), `Program.cs` (`AddLocalization(o => o.ResourcesPath = "Resources")`; `/Language` anonymous and `GuestSideOnly`), `Guests/SiteFacts.cs` (`("dg-lang", "Remembers the language a visitor picked for the guest pages. It lasts a year.")`), `wwwroot/css/site.css` (logical properties for any `left`/`right` rule used by guest pages)
- Test: `Launcher/DreamGrid.Launcher.Tests/LanguagePickerTests.cs`

**Interfaces:**
- Consumes: `LanguagePick.Cookie`, `ResidentLanguageFilter.Current`, `ResidentLanguages`, `Languages.NativeName/IsRightToLeft`.
- Produces: `IStringLocalizer<ResidentText>` available as `T` in every view; key naming `<Page>.<Thing>` (`Layout.Help`, `SignIn.Title`, `Common.Save`). Shared words used on several pages go under `Common.`.

- [ ] **Step 1: Tests:** `Layout_sets_lang_and_dir` (`de` → `lang="de" dir="ltr"`); `Layout_loads_rtl_bootstrap_for_a_right_to_left_language` (set `Offered` to include `ar` in the factory's settings and a cookie `ar` → link to `bootstrap.rtl.min.css`, `dir="rtl"`); `Picker_lists_offered_languages_in_their_own_names`; `Picker_is_hidden_when_one_language_is_offered`; `Posting_the_picker_sets_the_cookie_and_returns` (POST `/language` `code=de&returnUrl=/guest/sign-in` → 303 to `/guest/sign-in`, `Set-Cookie: dg-lang=de; … samesite=lax; httponly`, max-age 1 year); `Picker_refuses_an_unknown_code` (→ 400, no cookie); `Picker_never_redirects_off_site` (`https://evil.example/`, `//evil.example`, `/\evil.example` → `/`); `Picker_saves_the_choice_for_a_signed_in_guest` (`/__test/guest-signin` then POST → `ResidentLanguages.Get` is `de`); `Picker_needs_the_antiforgery_token`; `SiteFacts_lists_dg_lang` (extend the existing `SiteFactsTests` cookie check if it compares names to cookies set).
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement.** Layout: `<html lang="@code" dir="@(rtl ? "rtl" : "ltr")">`, `bootstrap.rtl.min.css` instead of `bootstrap.min.css` when rtl, the picker partial in `.guest-actions`, and move the layout's own words (`Back to staff pages`, `Ask for help`) to `Layout.*` keys. Picker: a `<form method="post" action="/language">` with a `<select name="code">`, hidden `returnUrl` = current path and query, and a submit button; `language.js` submits on `change` and hides the button. Return URL accepted only if `Url.IsLocalUrl` and it doesn't start with `//` or `/\`.
- [ ] **Step 4: Run** — PASS, plus Task 3's guest-page factory test.
- [ ] **Step 5: Commit** `feat(languages): language picker and right-to-left layout for guest pages`.

### Tasks 5–7: Move resident page text into `ResidentText`

Same steps for each task; each owns the page group listed. In every page in the group:
- every visible English string, `title`, `placeholder`, `aria-label`, `alt` and `ViewData["Title"]` becomes `@T["<Page>.<Thing>"]`, with the English added to `ResidentText.resx`;
- every message a page model builds (errors, notices in `TempData`, validation messages) comes from `IStringLocalizer<ResidentText>` injected into the model; text with values uses `T["Key", value]` and `{0}`-style placeholders, never string concatenation;
- dates use `ToString("D"/"g", CultureInfo.CurrentCulture)` or the page's existing format with the current culture; `RegionAdvice.FileSize` gets an overload `FileSize(long bytes, IFormatProvider culture)` and resident pages pass `CultureInfo.CurrentCulture` (staff callers unchanged).

**Per task, Step 1:** this is a move of text, not a change of behaviour, so the group's existing page tests are the safety net: run them first and note they pass. Add `<Group>KeysTests.ShowsEveryKeyItUses` — for each page in the group, render it in English and assert no output contains a raw key pattern `[A-Z][a-zA-Z]+\.[A-Z][a-zA-Z]+` (a key with no English entry renders as its own name). German assertions arrive in Task 9.

- **Task 5 — sign-in and accounts:** `Pages/Guest/SignIn`, `SignInCode`, `TwoFactor`, `ForgotPassword`, `ResetPassword`, `Confirm`, `ConfirmEmail`, `SignOut`, `Connect`; partial `_LoginAddress`.
- **Task 6 — joining:** `Pages/Home`, `Pages/SignUp`, `Pages/Guest/Index`, `Terms`, `Regions`, `Account` (text only; the language choice is Task 10); partials `_SignUpRows`, `_BotFields`, `_JoinFound`, `_HubLinks` if used by guest pages; `ImageCaptcha`/`ProofOfWork` messages shown to residents.
- **Task 7 — help, legal and welcome:** `Pages/Support/*`, `Pages/Legal`, `_LegalLinks`, `_PlainHttpWarning` if shown on guest pages, `Pages/Welcome` and `_SplashLayout`/`_SplashBlock(s)` built-in text (counts, labels; the owner's block text is untouched). `/welcome` uses `ResidentLanguageFilter(welcome: true)`. Add `Welcome_ignores_the_cookie_and_Accept_Language` (factory: cookie `de`, `Accept-Language: de` → `lang="en"`; `?lang=de` → `lang="de"`) and `Welcome_is_still_publicly_cacheable_for_60_seconds`. Legal fallback: when the owner's legal text for the current language is missing, show the default language's text with `Legal.ShownInDefault` above it ("This page is shown in {0}." with the default language's native name); `LegalPages.Get(s, page, language)` gains the language parameter, falling back to `s.Languages.Default` then `en`. Test `Legal_page_falls_back_to_the_default_language_with_a_note`.

**Steps 2–5 per task:** run the group's tests (`--filter` on the group's existing test classes, e.g. `GuestSignInTests`) — PASS before and after; build with warnings as errors; commit `feat(languages): <group> text in ResidentText`.

### Task 8: Hard-coded English scan

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Tests/ResidentTextScanTests.cs`

**Interfaces:**
- Consumes: the resident page list (same folders/pages as Task 3's filter).

- [ ] **Step 1: Write the test** `No_hard_coded_English_on_resident_pages`: for each resident `.cshtml` (and the guest-layout partials listed in Tasks 4–7), remove Razor comments `@* … *@`, `@{ … }` blocks, `@( … )` and `@identifier…` expressions, then parse with AngleSharp and fail on any text node or `title`/`placeholder`/`aria-label`/`alt` value containing `[A-Za-z]{2,}`. An explicit allow-list in the test holds the exceptions (only names that are the same in every language, such as `GridGate`, `Firestorm`, `OpenSim`). The failure message lists each file and its offending text.
- [ ] **Step 2: Run** — PASS (Tasks 5–7 are done). Plant `<p>Hello</p>` in a guest page locally, confirm it FAILS naming the file, remove it.
- [ ] **Step 3: Commit** `test(languages): resident pages carry no hard-coded English`.

### Task 9: Emails per language and the five translations

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Email/EmailText.cs` (marker), `Launcher/DreamGrid.Launcher.Core/Resources/EmailText.resx` (English subjects and bodies for the resident templates, keys `<id>.Subject`, `<id>.Body`, plus `ExpiresIn.30m`, `ExpiresIn.24h`, `Reason.Copyright|Illegal|Rules|Other`), `Resources/EmailText.{de,es,fr,pt,ru}.resx`, `Launcher/DreamGrid.Launcher/Resources/ResidentText.{de,es,fr,pt,ru}.resx`
- Modify: `EmailTemplates.cs`, `EmailSender.cs`, every `sender.Queue(...)` caller (`AccountChanges`, `PasswordResets`, `SignUpApprovals`, `SignUpNotices`, `SignUps`, `SupportTickets`, `TicketSweep`, `PeopleActions`, `TakedownActions`), `AccountChanges.ExpiresIn`/`PasswordResets.ExpiresIn`/`SignUps.ExpiresIn` (become keys), `TakedownRules.ReasonWords` callers that fill `{Reason}`
- Create: `Launcher/DreamGrid.Launcher/Email/EmailLanguage.cs`
- Test: `EmailTemplatesTests.cs` (extend), `Launcher/DreamGrid.Launcher.Tests/TranslationsTests.cs`, `Launcher/DreamGrid.Launcher.Tests/EmailLanguageTests.cs`

**Interfaces:**
- Consumes: `Languages`, `LanguageSettings`, `ResidentLanguages`, `ResidentLanguageFilter.Current`.
- Produces:
  - `EmailTemplates.IsStaffEmail(string id)` — true for `test`, `signups-waiting`, `ticket-new`, `ticket-reply-guest`.
  - `EmailTemplates.Default(string id, string language)` — resident templates read `EmailText` in that culture (resx fallback to English); staff templates ignore `language`. The old `Default(string id)` stays as `Default(id, Languages.English)`.
  - `EmailTemplates.Current(LauncherSettings s, string id, string language)` — owner's wording in `language` → built-in in `language` → English (design §8: never the owner's wording in another language).
  - `EmailTemplates.Words(string key, string language)` for `ExpiresIn.*` and `Reason.*` fill-in values.
  - `EmailSender.Queue(string templateId, string language, string to, string? avatarName, IReadOnlyDictionary<string,string> values, TimeSpan? validFor = null)` — `language` is required, so no caller is missed.
  - `public sealed class EmailLanguage(Func<LauncherSettings> settings, ResidentLanguages saved)` with `string ForResident(Guid principal)` (saved if offered, else default) and `string ForRequest(HttpContext ctx)` (`ResidentLanguageFilter.Current` if offered, else default).

- [ ] **Step 1: Tests**
  - `TranslationsTests.Every_language_has_every_key` for `ResidentText` and `EmailText` (read the `.resx` files as XML; compare key sets to English).
  - `TranslationsTests.Placeholders_match_English` — same `{0}`… set per key in `ResidentText`; same `{Word}` set per key in `EmailText`.
  - `TranslationsTests.Required_words_are_in_every_language` — for each resident template and language, `EmailTemplates.MissingWords(id, subject, body)` is empty. (Review Focus 5.)
  - `TranslationsTests.Each_translation_starts_with_the_AI_draft_note`.
  - `EmailTemplatesTests.Default_in_German_is_German` (`confirm-email`, `de` → subject contains `E-Mail`); `Staff_email_is_English_whatever_the_language`; `Owner_wording_in_another_language_is_not_used` (owner wrote `en` only → `de` gets the built-in German).
  - `EmailLanguageTests.Saved_language_is_used`; `Saved_language_no_longer_offered_gives_the_default`; `No_saved_language_gives_the_default`; `ForRequest_uses_the_page_language`.
  - Factory: `Forgot_password_from_a_German_page_sends_German` (FakeTransport from `EmailPageTests`); `Staff_reset_link_goes_in_the_residents_saved_language`; `Sign_up_confirmation_says_24_Stunden_in_German`.
- [ ] **Step 2: Run** `--filter "TranslationsTests|EmailTemplatesTests|EmailLanguageTests"` — FAIL.
- [ ] **Step 3: Implement** the interfaces; move the resident templates' English text from the `Templates` dictionary into `EmailText.resx` (the dictionary keeps `Title`, `Words`, `Required`); change callers: page-driven sends use `ForRequest`, staff-driven and background sends use `ForResident`, staff emails pass `Languages.English`. The email wording page keeps working on `en` (16b adds its language selector).
- [ ] **Step 4: Write the five translations** of `ResidentText` and `EmailText` per Global Constraints, then run the filter — PASS, then the whole test project — PASS.
- [ ] **Step 5: Commit** in two commits: `feat(languages): emails go out in the resident's language` and `feat(languages): German, Spanish, French, Portuguese and Russian (AI-drafted)`.

### Task 10: Language on the account page, at sign-up, and on tickets

**Files:**
- Modify: `Pages/Guest/Account.cshtml(.cs)` (new `OnPostLanguageAsync`), `Guests/SignUps.cs` (save the page language when the account is made), `Core/Tickets/Ticket.cs` (`string? Language = null` as the last record parameter), `Guests/SupportTickets.cs` (set from `EmailLanguage.ForRequest`), ticket email callers (`SupportTickets`, staff reply path, `TicketSweep`) — ticket emails use `ticket.Language` if offered, else default
- Test: `GuestAccountTests.cs` (extend), `SignUpTests` (extend), `SupportTicketTests` (extend)

**Interfaces:**
- Consumes: `ResidentLanguages`, `EmailLanguage`, `LanguagePick.Cookie`.

- [ ] **Step 1: Tests:** `Account_page_shows_the_saved_language_selected`; `Changing_language_on_the_account_page_saves_it_and_sets_the_cookie`; `Account_language_post_refuses_an_unknown_code`; `Sign_up_from_a_Spanish_page_saves_Spanish` (open mode and email-confirm mode); `Ticket_remembers_the_page_language` and `Ticket_staff_reply_email_is_in_the_tickets_language`; `Old_ticket_files_without_Language_still_load` (existing JSON fixture without the property → `null`, email in default).
- [ ] **Step 2: Run** — FAIL. **Step 3: Implement.** **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `feat(languages): residents choose their language; sign-up and tickets remember it`.

### Task 11: Documentation, work list and the owner's check

**Files:**
- Create: `docs/languages.md` (how a language is picked; how to correct a translation; how to add a built-in language: copy the two `.resx`, translate, add the code to `Languages.BuiltIn`, run `TranslationsTests`)
- Modify: `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md` (tick 1.5's items done by 16a; 1.4 "localised templates" and 2.4 "choose language" ticked; note 16b for the rest), spec section 4.8 / user manual pages that list GridGate features (one line on languages)

- [ ] **Step 1:** Write `docs/languages.md` and the work-list updates.
- [ ] **Step 2:** Full test run `dotnet test Launcher` — PASS; `dotnet build -warnaserror` — no warnings.
- [ ] **Step 3: Platform check on the Debian 12 VM** (package built from this branch, installed per the VM notes): open `/guest/sign-in` with each of the six languages from the picker; dates on the account page show in German format for `de`; a forgot-password email arrives in Russian for a resident who saved `ru`; `/welcome?lang=fr` shows French labels in Firestorm. Same on Windows with `dotnet run`. Record results in the work list.
- [ ] **Step 4: Commit** `docs(languages): how languages work and how to correct or add one`.
