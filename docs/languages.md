# Languages in GridGate

GridGate's resident web pages and emails come in six languages: English (en), German (de), Spanish (es), French (fr), Portuguese (pt) and Russian (ru). Portuguese is Brazilian Portuguese.

The built-in translations were drafted by AI. No native speaker has checked them yet. Every non-English file says so at the top. If you speak one of these languages, please help (see "How to correct a translation").

## How a language is picked

On resident pages GridGate looks for a language in this order. It uses the first one it finds.

1. The language chosen in the header picker. This is kept in the `dg-lang` cookie.
2. The signed-in resident's saved language.
3. The browser's `Accept-Language` setting.
4. The grid's default language.
5. English.

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

Now:

- Change the wording of emails at `/security/email/wording`. Wording is stored per language.
- Correct a translation in the code and send it to the project (see below).

Coming in plan 16b (not built yet):

- Edit the tone text.
- A Languages page, to turn languages on and off and to set the grid's default language.
- Add a new language without changing the code.
- Edit the wording and the legal texts for each language.

## How to correct a translation

1. Open the file for the language. There are two:
   - `Launcher/DreamGrid.Launcher/Resources/ResidentText.<lang>.resx` for web pages.
   - `Launcher/DreamGrid.Launcher.Core/Resources/EmailText.<lang>.resx` for emails.
2. Find the key and change its value. Keep the key name as it is.
3. Run the tests:

   `dotnet test Launcher/DreamGrid.Launcher.Tests --filter TranslationsTests`

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
3. Add the code to `Languages.BuiltIn` in `Launcher/DreamGrid.Launcher.Core/Languages/Languages.cs`.
4. Run `dotnet test Launcher/DreamGrid.Launcher.Tests --filter TranslationsTests`, then the whole test project.

Plan 16b will let owners add languages without this step.

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
