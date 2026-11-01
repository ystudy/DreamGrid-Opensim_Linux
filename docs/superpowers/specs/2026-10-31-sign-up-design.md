# Resident sign-up — design

Work list item 2.1 (`docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`), first version. Builds on the audiences and security work (`2026-10-30-audiences-security-design.md`: guest side, legal-page gate, bot protection), email (`2026-10-31-email-design.md`) and password recovery (`2026-10-31-password-recovery-design.md`: password rules, request limits, `EmailTokens`).

## 1. Why

Today only the owner can make an avatar. Sign-up lets a new person get an avatar on the grid by themselves, and then tells them exactly how to connect with a viewer.

**Quick and simple (owner).** This first version is a short form, an email link and a "how to connect" page. The extras come later.

**Success:** someone who has never used OpenSim fills in the form, clicks the link in the email, follows the "how to connect" steps and is standing in-world, with nobody else involved.

## 2. Goals, non-goals, anti-goals

**Goals**
- A sign-up page on the guest side: first name, last name, email, password (typed twice).
- An email link confirms the account before it can log in **(owner)**. When email isn't set up, sign-up works without the email step **(owner)**.
- A "How to connect" page that gives the grid's login address and plain steps for a viewer **(owner)**.
- A switch that opens and closes sign-up.
- The viewer's "create account" link (`register=`) goes to the new page.

