# Support tickets for guests — design

Guests ask the grid's staff for help through GridGate: they send a ticket from the guest side, read staff's replies and answer back. Staff work the tickets on the staff side. The owner asked for "a support system for guests" on 2026-11-05, showing another grid's ticket form (name, email, category, subject, message, evidence link, logs). The answers below are the owner's, given in the session that day; choices not quoted are marked **(my call)**.

Legend, as in the work list: **[S]** security requirement, **[X]** cross-platform note, **[?]** decision still open.

## Owner's answers (2026-11-05)
- **Who works tickets:** the owner, admins and supervisors. All of them see and answer every ticket, abuse reports included.
- **Who sends:** anyone. A signed-in resident sends at once; someone not signed in gives an email address and confirms it first. Until email is set up, only signed-in residents can send.
- **How staff hear:** they check the Tickets list. Anyone who switches on the new-ticket notice under Email notices also gets an email. There is no menu badge.
- **A ticket's life** (owner: "that looks ok"):
  - States are Open, Waiting for the guest and Closed.
  - Staff close a ticket. A guest reply within 14 days opens it again; after that, the guest starts a new ticket.
  - A staff member can take a ticket, and staff can add staff-only notes.
  - Closed tickets are kept for a year, then deleted. A ticket linked to a takedown case is kept as long as the case.

## Goals
- A locked-out person, a visitor from another grid or a resident can all get help without staff giving out their own email.
- **[S]** Security first: a public form that can't be used to flood staff, spam people, or reach anyone else's ticket.
- The same on Windows and Debian **[X]**: tickets are files in the data folder, like takedown cases. No new database tables.

## Not in this design
- **File uploads.** Evidence is a link and pasted text. Uploads from the public are the riskiest part, so they're a later item.
- **Replying by email.** The guest replies on the website through their private link, not by answering the email.
- **Live chat**, **satisfaction ratings** and **ticket priorities set by the guest** **(my call: a guest-set priority is mostly noise; staff can mark a ticket Urgent)**.
- **Languages.** English only until work list 1.5. The email wording goes through the existing editable templates, which are already stored by language.

## The guest side

### Sending a ticket: `/support` (public, guest side only)
- Linked from the guest pages' footer next to the legal links, from the Connect page's Help card, and from the sign-in and forgot-password pages ("Can't sign in? Ask for help").
- **Fields:**

  | Field | Rule |
  |---|---|
  | Your name | Required when not signed in, 1–64 characters; prefilled and fixed when signed in |
  | Email | Required when not signed in; prefilled from the account, and not shown, when signed in |
  | Category | Accounts and sign-in · Land and regions · Report abuse · Technical problem · Something else **(my call)** |
  | Subject | Required, 3–120 characters |
  | Message | Required, 10–8,000 characters |
  | Evidence link | Optional, one `https://` or `http://` address, at most 500 characters. Shown to staff as text with a copy button, **never as a clickable link or a picture** **[S]** |
  | Logs | Optional, at most 20,000 characters of pasted text |
- **[S]** Bot protection: the existing `BotGuard`, form name `support`, with the same shared layers and per-hour limit the owner sets on the Sign-up and bots page (one set of settings for every public form, as today) **(my call)**. A signed-in resident is held to the rate limit but not the puzzle or picture. `BotGuard` gets a rate-limit-only check for that.
- **[S]** At most 3 open tickets per email address or account at a time **(my call)**. A fourth is refused: "You already have 3 open tickets. Reply on one of them instead."
  - Unconfirmed tickets don't count, so a stranger can't lock someone out with fake tickets.
  - Confirmation emails to one address are capped at 3 a day instead, the same way `ResetLimits` caps password-reset emails. Past the cap the page gives its usual answer and sends nothing.
- A line under the button: "Staff will read this. Please don't put passwords in it."

