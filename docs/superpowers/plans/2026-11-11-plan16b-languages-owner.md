# Plan 16b — The owner's languages and wording Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The grid owner chooses which languages residents are offered and the grid's default, rewords the tone text of resident pages per language, writes email and legal wording per language, and adds languages GridGate doesn't ship by translating them on the site.

**Architecture:** Builds on plan 16a (branch `feature/gridgate-languages`, PR 65). Owner overrides live in `settings.json`, next to the existing email and legal wording, keyed by language. Owner-added languages live as one JSON file per language in `<data>/languages/`. A layered localiser serves owner tone override → owner-added language text → built-in `.resx` → English. `OfferedLanguages` becomes the one place that knows which languages exist (built-in plus owner-added).

**Tech Stack:** .NET 10, Razor Pages, `Microsoft.Extensions.Localization`, xunit v3, `LauncherFactory`, AngleSharp.

**Spec:** `docs/superpowers/specs/2026-11-10-languages-design.md` (sections 6, 7, 8, 12). Plan 16a: `docs/superpowers/plans/2026-11-10-plan16a-languages.md`. How 16a works: `docs/languages.md`.

## Global Constraints

- The new pages are owner-only staff pages, under `/security` (owner policy, `GuestSideOnly`), in English. They add no `ResidentText` keys. Any new resident-visible key needs all six built-in languages (`TranslationsTests`).
- Tone text is plain text. A blank line starts a new paragraph. `{grid}` is the only placeholder. It is always HTML-encoded.
- The lookup order per key is: owner tone override for this language → built-in or owner-added text for this language → English.
- Only overrides are stored, so built-in wording improvements still reach every key the owner didn't touch.
- An owner-added language can't be offered until every text is translated (page texts and resident email texts).
- After a GridGate update adds texts, an owner-added language that is already on stays on. The new texts show in English, and both the Languages page and the dashboard say "<Language>: N new texts to translate".
- Language codes come only from the system's culture list (`CultureInfo.GetCultures(CultureTypes.AllCultures)`, matched by `Languages.Match`'s character rule). A file name is built only from a matched code, never from the typed input.
- Uploaded language files are JSON with string values only, at most 1 MB. Keys must exist in English, and placeholders must match the English key's.
- The default language can't be turned off. A built-in language can't be deleted, only turned off.
- Turning languages on or off, changing the default, adding, uploading or deleting a language, and saving or resetting tone text, email wording or legal text are all written to the audit log (who, what, when, from where), never with the text itself.
- Staff pages, logs, audit and files stay English and invariant (16a).
- Commit messages end with a blank line followed by `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Work only in `C:\Users\ystud\Desktop\CODE\DreamGrid-languages-16b` on `feature/gridgate-languages-owner`.

**Legal versions (my call; the design leaves it open):**
- The default language's legal text is the source.
- A translation of a page is shown only while its `Version` equals the source's current `Version`.
- Saving a translation stamps it with the source's current `Version`. Saving the source bumps `Version` as today, so older translations are hidden until they are updated, and residents see the source with `Legal.ShownInDefault`.
- Terms acceptance stays per version number, so residents never re-accept because of a translation.
- The default can change only to a language whose required legal pages are up to date, meaning each one's `Version` equals the source's. The version numbers carry over, so acceptances stay valid.

## Review Focus

1. **An upload with markup or tricks.** Values such as `<script>`, `{0}{1}{99}`, a nested object or an array, `null`, a 5 MB file, a non-UTF-8 file, or unknown keys must each be refused with a plain message naming the problem. Nothing is half saved, and no markup ever renders. Test in Task 5.
2. **A typed language code that isn't a language.** `../x`, `en`, `EN-us`, `xx`, `de` (built-in) and an empty string must not create a file. Built-in codes can't be added, and there is no path escape. Test in Task 5.
3. **Owner tone text with `{0}`, `{`, `}`, or HTML.** It renders literally and encoded, with only `{grid}` replaced, and never throws a `FormatException` in `T["Key", gridName]`. Test in Task 3.
4. **Default-language edge cases.** Turning off the default, deleting the default or an offered owner-added language, or switching to a default whose Terms translation is out of date must each be refused with a clear message. Residents who saved a language that is then turned off get the default in pages and emails. Test in Task 6.
5. **New texts after an update.** An owner-added language file missing keys that were added later must still load. The missing texts show in English, the count is right, and nothing throws. Test in Task 2 (count) and Task 3 (render).

---

### Task 1: Owner-added language store

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Languages/OwnerLanguages.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/LauncherSettings.cs` (`LanguageSettings.OfferedOrDefault(IReadOnlyCollection<string> added)`; drop the built-in-only filter for codes in `added`), `Launcher/DreamGrid.Launcher.Core/LauncherPaths.cs` (`public string Languages => Path.Combine(Data, "languages");`), `Launcher/DreamGrid.Launcher/Guests/SiteFacts.cs` (`Stored`: "Languages the owner added, and their translations", "languages/<code>.json.")
- Test: `Launcher/DreamGrid.Launcher.Tests/OwnerLanguagesTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record OwnerLanguage(string Code, IReadOnlyDictionary<string, string> Page, IReadOnlyDictionary<string, string> Email, DateTimeOffset Saved);`
  - `public sealed class OwnerLanguages(string folder, TimeProvider time, ILogger<OwnerLanguages>? log = null)`, namespace `DreamGrid.Launcher.Core`, with these members:
    - `IReadOnlyList<string> Codes()`
    - `OwnerLanguage? Get(string code)`
    - `void Save(OwnerLanguage language)`: atomic; throws `ArgumentException` for a code that isn't a culture or is built in
    - `bool Delete(string code)`
    - `static string? CodeFor(string typed)`: returns the normalised culture name if the system knows it and it isn't built in, else null
  - The files are cached in memory and reloaded when they change, under a lock. A damaged file is moved aside and reads as absent, as in `PendingEmailChanges`.

