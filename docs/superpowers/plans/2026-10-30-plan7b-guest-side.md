# Plan 7b — Guest side — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Guests sign in on the grid's own website through a door of their own, see how to connect with Firestorm, accept the operator's Terms, and (when an admin gives them a region) look after it; the operator pastes in their own legal pages behind a sign-up gate; the email-token helper and the bot-protection guard are ready for Phase 2.

**Architecture:** A second cookie scheme (`dg-guest`) with its own policy sits beside plan 7a's staff scheme (`dg`); no staff policy names it, so a guest cookie can never pass one. Pure rules (legal gate, Markdown rendering, region-owner restart eligibility, email tokens, proof-of-work) live in `DreamGrid.Launcher.Core/Guests`, unit-tested; the website keeps guest records in small JSON files in a new owner-only `<data>/guests` folder, written atomically. Region rights move the region into an estate owned by the guest through the region program's console (`estate create`, `estate link region`); a background check applies moves that had to wait for the region to run.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, xUnit v3, AngleSharp (tests), MySqlConnector (existing), QRCoder (existing), Markdig (new, BSD-2, Markdown with raw HTML off).

**Spec:** `docs/superpowers/specs/2026-10-30-audiences-security-design.md` (sections 2.1, 3, 4, 5, 7.5, 7.6, 8, 10 for the guest side). Plan 7a (`docs/superpowers/plans/2026-10-30-plan7a-staff-security.md`, merged in PR 15) built the staff side this plan sits on. The worklist `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md` 1.2/1.3 stays binding where the spec doesn't change it.

## Global Constraints

- Windows, Debian 12 and Debian 13; CI `build-and-smoke` must stay green (launcher-windows, launcher-debian 12/13, debian, windows, secret-scan, install-smoke).
- No paid or registered outside service, no CDN, nothing loaded from another origin. The CSP from plan 7a stays exactly as it is; new pages have no inline script, no `on…=` handlers and no `style="…"` (`InlineScriptScanTests` enforces it).
- **DreamGrid ships no legal text and no fill-in template** (owner). No page, test fixture, seed or doc may contain sample Terms, Privacy, Cookies or Contact wording. Test texts are obviously fake ("Test terms one.").
- Guest side exists on Local, Simple Public and Grid Asset Server (`AdminRule.HasStaffPages(s)` is true); on Region Server and Connected Server every guest-side URL, the legal editor and the bot page answer 404, and `/` behaves as today.
- A guest is any account with `UserLevel` ≥ 0. Suspended accounts (level −1) can't sign in on either door.
- Never write a password, token, proof-of-work payload, CAPTCHA answer or secret to any log. Sign-in log lines name the cleaned first name only (`AvatarName.ForLog`), as today.
- Throttle: the same `LoginThrottle` instance (name 5 / IP 20 failures in 10 minutes → 10-minute lock) covers the guest door.
- Region-owner restart: at most one per region per **10 minutes**.
- Email tokens: **256-bit** random value, base64url, only its SHA-256 hash stored, single use, expiry, bound to purpose **and** account.
- Bot-protection defaults: A1 honeypot **on**; A2 per-IP limit **on**, **5** per hour per form; B proof-of-work **on**, difficulty (ALTCHA `maxnumber`) **50 000**, allowed 1 000–1 000 000; C image CAPTCHA **off**; admin approval **off**.
- Everything the guest side stores is in `<data>/guests` (owner-only like `<data>`), never in the `robust` or `opensim` databases. Writes go through `AtomicFile`. Settings changes go through `IGridController.UpdateSettings`.
- The site sets only cookies it needs to work. Every cookie it sets is listed in `SiteFacts.Cookies` (Task 4) and a test proves it.
- UI copy is plain English, short sentences, matching existing pages. Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Run tests with: `dotnet test Launcher/DreamGrid.Launcher.Tests --filter <Class>` (Windows locally; Debian-only integration tests run in CI).

## Review Focus

1. **A staff member's browser holds a staff cookie, from an address outside the admin allow-list** → the public and guest pages work, the staff cookie is ignored there, and `/` shows the front page, never the dashboard. (Task 2 test `Outside_caller_with_staff_cookie_gets_front_page`.)
2. **Pasted legal text carrying `<script>`, `<img onerror=…>` or `[x](javascript:…)`** → shown as plain text or as a link without `href`; nothing runs. (Task 3 test `Raw_html_and_script_links_are_inert`.)
3. **An admin gives a guest rights to a region that is stopped** → rights are saved at once, the estate move waits, the region page says so, and the move happens when the region next runs. (Task 9 test `Pending_move_applied_when_region_runs`.)
4. **A region owner double-clicks Restart, or has two tabs open** → one restart runs; the second is refused with the time they can try again; one audit line. (Task 10 test `Second_restart_within_ten_minutes_refused`.)
5. **A guest is suspended on the People page while signed in on the guest side** → their guest session ends on their next request, not 5 minutes later. (Task 1 test `Suspension_on_people_page_ends_guest_session_at_once`.)

---

