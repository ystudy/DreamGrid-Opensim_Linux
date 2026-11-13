# Languages in GridGate

GridGate's resident web pages and emails come in six languages: English (en), German (de), Spanish (es), French (fr), Portuguese (pt) and Russian (ru). Portuguese is Brazilian Portuguese.

The built-in translations were drafted by AI. No native speaker has checked them yet. Every non-English file says so at the top. If you speak one of these languages, please help (see "How to correct a translation").

## How a language is picked

On resident pages GridGate looks for a language in this order. It uses the first one it finds.

1. The language chosen in the header picker. This is kept in the `gg-lang` cookie.
2. The signed-in resident's saved language.
3. The browser's `Accept-Language` setting.
4. The grid's default language.
5. English.

Only languages the grid offers can be picked. The owner chooses them on the Languages page (see "What owners can do"). A resident whose saved language is turned off or deleted gets the grid's default language, in pages and in emails.

Two things work differently:

- `/welcome` is the page the viewer shows at login. Its answers are cached for everyone, so it can't look at cookies or the browser. It uses only `?lang=` in the address (for example `/welcome?lang=fr`) and the grid's default language.
- Staff pages are always in English.

## What residents can do

- Pick a language from the header on any resident page.
- Pick a language on the account page.
- Pick a language when they sign up.

When a resident is signed in, the choice is saved. It is kept in `<data>/guests/languages.json`. The saved language is used for pages on the next visit, and for emails.

The saved language is removed when the account is deleted, when staff deny a sign-up, and when an expired sign-up is cleaned up.

Dates and numbers follow the chosen language too. For example, German pages show dates the German way.

## Emails

An email goes out in the language of the person who gets it:

- For a known account, the language they saved.
- For a request made from a page, the language of that page.
- For a support ticket, the language of the ticket.
- Otherwise, the grid's default language.

Emails to staff are always in English.

## What owners can do

These pages are for the grid owner only. They are under **Guest pages** in the menu. Every change on them is written to the audit log, with who, what, when and from where, but never the text itself.

### Choose the languages and the default

The Languages page (`/security/languages`) lists every language: the six built in, and any you added. For each one it shows whether it is offered, whether it is the default, what is still missing, and how many residents chose it.

Tick the languages residents can pick, choose the default, and save. Some choices are refused, with a message that says why:

- The default must be one of the offered languages. Turn a language on before you make it the default.
- An added language can only be turned on when every text is translated.
- Once the Terms, Privacy and Cookies pages are ready for sign-up (written, with the box ticked that you are responsible for them), a new default needs its own translation of each, at the same version as the current ones. The message links to the legal pages in that language.
- At any time, a switch is refused when it would change the version number of a legal page that has text, so residents never have to accept again because the default changed.

When you turn off a language that residents use, the page tells you how many and asks you to confirm. Those residents get the default language from then on.

A built-in language can be turned off but not deleted.

### Reword the guest pages

The Page wording page (`/security/wording`) lets you write your own headings and opening words for the guest pages, in each language. These texts can be reworded:

- Front page: page title and introduction
- Sign in: introduction
- Create account: introduction
- Forgot password: introduction
- Your account: page title
- Ask for help: heading and introduction
- How to connect: help heading

Pick the language at the top. Each text shows GridGate's own wording beside a box for yours. Leave the box empty, or press "Put the original back", to use GridGate's wording. Only the texts you change are stored, so when an update improves GridGate's own wording, you get it everywhere you didn't change.

Your text is plain text, up to 2,000 characters. A blank line starts a new paragraph. Write `{grid}` where the grid's name goes. Anything else, including HTML and other curly brackets, is shown exactly as you typed it.

Your wording is used only in the language you wrote it for. Residents reading another language see that language's wording.

### Email wording in each language

The Email wording page (`/security/email/wording`) has a language selector. Pick a language, then change, reset, preview or send a test of each email in that language. A resident gets your wording in their language when there is one, and GridGate's built-in wording otherwise.

### Legal pages in each language

The legal pages (`/security/legal`) have a language selector too. The default language's pages are the **source**; the others are translations of them.

- Saving a source page gives it a new version number, as before.
- Saving a translation gives it the source's current version.
- A translation is shown only while its version is the same as the source's. After you change the source, readers of that language see the source, with a note that it is shown in the default language, until you update the translation. The legal page marks such translations "out of date".
- Version numbers never go backwards, even if a page is cleared and written again.
- Terms are accepted per version number. Translating a page, or changing the default language, never makes residents accept again.

### Add a language GridGate doesn't have

On the Languages page, pick a language under "Add a language". This makes an empty translation and opens its page (`/security/languages/<code>`), where every text is shown in English beside a box for your translation. Page texts and email texts are in separate sections.

- Save as often as you like. Partial work is kept.
- A translation that is missing a fill-in word, such as `{0}` or `{Link}`, is shown with the problem and isn't saved. The other rows are saved.
- The language can be offered to residents once nothing is missing.
- To delete it, turn it off first. Its file is removed, and residents who chose it get the default language.

You can also work on a file instead:

- **Download as a file** gives you `<code>.json` with every text and the English beside it.
- **Upload a file** puts the translations from a file in. The texts in the file replace the ones on the site, and the others stay. If anything in the file is wrong, nothing is saved, and the page lists each problem.

The file looks like this:

```json
{
  "code": "it",
  "page": { "SignIn.Title": "Accedi", "...": "..." },
  "email": { "...": "..." },
  "english": {
    "page": { "SignIn.Title": "Sign in", "...": "..." },
    "email": { "...": "..." }
  }
}
```

- `code` must be the language of the page you upload it on.
- `page` holds the web page texts, and `email` the email texts. The keys are the English keys. A key GridGate doesn't know is a problem.
- Every value must be text. Numbers, `null`, lists and nested objects are refused.
- Fill-in words in curly brackets must match the English exactly, and each email must keep the fill-in words it needs.
- `english` is there for the translator. It is ignored when the file is uploaded.
- The file must be UTF-8 and at most 1 MB.

The translations are kept in `<data>/languages/<code>.json`.

### After an update adds texts

A GridGate update can add new texts. An added language that is already on stays on. Its new texts show in English until you translate them (for a regional variety of a built-in language, such as pt-PT or fr-CA, in that built-in language instead), and both the dashboard and the Languages page say "<Language>: N new texts to translate", with a link to its translation page.

### Correct a built-in translation

Correct a translation in the code and send it to the project (see below).

## How to correct a translation

1. Open the file for the language. There are two:
   - `Launcher/GridGate.Launcher/Resources/ResidentText.<lang>.resx` for web pages.
   - `Launcher/GridGate.Launcher.Core/Resources/EmailText.<lang>.resx` for emails.
2. Find the key and change its value. Keep the key name as it is.
3. Run the tests:

   `dotnet test Launcher/GridGate.Launcher.Tests --filter TranslationsTests`

4. Open a pull request.

The tests check that:

- every key is in every language;
- the words in curly brackets, such as `{0}` or `{Link}`, are the same as in English;
- each email still has the fill-in words it needs, such as `{Link}`;
- every non-English file starts with the AI-draft note;
- file names are right for the built-in languages.

When a native speaker has checked a file, the AI-draft note can be taken out of it in the same pull request. Say so in the pull request.

## How to add a built-in language

1. Copy both English files:
   - `ResidentText.resx` to `ResidentText.<code>.resx`
   - `EmailText.resx` to `EmailText.<code>.resx`
2. Translate every value. Put the AI-draft note at the top if the translation has not been checked by a native speaker.
3. Add the code to `Languages.BuiltIn` in `Launcher/GridGate.Launcher.Core/Languages/Languages.cs`.
4. Run `dotnet test Launcher/GridGate.Launcher.Tests --filter TranslationsTests`, then the whole test project.

An owner can also add a language on their own grid without changing the code (see "Add a language GridGate doesn't have").

## Writing rules for translators

- Forms of address:
  - German: *Sie*.
  - French: *vous*.
  - Spanish: *usted*.
  - Russian: *вы*.
  - Portuguese is Brazilian, and uses *você*.
- Words from inside the virtual world follow the Firestorm viewer, so people can find them in the menus they see.
- Counts are written as a label and a number, for example "Regions: 5". Don't use plurals.
- Never translate the names inside curly brackets, such as `{GridName}` or `{Link}`. You may move them to where they fit your language.

## Open points for native speakers

Please check these first:

- The Firestorm menu labels in the connect steps. They may not match the viewer exactly in each language.
- French: "grille" for "grid", and "gestionnaire de grilles" and "Ajouter une nouvelle grille".
- Russian: "вьюер" or "вьювер" for "viewer".

Other words the drafts were unsure about:

- German:
  - `Account.RoleOwner`: "Betreiber" and "Betreiber des Grids".
  - "Team" for staff.
  - The Firestorm labels "Einstellungen", "Grid-Manager", "Neues Grid hinzufügen" and "Übernehmen".
  - `Regions.StateParked`: "geparkt".
  - `Account.RoleResident`: "Einwohner".
- Spanish:
  - "grid" (masculine) and "visor".
  - "Región/Estado".
  - The Firestorm labels "Preferencias", "Añadir nuevo grid" and "Aplicar". "Grid Manager" is kept in English.
  - "ticket" as a loan word.
  - The masculine "Bienvenido".
- French:
  - "viewer".
  - Past participles written in the masculine form for everyone.
  - "à la grille" in `Home.Intro`.
- Portuguese:
  - "visualizador" and "Região/Propriedade".
  - The Firestorm labels "Preferências", "Adicionar novo grid" and "Aplicar".
  - Past participles written in the masculine form for everyone.
- Russian:
  - "грид".
  - "Регион/Землевладение".
  - The Firestorm labels "Менеджер гридов", "Добавить новый грид", "Применить" and "Настройки".
  - "администрация" for staff, and "обращение" for a ticket.
  - `Account.RoleSupervisor`: "Супервайзер".
  - "Сейчас в мире: {0}".
- Spanish, French, Portuguese and Russian: check that the `Regions.StateParked` words agree in gender with "región", "région", "região" (all feminine) and "регион" (masculine).