### After sending
- **Signed in:** the ticket is open at once, and the guest lands on its page.
- **Not signed in:**
  - The ticket waits, unseen by staff, and a "Confirm your ticket" email goes to the address.
  - Its link is an `EmailTokens` token, purpose `ticket-confirm`, bound to the ticket's id, valid 24 hours.
  - Opening the link confirms the ticket and opens it.
  - Unconfirmed tickets are deleted after 24 hours.
  - The page says the same thing whether or not the address has an account or other tickets **[S]**.
- Every confirmed ticket gets a "Your ticket" email with its private link.
  - **Private links are the ticket's own, not `EmailTokens`.** `EmailTokens` makes single-use tokens and revokes older ones, but a guest needs every email's link to keep working.
    - Each link is 256 random bits. Only its SHA-256 is stored, in the ticket's file, with an expiry 30 days after it was sent.
    - It can be opened again and again until then. At most 5 live links per ticket: a sixth drops the oldest.
    - Every email that links to the ticket (`ticket-access`, `ticket-staff-reply`, a lost-link resend) adds a fresh link **(my call)**.
  - Opening a live link gives a session cookie for that one ticket only. It's separate from the guest sign-in cookie, and it's 8 hours sliding.
  - An expired or unknown link shows "This link has run out" and the lost-link form.
- `/support/lost-link` sends a new link for each open or recently closed ticket of an email address.
  - **[S]** The same answer either way: "If there are tickets for that address, we've sent their links."
  - **[S]** `BotGuard` form `support`, and at most 3 such emails per address a day.

### The ticket page: `/support/ticket`
- Reached by the private link, or by a signed-in resident from "My tickets" on their account page.
- **Shows:** the subject, category, state ("Open", "Waiting for you", "Closed"), and the conversation oldest first. Each message shows who wrote it: "You" or "Staff (first name)" **(my call: staff's first name only, not their avatar's full name)**. Staff-only notes are never sent to this page **[S]**.
- **Reply box:**
  - A reply on an Open or Waiting ticket sets it to Open.
  - A reply on a Closed ticket within 14 days opens it again.
  - After 14 days the box is replaced by "This ticket is closed. Start a new ticket", with a link.
- Every message is shown as plain text, line breaks kept, nothing rendered as HTML or links **[S]**.

## The staff side

### Menu
"Tickets" in the People group, for all staff, on plans with a guest side. It opens `/people/tickets`.

### The list: `/people/tickets`
- **Filters:** Open (default) · Waiting for the guest · Closed · Mine · All. There's also a search over subject and name.
- **Columns:** number (`T-0001`), subject, category, from, state, taken by, last message (time). The newest activity comes first.
- An Urgent tag shows on tickets staff marked urgent. The "Report abuse" category is tagged too.

### A ticket: `/people/tickets/T-0001`
- **The conversation:** like the guest's view, plus staff-only notes in a yellow box.
- **Who sent it:** their name, email (shown to staff), and a link to their person page if they have an account here.
- **Reply** (goes to the guest): it sets the ticket to Waiting for the guest unless staff tick "Keep it open".
- **Note** (staff only), **Take** / **Let go**, **Mark urgent**, **Close** and **Reopen**.
  - Close is one press, since a guest reply or Reopen undoes it.
  - These follow the confirmation rules in the menu and looks design.
- **Link to a takedown case:** on a Report abuse ticket, the owner or an admin can link the case number (`TD-…`), after a check that the case exists (`TakedownStore.Get`), and the case page links back. **Supervisors don't see the link or the case number, and can't add one** (owner, 2026-11-05), matching the takedown pages, which are for the owner and admins only (`TakedownAccess.MayUse`).
- `/support` and its pages sit outside the `/Guest` folder, because that folder needs the guest sign-in and the Terms gate. They get `GuestSideOnly` of their own.
- **[S]** Every action is checked again on the post and written to the audit log:
  - `ticket-reply`, `ticket-note`, `ticket-take`, `ticket-close`, `ticket-reopen`, `ticket-urgent`, `ticket-linked`;
  - with the ticket number and the staff member, never the message text.

