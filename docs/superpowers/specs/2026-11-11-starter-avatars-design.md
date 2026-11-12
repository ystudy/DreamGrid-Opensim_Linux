# Starter avatars — design

Date: 2026-11-11. Status: draft for the owner's approval. Builds on the IAR backups branch (`feature/iar-backups`, PR 70), and
replaces Wifi's model-account appearance picker (worklist, Phase 3 gaps).

Marks: **(owner)** is what the owner said. **(call)** is the designer's call, open to change. Everything else follows from those
or from the code as it is.

## 1. Why

When a new resident signs up today, they arrive as OpenSim's default avatar, the plain "Ruth" shape and clothes. Wifi let the owner offer
a choice of finished avatars. GridGate has to do the same before Wifi can be removed. The owner already has 18 complete avatars in
one file, `IAR-FULLAVATARS.iar`, each one a folder holding its body, skin, shape, eyes, hair, clothes, shoes and attachments:

- Professional Lady #1–#5, Professional Male #1–#5
- Lyra, Rana, Venus and Chara (complete female avatars)
- Leo, Nash, Orion and Draco (complete male avatars)

Success is that a new resident's first login is as someone they chose and like the look of, wearing the whole outfit, with the
pieces in their inventory.

What the file actually holds (read 2026-11-11):

- Each folder has 11 to 26 items, almost all of them wearables (shape, skin, eyes, hair, clothes) and attachments.
- The 8 named avatars (Lyra and the rest) each also have one texture named after the avatar, very likely its picture.
- The 10 Professional avatars each have an "unpacker" script, which next owners can't copy or modify, a leftover from boxed
  goods. Two of them also have an "About the skins" notecard.
