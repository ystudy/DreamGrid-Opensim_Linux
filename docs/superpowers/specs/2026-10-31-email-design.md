# Email — design

Phase 1.4 (Email) of `docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`, worked out with the owner on 2026-10-31. Marked **(owner)** where the owner decided, **(my call)** where the designer did; everything else is left to the builder. Where this document and worklist 1.4 differ, this document wins.

## 1. Why

Sign-up (2.1), pending accounts (2.2) and password recovery (2.3) all need the grid to send email, and today it can't. This work gives the website a way to send email reliably with **free** mail accounts, so the next plans only have to say "send this template to this person".

DreamGrid is open source with no budget (audiences design §1). Email is **optional**: a grid with no email set up works as today, and the features that need email stay hidden or explain why (worklist 1.4).

**Success:** an owner with a free Gmail account (or Brevo, or several accounts) can set up email in a few minutes from cards with step-by-step help, press "Send test email" and get it, and later password-reset emails keep going out even on a busy day because the grid moves on to the next account when one is full.

## 2. Goals, non-goals, anti-goals

**Goals**
- Provider cards for the free services that allow at least ~200 emails a day, plus a custom card for any other SMTP server.
- More than one account at once, used in an order the owner sets, with each account's limits respected.
- Passwords stored encrypted and never shown back.
- Email wording the owner can edit, with working defaults.
- One small sending service that later features call.

**Non-goals (later list)**
- **(owner)** Office 365 / Microsoft 365. Microsoft has switched off plain-password sending, so it needs OAuth; on the later list.
- Sign-in with Google or Microsoft (OAuth) for any provider.
- Mass email to residents (worklist 2.12, already "later").
- Email alerts to staff (for example "a region went down").
- The sign-up, pending-account and password-reset pages themselves (2.1–2.3). This work provides their templates and the sending service, not the pages.
- Translations. Templates are English for now and keyed by language from the start, so 1.5 can add languages beside them.

**Anti-goals — a failure even if it "works"**
- A password, app password or SMTP key ever appearing in a page, a log, the audit log or an error message, or written to disk unprotected. (A copy of the whole data folder also holds the data-protection keys, exactly as it already does for the two-factor secrets; see §7.)
- A reset or confirmation link sitting readable on disk while it waits in the queue.
- Silently dropping a password-reset email: if it can't go out, staff can see that it didn't.
- Promising a provider's limit as fact. The personal mailboxes don't publish their limits; the cards say "about".
- Depending on any paid or registered DreamGrid-run service.

## 3. Providers (owner)

**(owner)** Target the free services with roughly 200 or more free emails a day. Researched 2026-10-31; the figures are guides, not promises, and providers change them.

| Card | Kind | Free limit shown on the card | Starting limits | What you need |
|---|---|---|---|---|
| Gmail | Personal mailbox | about 500 a day | 500/day | 2-Step Verification + app password |
| Yahoo Mail | Personal mailbox | about 500 a day | 500/day | App password |
| Yandex | Personal mailbox | about 500 a day | 500/day | App password (starts working after 2–3 hours) |
| iCloud Mail | Personal mailbox | about 200 a day (new accounts), up to about 1,000 | 200/day | 2-step sign-in + app-specific password |
| SendPulse | Sending service | 400 a day, 50 an hour, 12,000 a month | 400/day, 50/hour | Free account + a sending address confirmed there |
| Brevo | Sending service | 300 a day | 300/day | Free account + a sender confirmed there + SMTP key |
| Mailjet | Sending service | 200 a day, 6,000 a month | 200/day | Free account + a sender confirmed there; API key and secret key |
| Other server | Custom | You set the limits | none | Server, port, security, username, password from the provider or web host |

Left out on purpose (below ~200 a day, or no plain SMTP): SMTP2GO (~1,000 a month), Resend (100/day), SendGrid, Mailgun, MailerSend, Zoho free (no SMTP on free plans), GMX (~11 recipients an hour), Outlook.com/Hotmail, Proton Mail. Any of them can still be used through **Other server**.

**Preset server settings** (the builder checks each against the provider's own help page when building, and fixes the table if it's wrong):

