# Plan 8a — Email — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The owner sets up one or more free mail accounts from provider cards on `/security/email`, edits the wording of the grid's emails, and later features hand the launcher an email to send; it goes out through the first account that is under its limits, waits in an encrypted queue when none is, and every failure is visible to the owner.

**Architecture:** Pure rules (the provider catalog, rolling-window limits and account choice, templates and their rendering) live in `DreamGrid.Launcher.Core/Email`, unit-tested. The website keeps accounts, the outbox, send counts and recent problems in small JSON files in a new owner-only `<data>/email` folder (`JsonStateFile`), with passwords and queued messages protected by data protection. One `EmailSender` singleton is the only way in; one background worker drains the outbox with MailKit behind an `IMailTransport` seam. Template wording is a setting (like the legal pages), keyed by language.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, xUnit v3, AngleSharp (tests), Markdig (existing, via `LegalMarkdown`), **MailKit** (new, MIT; latest stable 4.x), **SmtpServer** (new, MIT, test projects only — an in-process SMTP server for transport tests).

**Spec:** `docs/superpowers/specs/2026-10-31-email-design.md`. The audiences design (`docs/superpowers/specs/2026-10-30-audiences-security-design.md`) and worklist 1.4 stay binding where the spec doesn't change them.

## Global Constraints

- Windows, Debian 12 and Debian 13; CI `build-and-smoke` stays green. `ca-certificates` is already installed by `setup/debian/install.sh` and CI — check, don't add.
- Email is optional: with no account set up, every existing page behaves exactly as today.
- `/security/email` and `/security/email/wording` are **OwnerPolicy** (the `/Security` folder already is) and **404 where there is no guest side** (`GuestSideOnly`, as `/Security/Bots`).
- No inline script, no `on…=` handlers, no `style="…"` (`InlineScriptScanTests`). The pop-up is a native `<dialog>` opened by `wwwroot/js/email.js`; without script, `?edit=<key>` renders that dialog `open`.
- Never write a password, SMTP key, message body, link or full recipient address to any log, the audit log, an error message or a page. A recipient in the main log is masked (`EmailMask.Of("ann@example.com") == "a…@example.com"`).
- Data protection purposes: `DreamGrid.Email.Password.v1` (account passwords), `DreamGrid.Email.Outbox.v1` (queued recipient + rendered message). Existing key ring unchanged.
- Everything in `<data>/email` (owner-only, created in `Program.cs` like `Guests`). Writes through `JsonStateFile`. Template changes through `IGridController.UpdateSettings(..., writeConfig: false)`.
- Limits use **rolling** windows: last 60 minutes and last 24 hours. A test email counts.
- Named provider cards always use TLS and validate certificates; **Other server** may pick `None` only with the warning "Mail and password go over the network unencrypted. Use only for a mail server on your own network."
- Outbox: at most **1,000** messages; an email waits at most until the expiry its sender set (default **24 hours**); retry delay for an account after a temporary failure **1, 2, 5, 10, 30 minutes** then 30.
- Recent problems: kept **7 days**; no address, body or link.
- Warnings: (a) every usable account ≥ **80 %** of a limit, (b) an email has waited > **10 minutes**, (c) an email failed for good in the last **24 hours**, (d) an account needs attention.
- Test-email button: at most **5 per minute** per owner session.
- UI copy plain English, short sentences. Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Run tests with `dotnet test Launcher/DreamGrid.Launcher.Tests --filter <Class>`.

## Review Focus

1. **Gmail answers "daily sending quota exceeded" (550 5.4.5 / 421 4.7.0) halfway through a burst of resets** → that account counts as full for the window and the rest go out on the next account; none is lost or sent twice. (Task 5 test `Quota_reply_marks_full_and_moves_on_without_duplicates`.)
2. **The owner changes Gmail's password in Google, so the app password is revoked** → the card shows "Needs attention: the server refused the password", emails use the next account, and saving a new password (or a successful test) clears it. (Task 5 `Auth_failure_needs_attention_until_saved`; Task 6 `Saving_password_clears_needs_attention`.)
3. **The launcher restarts with emails in the queue, one of them past its expiry** → the live ones go out after start; the expired one is a recent problem, never sent late. (Task 5 `Queue_survives_restart_and_drops_expired`.)
4. **The owner opens the pop-up for a saved account and saves without touching the password** → the stored password is kept, never rendered into the page. (Task 6 `Blank_password_keeps_stored_one` and `Password_never_rendered`.)
5. **An avatar name or "From" name containing a CR/LF or `<script>`** → no extra header is created; the HTML version shows it as text. (Task 2 `Values_are_escaped_in_html`; Task 4 `Crlf_in_names_cannot_add_headers`.)

