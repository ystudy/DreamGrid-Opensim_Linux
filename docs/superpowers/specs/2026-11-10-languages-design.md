# GridGate in more than one language — design

Work list item 1.5 (`docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`), with the parts of 1.4 (email templates per language) and 2.4 (choose language on the account page) that wait on it. Talked through with the owner on 2026-11-10.

Marks: **(owner)** the owner decided it; **(my call)** decided by me while writing this, open to change.

## 1. Why

Wifi offered en, de, es, fr, pt and ru. GridGate replaces Wifi, and Wifi can't be removed (Phase 3) until a GridGate grid can talk to its residents in their own language. Many OpenSim grids are run by and for German, Spanish, French, Portuguese and Russian speakers.

When someone installs GridGate they own their grid, and they should be able to make it their own **(owner)**: the words their residents see, and the languages they offer, are theirs to shape.

## 2. Who it's for

- **Residents** (and people about to become residents) on grids where most people speak another language. They see the resident side in their language **(owner)**.
- **The grid owner**, who chooses which languages the grid offers, rewords the resident pages, and adds languages GridGate doesn't ship **(owner)**.
- **Staff and owner pages stay in English for now (owner)**. Their text is not moved into translation files in this work **(owner, 2026-11-10: "later")**; the localisation machinery is built so they can be moved later. Staff pages ignore the language cookie and always render in English with English dates and numbers, so a staff member who picked German on the resident side never sees a mixed page **(my call)**.

## 3. Goals

1. Every resident-facing page, and every email a resident gets, appears in the resident's language.
2. Six built-in languages: **en, de, es, fr, pt, ru (owner, from the work list)**.
3. The owner can reword the tone-setting text of resident pages in each language **(owner)** (section 6).
4. The owner can add a language GridGate doesn't ship, by translating it on the site **(owner)** (section 7). New languages also arrive with GridGate updates **(owner)**.
5. Dates, numbers and file sizes follow the chosen language.
6. A right-to-left language (Arabic, Hebrew) works if an owner adds one: the layout flips correctly.

## 4. Non-goals