- One hair attachment (Professional Lady #1) is no-transfer for next owners. Every other item is full permissions for next
  owners.

## 2. Goals

1. **A page of pictures to pick from (owner).** It's its own page, step 2 of signing up, right after the name, email and password form
   (owner chose layout B). It shows a large snapshot of each starter avatar with its name under it. The resident clicks one, then
   **Create my avatar**.
2. **Wearing it at first login (owner).** The new resident is wearing the avatar they picked. Copies of everything it's wearing
   (body parts, clothes, shoes, hair and attachments) are in their inventory, so they can change back to it later.
   **(call)** "Everything it's wearing" is exactly what OpenSim copies: the model's worn wearables and attachments, not the rest of
   its folder. With this file, that leaves out only each avatar's picture texture, the unpacker scripts and the two notecards,
   and none of those helps a resident.
3. **The owner sets each one up once.** The owner loads the archive, uploads a snapshot of each avatar (owner), and logs in once
   as each model account to put its outfit on (owner). That last step can't be avoided: OpenSim only stores an avatar's look
   after a viewer has worn it.
4. **Defaults that never get in the way:**
   - One avatar is selected when the page opens (owner agreed). A resident who just clicks through gets it.
   - If no starter avatar is ready, step 2 is skipped and the resident gets the plain avatar, as today (owner agreed).
   - An avatar isn't shown until it has a snapshot and has been worn once (owner agreed).
5. **Six languages (owner agreed).** The page's own words are in en, de, es, fr, pt and ru, like the other resident pages. Avatar
   names are shown exactly as the owner typed them.

## 3. Non-goals

- **Changing avatar after sign-up (owner agreed).** The pieces are already in the resident's inventory.
- **Choosing an avatar for accounts staff make** (People → add, approvals). They get the default avatar. **(call)** A later
  change could add this.
- **Building avatars in GridGate.** The owner dresses them in a viewer.
- **Taking snapshots automatically.** The owner uploads them (owner).
- **Hypergrid visitors.** They bring their own avatars.

## 4. Anti-goals (failure even if it works)

- A new resident ending up half-dressed, or bald. This happens when the model's appearance names items the model doesn't have, so
  the copy misses them. The page must only offer avatars that were checked as complete (§6.3).
- Anyone but the owner logging in as a model account and changing what every new resident gets.
- Sign-up breaking, or slowing badly, because a model is missing or Robust can't find it. The resident still gets an avatar,
  even if it's the plain one, and the owner is told (§5.5).
- The password from step 1 sitting on the server, or in a hidden field, while the resident looks at pictures.

## 5. The resident's side

### 5.1 The two steps

**(call)** Both steps are one HTML form shown as two screens. Step 1 is the existing fields plus the terms box, and **Next**.
Pressing **Next** checks the fields in the browser (required, password match, the same rules as now) and shows step 2. The
browser address doesn't change. **Create my avatar** sends the whole form once, through the same bot check and
`SignUps.SubmitAsync` as today.

The reason: the account is made when the form is sent (`ResidentAvatars.CreateAsync`, before any email confirmation). Doing it
in one post keeps the password off the server between the steps. It also leaves sign-up approval, email confirmation, the bot
check and the busy and closed answers exactly as they are.

- If the server refuses the form (name taken, bot check, closed, busy), the page comes back on step 1 with the message, and the
  chosen avatar stays chosen.
- Without JavaScript, both steps show on one long page and still work.
- **Back** on step 2 returns to step 1 without losing anything typed.
- When no avatar is ready, there is no step 2. The button on step 1 is **Create my avatar**, as now.

### 5.2 Step 2

- **Heading:** "Choose your avatar". A line under it says you can change clothes later from your inventory.
- **The cards:** a grid of large snapshot cards, each with the avatar's name, in the owner's order. The default card is selected
  when the page opens.
- **Choosing:** each card is a radio button, so it works from the keyboard. The snapshot's alt text is the avatar's name.
- **Button:** "Create my avatar".
- **Small screens:** the grid becomes two columns on a phone. Snapshots load lazily.

### 5.3 What gets sent and kept

The form carries the avatar's GridGate id (a GUID), never a model account's name. The server looks it up in the list of ready
avatars. An unknown or no-longer-ready id falls back to the default, and if there is none, to the plain avatar. A bad id is
never an error the resident sees. The sign-up record keeps which starter avatar was picked, by id and name, so staff can see it
on the person's page.

### 5.4 Making the account

`ResidentAvatars.CreateAsync` gets one more argument: the model account's name, "First Last". `GridConsoleCommands.CreateUserAsync`
puts it in the eighth slot it already sends as `""`. Robust's `CreateUser` then calls `EstablishAppearance`, which:

- copies every item the model is wearing, wearables and attachments alike, into the new account's Clothing folder;
- links them in Current Outfit;
- stores the appearance.

The name goes through the same name check as other avatar names, quoted as one argument.

What the code does (checked):

- `EstablishAppearance` splits the name on whitespace into exactly two parts.
- Body parts land in Clothing too, not Body Parts.
- Copies get the items' next-owner permissions.
- All of this needs `CreateDefaultAvatarEntries = true`, which the shipped Robust ini files set. **(call)** GridGate's Robust
  settings check makes sure it stays set.

Robust stores the `UserAccounts` row *before* it makes the inventory and copies the outfit. So the check below can't run the moment
the account appears. It waits, polling, until the copy is done or about 30 seconds pass **(call)**.

Decoy accounts, which sign-up makes when an email is already on the grid so the reply looks the same, copy the outfit too and run
the same check **(call)**. Otherwise the time the reply takes would show whether an email is registered.

### 5.5 If the copy goes wrong

Robust never fails the sign-up over the model. It only logs a warning, and the result is one of these:

- the model can't be found, or has no stored appearance: the resident gets the plain avatar;
- one worn item is missing from the model: that slot or attachment is left empty and the resident is half-dressed;
- a thrown error: no outfit at all.

**(call)** After making the account, GridGate compares the new account with the model, item by item. For each wearable slot and
attachment point in the model's appearance, the resident needs a copy in Clothing, worn in the same place. Anything short of
that counts as failed. If it doesn't, the sign-up still succeeds. The owner sees a warning on the
Starter avatars page, "Lyra wasn't copied to a new resident; check her model", and the audit log gets `starter-avatar-failed`.
That avatar is then marked "needs checking" and taken off the sign-up page until the owner checks it again (§6.3).

## 6. The owner's side

### 6.1 Where and who

The new page is **Guest pages → Starter avatars**, at `/security/starter-avatars`. **(call)** It's owner only (OwnerPolicy), like
the other guest-page settings. It's guest side only, like `/security/iar`. Every change goes in the audit log with the existing
`AuditEntry` fields.

### 6.2 Loading avatars from an archive

1. The owner uploads an IAR with no size limit, the same as the staff load from IAR backups, which is streamed to `IarsIncoming`.
   The archive is read with the strict reader from the IAR branch, and the upload is always rewritten before it's loaded.
2. GridGate lists the archive's folders two levels down. With the FULLAVATARS file, that's the 18 avatar folders under
   `FULLAVATARS/`. **(call)** Any folder that holds body parts, meaning a shape, skin, eyes or hair, is offered as an avatar.
3. The owner ticks which ones to add, and can edit each display name (it starts as the folder name).
4. For each ticked avatar, GridGate:
   - **Makes a model account.** **(call)** It's named `Starter<NN> Avatar`, where NN is the next free number with two digits, because
     folder names such as "Professional Lady #1" aren't valid avatar names. Level 0, a random password, and no email. The
     account is flagged as a starter model (§6.5).
   - **Writes a one-avatar archive** with `IarArchive.Rewrite`, keeping only that folder's items and the assets they reach. The
     banned-content filter runs on it the same as on any upload, and any match is dropped and noted on its case.
   - **Loads it** into a folder in the model's inventory named after the avatar. This uses the IAR job runner from the IAR
     branch (`RunIarJobAsync`), on the same one-job-at-a-time limit, while a region is running.
5. The page shows each avatar's progress, and the audit log gets `starter-avatar-added`.

The owner can also add a single avatar from an existing account. **(call)** For example, a model they already dressed by hand:
they give its name and a display name, and no archive is needed.

### 6.3 Making each one ready

Each avatar's row on the page shows a checklist:

1. **Snapshot.** **(call)** If the avatar's folder has a texture with the avatar's own name, as the 8 named FULLAVATARS avatars
   do, the page offers it as the snapshot. The owner confirms it or uploads their own. Otherwise the owner uploads a PNG, JPEG or WebP image (owner), 5 MB at most **(call)**. GridGate checks it really is one of
   those formats, re-encodes it as WebP at most 800 pixels wide, and strips its metadata **(call)**. It is stored under
   `<data>/starter-avatars/`. A new upload replaces the old one.
2. **Worn once (owner).**
   - The row has **Log in as this model**. It sets a new random password, shows it once alongside the model's name, and says: "Log
     in with a viewer, open the folder Lyra in your inventory, choose Replace Outfit, wait until you look right, then log out."
   - The owner then presses **Check it**. GridGate reads the model's stored appearance from Robust's database, and checks that
     there is one and that every item it wears is in the model's own inventory. It lists anything missing.
   - **(call)** It also warns, without blocking, about any worn item that a next owner can't copy or modify, naming the item and
     what's missing. This is because residents get the next-owner permissions. In the FULLAVATARS file that's one hair (no-transfer only).
   - When the check passes, GridGate gives the model a new random password that nobody is shown, so nobody can change the model by
     accident **(call)**. **Log in as this model** sets a new one whenever it's needed again.
3. **Ready** means it has a snapshot and passed the check. Only ready avatars are on the sign-up page.

### 6.4 Arranging them

- **Order:** move up and down, the order the sign-up page uses.
- **Default:** one ready avatar is marked as the default. **(call)** If the owner hasn't chosen one, it's the first ready avatar in
  the order.
- **Name:** rename the display name.
- **Hide or show:** hide an avatar from sign-up without deleting it.
- **Remove:** takes it off the list. **(call)** The page asks whether to also delete the model account. Residents who already
  picked it keep their copies, because the items were copied and not linked.

Each of these goes in the audit log: `starter-avatar-order`, `-default`, `-renamed`, `-hidden`, `-shown`, `-removed`, `-checked`.

### 6.5 Model accounts elsewhere in GridGate

- **(call)** People lists them with a "Starter avatar" tag, and they don't count as residents on the dashboard.
- They can't sign in to resident pages. The sign-up clean-up only deletes accounts on the sign-up board that are still at level -1,
  so it never touches them. A test pins that.
- Deleting one from People is refused while it's on the starter list.

### 6.6 State

The list is stored in `<data>/starter-avatars/avatars.json`, using `JsonStateFile`, in an owner-only folder. Each avatar has:

- an id and a display name;
- the model account's PrincipalID and name;
- its order;
- whether it's hidden;
- whether it's the default;
- its snapshot's file name;
- the result and time of its last check;
- the warning described in §5.5.

The snapshot files are served on a public path so the sign-up page can show them. **(call)** The path is
`/starter-avatars/<id>.webp`, which reveals nothing about the model account.

## 7. The owner's FULLAVATARS file

The owner's own setup, written in `docs/starter-avatars.md`:

1. Upload the file.
2. Tick all 18 avatars.
3. Upload 18 snapshots.
4. Log in as each model once and choose Replace Outfit.
5. Press **Check it** for each.
6. Choose the default and the order.

The worklist gets a VM check: sign up a test resident with Lyra and log in. They should be wearing her, and her pieces should be
in their Clothing folder.

## 8. What's left to the builder

- The exact layout and CSS of the cards and the owner's rows, using the existing Bootstrap styles.
- How the page shows load progress, which can reuse the IAR jobs' polling.
- How to read the model's appearance (Robust's `Avatars` table, the `Wearable` and `_ap_` rows) and how to match its items to
  inventory.
- Test doubles. `FakeGridController` and `FakeRobustAccounts` gain whatever the model accounts need.
