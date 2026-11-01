# Resident account page — design

Work list item 2.4 (`docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`). Builds on the guest side (`2026-10-30-audiences-security-design.md`), email (`2026-10-31-email-design.md`), password recovery (`2026-10-31-password-recovery-design.md`: `PasswordRules`, `RobustPasswords`, `ResetLimits`, `EmailTokens`, resident two-factor) and sign-up (`2026-10-31-sign-up-design.md`).

Written from the work list without a Q&A round (owner: "write the design from the work list"). Everything not in the work list is marked as my call.

## 1. Why

A resident can sign up and recover a lost password, but once signed in they can't change anything about their own account: a new email address or a new password means asking the grid owner. The account page lets them do both themselves and see what the grid holds about them.

**Success:** a signed-in resident changes their password in under a minute and stays signed in. A resident who moved to a new email address types it, clicks the link that arrives there, and the new address is the one password recovery uses from then on. The old address gets a warning, so someone who took over a session can't quietly move the account's email away.

## 2. Goals, non-goals, anti-goals

**Goals**
- An account page on the guest side showing the person's own details.
- Change password: asks for the current password, then the new one twice.
- Change email: asks for the current password and the new address; confirmed by a link sent to the **new** address, with a notice to the **old** one.

**Non-goals**
- **Choosing a language** (in the work list item). There is no localisation yet (1.5); a picker that changes nothing would mislead. My call: it comes with 1.5, which already plans a language picker in the header, and 1.5 can add a saved per-person choice to this page then.
- Changing the avatar name. OpenSim ties the name to inventory, friends and group records; out of scope.
- Deleting one's own account (2.5 covers deletion by an admin).
- An account page on a Region Server or Connected Server (no guest side, no email).
- Profile details (picture, about text): those live in the viewer.

**Anti-goals**
- A stolen guest cookie is enough to move the account's email and then "recover" the password to the thief.
- The email change tells someone whether an address is used by another avatar, unless they control that inbox.
- Changing a password signs the person out of the page they're using.
- Turning on OpenSim's `AllowSetPassword` or any other unauthenticated Robust call (`docs/SECURITY-NOTES.md`).

## 3. Where it lives