---

### Task 1: Provider catalog, accounts and limits (Core)

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Email/EmailProviders.cs`, `EmailAccount.cs`, `SendWindow.cs`, `EmailMask.cs`
- Test: `Launcher/DreamGrid.Launcher.Tests/EmailProvidersTests.cs`, `SendWindowTests.cs`

**Interfaces:**
- Produces:
  - `public enum MailSecurity { StartTls, SslOnConnect, None }`
  - `public sealed record EmailProvider(string Key, string Name, string Kind, string LimitText, string Needs, string Host, int Port, MailSecurity Security, int? PerDay, int? PerHour, string UserLabel, string PasswordLabel, string FromHint, IReadOnlyList<string> Steps, string? HelpUrl)`; `Kind` is `"Personal mailbox"`, `"Sending service"` or `"Custom"`.
  - `public static class EmailProviders { IReadOnlyList<EmailProvider> All; EmailProvider? Find(string key); const string Custom = "custom"; }` — keys and order: `gmail, yahoo, yandex, icloud, sendpulse, brevo, mailjet, custom`, with the values of spec §3 (custom: empty host, port 587, StartTls, no limits). Builder checks iCloud and SendPulse host/port against the providers' pages and fixes the row (and the spec table) if wrong.
  - `public sealed record EmailAccount(Guid Id, string Provider, string Host, int Port, MailSecurity Security, string UserName, string? ProtectedPassword, string FromName, string FromAddress, string? ReplyTo, int? PerDay, int? PerHour, bool Paused, int Order, string? Problem, DateTimeOffset? ProblemSince)`
  - `public static class SendWindow { (int Hour, int Day) Counts(IEnumerable<DateTimeOffset> sent, DateTimeOffset now); bool HasRoom(EmailAccount a, (int Hour, int Day) c); DateTimeOffset? FreesAt(EmailAccount a, IReadOnlyList<DateTimeOffset> sent, DateTimeOffset now); double Fullness(EmailAccount a, (int Hour, int Day) c); EmailAccount? Choose(IEnumerable<EmailAccount> accounts, Func<Guid, (int Hour, int Day)> counts, Func<Guid, bool> resting); }` — `Choose` = lowest `Order` that is not paused, has no `Problem`, is not resting and has room. `Fullness` = the larger of hour/day used ÷ limit (0 with no limits).
  - `public static class EmailMask { string Of(string address); }`
- [ ] **Step 1: Failing tests:** `Catalog_has_eight_cards_in_order` (keys as above; `Find("gmail").Host == "smtp.gmail.com"`, `Port == 587`, `PerDay == 500`; `Find("sendpulse").PerHour == 50`); `Named_cards_never_use_plain_text` (every non-custom `Security != None`); `Counts_use_rolling_windows` (sent at now-59m, now-61m, now-23h, now-25h → `(1, 3)`); `Choose_skips_paused_problem_resting_and_full` (four accounts, only the 4th usable → it); `Choose_prefers_lowest_order`; `FreesAt_is_oldest_send_in_window_plus_window`; `Fullness_uses_the_tighter_limit` (hour 40/50, day 100/400 → 0.8); `Mask_keeps_first_letter_and_domain` (`"a…@example.com"`, no `@` → `"…"`).
- [ ] **Step 2: Run** `--filter "EmailProvidersTests|SendWindowTests"` → FAIL (types missing).
- [ ] **Step 3: Implement** the four files.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Email: provider cards, accounts and rolling send limits`.

