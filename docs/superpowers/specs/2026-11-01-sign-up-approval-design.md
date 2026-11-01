# Sign-up approval (pending accounts): design

This is work list item 2.2 (`docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`). It also finishes the "admin approval" part of 2.1 and the hidden approval switch from 1.3. It builds on resident sign-up (`2026-10-31-sign-up-design.md`: the pending sign-ups list, level −1, the confirm link, the clean-up), email (`2026-10-31-email-design.md`) and audiences and security (`2026-10-30-audiences-security-design.md`: staff roles, policies, the audit log).

Marks: **(owner)** means the grid owner decided it in the design conversation. **(my call)** means the designer settled a detail the owner didn't speak to.

## 1. Why

Today a new sign-up gets in one of two ways. With email set up, the person clicks the confirm link. Without email, they get in at once. Nobody on staff is involved. On a private grid, email often isn't set up at all, or the person can't find the email. The owner wants staff to be able to let people in by hand **(owner)**: "if a user can't find the email they should be able to be approved. This is for private grids where email may not be relevant."

**Success:** a staff member opens Your world and sees who signed up. They let someone in or turn them away with one click. That person can then log in on the website and in a viewer with the name and password they chose.

## 2. Goals, non-goals, anti-goals

**Goals**
- Two ways in, both open at once **(owner)**:
  - **Email link (self-approved).** The guest registers, accepts the terms and clicks the link in the email. The site gives them their login.
  - **Staff approval.** The guest registers and waits. Staff approve, the guest is told (by email when email is set up), and the same login works on the website.
  - With email set up, whichever comes first lets the person in.
