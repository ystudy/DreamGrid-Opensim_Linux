# Password recovery — design

Work list item 2.3 (`docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`), with the two-factor part it needs. Builds on email (plan 8a, `2026-10-31-email-design.md`) and the audiences and security work (`2026-10-30-audiences-security-design.md`).

## 1. Why

Today, when someone forgets their password, the owner resets it by hand. Password recovery lets residents, staff and the owner get back in on their own, by email, without the owner stepping in, while making it hard for someone else to use "forgot password" to take an account over or to flood someone's inbox.

Grids differ a lot. Some are a home lab, some are a school or club, some are a public game grid. **Each owner decides how strict recovery is**; DreamGrid suggests a level from one plain question in Setup, and the owner can fine-tune it later.

**Success:** a resident who forgot their password asks for a reset, gets the email, sets a new password and signs in, in a couple of minutes, with nobody else involved. On a grid set up as "Public grid", an admin who has two-factor needs their code as well as the email link. Someone who only knows an avatar name gets nowhere and learns nothing.

## 2. Goals, non-goals, anti-goals

**Goals**
- One "Forgot password" page, on the guest side, for residents, staff and the owner, sending a single-use reset link by email.
- Three recovery rules, one each for **residents**, **staff** (admins and supervisors) and the **owner**. Each is one of: **Off**, **Email link**, **Email link + two-factor**.
- Two-factor sign-in for residents, built in but asleep: residents see it only when the residents' rule is "Email link + two-factor".
- One question in Setup ("What kind of grid is this?") that fills in all three rules and the existing "staff must use two-factor" switch; fine-tuning on the Advanced page.
- The viewer's "forgot password" link goes to the new Forgot password page.

**Non-goals (later list)**
- Recovery when someone's own email account is hacked. Any email-based recovery falls with it; out of scope **(owner)**.
- Requiring two-factor for residents at sign-in (only for recovery here).
- Sign-up (2.1), pending accounts (2.2), the resident account page (2.4: change email, change password). The resident two-factor page added here is the first page of that account area and 2.4 builds next to it.
- Recovery without email (security questions, SMS, an admin-issued code).
- Recovery on a Region Server or Connected Server: they have no email (the Email page is 404 there), so recovery stays manual there.

**Anti-goals** (would be a failure even if it works)
- The page tells a stranger whether an avatar name or email is on the grid.
- Someone can fill a resident's inbox by pressing "Send" over and over.
- A home-lab owner is pushed into two-factor or extra steps they didn't choose.
- Turning a stricter rule on locks everyone out at once.
- Turning on OpenSim's `AllowSetPassword` on Robust's private port, which is unauthenticated and open to the LAN (`docs/SECURITY-NOTES.md`).

## 3. Who uses it

| Group | Who | Where they sign in afterwards |
|---|---|---|
| Residents | Any avatar below the supervisor level | `/guest/sign-in` |
| Staff | Supervisors and admins (`SecuritySettings` levels) | `/login` |
| Owner | The owner's avatar | `/login` |