### Task 2: Templates and rendering (Core + settings)

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Email/EmailTemplates.cs`, `RenderedEmail.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/LauncherSettings.cs` (`EmailSettings Email`, null-safe like `Legal`)
- Test: `EmailTemplatesTests.cs`, `SettingsStoreTests.cs` (round-trip)

**Interfaces:**
- Consumes: `LegalMarkdown.ToHtml` (raw HTML off).
- Produces:
  - `public sealed record EmailTemplateText(string Subject, string Body, DateTimeOffset Changed)`; `public sealed record EmailSettings(Dictionary<string, Dictionary<string, EmailTemplateText>>? Templates = null)` — language → template id → text; missing means the default.
  - `public static class EmailTemplates { const string Test = "test", ConfirmEmail = "confirm-email", ResetPassword = "reset-password", AccountApproved = "account-approved"; IReadOnlyList<string> All; string Title(string id); IReadOnlyList<string> Words(string id); IReadOnlyList<string> Required(string id); EmailTemplateText Default(string id); EmailTemplateText Current(LauncherSettings s, string id); IReadOnlyList<string> MissingWords(string id, string subject, string body); RenderedEmail Render(string id, EmailTemplateText text, IReadOnlyDictionary<string, string> values); IReadOnlyDictionary<string, string> Sample(string id, string gridName); }`
  - Words: all templates `{GridName}`, `{AvatarName}`; `confirm-email` and `reset-password` add `{Link}` and `{ExpiresIn}` (both required); `account-approved` adds `{Link}` (optional, the sign-in page). Default wording is the builder's, plain and short, and must pass `MissingWords`.
  - `public sealed record RenderedEmail(string Subject, string Text, string Html)` — subject is single-line (CR/LF → space); `Text` is the body with words filled; `Html` is `LegalMarkdown.ToHtml` of the body with each value HTML-escaped before it is placed. Unknown `{Word}`s are left as typed.
- [ ] **Step 1: Failing tests:** `Defaults_contain_their_required_words`; `Missing_link_is_reported` (`MissingWords("reset-password", "s", "no link") == ["{Link}", "{ExpiresIn}"]`); `Current_falls_back_to_default`; `Values_are_escaped_in_html` (`AvatarName = "<script>x</script>"` → Html contains `&lt;script&gt;`, no `<script`); `Raw_html_in_body_is_inert`; `Subject_is_one_line`; settings round-trip with one template saved.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Email: templates with defaults, required words and safe HTML`.

### Task 3: Account store with protected passwords

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/LauncherPaths.cs` (`Email => Path.Combine(Data, "email")`), `Launcher/DreamGrid.Launcher/Program.cs` (`CreateOwnerOnly(paths.Email)`; register `EmailAccounts` at `<data>/email/accounts.json`)
- Create: `Launcher/DreamGrid.Launcher/Email/EmailAccounts.cs`
- Test: `EmailAccountsTests.cs`

**Interfaces:**
- Consumes: `JsonStateFile<T>`, `IDataProtectionProvider`, `EmailAccount`.
- Produces: `public sealed class EmailAccounts(string file, IDataProtectionProvider dp, ILogger<EmailAccounts>? log)` (`<data>/email/accounts.json`) with `IReadOnlyList<EmailAccount> All()` (by `Order`), `EmailAccount? Find(Guid id)`, `bool Save(EmailAccount account, string? newPassword)` (blank/null `newPassword` keeps the stored one; a new account goes last; saving clears `Problem`), `bool Remove(Guid id)` (renumbers `Order`), `bool Move(Guid id, int delta)`, `bool SetPaused(Guid id, bool paused)`, `bool MarkProblem(Guid id, string? problem, DateTimeOffset now)`, `string? PasswordOf(EmailAccount a)` (unprotects; null if it can't), `bool IsReady` (any account not paused, no problem, with a password).
- [ ] **Step 1: Failing tests:** `Password_is_not_stored_in_clear` (file text lacks the password); `Blank_password_keeps_stored_one`; `Save_clears_problem`; `Move_and_remove_keep_order_contiguous`; `Damaged_file_reads_as_no_accounts_and_is_moved_aside`; `IsReady_false_when_all_paused_or_failing`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Email: account store, passwords protected by data protection`.

### Task 4: MailKit transport and reply classification