### Notices
- **New ticket:** the template `ticket-new` goes to each staff member who has it switched on under Email notices (a new "New support tickets" switch, off by default **(my call)**). It holds the number, category and subject only, never the message or the guest's email **[S]**.
  - **[S]** It's sent at most once an hour per staff member, like `SignUpNotices.MinGap`. Tickets that come in meanwhile are named together in the next one ("3 new tickets: T-0012, T-0013, T-0014").
  - The Emails page shows this switch to every staff member, supervisors included; today it saves only for those who may approve sign-ups. The notice store keeps an opt-in list next to its opt-out list.
- **Guest replied:** `ticket-reply-guest` goes to the staff member who took the ticket, if they have the switch on. On a ticket nobody has taken, it goes to every staff member with the switch on. It's batched hourly the same way.
- **To the guest:**
  - `ticket-confirm` asks them to confirm the ticket;
  - `ticket-access` gives the private link;
  - `ticket-staff-reply` says "staff answered", with the private link, and holds no message text **[S: the reply stays on the site]**.
- All of these are editable under Email server → Email wording, like the existing templates.

## Storage and keeping
- One JSON file per ticket under `<data>/tickets/`, written through `JsonStateFile`, owner-only (0600 on Linux, the data folder's ACL on Windows) **[X]**. An index file holds the list's columns, so the list doesn't open every file.
- The format:
  - id (Guid), number (`T-0001`, counting up);
  - state, category, subject, urgent, the "taken by" staff id;
  - the sender: account id or null, name, email;
  - created and confirmed times, and when it was closed;
  - the linked case number;
  - messages: who, when, text, and whether it's a staff note.
- `<data>/tickets` is made owner-only at start, like the other private folders (`CreateOwnerOnly`).
- **Keeping:** an hourly sweep (like `SignUpCleanup`):
  - deletes unconfirmed tickets after 24 hours;
  - deletes closed tickets 365 days after closing. A ticket linked to a takedown case waits for the later of that and 365 days after the case was closed. Takedown cases are never deleted, so while a case stays open its tickets stay too **(my call)**.
  - Deleting also drops the ticket's tokens.
- **[S]** Limits:
  - At most 200 messages per ticket.
  - At most 2,000 open tickets in total. Past that, the form says the grid isn't taking new tickets right now, and the owner sees a warning on Home.

## Plans
Only where there is a guest side (`AdminRule.HasStaffPages`): Local, Simple Public and Grid Asset. Elsewhere `/support` and the staff pages are 404, like the other guest pages.

## Tests
- **The form:**
  - Every field rule.
  - Bot protection with the `support` form name.
  - The 3-open-tickets limit.
  - A signed-in resident's ticket opens at once.
  - An anonymous one waits until confirmed, and the confirm token works once and only for its ticket.
- **Access:**
  - A private link opens only its own ticket.
  - A guest cookie for ticket A can't read ticket B.
  - Staff notes never appear in the guest page's HTML.
  - Evidence links and messages are rendered as text: a `<script>` in a message shows as text.
- **States:**
  - A reply sets Waiting.
  - A guest reply reopens a ticket within 14 days, and not after.
  - The sweep deletes on the right days and keeps linked tickets.
- **Staff:**
  - Supervisors can do everything except the takedown link, which they neither see nor can post.
  - Each action is audited without the text.
  - The notices go only to staff who switched them on, and hold no message text.
- **Plans:** 404 on Region Server and Connected.

## Steps (one PR each)
1. **The guest form and storage:** `/support`, confirmation, the ticket store, the private link and the guest ticket page, with replies.
2. **The staff side:** the list, the ticket page, the actions, the audit, notes, take, urgent and close.
3. **Notices and keeping:** the new templates, the Email notices switch, the sweep and the takedown link.

## Left to the builder
- The exact layout of the forms, in the panel style of the Connect page and dashboard.
- The wording of the emails, which the owner can edit afterwards.
- How the index file is kept in step with the ticket files, as long as a damaged index is rebuilt from the files.

## Decisions still open
- **[?]** These are **(my call)**:
  - the category list;
  - the 3-open-ticket limit;
  - Urgent set by staff rather than the guest;
  - showing staff by first name;
  - the new-ticket notice being off by default.