### Task 1: Guest door — scheme, sign-in, home page

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/LauncherPaths.cs` (`Guests => Path.Combine(Data, "guests")`; created owner-only in `Program.cs` like `Security`)
- Create: `Launcher/DreamGrid.Launcher.Core/JsonStateFile.cs`
- Create: `Launcher/DreamGrid.Launcher/Guests/GuestStamps.cs`, `GuestSignIn.cs`, `GuestPrincipalValidator.cs`, `GuestSideOnly.cs`
- Create: `Launcher/DreamGrid.Launcher/Pages/Guest/SignIn.cshtml(.cs)`, `SignOut.cshtml(.cs)`, `Index.cshtml(.cs)`, `Pages/Shared/_GuestLayout.cshtml`
- Modify: `Security/AuthSchemes.cs`, `Program.cs`, `Security/StaffActions.cs` (suspend rotates the guest stamp)
- Test: `JsonStateFileTests.cs`, `GuestSignInTests.cs`, `GuestAccessTests.cs`

**Interfaces:**
- Consumes: `LoginThrottle`, `IRobustClient.FindAccountAsync`/`AuthenticateAsync`, `AuditLog`, `AdminRule.HasStaffPages`, `SuspendedStore`.
- Produces:
  - `public sealed class JsonStateFile<T>(string path, Func<T> empty, ILogger? log) where T : class` with `T Load()` (missing → `empty()`; unreadable → the file is renamed to `<name>.damaged-<yyyyMMddHHmmss>`, one warning naming the path, `empty()`) and `bool Save(T state)` (`AtomicFile`; a failure is logged once until a save succeeds again, returns false). Every guest store in this plan uses it.
  - `AuthSchemes.Guest = "dg-guest"`, `AuthSchemes.GuestPolicy = "guest"`, `AuthSchemes.GuestRole = "guest"`. Cookie `dg-guest`: HttpOnly, SameSite Strict, session cookie, 8-hour sliding, `LoginPath = "/guest/sign-in"`.
  - `GuestStamps(string file, ILogger<GuestStamps>? log)` (`<data>/guests/stamps.json`): `string StampOf(Guid principal)` ("0" when none was ever set), `void Rotate(Guid principal)`.
  - `GuestSignIn.SignInAsync(HttpContext ctx, RobustAccount account) -> Task` (claims: Name, `principal`, NameIdentifier, role `guest`, `stamp`, `checked`); `GuestSignIn.Read(ClaimsPrincipal) -> (Guid Principal, string Stamp, DateTimeOffset Checked)?`.
  - `GuestPrincipalValidator.ValidateAsync(CookieValidatePrincipalContext)`: rejects when the stamp isn't current; every 5 minutes re-reads the level with the strict lookup, rejects when the account is gone or level < 0; a Robust failure keeps the session (as `StaffPrincipalValidator` does).
  - `GuestSideOnly` resource filter: 404 when `!AdminRule.HasStaffPages(settings)`.
  - Routes: `/guest/sign-in`, `/guest/sign-out` (POST), `/guest` (home). The `/Guest` folder is left out of the admin folder convention and gets `GuestPolicy` (scheme `dg-guest` only) plus `GuestSideOnly`; `/Guest/SignIn` is anonymous.
- [ ] **Step 1: Failing tests:**
  - `JsonStateFileTests`: `Missing_file_gives_empty`, `Damaged_file_is_set_aside_and_warned` (the `.damaged-` file exists with the old bytes), `Save_then_load_round_trips`.
  - `GuestSignInTests`: `Level_zero_guest_signs_in_and_lands_on_guest_home`; `Suspended_account_refused_with_generic_message` (level −1); `Wrong_password_counts_on_shared_throttle` (5 wrong guest tries lock the staff door for that name too); `Robust_down_shows_service_message_and_counts_nothing`; `Sign_in_is_audited_as_guest` (role `guest`, action `sign-in`; refusals `sign-in-refused` with the first name only); `Level_recheck_signs_out_suspended_guest` (advance `Time` 6 minutes after setting level −1 in the fake Robust → next request redirects to `/guest/sign-in`); `Suspension_on_people_page_ends_guest_session_at_once`.
  - `GuestAccessTests`: `Guest_cookie_refused_on_dashboard`, `Guest_cookie_refused_on_api` (a `/api/grid/…` POST and `/api/logs` GET → 401), `Guest_cookie_refused_on_people_page`, `Staff_cookie_does_not_open_guest_home` (→ redirect to `/guest/sign-in`), `Guest_pages_404_on_region_server_and_connected`.
  - `GuestAccessTests.Guest_form_post_passes_antiforgery` (the sign-out POST from `/guest` works: guest pages' `User` is the guest principal, because the folder policy names only `dg-guest`, and antiforgery binds to its NameIdentifier).
  - Add `/__test/guest-signin` to `TestAuth.Apply` (signs in through `GuestSignIn` as the level-0 account `TestAuth.GuestId`, "Test Guest", which `AddStaff` now adds too).
- [ ] **Step 2: Run** `dotnet test Launcher/DreamGrid.Launcher.Tests --filter "JsonStateFileTests|GuestSignInTests|GuestAccessTests"` → FAIL.
- [ ] **Step 3: Implement.** Error copy: wrong name/password/suspended → "Name or password is wrong."; locked → "Too many attempts. Try again in a few minutes."; Robust down → "The grid's sign-in service isn't running right now. Try again later." Guest home (`/guest`): "You're signed in as <Name>.", then **Connect with Firestorm**: 1. Install Firestorm for OpenSim from `https://www.firestormviewer.org/downloads/` (a plain link). 2. In Firestorm open Preferences → OpenSim → Grid Manager. 3. Paste `<login URI>` (`GridEndpoints.LoginUri(s, HostAddress.Effective(s.Hostname))`, in a `<code>`) and click Apply. 4. Pick <grid name> on the login screen and sign in with your avatar name and password. A Sign out button. **(my call)** A staff member may sign in here with their password alone and gets only the guest pages: two-factor guards the staff side, and this door opens nothing on it. `_GuestLayout` has the grid name, no staff navigation, and a footer slot for Task 3's legal links.
- [ ] **Step 4: Run** the filter, then the whole test project → PASS.
- [ ] **Step 5: Commit** `Guests: their own sign-in door, cookie and home page`.