- Translating staff and owner pages now **(owner)**.
- Text drawn by OpenSim or the viewer itself (in-world dialogs, the viewer's own menus).
- A machine-translation service built into GridGate.
- Translating the owner's own splash-page blocks per language **(my call)**. The built-in text on the splash page (counts, labels) follows the viewer's language; the blocks the owner writes in the splash editor stay one version, so the splash editor and its storage don't change. Can be added later.
- `Pages/GuestPages` (`/guest-pages`) is a staff page despite its name, and stays English.

## 5. Anti-goals (a failure even if it "works")

- **A half-translated page**: a German heading over English buttons. A missing built-in translation is caught by a test before release.
- **An owner breaking a page** by editing text it depends on (button labels, field names, error messages, placeholders).
- **Owner fixes lost on update**, or **GridGate translation fixes never reaching an owner's site** because the owner's copy replaced the whole file.
- **Any security slip from language input**: a cookie, header or query value used as a file path, or owner text rendered as HTML.

## 6. Which text is which

Every piece of resident-facing text is one of three kinds **(owner agreed the split)**:

| Kind | Examples | Who can change it |
|---|---|---|
| **Fixed text** | button labels, field names, error and validation messages, menu items, the language picker | Only GridGate (built-in translations). An owner-added language translates them too (section 7). |
| **Tone text** | each resident page's heading, its opening paragraph ("Welcome to Our Grid, create your avatar here"), short help notes under fields | Built-in translation by default; the owner can replace it per language, and "put the original back" restores it. |
| **Owner documents** | emails (already editable at `/security/email/wording`), legal pages (already editable) | The owner, as today, now per language. The splash page's own blocks stay one version (section 4). |

- Tone text is marked as such in code (a fixed list of keys), so the wording editor offers only those.
- The owner's tone text is **plain text**; a blank line starts a new paragraph. It is always HTML-encoded. **(my call)** `{grid}` may be used for the grid name, the only placeholder.
- An owner's override is stored by key and language, beside the existing email and legal wording (LauncherSettings, by language). Only overrides are stored, so a GridGate update that improves the built-in wording still reaches every key the owner didn't touch.
- If a GridGate update removes or renames a tone-text key, the owner's old override is kept but unused, and the wording page lists it once as "no longer used" so it can be copied or deleted **(my call)**.

## 7. Languages the owner adds

On a **Languages** page (owner and admins, same people as the email wording page) **(my call on who)**:

- **Turn languages on and off.** The owner chooses which languages residents are offered (an Italian grid might offer only Italian and English) and the **grid's default language**, used when nothing else picks one. English can be turned off as an offered language but stays the last fallback for any missing text.
- **Add a language.** Pick it from the list of languages the system knows (by culture code, e.g. `it`, `ja`, `pt-PT`, `ar`). A table shows every resident-facing text in English beside an empty box. The owner fills them in, can save part-way, and can download the table as a file and upload it back (to translate offline or share with another grid).
- "Every text" means fixed text, tone text and the built-in subject and body of every resident email: an owner-added language carries its own email defaults, so an Italian resident never gets an English confirmation email **(my call)**.
- **Deleting or turning off a language** residents have saved: the page shows how many residents have it saved and asks first; their emails then go in the grid's default language **(my call)**.
- **A new language can't be offered until every text is translated (my call)**, so residents never meet a half-translated page. The page shows how many are left.
- **When a GridGate update adds new texts**, a language the owner added already in use stays on; the new texts show in English until translated, and the Languages page and the dashboard say "Italian: 12 new texts to translate" **(my call: the alternative, switching the language off on update, would hurt the owner's residents more than a few English words)**.
- An owner can also add translations of **tone text and owner documents** in their new language through the same wording, email and legal editors, which now have a language selector.
- Stored as one JSON file per language in the owner-only data folder (`<data>/languages/<code>.json`), written atomically like the other launcher state files. The file name is built only from a code that matched the system's culture list, never from user input as typed.
- Built-in languages can't be edited cell by cell here (that is what tone text is for); a correction to fixed text is a GridGate change. **(my call)**

## 8. How a language is picked

For each request, first match wins **(the order is my call; the pieces are from the work list)**:

1. `lang=` on the splash page URL (the viewer sends its own language there), for `/welcome` only.
2. The language-picker cookie, set by the picker in the resident page header.
3. The signed-in resident's saved language.
4. The browser's `Accept-Language`, matched to the offered languages (a `pt-BR` browser gets `pt`).
5. The grid's default language.

Only offered languages can be picked; anything else is ignored. The `<html>` element gets `lang` and `dir` from the result.

**The resident's saved language (2.4):** chosen on the account page or by using the picker while signed in, and saved at sign-up from the language the sign-up page was in. Kept in a launcher-owned file in the data folder, by account ID (`<data>/guests/languages.json`) **(my call; Robust has no field for it)**.

**Emails** go out in the resident's saved language while it is offered, else the grid's default. An email sent before there is an account (sign-up confirmation, forgot password for an unknown address) uses the language of the page the request came from. Emails staff cause (approval, rejection, a reset link staff send, a takedown notice) use the resident's saved language, else the grid's default. If the owner has written their own wording for that email in some languages but not this one, the built-in wording in this language is used, not the owner's wording in another language **(my call)**.

**Legal pages** the owner hasn't written in the resident's language show the grid's default-language version, with a one-line note in the resident's language saying so. Today they are stored under `"en"` (`LegalPages.DefaultLanguage`); existing texts stay under `en`, which is what every grid has now, so nothing moves on upgrade. The sign-up gate needs the three legal pages in the grid's default language. **The default language can't be changed to one whose legal pages aren't written yet**: the Languages page says "write the Terms, Privacy and Cookies pages in German first" with links, so sign-up never closes by surprise **(my call)**.

**Support tickets** (`Pages/Support`) are resident pages too. A ticket keeps the language of the page it was sent from, and emails about it (confirmation, lost link, staff replies) use that language; staff write replies in whatever language they like **(my call)**. Terms versioning and acceptance is item 2.6, next.

## 9. The built-in translations

- English is the source. The owner has no translators, so **I write de, es, fr, pt and ru (owner: "use your best knowledge")**.
- Forms of address websites use: German *Sie*, French *vous*, Spanish *usted*, Russian *вы*, Portuguese Brazilian *você* (`pt` means Brazilian Portuguese, the larger OpenSim audience) **(my call)**.
- In-world terms (avatar, region, estate, inventory, teleport, hypergrid, landmark) follow the Firestorm viewer's own translation, so a resident sees the same word in both places.
- Each translation file starts with a short note: drafted by AI, corrections from native speakers welcome, and where the contributor notes are.
- Wifi's `.po` files are read for its grid vocabulary only; they are removed with Wifi in Phase 3, not here.

## 10. Built on

- ASP.NET Core localisation: request-localisation middleware (with a custom provider for steps 1, 3 and the owner's offered list), `IStringLocalizer` / `IViewLocalizer`, `.resx` files for built-in languages, and a localiser that layers owner overrides and owner-added languages on top. How the layers fit together is the builder's call, as long as the lookup order per key is: owner tone override for this language → built-in or owner-added text for this language → English.
- Resident pages: `Pages/Guest/*`, `Pages/Home`, `Pages/Support/*`, `Pages/SignUp`, `Pages/Legal`, `Pages/Welcome` (built-in text), every page that uses `_GuestLayout`, the guest layout and its shared partials (`_GuestLayout`, `_SignUpRows`, `_BotFields`, `_LegalLinks`, `_LoginAddress`, and others the guest pages use), plus all resident emails (`EmailTemplates`).
- Dates, numbers and sizes: displayed with the current culture (`RegionAdvice.FileSize` and similar take a culture). Anything written to files, logs or OpenSim keeps the invariant culture.
- Right-to-left: the guest layout loads Bootstrap's RTL stylesheet and sets `dir="rtl"` when the culture's text runs right to left; GridGate's own CSS uses logical properties (`margin-inline-start`, not `margin-left`) on resident pages.

## 11. Tests and checks

- Every built-in language has every key, and every translation keeps the same placeholders (`{0}`, `{grid}`) as English.
- A scan of the resident `.cshtml` files fails on hard-coded English text outside the localiser.
- Language pick order, offered-only, cookie and query values that aren't languages, `pt-BR` → `pt`.
- Owner overrides: stored, shown, put back; HTML is encoded; a GridGate key rename leaves a "no longer used" entry.
- Owner-added language: can't be offered until complete; upload rejects unknown keys, oversize files and bad codes; new keys after an update fall back to English and are counted.
- Emails go out in the saved language, staff-caused ones too.
- Platforms **(from the work list)**: `InvariantGlobalization` stays off; ICU is present (`install.sh` already installs `libicu72` / `libicu76`); the culture switch is checked by hand on the Debian 12 VM and on Windows, including one owner-added RTL language.

## 12. Security

- Language values from the cookie, query, header or upload are matched against a known list before use; never used as a path or written back unchecked.
- Owner text is always encoded; uploaded language files are size-limited JSON with string values only.
- Turning languages on and off, adding or deleting a language, and changing tone text or wording are written to the audit log (who, what, when, from where), without the text itself.

## 13. Left to the builder

- How to split the work into plans (this design is likely two: built-in languages with the picker and emails; then owner wording, the Languages page and owner-added languages).
- Resource file layout, key names, the localiser layering, the exact page design of the Languages and wording editors (following the existing email wording page), and contributor notes on adding a language.