- [ ] **Step 1: Tests:**
  - `Save_then_Get_round_trips`
  - `Codes_lists_saved_languages_sorted`
  - `Delete_removes_the_file`
  - `Survives_a_new_instance`
  - `CodeFor` cases: `it` → `it`; `IT` → `it`; `pt-PT` → `pt-PT`; `ar` → `ar`; `de` → null (built in); `en` → null; `xx` → null; `../x` → null; `""` → null
  - `Save_refuses_a_built_in_code`
  - `Save_refuses_a_code_that_is_not_a_culture`
  - `A_damaged_file_reads_as_absent_and_is_moved_aside`
  - `The_file_name_comes_from_the_code_only`: saving `it` writes exactly `<folder>/it.json`
  - `OfferedOrDefault_keeps_added_codes`: `Offered = ["en","it"]`, `added = ["it"]` → contains `it`; with `added = []` → no `it`
- [ ] **Step 2: Run** `dotnet test Launcher/DreamGrid.Launcher.Tests --filter OwnerLanguagesTests` and expect FAIL.
- [ ] **Step 3: Implement.** Update the callers of `OfferedOrDefault()` to pass the store's codes. (After 16a's final fix they all go through `OfferedLanguages`, so Task 2 does the wiring and this task gives the parameter a default of `[]`.) Register the singleton in `Program.cs` with `paths.Languages`.
- [ ] **Step 4: Run** the filter again; expect PASS.
- [ ] **Step 5: Commit** `feat(languages): the store for languages the owner adds`.

### Task 2: One list of languages, and what each one is missing

**Files:**
- Modify: `Launcher/DreamGrid.Launcher/Languages/OfferedLanguages.cs`
- Create: `Launcher/DreamGrid.Launcher/Languages/LanguageCatalog.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/LanguageCatalogTests.cs`

**Interfaces:**
- Consumes: `OwnerLanguages` (Task 1), the English `ResidentText.resx`, and `EmailText.resx` (through the Core `ResourceManager` in `EmailTemplates`).
- Produces:
  - `OfferedLanguages(Func<LauncherSettings> settings, OwnerLanguages added)`, with these members:
    - `Current()`: offered ∩ (built-in ∪ added) ∩ `HasCulture`
    - `Default()`
    - `All()`: built-in plus added, offered or not
    - `bool IsBuiltIn(string code)`
  - The static `Of`/`DefaultOf` are removed. Every caller uses the instance.
  - `public sealed class LanguageCatalog(OwnerLanguages added)`, with these members:
    - `IReadOnlyList<string> PageKeys()`: every English `ResidentText` key, read once from the embedded neutral resources
    - `IReadOnlyList<string> EmailKeys()`: every English `EmailText` key for resident templates plus `ExpiresIn.*` and `Reason.*`
    - `string English(string key)` and `string EnglishEmail(string key)`
    - `int Missing(string code)`: page keys plus email keys with no non-blank value in that language's file; 0 for built-in languages
    - `IReadOnlyList<string> MissingKeys(string code)`