| Card | Server | Port | Security | Username | Notes |
|---|---|---|---|---|---|
| Gmail | `smtp.gmail.com` | 587 | STARTTLS | full Gmail address | "From" must be the Gmail address (or an alias set up in Gmail) |
| Yahoo Mail | `smtp.mail.yahoo.com` | 465 | SSL/TLS | full Yahoo address | |
| Yandex | `smtp.yandex.com` | 465 | SSL/TLS | full Yandex address | |
| iCloud Mail | `smtp.mail.me.com` | 587 | STARTTLS | full iCloud address | **not confirmed in research** — check Apple's page |
| SendPulse | `smtp-pulse.com` | 465 | SSL/TLS | SMTP login from the SendPulse account | **not confirmed officially** — SendPulse says to copy it from the account's SMTP page |
| Brevo | `smtp-relay.brevo.com` | 587 | STARTTLS | SMTP login from the Brevo account | password field is the "SMTP key" |
| Mailjet | `in-v3.mailjet.com` | 587 | STARTTLS | API key | password field is the secret key |

The preset list is data (one file or one table in code), so a wrong host or a changed limit is a one-line fix and a new provider is a new row.

## 4. The Email page

- **Who and where (my call):** owner only (OwnerPolicy), at `/security/email`, linked from the Security page next to Bot protection and Legal pages. Like the bot-protection and legal pages it exists only where there is a guest side (Local, Simple Public, Grid Asset Server); on Region Server and Connected Server it answers 404. The owner holds the mail account passwords, so admins don't see this page.
- **Not set up yet:** a notice at the top says email isn't set up, and that sign-up and password reset stay hidden until it is.
- **Cards (owner):** a grid of the eight cards in §3. Each shows the kind (personal mailbox / sending service / custom), the name, the free limit and what you need. A card that is set up also shows:
  - its place in the order ("1st", "2nd", …) and buttons to move it up or down,
  - its live count, for example "212 of 500 today" (and "31 of 50 this hour" when it has an hourly limit),
  - its state: **In use**, **Paused** (set up but switched off), **Full until 14:20**, or **Needs attention** with the last error in plain words (never the password).
- **Several accounts per card (my call):** "Other server" can be added more than once; for the named providers one account per card is enough for now, but the storage allows more.
- **Pop-up (owner):** clicking a card opens a dialog with:
  - "How to get your password": the numbered steps for that provider, with a link to the provider's page where the app password or key is made.
  - Email address / username; password (write-only: empty with "Saved — type to replace" once saved, never filled from the store).
  - "From" name and "From" address; **"Reply-to" address, optional (owner: the owner chooses and configures these per account; the grid never insists they match across accounts).** A hint says what the provider allows (for example "Must be your Gmail address").
  - Daily limit and hourly limit, prefilled from §3, editable; blank means no limit of that kind.
  - Server, port and security: filled in by the card and folded away under "Server details"; read-only for the named cards, editable and open for **Other server**.
  - Buttons: **Cancel**, **Send test email**, **Save**. For a card not yet in the list, Save adds it at the end; a **Remove** button deletes a saved account (and its stored password).
- **Send test email (my call):** sends the test template to the signed-in owner's email from Robust, or to an address typed in the dialog. It uses the values in the dialog, even unsaved, and shows the server's answer in plain words ("Signed in and sent" / "The server refused the password" / "Couldn't reach smtp.gmail.com on port 587"). **(my call)** A test email counts against the account's limits, since it really was sent.
- A mockup of the cards and pop-up was approved by the owner: `.superpowers/brainstorm/55-1793470221/content/email-cards.html`.

## 5. Several accounts, limits and the queue