### Task 2: Public front page and the allow-list

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Pages/Home.cshtml(.cs)` (route `/home`, anonymous)
- Modify: `Security/SetupGate.cs`, `Security/AdminNetworkFilter.cs`, `Security/StaffPrincipalValidator.cs`
- Test: `FrontPageTests.cs`, `SecurityPageTests.cs` (allow-list cases)

**Interfaces:**
- Consumes: `AdminRule.HasStaffPages`, `GuestSignIn.Read`.
- Produces:
  - `SetupGate`: a GET of `/` (no legacy keys) by someone not signed in as staff, once the owner exists, on a plan with a guest side → rewritten to `/home`; a signed-in supervisor still goes to `StaffSignIn.Home`; Region Server and Connected Server unchanged (challenge).
  - `AdminNetworkFilter.Exempt(HttpRequest r) -> bool` (signature now takes the request): `/setup…` as today, plus — only on a plan with a guest side — `/home`, `/guest…`, `/terms`, `/privacy`, `/cookies`, `/contact`, `/signup`, `/api/bots…`, and a GET of `/` without legacy keys. A caller outside the list on an exempt path gets `ctx.Items[AdminNetworkFilter.OutsideKey] = true`.
  - `StaffPrincipalValidator`: when `OutsideKey` is set, `RejectPrincipal()` without signing out (the cookie stays good inside the list; it's only invisible from outside).
- [ ] **Step 1: Failing tests** in `FrontPageTests`: `Anonymous_root_shows_front_page` (grid name, a link to `/guest/sign-in`, a link "Create an account" to `/signup`, a small "Staff sign in" link to `/login`); `Signed_in_admin_root_shows_dashboard`; `Supervisor_root_goes_to_people`; `Root_on_region_server_challenges_as_today`; `Legacy_root_post_unchanged` (POST `/` with `alt` still reaches `LegacyCommandApi`); `Dashboard_route_still_admin_only`. In `SecurityPageTests`: `Outside_caller_reaches_front_and_guest_sign_in`; `Outside_caller_with_staff_cookie_gets_front_page`; `Outside_caller_still_404_on_login_and_api`; `Outside_legacy_command_still_404`.
- [ ] **Step 2: Run** `--filter "FrontPageTests|SecurityPageTests"` → FAIL.
- [ ] **Step 3: Implement.** The front page uses `_GuestLayout`. Update the Security page's allow-list help text: "Guests and the public pages are reachable from any address; this list covers the staff pages."
- [ ] **Step 4: Run** filter, full project → PASS.
- [ ] **Step 5: Commit** `Guests: a public front page; the allow-list covers only the staff side`.

### Task 3: Legal pages — settings, rendering, public pages, sign-up notice

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Settings/LauncherSettings.cs` (`LegalSettings`, `BotSettings`; `CurrentSchema = 6`), `SettingsStore.cs` (5 → 6: version only), `SettingsValidator.cs`
- Modify: `Launcher/DreamGrid.Launcher.Core/DreamGrid.Launcher.Core.csproj` (Markdig, the newest stable version pinned exactly)
- Create: `Launcher/DreamGrid.Launcher.Core/Guests/LegalPages.cs`, `LegalMarkdown.cs`
- Create: `Launcher/DreamGrid.Launcher/Pages/Legal.cshtml(.cs)` (routes `/terms`, `/privacy`, `/cookies`, `/contact`), `Pages/SignUp.cshtml(.cs)` (route `/signup`), `Pages/Shared/_LegalLinks.cshtml`
- Test: `LegalMarkdownTests.cs`, `LegalPagesTests.cs`, `SettingsStoreTests.cs`, `SettingsValidatorTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record LegalText(string Markdown, int Version, DateTimeOffset Saved);`
  - `public sealed record LegalSettings(IReadOnlyDictionary<string, IReadOnlyDictionary<string, LegalText>>? Texts = null, bool OperatorResponsible = false, DateTimeOffset? ResponsibleSince = null);` — outer key a language ("en"), inner key a page id.
  - `public sealed record BotSettings(bool Honeypot = true, bool RateLimit = true, int PerHourPerIp = 5, bool ProofOfWork = true, int Difficulty = 50_000, bool ImageCaptcha = false, bool AdminApproval = false);`
  - `LauncherSettings.Legal`, `LauncherSettings.Bots` (null in the file reads as the defaults, like `Security`).
  - `LegalPages`: `DefaultLanguage = "en"`; page ids `Terms = "terms"`, `Privacy = "privacy"`, `Cookies = "cookies"`, `Contact = "contact"`, `All`; `LegalText? Get(LauncherSettings s, string page)` (default language; null when missing or whitespace); `bool SignUpGateOpen(LauncherSettings s)` (Terms, Privacy and Cookies have text and `OperatorResponsible`); `IReadOnlyList<string> Missing(LauncherSettings s)` (which of the three still need text, in that order).
  - `LegalMarkdown.ToHtml(string markdown) -> string`: Markdig with `DisableHtml()` and the advanced extensions off except pipe tables and emphasis extras; links and images whose URL isn't `http:`, `https:`, `mailto:` or relative are rendered without `href`/`src`; images are rendered as their alt text (no outside image loading).