**Non-goals (later, owner: "the extras will come later")**
- Choosing a starter avatar or copying an outfit (rest of 2.1).
- Admin approval and the pending-accounts list (1.3's approval switch, 2.2).
- Modes beyond open and closed (approval required).
- The resident account page (2.4), languages (1.5).

**Anti-goals**
- A form so long or strict that a real person gives up.
- The form tells a stranger whether an email address is used on the grid.
- Unconfirmed sign-ups pile up and hold names forever.
- Bots can make accounts freely on a grid that has protection switched on.
- Turning on OpenSim's `AllowSetPassword` or any other unauthenticated Robust call (`docs/SECURITY-NOTES.md`).

## 3. Where it lives

- Pages on the guest side, next to `/guest/sign-in` (paths are the builder's call, e.g. `/guest/sign-up`, `/guest/confirm`, `/guest/connect`). They exist only where there is a guest side (`GuestSideOnly`: Local, Simple Public, Grid Asset Server). They need no sign-in, are exempt from Admin networks like the rest of `/guest`, and are not behind `TermsGate` (the form takes Terms acceptance itself, §4).
- **The switch (my call).** "Let people sign up" is **off** by default, on the owner's Security area (the builder's call: next to the Legal pages, or on the Bots page). It can be turned on only while `LegalPages.SignUpGateOpen` is true; that gate already exists and is waiting for this switch. Owner only (OwnerPolicy). Turning it on or off goes to the audit log.
- When sign-up is closed (switch off or the legal gate shut), the sign-up page says "This grid isn't open for sign-ups yet." and shows no form. The guest front page and `/guest/sign-in` show a "Sign up" link only when sign-up is open.
- **Admin approval switch (owner: hide).** The Bots page's "An admin approves each new account" switch (`BotSettings.AdminApproval`) is hidden until approval is built (2.2). The setting stays in `LauncherSettings`, and sign-up ignores it; posting the Bots form keeps whatever value is stored (it must not reset it to off because the field is missing).
- The settings page warns, the way the Bots page does today, when sign-up is open and every bot layer is off, and when sign-up is open without email (anyone can make an account without proving an email address).

## 4. The form

Fields: first name, last name, email, password, password again, a Terms tick ("I accept the Terms of Service and the Privacy notice", linking to both). The bot fields come from `_BotFields`.

On post, in this order:
1. `BotGuard.Check(ctx, "signup")`: every layer the owner switched on. On a fail, the same error the other forms show.
2. **Name rules (my call):** each part 1 to 31 characters (OpenSim's limit is 64 for both together), letters, digits and the hyphen only, no spaces. The pair isn't the owner's name or any reserved name the builder lists (e.g. "Test User", "Grid Owner"). Plain-words errors.
3. **Password rules:** the same `PasswordRules` recovery uses (at least 8 characters, not common, not the avatar's name, both copies match). These allow spaces and `"`, which the console line can't carry, so the typed password never goes on the console (§5).
4. **Email:** looks like an address (one `@`, something on both sides, no spaces). No deeper check.
5. Terms ticked.
6. **Name taken:** said plainly ("That name is taken. Try another.") **(my call)**: a person must know to pick another name, and avatar names are public in-world anyway.
7. **Email already used:** not said. With email set up, the reply is the normal "check your email" reply and the existing owner of that address gets a short notice instead ("Someone tried to sign up on {GridName} with this address. If that was you, you already have an avatar: {Name}. Forgot your password? {Link}") (my call; a new template, editable like the others). Without email set up, the email isn't checked for duplicates at all (my call: nothing would be sent either way, and two avatars sharing one address is harmless on a home lab).
8. **Limits:** at most 3 confirmation emails per account and per typed email address per hour, and the per-address limit from `BotGuard`'s rate-limit layer (counted like recovery's `ResetLimits`, `SendAllowed`/`CountSend`, as a separate purpose and persisted the same way).

Terms acceptance is recorded in the existing `TermsAcceptances` store at sign-up, so the first sign-in doesn't ask again.

## 5. Making the account

The avatar is made through the existing Robust console path (`GridConsoleCommands.CreateUserAsync`, as Setup makes the owner's avatar), with a new random PrincipalID, the email address and a **random console-safe password** (my call). The typed password is then written with `RobustPasswords` (the database path recovery uses), so sign-up and recovery share one set of password rules. Level changes go through the console's `set user level` under `GridController`'s operation lock, with its existing confirmation (my call: the same path every other level change uses). Robust must be running; if it isn't, or any step fails, the page says "Sign-up isn't working right now. Try again in a few minutes." and nothing is half-made: an avatar whose password or level step failed is deleted at once (and if that fails too, the clean-up job in step 6 removes it later).

### Pending is not the same as suspended (my call)

The launcher already treats `UserLevel < 0` as **suspended** (`SuspendedStore`, `StaffActions.RoleOf`, the People page, `GuestPrincipalValidator`, `PasswordResets`). An unconfirmed sign-up also sits at -1, so the viewer refuses it, but it is told apart by a launcher-owned **pending sign-ups list** in `<data>/guests/` (PrincipalID, when made, which never holds the email or password):
- On the list: *waiting for email*. Not on the list and below 0: *suspended*, as today.
- The People page shows "Waiting for email" for listed avatars and offers no "restore" for them, so confirming always takes the email.
- The guest sign-in says "Confirm your email first. [Send the link again]" only to a listed avatar; a suspended one keeps today's answer.
- Confirming removes the avatar from the list. The clean-up job deletes **only** listed avatars, so a confirmed resident who is later suspended is never deleted.

### With email set up (`EmailSender.IsReady`)

1. The avatar is made and its level set to **-1**, and it is added to the pending list. Robust's `MinLoginLevel` is 0 (the default; the config files leave it commented out), so the viewer refuses the login. **[?]** Check the message the viewer shows (expected: "account is not active" or similar).
2. The guest sign-in refuses it as above.
3. A link goes out: `EmailTokens` purpose `confirm-signup`, the PrincipalID, valid **24 hours** (my call), using the existing `EmailTemplates.ConfirmEmail` template (`{GridName}`, `{AvatarName}`, `{Link}`, `{ExpiresIn}`, already editable on the Email wording page).
4. The page shows "Check your email. We sent a link to confirm your account. It works once, for 24 hours. Check your spam folder." with a "Send it again" button (subject to §4 limits, same reply whatever happens).
5. **Opening the link** sets `UserLevel` to 0 and spends the token (only after the write succeeds, as recovery does), signs the person in to the guest side (my call: they just proved the email and chose the password a minute ago), and opens "How to connect". A used, expired or wrong link: "This link has expired or was already used." with a link to sign up again or ask for a new link.
6. **Clean-up:** an avatar still on the pending list, older than 24 hours with no live confirm token, is deleted by a background job (my call; e.g. hourly), so the name is free again. The job never touches an avatar that isn't on the list. How to delete (console `delete user` if present, or the database rows the builder lists) is the builder's call; nothing else (inventory, assets) needs to exist for an avatar that never logged in.

### Without email set up **(owner)**

The avatar is made at level 0, the person is signed in to the guest side, and "How to connect" opens at once. No email checks, no confirm step.

Every sign-up (made, confirmed, cleaned up, refused by a rule) goes to the audit log with the name, door and address, never the password, token or email text.

## 6. How to connect

A guest page anyone can open (my call: no sign-in needed, since the login address is not a secret; it's linked from the guest front page and the end of sign-up). It shows:

- The grid name and the **login address**: `GridEndpoints.LoginUri` for this grid (e.g. `http://yourhost:8002`), in a box with a copy button.
- **Steps for Firestorm**, the viewer most OpenSim grids recommend:
  1. Download Firestorm for OpenSim (link to firestormviewer.org's download page; the OpenSim build, not the Second Life one).
  2. Open it, go to **Preferences → OpenSim → Grid Manager**, paste the login address in "Add new grid" and press **Apply**.
  3. On the login screen, pick **{GridName}** from the grid list.
  4. Log in with your first and last name and your password.
- When the person is signed in, it greets them with their avatar name ("Log in as Ann Example").
- One line for other viewers: "Any OpenSim viewer works: add the login address as a new grid."

Wording, layout and whether to add screenshots are the builder's call. The address comes from `GridEndpoints.LoginUri`, as the guest front page already gets it; the owner's `Pages/Setup/Connect` shows the same thing, and the two can share a partial (the builder's call).

## 7. Viewer link

`bin/Robust.HG.ini.proto` still has `register=…/wifi/user/account/`. `GridConfigWriter` sets `register` to the sign-up page (`http://{host}:{WebPort}/guest/sign-up`) under the same test as `password=` (`AdminRule.HasStaffPages`, the test `GuestSideOnly` uses). It is written even while sign-up is closed (my call): the page then says the grid isn't open for sign-ups yet.

## 8. Testing

Unit: name rules; the form's order of checks; name taken reported, email taken not reported (and the notice email queued); limits; the switch refusing to open while the legal gate is shut; clean-up deletes only expired sign-ups it made; nothing secret logged.

Integration through the real website (as the existing Debian CI does): sign-up with email captured, guest sign-in refused before the link, the link confirms and lands on "How to connect"; sign-up with no email set up logs straight in; sign-up closed; an expired sign-up cleaned up; `register=` in the written Robust.HG.ini. Where the CI can run Robust, a viewer-style login (`login_to_simulator`) is refused at -1 and accepted at 0.

## 9. Left to the builder

Page paths and wording; where the switch lives; the reserved-name list; how the clean-up job is scheduled and how it deletes; the confirm and "already have an account" templates' wording; whether "How to connect" has screenshots.
