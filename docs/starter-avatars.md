# Starter avatars

New residents choose a finished avatar on the second step of signing up. They log in wearing it, with copies of everything it
wears in their inventory. This page is for the grid owner.

## What residents see

After the name, email and password form, **Step 2: Choose your avatar** shows a snapshot of each ready starter avatar with its
name. One is already picked: your default, or the first ready one if you haven't chosen. **Create my avatar** makes the account.

- When no starter avatar is ready, there is no step 2, and residents get the plain OpenSim avatar.
- An avatar that stops being ready while someone is choosing (you hid it, or its check failed) is swapped for the default. Sign-up
  still works.
- Residents choose only once. They can change clothes later from their inventory.

## Setting them up

Open **Guest pages → Starter avatars** (`/security/starter-avatars`). Only the owner can use this page.

1. **Upload your archive** (an IAR, with no size limit). GridGate lists every folder two levels down that holds body parts. In the
   FULLAVATARS file, that's the 18 avatars under `FULLAVATARS/`.
2. **Tick the avatars to add**, and change the names residents will see if you like. Each one gets its own model account, named
   `Starter01 Avatar`, `Starter02 Avatar` and so on. Its folder is loaded into that account, with taken-down items left out (the
   case gets a note). A region must be running.
3. **Upload a snapshot** of each avatar: a PNG, JPEG or WebP of up to 5 MB. GridGate shrinks it to 800 pixels wide and stores it
   again as a WebP with nothing else from the file. The pictures inside the FULLAVATARS folders are JPEG 2000 textures, which
   GridGate can't read, so take a snapshot in the viewer.
4. **Log in as this model.** GridGate sets a new password and shows it once, with the folder to open. In a viewer, open that
   folder, choose **Replace Outfit**, wait until the avatar looks right, then log out.
5. **Check it.** GridGate reads what the model is wearing and checks that it owns every piece, that every piece comes from the
   avatar's own folder (a new model starts out in OpenSim's plain default outfit, which never passes), and that no attachment
   point holds two items. OpenSim copies only one attachment per point to new residents, so move extras such as a mesh head and
   its hair onto different points. When the check passes, the avatar
   goes on the sign-up page. The model gets a new password that nobody sees, so nobody changes it by accident. The check also lists
   any piece residents won't be able to copy or modify (they get each item's next-owner permissions). In FULLAVATARS that's one
   hair, which is no-transfer.
6. **Arrange them.** Move them up and down, make one the default, rename them, or hide one without deleting it.

You can also add an account you already dressed by hand under **Or add an account you already dressed**. Then upload its snapshot
and press **Check it**.

## The checklist on each row

| Line | Means |
|---|---|
| Snapshot | You uploaded a picture |
| Worn and checked | The last **Check it** passed, and nobody logged in as the model since |
| Ready | Both of those, it isn't hidden, and no warning stands. Only ready avatars are on the sign-up page |

## When a copy goes wrong

After each sign-up, GridGate checks in the background that the new resident is wearing every piece the model wears, slot by
slot. If anything is missing, the avatar comes off the sign-up page and its row shows "*Name* wasn't copied to a new resident;
check its model". The audit log gets `starter-avatar-failed`. Log in as the model, fix the outfit, and press **Check it** again.

OpenSim doesn't store the Physics and Universal wearable layers in an appearance, so new residents don't get them worn. They're
still in the model's inventory if you want to hand them out another way.

Robust copies the outfit only while `CreateDefaultAvatarEntries` is `true` in `Robust.HG.ini`, which GridGate always writes.

## Model accounts elsewhere

- People shows them with a **Starter avatar** tag, and they can't be deleted there while they're on the list.
- They can't sign in to the resident pages, and the dashboard doesn't count them as residents.
- A resident's People page shows which starter avatar they picked.
- **Remove** takes an avatar off the list. Tick "also delete its model account" to delete the model too. Residents who picked it keep
  their copies.

Every change is in the audit log: `starter-avatar-added`, `-snapshot`, `-login`, `-checked`, `-order`, `-default`, `-renamed`,
`-hidden`, `-shown`, `-removed` and `-failed`.