- [ ] **Step 1: Failing tests:**
  - `LegalMarkdownTests`: `Headings_lists_links_render`; `Raw_html_and_script_links_are_inert` (`<script>alert(1)</script>` comes out escaped; `<img src=x onerror=alert(1)>` escaped; `[x](javascript:alert(1))` → `<a>` without `href`; `[x](JaVaScRiPt:…)` too); `Images_become_alt_text`.
  - `LegalPagesTests`: `Gate_closed_until_three_texts_and_tick` (each missing text and a missing tick keeps it closed; `Missing` lists them); `Whitespace_text_counts_as_missing`; `Legal_page_shows_rendered_text`; `Legal_page_without_text_is_404`; `Contact_is_optional`; `Signup_says_not_open` (exact text "This grid isn't open for sign-ups yet." — always in this plan; sign-up itself is Phase 2.1); `Footer_links_only_pages_with_text` (front page and guest home); `Legal_pages_404_on_region_server`.
  - `SettingsStoreTests.Schema5_file_loads_with_default_legal_and_bots` (`SchemaVersion == 6`, the `BotSettings` defaults above, no texts, not responsible). `SettingsValidatorTests`: `Difficulty_out_of_range_refused` (999 and 1 000 001), `PerHourPerIp_out_of_range_refused` (0 and 1 001).