**One recovery flow, on the guest side, for everyone (owner).** There is one "Forgot password" ask page and one reset page (the page the email link opens), both on the guest side (names of the builder's choosing, e.g. `/guest/forgot-password` and `/guest/reset-password`, next to `/guest/sign-in`). The staff sign-in page `/login` links to the same ask page. Being on the guest side, they are exempt from Admin networks (`AdminNetworkFilter` already lets `/guest` through), so an admin at home can open the link from their email. Signing in to the staff side afterwards still follows Admin networks as today. The reset pages need no sign-in and must not be behind the guest `TermsGate` (someone who can't sign in can't accept terms).

The rule that applies is chosen by **the account found**, not by who asks: a staff avatar gets the staff rule, the owner the owner's rule, so the shared page is never a softer back door into a staff account. The pages only ever change a password; they never sign anyone in.

The pages exist only where there is a guest side (Local, Simple Public, Grid Asset Server), like the other guest pages (`GuestSideOnly`). On a Region Server or Connected Server there is no email and no guest side, so the staff sign-in shows no "Forgot password" link there (or one that says to ask the grid owner; the builder's call).

## 4. The three rules

Each group's rule is one of:

- **Off.** No reset email is ever sent for this group's accounts (the reply stays the normal one, §5.2); the owner resets by hand.
- **Email link.** A single-use link, valid for 30 minutes, lets the person choose a new password.
- **Email link + two-factor.** The same link, then, **if the person has two-factor set up**, a code from their authenticator app or one of their backup codes before they can choose the new password. Someone who has not set up two-factor gets the email link alone. That is the owner's decision (it keeps a stricter rule from locking everyone out at once); the Advanced page says so in a sentence under the choice.

Lost phone **and** lost backup codes: the person can't finish; the page says to contact the grid owner, who resets the password by hand as today (and the owner's existing "reset two-factor" on the Security page).

### Levels from Setup

| Setup answer | Residents | Staff | Owner | "Staff must use two-factor" switch |
|---|---|---|---|---|
| **Home lab** | Email link | Email link | Email link | off |
| **School or club** | Email link | Email link + two-factor | Email link + two-factor | off |
| **Public grid** | Email link + two-factor | Email link + two-factor | Email link + two-factor | on |

All agreed by the owner.

- The Setup question is plain: "What kind of grid is this?" with the three answers and one line under each. It is asked once, in the Setup checklist, on plans with a guest side (my call: on a Region Server or Connected Server it isn't asked, since recovery doesn't exist there; the switch keeps its default). Exact position in the checklist is the builder's call (after the owner's avatar is made is natural).
- **Existing installs** (upgraded, Setup already done): the three rules start as **Off** (nothing changes until the owner chooses), and the Advanced page shows a one-line note that password reset is available. My call: an upgrade never turns on something that sends email on its own.
- The **Advanced** page gets a "Password reset" section: the three rules (each Off / Email link / Email link + two-factor), the "staff must use two-factor" switch (the same setting the Security page has today; both pages change the same value), and a button to apply one of the three Setup levels again. Who may change it: the owner only (OwnerPolicy), like the Security page. Exact layout is the builder's call.
- **The guest door and staff two-factor** (my call): today `/guest/sign-in` never asks staff for their staff two-factor code, because it opens nothing on the staff side. That stays. The guest door asks for a code only from someone who set up two-factor **as a resident**, from the resident two-factor page; staff two-factor and resident two-factor are kept apart (a separate store or a marker; the builder's call), so no existing staff sign-in changes. A staff avatar's recovery uses their staff two-factor (§3: the rule follows the account).
- **When two-factor is in the residents' rule**, signed-in residents get a "Two-factor sign-in" page (set up, new backup codes, turn off), the same as staff have at `/profile/two-factor`, and the resident sign-in asks for the code once they've set it up. When the rule is changed away from two-factor, residents who already set it up **keep it** (my call): their sign-in still asks for the code, and their page still lets them turn it off. Nobody silently loses protection they chose.
- **When email isn't set up** (no working mail account), the rule can still be set, but the Forgot password page says "contact the grid owner" and the Setup step and Advanced section say "Set up email first" with a link to `/security/email`.

## 5. What the person sees

1. **Ask.** The page asks for **avatar name (first and last) and email address** (my call: both must match, so knowing only a name, which is public in-world, is not enough to trigger an email; that's how Wifi did it too). Bot protection (my call): both ask pages call `BotGuard.Check` like sign-up does, so they follow every layer the owner switched on in the Bots settings (honeypot, its per-address limit, proof-of-work, image check). A failed bot check shows the same error sign-up shows; it says nothing about the account, so it gives nothing away, and a real person whose proof-of-work failed isn't left waiting for an email that never comes. The address limit in §6 still applies on top.
2. **Always the same reply**, whether or not the name and email match an account, the rule is Off for that account, or the request was over a limit: "If that name and email match an account, we've sent a link. It works once, for 30 minutes. Check your spam folder." Response time shouldn't give it away either (the email is queued, not sent, inside the request; any Robust lookup happens on every request).
   - The one exception is about the grid, not the account: when all three rules are Off, or email isn't set up, the page says "Password reset isn't turned on for this grid. Contact the grid owner." before asking anything. When only some groups are Off, the page asks as normal, and an account whose own group's rule is Off gets the normal "if that matches…" reply and no email (saying "Off" there would tell a stranger which group the account is in).
3. **Email.** The existing `reset-password` template (`EmailTemplates.ResetPassword`, already editable on the Email wording page) with `{Link}` and `{ExpiresIn}` = "30 minutes". The link carries the token only, never the name or email.
4. **Open the link.** A link that's used, expired, replaced by a newer one or wrong: "This link has expired or was already used. Ask for a new one." with a link to the ask page.
5. **Two-factor** (rule "Email link + two-factor" and the person has it set up): "Enter the code from your authenticator app, or a backup code." Wrong codes count in the existing `LoginThrottle` for that name and address at the matching door (guest or staff), as at sign-in; while locked, no code is checked. A used backup code is spent.
6. **New password**, typed twice. Rules: at least 8 characters (my call; matches common guidance and isn't hostile on a home lab), not on a short built-in list of common passwords (list size and source the builder's call, shipped with DreamGrid, no network call), and not the avatar's name. Plain-words errors.
7. **Done.** "Your password is changed. Sign in with your new password." with a link to the right sign-in page. Every session the person had ends: staff sessions (`StaffSessions.EndAll`) and guest sessions (`GuestStamps.Rotate`), and the viewer password is the new one at once.
8. A short notice email afterwards, "Your password on {GridName} was changed. If it wasn't you, contact the grid owner." (a new template, editable like the others; my call: it's the only warning a person gets if someone else did it).

## 6. Limits (always on, at every level)

- **Per account:** at most 3 reset emails per account per hour, and each new link replaces the last (`EmailTokens.Issue` already does this). Requests over the limit get the normal reply and no email.
- **Per address (IP):** at most 10 requests per hour from one address. Over it: the normal reply, no lookup, no email.
- **Per email address typed:** counted like the account, so typing one victim's email with many names doesn't flood them.
- Counts survive a restart (persisted like `LoginThrottle`), keyed by hashes, never the typed text.
- The numbers are my call; the builder may keep them as constants. They are not on the Advanced page.
- Every reset request, link used, code failure and password change goes to the audit log (who, which door, from which address, outcome), never the token, code, password or the typed email.

## 7. How the password is changed

Not through Robust's `setpassword` (it needs `AllowSetPassword = true` on the unauthenticated private port). The launcher already reads Robust's database directly as `robustuser` (`StaffDirectory`); the new password is written the same way OpenSim's `AuthenticationServiceBase.SetPassword` does it (new random salt, `md5(md5(password) + ":" + salt)`, into `robust.auth` for that principal), so the viewer and website both accept it at once. The exact query and transaction are the builder's call; a failure says "Couldn't change the password right now. Try again in a few minutes." and the link is **not** spent (it's redeemed only after the write succeeds; `EmailTokens` may need a check-then-redeem split for that).

Token purpose: `reset-password`; principal: the account's PrincipalID; lifetime 30 minutes; stored hashed in `<data>/guests/email-tokens.json` (`EmailTokens`, already built for this).

## 8. Viewer links

`bin/Robust.HG.ini.proto` `[GridInfoService]` still points `password=` at Wifi (`/wifi/forgotpassword`). `GridConfigWriter` sets it to the Forgot password page on the website (`http://{host}:{WebPort}/guest/forgot-password`, like `welcome=`) on plans with a guest side. `register=` belongs to sign-up (2.1) and isn't changed here.

## 9. Testing

Unit tests: the three rules and the level table; the same reply in every case (match, no match, rule Off for that account, over a limit); staff and owner accounts get their own rule on the shared page; the reset pages open from outside Admin networks; token single use, expiry, replacement, not spent when the write fails; the limits per account, email and address, and their persistence; password rules; two-factor step with code, backup code, wrong code counting in the throttle; residents keeping two-factor after the rule changes; nothing logged that's secret. Integration tests through the real website (as the existing Debian CI does): a full reset with the email captured; the rule Off; the two-factor step; old sessions refused after a reset; the Setup question setting the rules; the Advanced section owner-only; the viewer `password=` link in the written Robust.HG.ini.

## 10. Left to the builder

Page paths and names; where the Setup question sits in the checklist and its exact wording; the Advanced section's layout; the common-password list; how the limits are stored; the DB write; how resident two-factor shares code with staff two-factor (`TwoFactorStore`, `TwoFactorGate`); wording of pages and the new notice email.