- [ ] **Step 1: Tests:**
  - `PageKeys_match_the_English_resx`: same set as an XML read of `ResidentText.resx`
  - `EmailKeys_exclude_staff_templates`
  - `Missing_counts_every_untranslated_text`: an added `it` with 2 page values gives total − 2
  - `Missing_ignores_unknown_keys_in_the_file`
  - `Missing_is_zero_for_built_in`
  - `A_file_from_before_new_keys_counts_them_missing` (Review Focus 5)
  - `Current_includes_an_added_offered_language`
  - `Current_skips_an_offered_code_with_no_file`
  - `Default_falls_back_to_English_when_the_default_file_is_gone`
- [ ] **Step 2: Run** the filters `LanguageCatalogTests|LanguagePickTests|EmailLanguageTests|LanguagePickerTests`; expect FAIL.
- [ ] **Step 3: Implement.** Replace the static `Of` callers. Keep 16a behaviour identical when there are no added languages.
- [ ] **Step 4: Run** the same filters and the full project once; expect PASS.
- [ ] **Step 5: Commit** `feat(languages): one list of languages, built in and added, with what each one misses`.

### Task 3: Layered localiser and tone text

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Languages/ToneText.cs`, `Launcher/DreamGrid.Launcher/Languages/ResidentTextLocalizer.cs`
- Modify:
  - `Launcher/DreamGrid.Launcher.Core/Settings/LauncherSettings.cs`: add `IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Wording` on a new `WordingSettings` record, or directly on `LauncherSettings`, `[AllowNull]` like `Languages`
  - `Program.cs`: register `IStringLocalizer<ResidentText>` as `ResidentTextLocalizer`, wrapping the factory-made one
  - `wwwroot/css/site.css`: `.tone-text { white-space: pre-line; }`
  - the views that show a tone key: add `class="tone-text"` to the element
- Test: `Launcher/DreamGrid.Launcher.Tests/ToneTextTests.cs`

**Interfaces:**
- Consumes: `OwnerLanguages`, `ResidentLanguageFilter.Current`, settings.
- Produces:
  - `public static class ToneText` with:
    - `IReadOnlyList<string> Keys`, exactly: `Home.Title`, `Home.Intro`, `SignIn.Intro`, `SignUp.Intro`, `ForgotPassword.Intro`, `ResetPassword.Intro`, `Guest.Title`, `Support.Title`, `Support.Intro`, `Connect.Help`. Check that each exists in `ResidentText.resx`, and that its English has no placeholder or only the grid name as `{0}`. Drop any that fails, and say so in the report.
    - `string Title(string key)`, a short English label for the editor, e.g. "Front page: heading"
    - `string ToFormat(string ownerText)`: escapes `{` and `}` to `{{`/`}}`, then turns `{grid}` into `{0}`
  - `public sealed class ResidentTextLocalizer(IStringLocalizerFactory factory, Func<LauncherSettings> settings, OwnerLanguages added) : IStringLocalizer<ResidentText>`. For the current UI culture's language (`CultureInfo.CurrentUICulture`, matched to built-in or added codes):
    1. if `key ∈ ToneText.Keys` and `settings().Wording[lang][key]` is non-blank, return `ToFormat(ownerText)`;
    2. else if `lang` is an added language and its `Page[key]` is non-blank, return that;
    3. else return the built-in localiser's result, where `.resx` already falls back to English.

    `GetAllStrings` follows the same layers. `ResourceNotFound` is true only when no layer has the key.

- [ ] **Step 1: Tests:**
  - `Owner_override_wins_in_its_language`: owner `de` `Home.Intro` = "Willkommen bei {grid}!" renders on `/home` with `dg-lang=de` as "Willkommen bei <grid name>!"
  - `Owner_override_does_not_leak_to_other_languages`: `fr` still shows the built-in French
  - `Blank_override_means_built_in`
  - `Override_with_braces_and_html_renders_literally` (Review Focus 3): "Hi {0} {x} <b>&" renders encoded and literal, with no exception
  - `Paragraphs_keep_their_line_breaks`: the element has class `tone-text` and the text keeps `\n\n`
  - `Added_language_text_is_used`: `it` file `SignIn.Title` = "Accedi", with `it` offered and the cookie `it`, gives the heading "Accedi"
  - `Added_language_missing_key_falls_back_to_English` (Review Focus 5)
  - `Non_tone_keys_ignore_owner_wording`: an override stored for `SignIn.Title` is not used
  - `Staff_pages_are_unaffected`
- [ ] **Step 2: Run** `--filter ToneTextTests` and expect FAIL. **Step 3: Implement.** **Step 4: Run** the filter and the 16a keys and scan tests; expect PASS.
- [ ] **Step 5: Commit** `feat(languages): the owner's tone text and added languages reach resident pages`.