- [ ] **Step 2: Run** `--filter "LegalMarkdownTests|LegalPagesTests|SettingsStoreTests|SettingsValidatorTests"` → FAIL.
- [ ] **Step 3: Implement.** Page titles: Terms, Privacy, Cookies, Contact. Each legal page shows "Version <n>, <date>" under the title. Validation on load (as plan 7a's security levels): out-of-range bot values → `SettingsCorruptException`.
- [ ] **Step 4: Run** filter, full project → PASS.
- [ ] **Step 5: Commit** `Legal: operator-written pages, rendered safely, and the sign-up notice`.

### Task 4: The owner's legal editor, facts list, cookie names, plain-HTTP warning

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Pages/Security/Legal.cshtml(.cs)` (route `/security/legal`, owner policy via the folder, plus `GuestSideOnly`)
- Create: `Launcher/DreamGrid.Launcher/Guests/SiteFacts.cs`, `Pages/Shared/_PlainHttpWarning.cshtml`
- Modify: `Program.cs` (antiforgery cookie name `dg-af`; TempData cookie name `dg-notice`), `Pages/Index.cshtml` (warning), `Pages/Security/Index.cshtml` (link to the legal editor)
- Test: `LegalEditorTests.cs`, `SiteFactsTests.cs`

**Interfaces:**
- Consumes: `LegalSettings`, `LegalPages`, `AuditLog`, `IGridController.UpdateSettings`.
- Produces:
  - Editor: one textarea per page (default language), a Preview button (renders through `LegalMarkdown`), Save. Saving a page whose text changed sets `Version = previous + 1` (first save is 1) and `Saved = now`; an unchanged text keeps its version. The tick "I wrote these notices and I am responsible for them." saves `OperatorResponsible` and `ResponsibleSince`. Shows the gate state: "Sign-ups can be opened" or "Before sign-ups can be opened: <missing list>". Audit: `legal-text-saved` (target the page id and new version), `legal-responsibility` (target `on`/`off`).
  - `SiteFacts.Cookies -> IReadOnlyList<(string Name, string Purpose)>`: `dg-auth` (staff sign-in), `dg-2fa` (two-factor step, 5 minutes), `dg-setup` (setup link, until the owner exists), `dg-guest` (guest sign-in), `dg-af` (form protection), `dg-notice` (one-time messages after a form). `SiteFacts.Stored -> IReadOnlyList<(string What, string Where)>`: avatar name and email (the grid's account service), sign-in times, IP addresses (`audit.log`, `security/throttle.json`, staff sessions), Terms acceptances with IP (`guests/terms.json`), region rights (`guests/region-rights.json`). Rendered beside the editor under the heading "Facts about this software, not legal text".
  - `_PlainHttpWarning`: shown when `HostAddress.IsPublic(s.Hostname)`, the plan has a guest side, and `!Request.IsHttps`. Copy: "This website uses plain HTTP. On a public grid, passwords typed here cross the internet unencrypted and could be read on the way." On the dashboard and the legal editor only.
- [ ] **Step 1: Failing tests:**
  - `LegalEditorTests`: `Owner_saves_terms_version_1_then_2`; `Unchanged_save_keeps_version`; `Admin_gets_403`; `Saves_and_tick_are_audited`; `Preview_renders_without_saving`; `Gate_state_lists_missing`; `Plain_http_warning_on_public_plan_only` (SimplePublic with a public hostname → shown on `/security/legal` and the dashboard; Local → absent); `Editor_404_on_connected`.
  - `SiteFactsTests.Every_cookie_set_is_listed`: drives the flows that set cookies (setup link, staff sign-in, two-factor step, guest sign-in, a form GET, a POST that sets a notice) through `LauncherFactory`, collects every `Set-Cookie` name, and asserts each is in `SiteFacts.Cookies` and each listed name was seen.
- [ ] **Step 2: Run** `--filter "LegalEditorTests|SiteFactsTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** filter, full project → PASS.
- [ ] **Step 5: Commit** `Legal: the owner's editor, the facts list and the plain-HTTP warning`.

### Task 5: Terms acceptance

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Guests/TermsAcceptances.cs`, `Guests/TermsGate.cs`, `Pages/Guest/Terms.cshtml(.cs)` (route `/guest/terms`)
- Test: `TermsAcceptanceTests.cs`

**Interfaces:**
- Consumes: `JsonStateFile<T>`, `LegalPages.Get(s, LegalPages.Terms)`, `GuestSignIn.Read`.
- Produces:
  - `public sealed record TermsAcceptance(Guid Principal, int Version, DateTimeOffset At, string? Ip);`
  - `TermsAcceptances(string file, TimeProvider time, ILogger<TermsAcceptances>? log)` (`<data>/guests/terms.json`): `bool HasAccepted(Guid principal, int version)`, `void Accept(Guid principal, int version, string? ip)` (keeps every acceptance, newest last), `IReadOnlyList<TermsAcceptance> Of(Guid principal)`.
  - `TermsGate` page filter on the `/Guest` folder (except `Terms` and `SignOut`): when Terms has text and the signed-in guest hasn't accepted its current version → redirect to `/guest/terms?ReturnUrl=…`. No Terms text → no gate.
  - `/guest/terms`: the rendered Terms, "Version <n>", a button "I accept these terms". A POST naming a version that isn't current is refused ("The terms changed while you were reading. Read the new version."). Audit `terms-accepted` (target `terms v<n>`).
- [ ] **Step 1: Failing tests:** `Guest_without_acceptance_is_sent_to_terms`; `Accepting_records_principal_version_time_ip`; `New_version_asks_again`; `Stale_version_post_refused`; `No_terms_text_no_gate`; `Damaged_file_asks_everyone_again` (via `JsonStateFile`); `Sign_out_reachable_before_accepting`.
- [ ] **Step 2: Run** `--filter TermsAcceptanceTests` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** filter, full project → PASS.
- [ ] **Step 5: Commit** `Guests: accept the current Terms before anything else`.

### Task 6: Email-token helper

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Guests/EmailTokens.cs`
- Modify: `Program.cs` (registered, `<data>/guests/email-tokens.json`; nothing uses it yet)
- Test: `EmailTokensTests.cs`

**Interfaces:**
- Produces: `EmailTokens(string file, TimeProvider time, ILogger<EmailTokens>? log)`:
  - `string Issue(string purpose, Guid principal, TimeSpan lifetime)` — 32 bytes from `RandomNumberGenerator`, base64url without padding; stores only SHA-256(token) hex with purpose, principal, expiry. Issuing a new token for the same purpose and principal removes the older ones.
  - `bool TryRedeem(string purpose, Guid principal, string token)` — true once, only for matching hash, purpose and principal before expiry; the entry is removed on success **and** on a purpose/principal mismatch; comparison with `CryptographicOperations.FixedTimeEquals` on the hashes.
  - Expired entries are dropped whenever the file is written.
- [ ] **Step 1: Failing tests:** `Token_is_256_bit_base64url`; `Redeem_once_only`; `Wrong_purpose_refused`; `Wrong_account_refused`; `Expired_refused`; `File_holds_no_token`(the saved JSON doesn't contain the token string); `Survives_restart`; `New_token_replaces_old`; `Malformed_token_refused_without_throwing` (empty, non-base64, 1 MB string).
- [ ] **Step 2: Run** `--filter EmailTokensTests` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** filter, full project → PASS.
- [ ] **Step 5: Commit** `Security: single-use email tokens, stored as hashes`.

### Task 7: Bot protection — honeypot, rate limit, proof-of-work, owner page

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Guests/ProofOfWork.cs`
- Create: `Launcher/DreamGrid.Launcher/Guests/BotGuard.cs`, `Endpoints/BotsApi.cs` (`GET /api/bots/challenge`, anonymous), `Pages/Shared/_BotFields.cshtml`, `wwwroot/js/pow.js`, `Pages/Security/Bots.cshtml(.cs)` (route `/security/bots`, plus `GuestSideOnly`)
- Modify: `Program.cs`, `wwwroot/css/site.css` (`.dg-hp` hides the honeypot), `Pages/Security/Index.cshtml` (link)
- Test: `ProofOfWorkTests.cs`, `BotGuardTests.cs`, `BotsPageTests.cs`

**Interfaces:**
- Produces:
  - `ProofOfWork(byte[] key, TimeProvider time)`: `Challenge Create(int maxNumber)` → `record Challenge(string Algorithm, string ChallengeHex, int MaxNumber, string Salt, string Signature)` in ALTCHA's format: `Algorithm = "SHA-256"`, salt = 12 random bytes hex + `?expires=<unix seconds, now + 10 min>`, secret number uniform in [0, maxNumber], `ChallengeHex = hex(SHA256(salt + number))`, `Signature = hex(HMAC-SHA256(key, ChallengeHex))`. `bool Verify(string payloadBase64)`: the payload is base64 JSON `{algorithm, challenge, number, salt, signature}`; true when the signature matches, the salt's `expires` is in the future and SHA256(salt + number) equals the challenge. The key is 32 random bytes in `<data>/security/pow.key` (owner-only, made on first use).
  - **(my call, recorded)** The client is our own `wwwroot/js/pow.js` (SubtleCrypto SHA-256 loop that fills a hidden `pow` field), not the ALTCHA widget: the widget's default build injects inline styles and runs its worker from a `blob:` URL, which the CSP refuses. The wire format is ALTCHA's, so the widget can replace it later.
  - `BotGuard`: `BotCheck Check(HttpContext ctx, string form)` → `record BotCheck(bool Passed, string? Layer)`; layers run in order honeypot (`website` field non-empty → fail), rate limit (in memory, per IP per form, `PerHourPerIp` in a sliding hour; a check that passes counts), proof-of-work (`pow` field; each payload accepted once until its expiry), image CAPTCHA (Task 11; until then this layer is skipped). A failed check logs the layer and the form, never field values. `_BotFields` renders whatever is on: the honeypot input (class `dg-hp`, `autocomplete="off"`, `tabindex="-1"`, label "Leave this empty"), the `pow` field with `data-challenge-url="/api/bots/challenge"`.
  - **Try it** on the Bots page: a small form that renders `_BotFields` for the layers that are on and posts to a handler that reports "Passed" or "Stopped by <layer>". It's how the owner checks the protection works in their browser, and the test surface for the guard.
  - The Security page's link to the Bots page is hidden on Region Server and Connected Server.
  - Bots page: one switch per layer (A1, A2 with its number, B with its difficulty, C, admin approval with "Used once sign-ups exist."), saved through `UpdateSettings`. Turning any layer off writes `bot-layer-off` (target the layer id: `honeypot`, `rate-limit`, `proof-of-work`, `image-captcha`); turning on writes `bot-layer-on`. Warning when all four layers are off: "Every protection is off. Once sign-ups are open, bots can create accounts freely." **(my call)** The spec's "and sign-up is open" can't be true until 2.1 adds the switch, so the warning shows whenever every layer is off; 2.1 adds the sign-up condition.
- [ ] **Step 1: Failing tests:**
  - `ProofOfWorkTests`: `Solved_challenge_verifies` (the test solves it by brute force with `maxNumber = 1000`); `Tampered_number_refused`; `Tampered_signature_refused`; `Expired_refused` (advance time 11 minutes); `Garbage_payload_refused_without_throwing`; `Matches_altcha_reference_vector` (a fixed key, salt and number give the documented challenge and signature hex, computed once by hand in the test with `SHA256`/`HMACSHA256` over the same strings).
  - `BotGuardTests`: `Honeypot_filled_fails`; `Sixth_post_in_an_hour_fails_when_limit_5`; `Reused_pow_payload_fails`; `Layers_off_are_skipped`; `Failure_log_has_no_field_values`.
  - `BotsPageTests`: `Try_form_passes_with_solved_pow_and_stops_honeypot`; `Owner_saves_layers`; `Turning_off_is_audited`; `All_off_warns`; `Admin_gets_403`; `Bots_page_404_on_region_server`; `Challenge_endpoint_anonymous_and_uses_difficulty`.
- [ ] **Step 2: Run** `--filter "ProofOfWorkTests|BotGuardTests|BotsPageTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** filter, full project → PASS. Then start the launcher locally on a scratch `DREAMGRID_DATA`, open `/security/bots` with the browser console open and use **Try it**: `pow.js` fills the field, the form passes, and there is no CSP violation.
- [ ] **Step 5: Commit** `Security: bot protection guard with honeypot, rate limit and proof-of-work`.

### Task 8: Region rights — granting, listing, People

**Files:**
- Create: `Launcher/DreamGrid.Launcher/Guests/RegionRights.cs`
- Modify: `Pages/Regions/Region.cshtml(.cs)` (a "Region owner" card, admin), `Pages/Regions/Index.cshtml(.cs)` (marker), `Pages/People/Index.cshtml(.cs)` (regions in the find result), `Pages/Regions/Deleted.cshtml.cs` (erasing a region forgets its right), `Program.cs`
- Test: `RegionRightsTests.cs`, `RegionOwnerCardTests.cs`

**Interfaces:**
- Consumes: `IRobustClient.FindAccountAsync`, `AuditLog`, `JsonStateFile<T>`, `IGridController.GetStatus`.
- Produces:
  - `public sealed record RegionRight(Guid Region, Guid Owner, string OwnerName, DateTimeOffset Since, EstateMove? Pending);` `public enum EstateMove { ToOwner, ToGrid }`.
  - `RegionRights(string file, TimeProvider time, ILogger<RegionRights>? log)` (`<data>/guests/region-rights.json`): `RegionRight? Of(Guid region)`, `IReadOnlyList<RegionRight> OwnedBy(Guid owner)`, `void Grant(Guid region, Guid owner, string ownerName)` (replaces an existing owner; `Pending = ToOwner`), `void Remove(Guid region)` (keeps the entry with `Pending = ToGrid` until Task 9 completes the move, then drops it), `void Forget(Guid region)` (erase), `int? EstateOf(Guid owner)` / `void SetEstate(Guid owner, int estateId, string name)`, `int? HomeEstateOf(Guid region)` / `void SetHomeEstate(Guid region, int estateId)`, `void MoveDone(Guid region)`, `IReadOnlyList<RegionRight> PendingMoves()`.
  - Region page card (admin): no owner → an avatar-name field and "Give region rights"; with an owner → "<Name> has region rights since <date>." and "Remove region rights" (with the existing `confirm.js` confirmation). When `Pending` is set: "Moves to <Name>'s estate when <Region> next runs." / "Moves back to the grid's estate when <Region> next runs." When the region shares its program: "<Region> shares its program with <others>. Its owner can restart it from the website only when every region in that program is theirs." and a button "Give <Region> its own program" posting to the existing Runs-with action (`ChangeRunsWithAsync(id, null)`). Errors: unknown name → "There's no avatar called <name> on this grid."; level < 0 → "<Name> is suspended."; Robust down → "The grid's account service isn't running. Start your world first."
  - Regions list: a small "Owner: <Name>" under the region's name.
  - People find result: "Region rights: <Region>, <Region>" or "Region rights: none".
  - Audit: `region-rights-granted`, `region-rights-removed` (target `<Region> → <Name>`).
- [ ] **Step 1: Failing tests:**
  - `RegionRightsTests`: `Grant_sets_pending_to_owner`; `Grant_replaces_previous_owner`; `Remove_sets_pending_to_grid`; `Estate_per_owner_reused_across_regions`; `Forget_drops_entry`; `Damaged_file_means_no_rights` (fail closed, set aside by `JsonStateFile`); `Survives_restart`.
  - `RegionOwnerCardTests`: `Admin_grants_by_name`; `Unknown_name_shows_error`; `Suspended_avatar_refused`; `Supervisor_cannot_post_grant` (403); `Remove_is_audited`; `Shared_program_note_and_own_program_button`; `Regions_list_shows_owner`; `People_find_shows_regions`; `Erase_forgets_rights`.
- [ ] **Step 2: Run** `--filter "RegionRightsTests|RegionOwnerCardTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** filter, full project → PASS.
- [ ] **Step 5: Commit** `Regions: admins give a guest region rights`.

### Task 9: Estates — moving a region into its owner's estate

**Files:**
- Modify: `Launcher/DreamGrid.Launcher.Core/Grid/IGridController.cs`, `GridController.cs`, `Database/RegionStore.cs` (or a new `Database/EstateStore.cs` for the one read)
- Create: `Launcher/DreamGrid.Launcher/Guests/RegionEstateSync.cs` (hosted service)
- Modify: `Launcher/DreamGrid.Launcher.Tests/FakeGridController.cs`, `Program.cs`
- Test: `RegionEstateSyncTests.cs`, `GridControllerTests.cs` (console parsing), `Launcher/DreamGrid.Launcher.IntegrationTests/GridBootTests.cs`

**Interfaces:**
- Consumes: `RegionRights` (Task 8), `GridConsoleCommands.ValidateAvatarName` naming rules.
- Produces, on `IGridController` (each throws `InvalidOperationException` "This computer doesn't run the grid service." on Region Server and Connected Server):
  - `Task<int?> EstateOfAsync(Guid regionId, CancellationToken ct)` — `SELECT EstateID FROM opensim.estate_map WHERE RegionID = @id`; null when there's no row; `InvalidOperationException("Start the database first.")` while MariaDB isn't running.
  - `Task<(int Id, string Name)> CreateEstateAsync(Guid regionId, Guid owner, string name, CancellationToken ct)` — on the region's program console: `estate create <owner> <name>`; output `Estate (\d+) created as` → the id; `An estate named "…" already exists.` → tries `<name> 2`, `<name> 3`, … up to `<name> 20`, then throws; anything else throws `InvalidOperationException` with the transcript. Region not running → `InvalidOperationException("Start <Region> first.")`.
  - `Task LinkEstateAsync(Guid regionId, int estateId, CancellationToken ct)` — `estate link region <estateId> <regionId>`; done on `is now attached to estate` or `is already part of estate`; otherwise throws with the transcript.
  - All three take the controller's operation lock like `SetUserLevelAsync`; no "change region" is needed (OpenSim's handlers use `CurrentOrFirstScene` and `TryGetScene(regionId)`; checked in `OpenSim/Region/Application/OpenSim.cs`).
  - `RegionEstateSync : BackgroundService`, every **60 s** (`TimeProvider`), and `Task SyncOnceAsync(CancellationToken ct)` for tests: for each `PendingMoves()` whose region is Running — `ToOwner`: remember the home estate the first time (`EstateOfAsync`), create the owner's estate when `EstateOf(owner)` is null (name `"<First Last> Estate"`), link, `MoveDone`; `ToGrid`: link to `HomeEstateOf(region)` (none remembered → leave it where it is and drop the right, one warning), `MoveDone`. Each move is audited as actor `DreamGrid`, action `estate-move`, target `<Region> → <estate name>`. A failure is logged (Warning) once per region until it succeeds; the move stays pending. The owner's estate is never deleted.
- [ ] **Step 1: Failing tests:**
  - `RegionEstateSyncTests` (fake controller): `Pending_move_applied_when_region_runs` (stopped → nothing called; Running → create + link, `MoveDone`); `Second_region_reuses_owners_estate` (one create); `Remove_links_back_to_home_estate`; `Failure_keeps_move_pending_and_warns_once`; `Moves_are_audited`.
  - `GridControllerTests`: `CreateEstate_parses_id`; `CreateEstate_numbers_taken_name`; `CreateEstate_unexpected_output_throws_with_transcript`; `LinkEstate_accepts_already_part`; `Estate_calls_refused_on_region_server`.
  - `GridBootTests.Region_moves_to_owner_estate_and_back` (Debian CI, real grid): create a second avatar, grant, `SyncOnceAsync`, then `estate_map` names the new estate, `estate show` on the console lists it with that owner; restart the region → still in it; change how it runs (its own program) → still in it; remove rights → back in the grid's estate; the guest's estate still exists.
- [ ] **Step 2: Run** `--filter "RegionEstateSyncTests|GridControllerTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** filter, full project → PASS. The `GridBootTests` case runs in CI (`launcher-debian`); push the branch and read its result before calling the task done.
- [ ] **Step 5: Commit** `Regions: a region owner's regions move into their own estate`.

### Task 10: My regions and the stuck-region restart

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Guests/RegionOwnerRules.cs`
- Create: `Launcher/DreamGrid.Launcher/Pages/Guest/Regions.cshtml(.cs)` (route `/guest/regions`)
- Modify: `Pages/Guest/Index.cshtml` ("My regions" link when they own any)
- Test: `RegionOwnerRulesTests.cs`, `MyRegionsTests.cs`

**Interfaces:**
- Consumes: `RegionRights.OwnedBy`, `IGridController.GetStatus`, `IGridController.RegionPowerAsync`, `IOnlineUsers.ListAsync`, `AuditLog`.
- Produces:
  - `RegionOwnerRules.Restart(GridStatus status, RegionStatus region, Guid owner, Func<Guid, Guid?> ownerOf, DateTimeOffset? lastRestart, DateTimeOffset now) -> RestartOffer` with `record RestartOffer(bool Shown, bool Allowed, string? Reason, RegionPower? Action)`:
    - Shown only when the region is stuck: `Failed`, or `Stopped` (not `Parked`) while the grid's Robust is Running. Running, Starting, Stopping and Parked → not shown.
    - Not allowed when another region in its program (`ProgramStatus.Regions`) has a different owner or none: reason "This region shares its program with regions that aren't yours. To restart it, contact the grid's staff."
    - Not allowed when `lastRestart` is less than 10 minutes ago: reason "This region was restarted a few minutes ago. Try again after <HH:mm> (UTC)."
    - Action: `RegionPower.Start` for Stopped, `RegionPower.Restart` for Failed.
  - My regions page: one card per owned region: name, state in plain words (running, stopped, starting, stopping, not responding, parked), "People here now: <n>" (from `IOnlineUsers`, "unknown" when unavailable), "Last started <time> (UTC)" (program uptime; "not running" otherwise), the restart button when `Shown` (disabled with the reason when not `Allowed`), and the line "Normal restarts are done in-world from the viewer's Region/Estate window."
  - Restart POST re-checks ownership and `RegionOwnerRules` server-side; last restart times kept in memory per region (**my call**: a launcher restart clears them). Audit `region-restart` (actor the guest, role `guest`, target the region name, result `ok` / `refused`).
- [ ] **Step 1: Failing tests:**
  - `RegionOwnerRulesTests`: `Running_region_offers_nothing`; `Failed_region_offers_restart`; `Stopped_offers_start_only_while_robust_runs`; `Parked_offers_nothing`; `Shared_with_unowned_region_not_allowed`; `Shared_with_own_regions_allowed`; `Within_ten_minutes_not_allowed_with_time`.
  - `MyRegionsTests`: `Owner_sees_only_their_regions`; `Guest_without_regions_has_no_link_and_empty_page`; `Restart_runs_and_is_audited`; `Second_restart_within_ten_minutes_refused`; `Crafted_post_for_someone_elses_region_refused` (403, audited `refused`); `Online_unavailable_shows_unknown`; `Staff_cookie_cannot_open_my_regions`.
- [ ] **Step 2: Run** `--filter "RegionOwnerRulesTests|MyRegionsTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** filter, full project → PASS.
- [ ] **Step 5: Commit** `Guests: My regions, with a restart for a stuck region`.

### Task 11: Image CAPTCHA (layer C, off by default)

**Files:**
- Create: `Launcher/DreamGrid.Launcher.Core/Guests/ImageCaptcha.cs`, `Launcher/DreamGrid.Launcher/Endpoints/BotsApi.cs` (`GET /api/bots/captcha`)
- Modify: `Guests/BotGuard.cs` (layer C), `Pages/Shared/_BotFields.cshtml`
- Test: `ImageCaptchaTests.cs`, `BotGuardTests.cs`

**Interfaces:**
- Produces:
  - `ImageCaptcha(byte[] key, TimeProvider time)`: `CaptchaChallenge Create()` → `record CaptchaChallenge(string Svg, string Question, string Token)`. The SVG draws **5** characters from `ACDEFHJKMNPRTUVWXY34679` as stroked paths from a small built-in stroke font (no `<text>` element, so the answer isn't in the markup), each glyph rotated −20° to 20° and offset, plus 6 random noise lines. `Question` is the text alternative: a sum of two numbers 1–9 written as words ("What is four plus seven?"), accepted as digits or words. `Token` = base64url of (answer hashes, expiry 10 minutes) HMAC-signed with the proof-of-work key. `bool Verify(string token, string? imageAnswer, string? questionAnswer)` — either answer may pass; case-insensitive; each token accepted once (the guard remembers it until expiry).
  - `_BotFields`: when C is on, the image (served from `/api/bots/captcha` as an `<img>` with `alt="Picture of letters; answer the question below instead if you can't see it"`), a field "Type the letters", the question with its own field, and a hidden token. **(my call)** The worklist asks for "audio or text"; this is the text alternative, and audio is left out (no self-hosted speech engine on both systems).
  - Copy is in English for now; localisation is 1.5.
- [ ] **Step 1: Failing tests:** `Svg_has_no_text_element_and_no_answer`; `Right_letters_pass`; `Right_question_answer_passes_as_digits_or_words`; `Wrong_answers_fail`; `Expired_token_fails`; `Reused_token_fails`; `Tampered_token_fails_without_throwing`; `BotGuardTests.Captcha_layer_only_when_on`.
- [ ] **Step 2: Run** `--filter "ImageCaptchaTests|BotGuardTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** filter, full project → PASS.
- [ ] **Step 5: Commit** `Security: self-hosted image CAPTCHA with a text alternative`.

### Task 12: Owner docs and worklist

**Files:**
- Modify: `docs/SECURITY-NOTES.md` (sections: The guest side; Legal pages and the sign-up gate (DreamGrid provides no wording); Region owners and estates; Bot protection; What lives in `<data>/guests` and how to recover a damaged file)
- Modify: `content/README.md` ("People who run the grid" gains a "Guests and region owners" part)
- Modify: `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`: tick 1.2's resident area and 1.3's email-token helper and bot-protection items (admin approval stays open: its switch exists, its effect is 2.1/2.2); under "Later" add "Region owners for regions on a Region Server", "OAR download for region owners", "Audio alternative for the image CAPTCHA".
- [ ] **Step 1:** Write the docs; every route, file name, cookie name and page title matches the code (`grep` each one). No legal wording anywhere.
- [ ] **Step 2: Run** the full test project → PASS.
- [ ] **Step 3: Commit** `Docs: guests, legal pages, region owners and bot protection`.