**Files:**
- Modify: `Launcher/DreamGrid.Launcher/DreamGrid.Launcher.csproj` (MailKit), `Launcher/DreamGrid.Launcher.Tests/DreamGrid.Launcher.Tests.csproj` (SmtpServer)
- Create: `Launcher/DreamGrid.Launcher/Email/MailTransport.cs`
- Test: `MailTransportTests.cs` (against an in-process SmtpServer on a `TestPorts` port)

**Interfaces:**
- Produces:
  - `public enum SendResult { Sent, AuthFailed, AccountFull, Temporary, RecipientRefused }`
  - `public sealed record SendOutcome(SendResult Result, string Plain)` — `Plain` is the owner-facing sentence, e.g. "Signed in and sent", "The server refused the password", "The account has reached its sending limit", "Couldn't reach smtp.gmail.com on port 587", "The server refused this address". Never contains the password or the body.
  - `public interface IMailTransport { Task<SendOutcome> SendAsync(EmailAccount account, string password, string to, RenderedEmail email, CancellationToken ct); }`
  - `public sealed class SmtpMailTransport(ILogger<SmtpMailTransport> log) : IMailTransport` — MailKit `SmtpClient`, `SecureSocketOptions.StartTls` / `SslOnConnect` / `None` (never `Auto`), 30-second timeout, `MailboxAddress` for From/Reply-To/To, multipart/alternative text + HTML.
  - Classification: authentication exception / 535 → `AuthFailed`; 421/451/452/454 or 550/552/554 whose text mentions quota, limit, rate or "too many" (and Gmail's `5.4.5`, `4.7.0`) → `AccountFull`; other 5xx on RCPT → `RecipientRefused`; socket, TLS, timeout, other 4xx → `Temporary`.
- [ ] **Step 1: Failing tests:** `Sends_text_and_html_with_reply_to`; `Wrong_password_is_auth_failed`; `Quota_reply_is_account_full` (fake replies `550 5.4.5 Daily user sending quota exceeded`); `Unknown_recipient_is_refused`; `Nothing_listening_is_temporary`; `Crlf_in_names_cannot_add_headers` (From name `"A\r\nBcc: x@y"` → received message has no `Bcc`); `Outcome_never_contains_password`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Email: MailKit transport with plain-words outcomes`.

### Task 5: Outbox, sender and background worker

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Email/EmailOutbox.cs`, `EmailSender.cs`, `EmailWorker.cs`
- Test: `EmailSenderTests.cs` (fake `IMailTransport`, `FakeTimeProvider`)

**Interfaces:**
- Consumes: Tasks 1–4, `AuditLog` (not for sends), `TimeProvider`.
- Produces:
  - `public sealed record EmailProblem(DateTimeOffset At, string Template, string? AvatarName, string Reason)`
  - `EmailOutbox(string folder, IDataProtectionProvider dp, TimeProvider time, ILogger<EmailOutbox>? log)` — files `outbox.json` (each entry: id, template, avatar name, expires, attempts, `Protected` = protected JSON of `{to, subject, text, html}`), `sent.json` (account id → send times within 24 h), `problems.json` (trimmed to 7 days on every write).
  - `public enum QueueResult { Queued, NotReady, Full }`
  - `public sealed class EmailSender` with `bool IsReady`, `QueueResult Queue(string templateId, string to, string? avatarName, IReadOnlyDictionary<string, string> values, TimeSpan? validFor = null)` (renders with `EmailTemplates.Current`, `{GridName}` filled from settings; `Full` adds a problem "The email queue was full"; `NotReady` stores nothing), `Task<SendOutcome> SendTestAsync(EmailAccount account, string password, string to, CancellationToken ct)` (bypasses the queue, counts against limits, does **not** mark problems), `EmailStatus Status()`.
  - `public sealed record EmailStatus(IReadOnlyList<AccountStatus> Accounts, int Waiting, DateTimeOffset? OldestWaiting, IReadOnlyList<EmailProblem> Problems, IReadOnlyList<string> Warnings)`; `public sealed record AccountStatus(EmailAccount Account, int Hour, int Day, DateTimeOffset? FullUntil, DateTimeOffset? RestingUntil)`. `Warnings` holds the four sentences of the spec's §5 conditions (a)–(d) that apply now.
  - `EmailWorker : BackgroundService` — wakes on `Queue` (a `SemaphoreSlim` signal) and every 30 seconds; sends one at a time: drop expired entries (problem "Expired before it could be sent"), `SendWindow.Choose`, then by result: `Sent` → record send time, remove; `AuthFailed` → `MarkProblem`, try next account; `AccountFull` → treat as full until the window frees (an in-memory "full until" = now + 1 h for hourly, else + 24 h), try next; `Temporary` → rest the account (1, 2, 5, 10, 30 min), try next; `RecipientRefused` → remove, problem "The receiving server refused the address". An entry is removed from `outbox.json` only after its outcome is recorded, so a crash re-sends at most that one. Each attempt logs one line: account provider key, template, `EmailMask.Of(to)`, result.
- [ ] **Step 1: Failing tests:** `Queue_not_ready_without_accounts`; `Sends_with_first_account_and_counts`; `Quota_reply_marks_full_and_moves_on_without_duplicates` (account A returns `AccountFull` on its 3rd send, 5 queued → A got 2, B got 3, each recipient once); `Auth_failure_needs_attention_until_saved`; `Temporary_failure_rests_account_then_retries`; `Recipient_refused_is_a_problem_not_retried`; `Queue_survives_restart_and_drops_expired` (new sender over the same folder); `Outbox_file_has_no_address_or_link`; `Full_queue_refuses_and_records_problem` (1,000 waiting); `Problems_older_than_seven_days_are_dropped`; `Warnings_cover_the_four_conditions`; `Test_send_counts_but_marks_no_problem`; `Log_line_masks_recipient` (captured log has `a…@example.com`, not the address).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement**, register `EmailOutbox`, `EmailSender`, `IMailTransport` → `SmtpMailTransport`, and `AddHostedService<EmailWorker>` in `Program.cs`.
- [ ] **Step 4: Run** → PASS, and the whole `DreamGrid.Launcher.Tests` suite.
- [ ] **Step 5: Commit** `Email: encrypted outbox, account fallback and a background sender`.

### Task 6: The Email page — cards and pop-up

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Pages/Security/Email.cshtml(.cs)`, `Launcher/DreamGrid.Launcher/wwwroot/js/email.js`
- Modify: `Program.cs` (`/Security/Email` gets `GuestSideOnly`), `Pages/Security/Index.cshtml` (link `id="email-link"` "Send email from the grid"), `wwwroot/css/site.css` (card grid, dialog)
- Test: `EmailPageTests.cs`

**Interfaces:**
- Consumes: `EmailProviders`, `EmailAccounts`, `EmailSender`, `AuditLog`, `AuditActor`, `IRobustClient.FindAccountAsync` (the owner's email for the test).
- Produces: route `/security/email`; handlers `OnGet(string? edit)`, `OnPostSave`, `OnPostTest`, `OnPostRemove`, `OnPostMove(Guid id, int delta)`, `OnPostPause(Guid id, bool paused)`. Bound form: `Id` (empty for new), `Provider`, `UserName`, `Password`, `FromName`, `FromAddress`, `ReplyTo`, `PerDay`, `PerHour`, and for custom `Host`, `Port`, `Security`, `TestTo`.
- Page content, top to bottom: the not-set-up notice "Email isn't set up yet. Sign-up and password reset stay hidden until it is." (only when `!IsReady`); the warnings (Task 8 fills them); one card per catalog entry not yet saved plus one per saved account (custom may be added more than once). A saved card shows order ("1st"), the count ("212 of 500 today", "31 of 50 this hour"), its state ("In use" / "Paused" / "Full until 14:20" / "Needs attention: …"), Up/Down/Pause forms. Each card has `data-dialog="<key>"` and `href="?edit=<key>"`; its `<dialog id="dlg-<key>">` holds the form of spec §4 (steps list, help link, password `type=password` always empty with placeholder "Saved — type to replace" when one is stored, server fields `readonly` unless custom, the plain-text warning when custom picks None), buttons Cancel / Send test email / Save, Remove for a saved one.
- Validation: email-looking `FromAddress`/`ReplyTo` (`MailboxAddress.TryParse`); limits blank or 1–100 000; custom host required, port 1–65 535. Errors re-render with that dialog `open`.
- Audit actions (target = provider key and account id, never secrets): `email-account-saved`, `email-account-removed`, `email-account-moved`, `email-account-paused`/`-resumed`, `email-test-sent` (result `ok` or the `SendResult`).
- [ ] **Step 1: Failing tests:** `Owner_only_and_404_without_guest_side` (admin → 403/redirect, Region Server plan → 404); `Shows_eight_cards_and_not_set_up_notice`; `Save_adds_account_and_audits_without_password` (audit line has no password); `Password_never_rendered` (GET after save: the password string is nowhere in the HTML); `Blank_password_keeps_stored_one`; `Saving_password_clears_needs_attention`; `Named_card_ignores_posted_host` (posting `Host=evil` for gmail keeps `smtp.gmail.com`); `Custom_none_security_shows_warning`; `Test_uses_unsaved_values_and_shows_outcome` (fake transport); `Test_is_limited_to_five_a_minute`; `Move_and_pause_change_card_order_and_state`; `Edit_query_renders_dialog_open_without_script`. `InlineScriptScanTests` passes.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement** the page and `email.js` (cards with `data-dialog` call `showModal()`, Cancel calls `close()`, the Security select shows/hides the warning). **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Email: the owner's Email page with provider cards and pop-ups`.

### Task 7: Email wording page

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Pages/Security/EmailWording.cshtml(.cs)` (route `/security/email/wording`, `GuestSideOnly`)
- Modify: `Pages/Security/Email.cshtml` (link "Change the wording of the emails")
- Test: `EmailWordingPageTests.cs`

**Interfaces:**
- Consumes: `EmailTemplates`, `EmailSender.SendTestAsync` (first usable account), `IGridController.UpdateSettings`, `AuditLog`.
- Produces: handlers `OnGet`, `OnPostSave(string id)`, `OnPostPreview(string id)`, `OnPostReset(string id)`, `OnPostTest(string id)`. One section per template: title, the list of fill-in words (required ones marked), subject input, body textarea, buttons. Saving a template equal to its default removes the stored copy. Audit: `email-template-saved`, `email-template-reset`.
- [ ] **Step 1: Failing tests:** `Shows_default_wording_for_each_template`; `Save_refuses_missing_required_word` (message names `{Link}`; nothing stored); `Save_stores_under_default_language_and_audits`; `Reset_restores_default`; `Preview_shows_sample_values_escaped`; `Test_without_ready_account_says_set_up_first`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Email: the owner can edit each email's wording`.

### Task 8: Warnings, recent problems, docs

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Pages/Shared/_EmailWarnings.cshtml`
- Modify: `Pages/Security/Email.cshtml` (warnings at top, "Recent problems" table), `Pages/Index.cshtml(.cs)` (dashboard banner), `Guests/SiteFacts.cs` (`Stored`: "Mail account settings and passwords (encrypted)", "Emails waiting to be sent (encrypted, until sent or expired)", "Recent email problems: time, email, avatar name, reason (7 days)" — all `<data>/email`), `docs/SECURITY-NOTES.md` (email section: what is stored where, keep data-folder backups private), `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md` (tick 1.4 items, note templates are English until 1.5), `.gitignore` (`.superpowers/`)
- Test: `EmailWarningsTests.cs`, `SiteFactsTests.cs`, `DashboardTests.cs`

**Interfaces:**
- Consumes: `EmailSender.Status()`.
- Produces: the dashboard shows, for the owner, each warning sentence with a link to `/security/email`; for admins and supervisors only "Email has a problem — the grid owner can fix it on the Email page." (no link, no account details); nothing at all when there are no warnings or no guest side. The Email page lists `Problems` newest first: time, template title, avatar name (or "—"), reason.
- [ ] **Step 1: Failing tests:** `Owner_sees_warning_with_link`; `Admin_sees_short_banner_without_details`; `No_banner_without_warnings_or_guest_side`; `Recent_problems_listed_without_addresses`; `SiteFacts_lists_email_files`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement** and write the docs. **Step 4: Run** the whole `DreamGrid.Launcher.Tests` suite → PASS.
- [ ] **Step 5: Commit** `Email: warnings, recent problems and docs`.