### Task 4: Page wording editor

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Pages/Security/Wording.cshtml(.cs)` (`@page "/security/wording"`)
- Modify:
  - `Navigation/StaffMenu.cs`: in the "Guest pages" group, add `(owner, new("Page wording", "/security/wording", "/security/wording"))` and `(owner, new("Languages", "/security/languages", "/security/languages"))` after "Legal pages"
  - `Pages/GuestPages/Index.cshtml` hints:
    - "/security/wording" → "Reword the headings and opening words of the guest pages, in each language."
    - "/security/languages" → "Choose the languages residents can use, and add your own."
  - `Program.cs`: `GuestSideOnly` on both pages
- Test: `Launcher/DreamGrid.Launcher.Tests/WordingPageTests.cs`

**Interfaces:**
- Consumes: `ToneText`, `OfferedLanguages.All()`, the built-in localiser (via `IStringLocalizerFactory` with a culture), `AuditLog`, `IGridController.UpdateSettings`.

**The page:**
- A language selector (`?lang=`), limited to `OfferedLanguages.All()`. Unknown values fall back to the default.
- One row per tone key, each with:
  - the title;
  - the built-in text in that language (read-only);
  - a textarea with the owner's text;
  - Save and "Put the original back" buttons.
- A "No longer used" list: stored keys not in `ToneText.Keys`, each with its text shown read-only and a Delete button.
- Limits: 2,000 characters per text. Line endings are normalised to `\n`.
- Audit actions: `page-wording-saved` and `page-wording-reset`, with the target `<lang>/<key>` and no text.

- [ ] **Step 1: Tests:**
  - `Owner_only`: admin gets 403 or a redirect, as the existing owner pages do
  - `Shows_built_in_text_for_the_chosen_language`
  - `Save_stores_only_an_override`: saving text equal to the built-in removes the override
  - `Reset_removes_the_override`
  - `Unknown_language_falls_back_to_default`
  - `Too_long_is_refused`
  - `Audited_without_text`
  - `No_longer_used_overrides_are_listed_and_deletable`
  - `Menu_shows_both_items_to_the_owner_only`
- [ ] **Step 2: Run** and expect FAIL. **Step 3: Implement**, following `EmailWording.cshtml(.cs)` for layout, the save pattern and audit. **Step 4: Run**; expect PASS, plus `InlineScriptScanTests`.
- [ ] **Step 5: Commit** `feat(languages): the owner rewords guest pages in each language`.

### Task 5: Languages page: add, translate, upload, download, delete

**Files:**
- Create:
  - `Launcher/DreamGrid.Launcher/Pages/Security/Languages.cshtml(.cs)` (`@page "/security/languages"`)
  - `Launcher/DreamGrid.Launcher/Pages/Security/Translate.cshtml(.cs)` (`@page "/security/languages/{code}"`)
  - `Launcher/DreamGrid.Launcher/Languages/LanguageFile.cs` (parse, check, write)
- Test: `Launcher/DreamGrid.Launcher.Tests/LanguagesPageTests.cs`, `Launcher/DreamGrid.Launcher.Tests/LanguageFileTests.cs`

**Interfaces:**
- Consumes: `OwnerLanguages`, `LanguageCatalog`, `OfferedLanguages`, `ResidentLanguages.CountUsing`, `AuditLog`.
- Produces:
  - `public sealed record LanguageFileProblem(string Key, string Problem);`
  - `public static class LanguageFile` with:
    - `const int MaxBytes = 1_048_576`
    - `(OwnerLanguage? Language, IReadOnlyList<LanguageFileProblem> Problems) Read(Stream json, string code, LanguageCatalog catalog, TimeProvider time)`
    - `byte[] Write(OwnerLanguage language, LanguageCatalog catalog)`
  - The format is `{ "code": "it", "page": { key: text }, "email": { key: text } }`. `Write` includes every catalogue key, with English beside it under `"english"`, so a translator works from one file. `Read` ignores `"english"`.

**`/security/languages` (owner):**
- A table of every language with these columns:
  - name (`Languages.NativeName`) and code;
  - built in / added;
  - an Offered checkbox;
  - a Default radio button;
  - "Missing: N" for added languages, linking to the translate page;
  - "Residents using it: N" (`CountUsing`);
  - Delete, for added languages only.
- Save the offered set and the default together in one form. The validation rules are in Task 6, which owns them; this task renders the form and posts to Task 6's handler names.
- An "Add a language" form: a select of `CultureInfo.GetCultures(AllCultures)` entries that `OwnerLanguages.CodeFor` accepts, shown as native name plus English name. Adding one creates an empty file and opens the translate page.

**`/security/languages/{code}` (owner, added languages only):**
- A form with one row per key: English beside a textarea. Page keys and email keys are in separate sections, and email bodies use larger textareas.
- Save keeps partial work.
- "Download as a file" (`Content-Disposition: attachment; filename="<code>.json"`).
- "Upload a file" (`multipart`, `MaxBytes`). An upload replaces the given keys and keeps the others. If there are any problems, nothing is saved and the problem list is shown.
- Each value must match its English key's placeholders: the same `{0}`-style set for page keys, and the same `{Word}` set and required words (`EmailTemplates.MissingWords`) for email keys. A row that fails is shown with its problem and the text isn't saved; other rows save.
- Delete: needs confirmation, and refuses an offered language (turn it off first). It shows the residents count, and saved choices of that language are left to fall back to the default (16a picks).
- Audit actions: `language-added`, `language-translated`, `language-uploaded`, `language-downloaded`, `language-deleted`, with the target `<code>`.

- [ ] **Step 1: Tests (`LanguageFileTests`, Review Focus 1):**
  - `Round_trip`
  - `Unknown_keys_are_problems`
  - `Non_string_values_are_problems`: number, null, array, object
  - `Oversize_is_refused`
  - `Not_JSON_is_refused`
  - `Not_UTF8_is_refused`
  - `Placeholder_mismatch_is_a_problem`
  - `Missing_required_email_word_is_a_problem`
  - `Markup_is_kept_as_text`: `<script>` is stored as is; render tests prove it's encoded
  - `Code_in_file_must_match_the_page_code`
- [ ] **Step 2: Tests (`LanguagesPageTests`, Review Focus 2):**
  - `Owner_only`
  - `Lists_built_in_and_added_with_missing_counts`
  - `Add_creates_an_empty_file_and_redirects`
  - `Add_refuses_built_in_unknown_and_path_codes` (`../x`, `en`, `EN-us`, `xx`, `de`, `""`): no file is created
  - `Translate_page_saves_partial_work`
  - `Bad_row_is_shown_others_save`
  - `Download_has_every_key_with_English`
  - `Upload_with_problems_saves_nothing`
  - `Upload_merges`
  - `Delete_refuses_an_offered_language`
  - `Delete_needs_confirmation_and_shows_residents_count`
  - `Every_action_audited_without_text`
  - `Translate_page_404s_for_a_built_in_or_unknown_code`
- [ ] **Step 3: Run** and expect FAIL. **Step 4: Implement.** **Step 5: Run**; expect PASS, plus `InlineScriptScanTests`.
- [ ] **Step 6: Commit** `feat(languages): the owner adds a language and translates it on the site`.

### Task 6: Offered languages and the default, with the legal rule

**Files:**
- Modify:
  - `Pages/Security/Languages.cshtml.cs`: `OnPostChoose(string[] offered, string @default)`
  - `Core/Guests/LegalPages.cs`:
    - `Missing(s)` checks `s.Languages.Default`, not `"en"`
    - `Find` shows a translation only if its `Version` equals the source's
    - new `UpToDate(LauncherSettings s, string language)`
  - `Pages/Security/Legal.cshtml(.cs)`: a language selector; saving a translation stamps the source's current `Version`; the source bumps as today; an "out of date" badge on translations whose `Version` differs
  - `Pages/Security/EmailWording.cshtml(.cs)`: a language selector; save, reset, preview and test-send in the chosen language; the 16a note replaced by "This wording is used for emails in <Language>."; the per-template warning is removed
  - `Core/Email/EmailTemplates.cs`: `Current`/`Default` take the added language's email text when the language isn't built in: `Default(string id, string language, OwnerLanguages? added)`, falling back to English per key
- Test: `LanguagesPageTests` (extend), `LegalPagesTests` (extend), `EmailWordingPageTests` (extend), `EmailTemplatesTests` (extend)

**Interfaces:**
- Consumes: `OfferedLanguages`, `LanguageCatalog.Missing`, `LegalPages`, `ResidentLanguages`.
- Produces:
  - `LegalPages.UpToDate(LauncherSettings s, string language) -> IReadOnlyList<string>`: the required pages whose text in `language` is missing or whose `Version` differs from the source's
  - the email wording page's `?lang=`

**Rules for `OnPostChoose`, in this order (Review Focus 4):**
1. Every code must be in `OfferedLanguages.All()`. Otherwise answer 400.
2. The default must be among the offered. Otherwise: "Turn on <Language> before making it the default."
3. An added language being turned on must have `Missing == 0`. Otherwise: "<Language> still has N texts to translate." This doesn't apply to an added language that was already on.
4. A new default must have `UpToDate(s, newDefault)` empty when sign-up is open (`SignUpGateOpen`). Otherwise: "Write the Terms, Privacy and Cookies pages in <Language> first", with links to the legal editor for that language. When the default changes, nothing is renumbered.
5. Turning off a language that residents use shows a confirmation with the count before saving.

Save the result, then audit `languages-offered-changed` (target: the code list) and `language-default-changed` (target: from → to).

- [ ] **Step 1: Tests:**
  - `Default_must_be_offered`
  - `Turning_off_the_default_is_refused`
  - `Incomplete_added_language_cannot_be_turned_on`
  - `Added_language_already_on_stays_on_with_new_keys` (Review Focus 5)
  - `New_default_needs_up_to_date_legal_pages`
  - `Default_change_keeps_terms_acceptances`: a resident accepted v3 in `en`; `de` Terms translated at v3; switching the default to `de` means no re-accept
  - `Turning_off_a_used_language_asks_first_and_then_residents_get_the_default` (pages and one email)
  - `Legal_translation_hidden_when_source_is_newer`: source v4, `de` v3, so a `de` reader sees the source with the note
  - `Saving_a_translation_stamps_the_source_version`
  - `Sign_up_gate_uses_the_grid_default`
  - `Email_wording_per_language_saved_and_used` (`de` override reaches a `de` resident; `fr` gets the built-in)
  - `Email_in_an_added_language_uses_its_file_then_English_per_key`
  - `Email_wording_page_lists_added_languages`
- [ ] **Step 2: Run** and expect FAIL. **Step 3: Implement.** **Step 4: Run** these and the 16a email and legal tests; expect PASS.
- [ ] **Step 5: Commit** `feat(languages): the owner chooses the languages and the default; legal and email wording per language`.

### Task 7: "New texts to translate" notice, docs, work list

**Files:**
- Modify:
  - the dashboard notices: find where the dashboard shows owner notices (`Dashboard*`, e.g. `DashboardSnapshot` or the warnings list) and add one line per added language that is on and has `Missing > 0`: "<Language>: N new texts to translate", linking to `/security/languages/<code>`, owner only
  - `docs/languages.md`: the owner section, covering choosing languages, the default rule, page wording, email and legal wording per language and the legal version rule, and adding a language, including the file format for translators
  - `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`: tick 1.4's per-language editor, 1.5's remaining items if any, and 2.6's "ToS text per language" only if Task 6 covers it (it covers per-language text; versioning per language is 2.6's own item, so add a note rather than a tick)
- Test: `DashboardTests` (extend)

- [ ] **Step 1: Tests:**
  - `Notice_for_added_language_with_missing_texts`
  - `No_notice_when_complete_or_off`
  - `Notice_owner_only`
- [ ] **Step 2: Run** and expect FAIL. **Step 3: Implement**, then write the docs. **Step 4: Run** the full project once, then `dotnet build Launcher/DreamGrid.Launcher.Tests -warnaserror`.
- [ ] **Step 5: Commit** `feat(languages): the dashboard says when an added language has new texts; docs for owners`.