- A **New sign-ups** card on the dashboard (Your world, `Pages/Index`), next to the grid status and Who's online. It lists each sign-up's name with Approve and Deny buttons, or a badge saying how the person got in **(owner)**.
- **Deny**: removes the account and sends a polite email with an optional note **(owner accepted the designer's suggestion)**.
- **An owner switch: "When email isn't set up, new sign-ups wait for staff approval."** When it's off, sign-ups without email get in at once, as today **(owner: "a switch for that logic")**.
- **An owner switch: "Who can approve new sign-ups":** owner only; owner and admins; or owner, admins and supervisors **(owner: "a switch for that")**.
- Staff hear that someone is waiting by email, sent at most once an hour **(owner: "send them an email")**, and by a badge in the menu.
- A row stays on the board for **14 days** after the person is approved, then drops off, "so it is not clogging things up" **(owner)**.
- A full **sign-ups list** with **sort and search** **(owner)**.

**Non-goals**
- Choosing a starter avatar or copying an outfit (the rest of 2.1).
- Blocking an email address or a name after a deny (it could come later).
- Languages (1.5). The new emails are stored per language like the others, English only for now.
- Mass email to residents (2.12).

**Anti-goals**
- A sign-up that nobody can let in: someone stuck because they can't find the email, with no staff path.
- A burst of sign-ups (or a bot that gets past the protections) flooding staff inboxes.
- Staff seeing or handling anyone's password.
- The board filling up with old rows.
- Someone without the approval right approving or denying by posting the form directly.
- An upgrade that quietly changes how an existing grid without email behaves.

## 3. States a sign-up can be in

The sign-up design tells "waiting" apart from "suspended" with the launcher's pending list. Both sit at level −1 in Robust. This design keeps that rule and extends the list into a **sign-ups board** (my call: one store in `<data>/guests/`, replacing or growing `pending-signups.json`, written atomically and handling damaged files the way `PendingSignUps` does today). Each entry holds the PrincipalID, the avatar name (avatar names are public, and search and sort need them), when the account was made, its state, when the state changed, and which staff member changed it. It never holds the email, password or token.

| State | Level | Shown as | How it gets there |
|---|---|---|---|
| Waiting for email | −1 | "Waiting for email" plus Approve/Deny | Sign-up with email set up |
| Waiting for staff | −1 | "Waiting for approval" plus Approve/Deny | Sign-up without email, while the switch is on |
| Self-approved | 0 | ✉ "Self-approved (email link)" | The person opened the confirm link |
| Approved | 0 | ✔ "Approved by {staff name}" | A staff member approved |
| Let in at once | 0 | "Signed up (no approval needed)" | Sign-up without email, while the switch is off (my call: it still shows, so staff see who arrived) |
| Denied | (deleted) | ✖ "Denied by {staff name}" | A staff member denied (my call: shown for 14 days like the others, then gone) |

**Decoy sign-ups are never on the board** (my call, for safety). When someone signs up with an email address another account already uses, `SignUps.SubmitAsync` deliberately still makes a −1 avatar with no link, so the reply never reveals whether the address is registered. That entry gets its own **decoy** state. It never shows on the board, the list, the badge or the staff email, and it can't be approved. The clean-up still deletes it after 24 hours, as today. Approving one would give a second account someone else's address, and that account could then receive their password resets. The guest-facing "staff can approve you too" line is shown on decoy replies as well, so nothing gives them away.

Each entry keeps a fixed **made** time, separate from the time of the last link (`SendLink` resets the pending timestamp today). The 30-day wait counts from **made**.

Both "waiting" states count as waiting. Only waiting entries are ever deleted by the clean-up, as today. Rows in the four finished states leave the board **14 days** after their state changed **(owner)**. That removes the row only. It never touches the account.

## 4. The two paths

### With email set up (`EmailSender.IsReady`)
1. Sign-up works as today: the account is made at −1, the confirm link goes out, and the page says "check your email". The page adds one line: "Can't find the email? Staff can approve you too."
2. The person shows on the board as **Waiting for email**, with Approve and Deny.
3. If the person opens the link first, sign-up confirms as today. The entry becomes **Self-approved**, and the board shows the badge.
4. If staff approve first, the entry becomes **Approved**. The confirm token for that account is spent or ignored, so the link page says "You're already approved. Sign in." rather than an error (my call). The **Account approved** email goes out (the existing `EmailTemplates.AccountApproved` template; `{Link}` is the guest sign-in page).
5. **Waiting time.** A waiting entry is now kept for **30 days** instead of 24 hours, so staff have time to act (my call). The confirm link itself still lasts 24 hours, and "Send it again" still works while the entry waits.

### Without email set up
- **Switch on:** the account is made at −1 and the entry is **Waiting for staff**. The page says "Thanks. The grid's staff will look at your sign-up. Once you're approved, you can sign in here with the name and password you just chose." It doesn't sign them in.
- **Switch off:** the account is made at 0 and the person is signed in to the guest side at once, as today. The entry is **Let in at once**.
- When staff approve a sign-up without email, no message is sent. Staff tell the person however they like **(owner: "staff approves and notifies guest the login information")**. The site never shows staff a password: the login is the name plus the password the person chose (my call, discussed with the owner).

### Signing in to the website while waiting
- For an account waiting for email, the guest sign-in still says "Confirm your email first. [Send the link again]", plus "or wait for staff to approve you" (my call).
- For an account waiting for staff: "Your account is waiting for approval by the grid's staff."
- After approval (either way), sign-in works as it does for any resident. The first sign-in after a staff approval opens "How to connect" with "You're approved. Log in as {name}." (my call; this is the owner's "the site has the login ready for them").
- The viewer refuses the login while the account is at −1, as today.

## 5. Approve and deny

Both actions are posts with an antiforgery token. The server checks the "who can approve" switch on every post, not just when it draws the buttons. An action on an entry that's no longer waiting does nothing and says "already done by {name}". So if a link click and an Approve land at the same moment, whichever wins, nothing breaks, and the board shows which one happened.

**Approve**
- The level goes from −1 to 0 through the same console path and operation lock sign-up uses. The entry becomes Approved.
- The Account approved email is sent when email is set up.
- Audit log: who approved, whom, from where.

**Deny** **(owner accepted)**
- Asks to confirm first: "Deny Maple Brightwater?". It has an optional note box (plain text, up to 500 characters, my call).
- The account is deleted the way the clean-up deletes it (`IAccountRemover`), so the name is free again. Nothing else exists for an account that never logged in.
- When email is set up, a polite **Sign-up not approved** email goes out. It's a new template with `{GridName}`, `{AvatarName}` and `{Note}`, editable on the Email wording page. With no note, the email gives no reason.
- Audit log: who denied, whom, from where. The note isn't logged.
- There's no block list. The person can sign up again, and the existing rate limits still apply.
- If the account can't be deleted right now (Robust or the database isn't answering), the entry stays waiting, no email goes out, and the page says "Couldn't deny right now. Try again in a few minutes." (my call).

Entries already waiting for staff stay waiting when the owner turns the switch off (my call). Only new sign-ups follow the new setting.

## 6. The switches

Both live on the owner's **Bots page** (`/security/bots`), where the hidden "An admin approves each new account" switch already waits for this work. Both are owner only (OwnerPolicy), and changing either one is written to the audit log.

1. **"When email isn't set up, new sign-ups wait for staff approval."** This reuses `BotSettings.AdminApproval` (my call: same meaning, new wording). It defaults to **off**, so an existing grid keeps working as it does today after the upgrade (my call; see §11). The page warns when sign-up is open, email isn't set up and this switch is off, as it already warns about open sign-up without email.
2. **"Who can approve new sign-ups":** owner only / owner and admins / owner, admins and supervisors. Default: **owner and admins** (my call). It's a new setting next to `AdminApproval` (a settings schema bump, with migration filling in the default).

The Bots form keeps whatever values it doesn't post, as it already does for `AdminApproval`.

## 7. Where staff see it

### The dashboard card **(owner; layout shown in the mockup)**
- A **New sign-ups** card on Your world, after the grid status and next to Who's online. Its title carries a "{n} waiting" badge.
- Rows: name, "signed up 20 minutes ago", then either the Approve and Deny buttons (waiting rows, for approvers) or the state badge.
- Waiting rows come first, oldest first. Then the rest, newest first. The card shows up to 10 rows (my call) and links to the full list.
- Staff who can't approve see the card without buttons (my call). It's hidden on plans with no guest side (`GuestSideOnly`).
- The dashboard stays admin-only (`AdminPolicy`, as in `Program.cs` today). **Supervisors never see the card**, even when they're allowed to approve. They approve from the full sign-ups list **(owner)**.

### The full sign-ups list **(owner: sort and search)**
- A staff page under `/People` (StaffPolicy, so supervisors can open it; for example `/people/sign-ups`), linked from the card and the People page. This is where supervisor approvers work **(owner)**. It shows the same rows as the board: everything waiting, plus 14 days of finished ones.
- **Search** by name, and **sort** by name, sign-up time or state. Paged.
- The same Approve and Deny buttons, with the same rights.

### The People page
- Waiting accounts show "Waiting for email" or "Waiting for approval", with no restore button, as the sign-up design already does for "Waiting for email".

## 8. Telling staff

- **The menu badge:** "{n} waiting", for approvers, on every staff page. It shows whether or not email is set up. For the owner and admins it sits next to Your world and links to the dashboard. For supervisors it sits next to People and links to the sign-ups list **(owner)**.
- The staff email's link follows the same rule: the dashboard for the owner and admins, the sign-ups list for supervisors.
- **The email** **(owner)**, sent only when email is set up:
  - It goes to every approver whose avatar has an email address and who hasn't turned these emails off. Each approver can turn them off on their own profile (next to Sessions and two-factor).
  - It's sent at most once an hour, and only when someone new has started waiting since the last one: "3 people are waiting for approval on {GridName}" with their names and a link to the dashboard. It's a new template, editable like the others.
  - It never includes the applicants' email addresses.
  - It's sent from a background job (my call: the same hourly pass as the clean-up, or a sibling job), through the existing outbox.

## 9. Clean-up

The existing `SignUpCleanup` pass grows to do three things:
- Delete accounts that have been **waiting for more than 30 days** (both waiting states). The audit log notes "signup-expired". As today, it only touches listed accounts at −1.
- Drop **finished rows** from the board 14 days after their state changed **(owner)**.
- Take off the board any entry whose account is gone or was changed outside this flow (for example, set to level 0 on the People page). As today, it only removes the entry.

## 10. Security

- Approve and deny check the approvers setting on the server, need the staff cookie and an antiforgery token, and are audited.
- The staff email and the board hold names only, never applicants' emails, passwords or tokens.
- The deny note goes only into the email. It's never logged or stored.
- Approving only ever raises a listed account from −1 to 0. It can't be used to raise anyone else or to unsuspend a suspended resident.

## 11. Upgrade

- Entries already on the pending list become **Waiting for email**. Their names come from Robust on the first pass (my call), and their 30-day wait counts from when they were made.
- `AdminApproval` keeps its stored value (default off), so no existing grid changes how it behaves. The new approvers setting gets its default.

## 12. Testing

Unit: state changes, including a race between a link click and Approve; the approvers setting enforced on the server; deny deletes only waiting accounts; the 14-day and 30-day clean-up; the hourly staff email (batching, opt-out, no applicant emails); the switch off means people get in at once; the settings migration.

Integration through the real website, with the fake mail server the tests already use:
- Sign-up with email, approved by staff before the link: the link page says "already approved", the approved email is sent, and the website sign-in works.
- Sign-up with email, then the link: the board shows Self-approved.
- Sign-up without email and the switch on: the person waits, staff approve, sign-in works.
- Deny: the account is gone, the email has the note, and the name is free.
- A supervisor's post to the sign-ups list is refused when only admins may approve, and accepted when supervisors may.
- A supervisor still can't open the dashboard.

## 13. Left to the builder

Page paths and wording; the store's file format and whether it replaces `pending-signups.json`; how many rows the card shows; how the menu badge is drawn; how the staff email lists names; where on the profile the opt-out sits; the page size of the full list.