- One page on the guest side (path the builder's call, e.g. `/guest/account`), GuestPolicy by the `/Guest` folder convention, behind `TermsGate` like the other signed-in guest pages. It exists only where there is a guest side (`GuestSideOnly`). The guest home page (`/guest`, already titled "Your account") links to it.
- **Staff use the same page (my call).** The guest door already lets staff and the owner in, and there is no staff page for changing one's own password or email today. One page for everyone is simpler than two; the rules below say where staff differ.
- Change password and change email can be one page with sections or separate small pages (the builder's call).

## 4. Your details

Read-only, from Robust (`IRobustClient` / `StaffDirectory`, as the other guest pages read the account):
- Avatar name.
- Email address on file, in full (it's the person's own), or "No email address" when empty.
- When the account was made (`UserAccounts.Created`), in plain words.
- Role in plain words: Resident, Supervisor, Admin or Owner. These are the People page's roles, except that the People page calls a non-staff account "Guest"; this page says "Resident" (my call: it's the person's own page, and "Guest" reads as a visitor without an account). The People page stays as it is.
- Links to the existing pages that apply: "Two-factor sign-in" (when `ResidentTwoFactor.PageShown`), "My regions" (when they own regions).

Anything else (last login, online now) is the builder's call; nothing secret and nothing about other people.

## 5. Change password

Form: current password, new password, new password again.

On post, in this order:
1. **Throttle.** While `LoginThrottle` has the person's name locked at the guest door (or the address), nothing is checked: "Too many attempts. Try again in a few minutes."
2. **Current password** checked with `IRobustClient.AuthenticateAsync`, as the guest sign-in does. Wrong: "That isn't your current password." and a failure counts in the throttle for the name at the guest door and the address (my call: the same count as sign-in, so this form can't be used to guess passwords faster).
3. **Two-factor (my call).** If the person has resident two-factor on (`ResidentTwoFactor`), the form also asks for a code or backup code, checked like the two-factor page does; a wrong code counts in the throttle, a used backup code is spent. Staff two-factor is not asked here, as the guest door never asks it (recovery design §4).
4. **New password** checked with `PasswordRules.Problem` (the same rules as sign-up and recovery). Also refused when it equals the current one (my call): "Choose a password you haven't been using."
5. **Write** with `RobustPasswords.SetAsync`. A failure: "Couldn't change the password right now. Try again in a few minutes." and nothing else changes.
6. **Other sessions end; this one stays (my call).** `GuestStamps.Rotate` for the person, then this request signs them in again with the new stamp (`GuestSignIn.SignInAsync`), so every other guest cookie stops working and this page keeps going. For staff, `StaffSessions.EndAll` too: a changed password ends every staff session, as recovery does, including one open in another tab of the same browser (my call: intended; getting back in goes through the staff door and its two-factor, and no "all but this one" is needed on the staff side). The viewer uses the new password at once.
7. **Notice email** with the existing `password-changed` template to the address on file, when email is set up (`EmailSender.IsReady`) and the account has an address.
8. "Your password is changed." on the page.

Audited: `password-changed` (door guest, address, outcome) and every refusal, never a password or code.

## 6. Change email

### When email isn't set up

The section shows the address on file and says "Changing your email needs the grid's email to be set up. Ask the grid owner." and no form (my call: without email the new address can't be confirmed, and an unconfirmed address would become the recovery address).

### Asking

Form: new email address, current password (and a two-factor code when the person has resident two-factor on).

On post, in this order:
1. Throttle, current password and two-factor exactly as §5 steps 1–3 (my call: asking for the password is what keeps a stolen cookie from moving the email, the first anti-goal).
2. **Looks like an address** (the sign-up check: one `@`, something on both sides, no spaces). Same as the address on file (compared normalised, as `StaffDirectory.FindByEmailAsync` normalises): "That's already your email address."
3. **Limits:** at most 3 change-email links per account and per typed address per hour, and the per-address (IP) limit, counted by its own `ResetLimits` instance with its own state file (`SendAllowed` / `CountSend`, `AddressAllowed` / `CountAddress`), the way `SignUpLimits` wraps one, so these counts never mix with recovery's or sign-up's. Over a limit: the normal reply (step 6), no email.
4. **Remember the change.** The new address waits in a launcher-owned list in `<data>/guests/` (PrincipalID, new address, when asked), one per person: a new request replaces the old one (`EmailTokens.Issue` already cancels the older link for the same purpose and account). The address is kept only until the link is used or expires (my call; the builder decides the file and its clean-up).
5. **Emails.**
   - To the **new** address: a link, `EmailTokens` purpose `change-email`, the PrincipalID, valid **24 hours** (my call: like the sign-up link, since the inbox may be one the person rarely checks). A new template (my call), editable like the others: "Confirm your new email address for {AvatarName} on {GridName}: {Link}. It works once, for {ExpiresIn}. If you didn't ask for this, ignore this email."
   - To the **old** address (when there is one), at the same time (my call: before the change happens, so the real owner can act while the link is still unused): a new template, "Someone asked to change the email address for {AvatarName} on {GridName}. If that was you, there's nothing to do. If not, change your password now: {Link}" where `{Link}` goes to the Forgot password page. It never names the new address.
6. **The reply**, whatever happened in steps 3–5: "We sent a link to the new address. Your email stays the same until you open it. It works once, for 24 hours."

### Opening the link

The link works without being signed in (my call: people often open email on another device). It carries the token only.
1. A used, expired, replaced or wrong link: "This link has expired or was already used." with a link to the account page.
2. **Address taken.** If another avatar now has the new address on file, the change is refused: "That address is used by another avatar on this grid." (my call: only someone who controls that inbox gets this far, so it tells a stranger nothing; and two avatars sharing one recovery address would make recovery ambiguous). The token is spent and the waiting address dropped. **Which accounts count (my call):** every other account with that address (normalised), at any level, **except** sign-ups still on the pending list (`PendingSignUps`), so nobody can block a victim's address with a sign-up they never confirm. A suspended account keeps its address and does block. `StaffDirectory.FindByEmailAsync` returns one row with no filter, so this needs its own query returning every match other than the person themself.
3. **Write** the new address to Robust's account record (§7). A failure: "Couldn't change your email right now. Try again in a few minutes." and the link is **not** spent (redeemed only after the write succeeds, as recovery does).
4. Done: "Your email address is now {new address}." with a link to the account page. The waiting address is dropped. Sessions are left alone (my call: changing the email doesn't mean someone else knows the password).

Audited: `email-change-asked`, `email-changed`, refusals (who, door, address, outcome), never the typed addresses, password, code or token.

## 7. How the email is written

Directly on Robust's database as `robustuser`, the way `RobustPasswords` writes the password: `UPDATE robust.UserAccounts SET Email = @email WHERE PrincipalID = @id` (the exact query and the name of the new class or method are the builder's call). Not through the console: `reset user email` exists, but the console line can't safely carry any text a person types. Region servers may show the old address from their account cache for a while; nothing on the website or in recovery depends on that.

## 8. Testing

Unit: change password's order of checks; a wrong current password and a wrong code count in the guest throttle; same-as-current refused; other guest sessions refused after the change and this one kept; staff sessions ended for staff; the notice email queued; change email refused without the password; the limits; the new request replacing the old one; both emails queued with the old one not naming the new address; the link single use, expiry, not spent when the write fails; address-taken refused at the link; nothing secret logged; the page 404s off the guest side.

Integration through the real website (as the existing Debian CI does): change the password and sign in at the guest door with the new one, the old one refused; change the email with both emails captured, open the link, and password recovery then finds the account by the new address and not the old one; the email form hidden when email isn't set up.

## 9. Left to the builder

Page paths, layout and wording; one page or several; the waiting-address file and its clean-up; the two new templates' ids and wording; which details beyond §4 to show; the class that writes the email.