- **(owner)** The owner can set up several accounts. The ones not paused form an ordered list.
- Every email goes into a queue first; a background worker sends it with the **first** account in the order that is under its limits and not marked "needs attention".
- **Counting (my call):** the grid counts what each account has sent in the last hour and the last 24 hours (rolling windows, since providers differ on when their day starts). An account at its hourly or daily limit is skipped until the window frees up.
- **When the provider says no:**
  - Wrong password / not allowed to sign in → the account is marked **Needs attention** and skipped until the owner saves it again or a test succeeds. The email goes to the next account.
  - "Too many messages" or a quota reply → the account counts as full for the rest of the window; next account.
  - Can't connect, timeout, or another temporary (4xx) reply → try the next account; retry this one later with a growing delay.
  - Permanent refusal of the **recipient** (for example the address doesn't exist) → that email fails; it isn't retried on other accounts. It's logged without the address in the main log.
- **When every account is full or failing,** emails wait in the queue and go out as soon as an account frees up.
- **Expiry:** each queued email carries an expiry set by the feature that sent it (a password-reset link is useless after 30 minutes). An expired email is dropped and counted as failed, not sent late.
- **The queue survives a restart (my call)**, so a reset email isn't lost if the launcher restarts. It is kept in the data folder with each message body and recipient **protected by data protection**, because it contains live links. It's bounded (the builder picks a limit, for example 1,000 messages); when full, new emails are refused and the feature that asked says "couldn't send email right now".
- **Warnings (my call):** the Email page and the staff dashboard show a banner when (a) every account is above 80 % of a limit, (b) an email has waited more than 10 minutes, (c) an email failed for good in the last 24 hours (expired unsent, recipient refused, or refused because the queue was full), or (d) any account needs attention.
  - On the dashboard the owner's banner links to the Email page. **(my call)** Admins and supervisors see a short banner without account details: "Email has a problem — the grid owner can fix it on the Email page."
- **Recent problems (my call):** the Email page has an owner-only list of emails that failed for good in the last 7 days: when, which template, the avatar name it was for (when the feature knows it), and the reason in plain words. No email address, no body, no link. Entries older than 7 days are deleted. This is what lets the owner help someone whose reset email never came.
- **"Email is ready"** means at least one account is saved, not paused and not "needs attention". Features (2.1–2.3) ask the sending service this before they show email-dependent links.

## 6. Email wording (templates)

- **(owner)** The owner can edit each email's wording, and every template ships with default wording that works as-is. A **Reset to default** button puts the default back.
- Templates in this work: **Test email**, **Confirm your email** (2.1), **Reset your password** (2.3), **Account approved** (2.2). Later features add theirs the same way.
- Each template is a subject and a body with fill-in words: `{GridName}`, `{AvatarName}`, `{Link}`, `{ExpiresIn}` — the builder settles the exact list per template. **(my call)** No support-address fill-in word: there's no grid-wide support address, and replies already go to each account's Reply-to.
- **(my call)** The owner edits plain text written in Markdown with raw HTML switched off, the same rule as the legal pages (audiences design §4). The grid sends both a plain-text and an HTML version made from it, so nobody writes HTML and nothing unsafe gets into an email. Fill-in values are escaped before going into the HTML.
- **(my call)** A template that's missing a required fill-in word can't be saved (a reset email without `{Link}` is useless); the page says which word is missing.
- **Preview** shows the subject and both versions with sample values; **Send test** sends that template to the owner with sample values.
- Where it's edited: a "Wording" section on the Email page (or its own page `/security/email/wording`, builder's choice).
- Storage is keyed by language from the start (like the legal pages); only the default language exists until 1.5.

## 7. Security

- **Passwords:** stored only protected by ASP.NET data protection (its own purpose string, for example `DreamGrid.Email.v1`), in the owner-only data folder. Write-only in the page. Never logged, never in the audit log, never in error text. Removing an account deletes its stored password.
- **Keys and backups (my call):** this uses the launcher's existing data-protection key ring (`Data/keys`) unchanged, the same as the two-factor secrets. Whoever has a copy of the whole data folder can decrypt both; changing how the keys themselves are protected is outside this work. The owner manual says to keep data-folder backups private.
- **Connection security:** the named cards always use TLS (STARTTLS or SSL/TLS as in §3) and refuse to fall back to plain text. **Other server** may choose "none" only with a visible warning, for a LAN mail server. Certificates are checked; no "accept any certificate" switch.
- **Audit log:** adding, changing, pausing, reordering or removing an account; editing or resetting a template; sending a test email. It records who, what and when — never passwords, message bodies or links.
- **No open relay:** only the launcher's own code queues email; there is no endpoint that sends arbitrary mail to arbitrary addresses. The test-email button is owner-only and rate limited (my call: a few per minute).
- **Header safety:** the "From" name, subject and fill-in values can't inject extra headers (MailKit's address and header types, not string building).
- **Logs:** the main log records account name, outcome and the server's reply code, never the recipient's full address or the body.

## 8. Building blocks (worklist, unchanged)

- **MailKit** for SMTP, so Windows and Debian behave the same (worklist 1.4). No `System.Net.Mail`.
- **Debian:** CA certificates must be present for TLS (worklist Phase 4); add `ca-certificates` to the `.deb` dependencies if it isn't there already.
- Background sending uses the launcher's existing background-work pattern; the store follows the existing file stores (atomic writes, a damaged file never reads as "empty", as `TwoFactorStore` does).

## 9. Left to the builder

- File names and layout of the account store, counters, queue and templates in the data folder.
- The exact retry delays, queue size and send concurrency (one at a time is fine).
- The shape of the sending service other features call (for example `Queue(template, to, values, expiresAt)` and `IsReady`).
- The exact default wording of the four templates.
- How tests fake SMTP (for example a small in-process SMTP server in the integration tests).
- Whether the dashboard banner and the Email page share one component.

## 10. Later

- Office 365 / Microsoft 365, and OAuth sign-in for Gmail or Microsoft.
- Translated templates (1.5).
- Mass email to residents; email alerts to staff.
- More provider cards as free tiers change.
