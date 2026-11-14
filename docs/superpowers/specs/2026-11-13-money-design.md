# Money — design

Date: 2026-11-13. Status: draft for the owner's approval. Builds on `main` at `72a36548`.

Marks: **(owner)** is what the owner said. **(call)** is the designer's call, open to change. Everything else follows from those
or from the code as it is. File references are to this tree unless they name another repository.

## 1. Why

**(owner)** People on a grid want to do what they do in Second Life: sell what they make, buy and rent land, tip, and, on grids
without real money, play games of chance. Money makes a grid attractive to artists, DJs and event hosts, because they can be
paid for their effort. For the grid owner it is a hobby that can pay for itself: selling coins can cover some of the hardware and
electricity. **It is not a get-rich scheme.** Tipping is a big part of it, especially for DJs and event people.

**(owner)** GridGate offers the owner a choice, and the owner decides:

1. **Off**: no money, as today.
2. **Play coins**: a token currency that isn't real money.
3. **Real coins**: the same coins, which residents can now buy with real money. Play coins can be turned into real coins later
   without losing anyone's balance. Nobody can cash coins back into dollars.
4. **Gloebit**: the outside Gloebit service, for owners who want artists paid in real dollars. Gloebit handles the payments
   and the cash-outs.

**(owner)** The money code must be reviewed and approved as safe before it ships (§12).

What success looks like:

- A resident pays a DJ's tip jar, buys a dress from a vendor or a parcel of land, and it just works in Firestorm, with the
  balance shown in the viewer's status bar.
- The owner can see every coin: where it came from, who holds it, and every movement, and can fix a mistake.
- No one, inside the grid or outside it, can create coins or move someone else's coins without the owner's say-so.

## 2. Where this stands today

- The launcher blanks every `economymodule` key it writes (`Launcher/GridGate.Launcher.Core/Config/GridConfigWriter.cs:332`), so
  OpenSim's built-in sample module (`OpenSim/Region/OptionalModules/World/MoneyModule/SampleMoneyModule.cs`, named
  `BetaGridLikeMoneyModule`) is always the active one. It keeps no balances; everything is free.
- Gloebit's source is in `addon-modules/Gloebit`, but `Gloebit.dll` is left out of the runtime and the packages
  (`Launcher/GridGate.Launcher.Core/Runtime/RuntimeStager.cs:11`, `setup/package.ps1:106`), and `gloebit.com` values are commented
  out of generated INIs (`GridConfigWriter.cs:14`). This follows the launcher design §4.15: third-party services come back later
  as feature modules with a dashboard switch.
- The launcher already has what a money system leans on: a private MariaDB it provisions itself (launcher design §4.14), an
  audit log (`Logs/AuditLog.cs`), staff and guest sign-in with roles (audiences design), feature modules with settings pages
  (`Modules/IFeatureModule.cs`, `Modules/ModuleUi.cs`), HTTPS with a self-signed certificate (HTTPS design), and per-program INI
  generation, so it can hand each region program its own secret.
- `addon-modules/dtl-paypal` is an empty folder.

## 3. Goals

1. **The choice (owner).** On the grid's Money page the owner picks Off, Play coins, Real coins or Gloebit. Changing it is
   explained before it happens (§10).
2. **Everything people do with money in Second Life (owner):** buy objects (original, copy, contents), pay another avatar, pay
   an object (tip jars, vendors, rental boxes), scripts paying out (`llGiveMoney`, `llTransferLindenDollars`), buy land, and
   the optional fees for uploads, group creation and joining, and classified ads.
3. **Play coins first.** Play coins are the first thing that ships. Real coins reuse all of it.
4. **Real coins later, with no cash-out (owner).** Residents buy coins; the owner credits them by hand at first (§9), and an
   automatic PayPal button is a later design. Balances carry over from play coins.
5. **Gloebit as the real-dollar option (owner).** Turning it on is a settings page, not hand-edited INIs.
6. **The owner sees and controls every coin (call):** a complete ledger, totals, per-person history, give and take back with
   a reason, a freeze switch, and a self-check that alarms when the books don't balance.
7. **Hypergrid visitors can be tipped and can spend (call).** Many people at events are visitors from other grids (§7.6).
8. **Safe by design and reviewed (owner).** The threats in §11 are designed out, not patched later, and the review gate in §12
   holds the release.

## 4. Non-goals

- **Cash-out on a self-run grid (owner).** Coins never turn back into dollars, except through Gloebit.
- **Paying for each item with PayPal (owner agreed).** §13.7 explains why.
- **The automatic PayPal "Buy coins" button.** It comes after hand crediting, in its own design (§9.4).
- **Money on a Connected Server.** Those regions belong to someone else's grid and use that grid's money. GridGate leaves the
  economy module empty there, as today.
- **Moving balances between GridGate coins and Gloebit.** They are separate systems (§10).
- **A marketplace website.** Selling happens in-world.
- **Exchange between grids.** A visitor's coins on this grid stay on this grid.
- **Taxes, invoices or legal advice.** The owner is responsible for real-money obligations; GridGate says so plainly (§9.2).

## 5. Anti-goals (failure even if it works)

- **Coins created from nothing** by anyone but the owner and the rules the owner set (starting balance, allowance).
- **Spending the same coins twice**, or any balance going below zero.
- **Being charged for something not received** (an object that didn't arrive, land that didn't transfer) without the coins
  coming back automatically.
- **A payment that happened twice** because a region or viewer retried.
- **The money port reachable from the internet**, or a secret anyone else who downloaded GridGate also has.
- **Silent loss**: a balance that changes with no ledger entry explaining it.
- **Gambling with real coins** encouraged by GridGate's own wording, or a grid switched to real coins without the owner being
  told that games of chance then become gambling.
- **A money outage that breaks the rest of the grid.** If money is down, payments are refused with a clear message; regions,
  logins and everything else keep working.

## 6. Constraints

- Windows and Debian, same behaviour (launcher design).
- Firestorm is the viewer that must work; other current viewers should (§8).
- OpenSim changes stay small and mergeable (`MERGING.md`); OpenSim updates arrive by cherry-pick from opensim/opensim.
- No outside service is contacted unless the owner picks Gloebit.
- All settings changes go through `IGridController.UpdateSettings`.
- GridGate is BSD-licensed. Gloebit's module is LGPL and is used as a separate DLL, never copied into GridGate code.

## 7. How it works

Three parts, plus the viewer-facing pages:

```
 viewer ──UDP──▶ region program ──HTTPS + region key──▶ launcher: money service ──▶ MariaDB `money`
   │             (GridGateMoneyModule:                   (all rules, the ledger,       (accounts, entries)
   │              thin, decides nothing)                  session checks)
   │                                                           │
   └──HTTP(S)──▶ launcher website: economy helper ─────────────┘   reads Robust `Presence` to check sessions
                 (Buy currency / Buy land windows)
```

### 7.1 The ledger (call)

A new database, `money`, in the private MariaDB, with its own user (`moneyuser`) that only the launcher uses. The launcher
creates it the first time money is turned on, as it does `robust` and `opensim` (`Database/DatabaseProvisioner.cs`).

- **Accounts:** one per resident or visitor, plus three system accounts:
  - **Grid bank**, where coins are created;
  - **Fees**, where upload and classified fees go (the owner can move them out);
  - **Holding**, where a buyer's coins wait while an object or parcel is delivered (§7.2).

  Each account has a balance and a frozen flag.
- **An account is identified by avatar UUID plus home grid (call).**
  - Residents' home grid is this grid.
  - A visitor's is the home address the region reports (`IUserManagement.GetUserHomeURL`).
  - The Gatekeeper already refuses a visitor who reuses a resident's UUID (`GatekeeperService.cs:452-468`). It can't stop a rogue
    grid presenting another grid's visitor's UUID, though, so the same UUID from a different home grid gets a separate, empty
    wallet. The rogue can't spend the real visitor's coins.
  - If a real home grid changes its address, staff can merge the old wallet into the new one on the Money page. The merge is an
    entry like any other.
- **Groups hold no coins (owner).** There are no group accounts in this version (§7.3 lists what that refuses).
- **Entries:** one row per movement, never edited or deleted. Each holds: a unique transaction id, time, type (§7.4), from
  account, to account, amount, both balances after, region, object id and name when there is one, the staff member when staff
  did it, a reason text, a payment reference for bought coins, and the id of the entry it reverses, for refunds.
- **Rules:**
  - Every movement is a pair of balance changes and one entry, in one database transaction.
  - Only the Grid bank may go below zero. Its negative balance is the number of coins in existence.
  - A balance changes only with a matching entry.
- **Self-check (call):** on start-up and once a day, the service recomputes every balance from the entries and checks that all
  balances add up to zero. A mismatch freezes all payments, puts a red notice on the dashboard and writes the difference to the
  launcher log. The owner unfreezes after looking.
- **Backups:** the `money` database is included wherever the launcher backs up or exports the grid's databases (builder: follow
  what exists for `robust`/`opensim`).

### 7.2 The money service (call)

The money service runs **inside the launcher process**, on **its own listener and port**, separate from the website.

- **Port:** chosen and checked like the other ports (`Grid/PortAllocator.cs`, `Grid/PortCheck.cs`). Builder picks the default.
- **Who can reach it:**
  - On Local and Simple Public Server it listens on `127.0.0.1` only.
  - On a Grid Asset Server it also listens on the LAN addresses, for Region Servers, and the plan page's advisory says so.
  - It is never forwarded by the router. The Simple Public Server and Grid Asset Server advisories (install-and-plans design
    §7) gain one line saying not to forward it.
- **Always HTTPS.** It uses a certificate the launcher makes just for money, separate from the website's. Regions pin its
  fingerprint, which the launcher writes into their INIs, so a stolen or swapped certificate is refused.
- **Region keys:** every region program gets its own random key (`Settings/Secrets.cs` `NewPassword`), written into that program's
  INI. The service keeps only a hash of each key and knows which regions each key covers. A request without a valid key gets a
  bare 401 and is counted; repeated failures from one address are slowed down and logged.
- **A key may act only for avatars in its own regions.** For anything an avatar does (pay, buy, land), the service checks Robust's
  `Presence` table: the avatar's session id and secure session id must match, and the avatar's current region must be one the
  key covers. This also covers hypergrid visitors, because the Gatekeeper records them in `Presence` too
  (`OpenSim/Services/HypergridService/GatekeeperService.cs:552`).
- **Every request carries a transaction id** that the region makes. A repeated id returns the first answer and moves nothing.
- **Payments are one database transaction each:**
  - Debit with a guard: `balance = balance - amount WHERE balance >= amount AND NOT frozen`. If no row changes, the answer is
    "not enough coins" or "frozen".
  - Credit, then write the entry.
  - Accounts are locked in a fixed order to avoid deadlocks. A deadlock is retried a few times.
- **Purchases use a hold (call).** Buying an object or a parcel runs in three steps:
  1. **Hold:** the buyer's coins move to Holding, guarded like any debit.
  2. **Complete:** once delivery succeeded, Holding pays the seller.
  3. **Return:** if delivery failed, Holding pays the buyer back.

  A hold that is neither completed nor returned within a few minutes returns to the buyer on its own (builder: about 5
  minutes). This covers a region that crashes mid-sale. Completing, returning and expiring a hold that already exists aren't
  blocked by freezes, because Holding always has the coins. So **"paid but not delivered" never depends on the seller still
  having the money.** Each step is an entry.
- **Script payouts need the payer's say-so (owner).**
  - A resident's objects can pay out only after that resident has turned on "my scripts may pay out" on their My coins page
    (§9.5). It is off by default; staff can turn it on for someone, with an audit entry.
  - Each resident also has a **daily payout limit**, set by the owner (§7.5).
  - Visitors have no website account, so their objects can't pay out.
  - The reason: the object's owner is often offline, so `Presence` can't vouch for them. The service can't see objects
    either, because a Region Server keeps its objects in its own database. Opt-in and the limit cap what a taken-over region
    could do (§11).
- **Amounts are whole coins,** greater than zero and below a sanity limit the owner can raise (call: 1,000,000).
- **When something is wrong, payments are refused, never guessed.** If the database or Robust can't be reached, or the service is
  frozen, the region gets "money is unavailable right now". Nothing else in the grid is affected.
- **No secret in errors or logs.** Callers get short, fixed error codes. Keys, session ids and connection strings are never
  logged or returned. Logging defaults to normal, not debug.
- **The API** is a small JSON-over-HTTPS interface, versioned (`/money/v1/...`). The builder designs the exact shape. It needs:
  balance, transfer (with type), hold, complete and return, refund (reverse an entry), economy settings (prices and the
  currency name) and a health check.

### 7.3 The region module (call)

A new region module, **`GridGateMoneyModule`**, in GridGate's own code. It implements `IMoneyModule`
(`OpenSim/Framework/IMoneyModule.cs:33`) and replaces the sample module when `[Economy] EconomyModule = GridGateMoneyModule`.
**It decides nothing.** It passes each money action to the service and reports the answer back to the viewer. The module only
holds what the service told it, such as the last balance and the prices.

What it handles (research notes in `SampleMoneyModule.cs`, Gloebit's module, and §16):

| Viewer or script action | Hook | What the module does |
|---|---|---|
| Balance request | client `OnMoneyBalanceRequest` | Ask the service; `SendMoneyBalance`. |
| Pay avatar | client `OnMoneyTransferRequest`, subscribed in `OnNewClient` | **Refuse unless the source is `client.AgentId`.** The packet's source isn't checked anywhere else (`LLClientView.cs:8598`). Transfer; send both sides their new balance. |
| Pay object (tip jar, vendor) | same, type 5008 | Same check. Also refuse an amount the object doesn't offer when its pay box hides the free-amount field (`llSetPayPrice`; nothing server-side checks this today). After the coins move, raise `OnObjectPaid`, so the script's `money()` event fires. |
| Buy object | client `OnObjectBuy` | Use `remoteClient.AgentId`, not the packet's. Check the part is a root, is for sale, and that price and sale type match. For "contents", check there is something transferable. Hold, then call `IBuySellModule.BuyObject`. **True: complete. False: return at once.** |
| Buy land | scene `OnValidateLandBuy` / `OnLandBuy` | The buyer is never charged for land they don't get. The seller is the parcel's owner at the time of sale. Hold, and complete only once the new owner is confirmed; otherwise return. The builder chooses the sequencing. Gloebit re-runs the land validation itself, then confirms the new owner (Gloebit `GloebitMoneyModule.cs:3166`). |
| `llGiveMoney`, `llTransferLindenDollars` | `ObjectGiveMoney` | Refuse an amount of 0 or less. Transfer from the object owner, with type "script payout", the object id and name. The service refuses unless the owner opted in, the daily limit allows it, and grid-wide script payouts are on (§7.2, §7.5). |
| Upload fee | `UploadCovered` / `ApplyUploadCharge` | Check, then charge to Fees. |
| Group join fee, classified ads | `AmountCovered` / `ApplyCharge` | Check first. OpenSim charges *after* the action (`GroupsModule.cs:1151`, `UserProfileModule.cs:812`). If the charge then fails, the entry is recorded as uncollected and isn't retried. Group creation is always free (§8.8). |
| Parcel pass | `MoveMoney` (bool) | Transfer. |
| Economy data | client `OnEconomyDataRequest` | Send the prices the owner set, the same numbers the service charges. The sample sends prices it never charges (`SampleMoneyModule.cs:114`). |
| Region entry | `OnCompleteMovementToRegion` (as Gloebit) | Push the balance, so the status bar is right after a teleport. |

**Group-owned things (owner: no group wallets).** The module refuses each of these with a short message, for example
"Group-owned objects can't take payments on this grid. Ask the owner to set it to a person.":

- paying a group-owned object;
- buying a group-owned object;
- buying land owned by a group;
- a parcel pass on group land;
- a payout from a group-owned object.

`llTransferLindenDollars` already refuses group-owned objects (`LSL_Api.cs:17551-17653`); `llGiveMoney` gets the same rule.
A **group join fee** goes to the group's founder (call), or to Fees if the founder isn't known. Firestorm shows join fees, so
residents see what they pay.

The module registers in `AddRegion`, because YEngine hooks `OnObjectPaid` in `RegionLoaded` (`XMREngine.cs:745`). Messages to
the viewer stay under about 250 characters (Gloebit's note at `GloebitMoneyModule.cs:3434`).

### 7.4 Entry types (call)

Starting balance, allowance, staff gift, staff take-back, bought coins (real coins only), pay avatar, pay object, object sale,
land sale, parcel pass, script payout, upload fee, group join fee, classified fee, hold, hold completed, hold returned, refund,
wallet merge, and fee moved (Fees to an account).

### 7.5 Owner settings (call)

All on the Money page (§9), stored in the launcher settings under the money module, with these defaults:

- **Mode:** Off.
- **Currency:** symbol "C$" for the viewer, name "coins" for GridGate's pages (§8.7).
- **Starting balance** for new residents: 100 in play mode, 0 in real mode.
- **Weekly allowance:** 0, off.
- **Fees:** upload 0 and classified ads 0. Group join fees are set per group in-world, as today. There is no group creation fee (§8.8).
- **Script payouts:** allowed grid-wide. Each resident still has to opt in (§7.2).
- **Daily script payout limit per resident:** 5,000 coins (call). Owners running games may raise it.
- **Visitor wallets:** on (§7.6).
- **Largest single payment:** 1,000,000.

### 7.6 Hypergrid visitors (call)

- A visitor keeps their home-grid UUID on this grid (`GatekeeperService.cs:578`), so their wallet is keyed by it, plus their
  home grid address.
- A wallet is created the first time someone pays them or they look at their balance. It starts at 0, with no starting balance
  and no allowance.
- Visitors can receive tips and payments, and spend what they have. In real mode, staff can credit coins a visitor bought.
- The balance stays on this grid. If they come back, it's still there.
- `llTransferLindenDollars` refuses non-local recipients today (`LSL_Api.cs:17623`). It is changed to accept a visitor the region
  knows when visitor wallets are on (§7.8).
- With visitor wallets off, visitors can't pay or be paid. They are told so politely.

### 7.7 Region Servers (call)

- Money lives only on the Grid Asset Server. A Region Server's regions call the Grid Asset Server's money service over the LAN.
- The Grid Asset Server publishes what Region Servers need in its grid info: the money mode, the money service address and the
  certificate fingerprint (`GridConfigWriter.cs:76`, alongside the existing join keys). It still publishes **no secret**.
- **Pairing:** the Grid Asset Server's Money page shows a one-time pairing code. The owner types it into the Region Server's
  Money page (or join step). The Region Server's launcher exchanges it, over the pinned HTTPS connection, for one key that covers
  that server's regions. The Money page lists paired Region Servers, and the owner can remove one, which ends its key at once.
- If the Region Server isn't paired, its regions run with money refused ("this region isn't connected to the grid's money yet").
- Gloebit on a Region Server: the owner enters the Gloebit settings on that computer too. Gloebit runs inside each region.

### 7.8 Small changes to OpenSim itself (call)

Each is small, kept in its own commit, and listed in `MERGING.md`:

1. **`PERMISSION_DEBIT` must be granted by the object's owner.** Today the granter is never compared with the owner
   (`LSL_Api.cs:4470`), so a script could take money from whoever clicked "yes". `llGiveMoney` and `llTransferLindenDollars`
   refuse unless the granter is the owner.
2. **`auto_grant_all_perms` never grants `PERMISSION_DEBIT`** (`LSL_Api.cs:4521`).
3. **`llGiveMoney` refuses amounts of 0 or less** at the script side too (`LSL_Api.cs:3224`).
4. **`llTransferLindenDollars` accepts hypergrid recipients** the region's user management knows, when the money module says
   it can pay them (§7.6).
5. **Uploads through the older UDP path are charged** as well as checked (`AssetTransactionModule.cs:259`; today only caps
   uploads are charged, `Scene.Inventory.cs:102`). This only matters when the upload fee isn't 0.

## 8. The viewer side

Read from Firestorm's source (FirestormViewer/phoenix-firestorm, `master` at `6759a401`, 2026-11-13). Paths are under
`indra/newview` unless they say otherwise. OpenSim builds of Firestorm use `fsgridhandler.cpp` for grid settings.

### 8.1 How Firestorm finds the economy helper

The **Buy currency** and **Buy land** windows call the grid's "economy helper" over XML-RPC. The viewer picks the helper address
in this order (`fsgridhandler.cpp:1148-1163`):

1. the `-helperuri` command-line flag;
2. the region's simulator feature `OpenSimExtras["currency-base-uri"]` (`lfsimfeaturehandler.cpp:219-230`);
3. the grid info `economy` key (`helperuri` is an alias; `economy` wins, `fsgridhandler.cpp:474-486`).

It appends `currency.php` or `landtool.php` by plain string joining (`llcurrencyuimanager.cpp:297`, `llfloaterbuyland.cpp:856`),
so **the address must end with `/`**. If no helper is known, the call goes to a bare `currency.php`, fails, and the viewer shows
Second Life's "server is down" text and links.

**What GridGate does (call):**

- Robust's grid info `economy` = the launcher website's helper address, ending in `/` (`bin/Robust.HG.ini.proto:658`).
- `GridGateMoneyModule` also sets `currency-base-uri` on every region to the same address. This matters for **hypergrid
  visitors**: their viewer reads the grid info of their home grid, so without the region setting their Buy windows would go to
  their home grid's helper. Firestorm shows a one-time "third-party currency portal" notice when the address changes
  (`CurrencyURIOverrideReceived`). That's correct for visitors, and residents only see it once.
- The helper is served at `<website>/economy/currency.php` and `<website>/economy/landtool.php` (the `.php` names are the
  viewer's, not a PHP dependency).

### 8.2 HTTPS and the helper

Firestorm checks the helper's certificate and host name, and has no "accept anyway" for it (`llxmlrpctransaction.cpp:226-230`).
**A self-signed certificate makes both Buy windows fail.** Plain HTTP isn't blocked.

**(call)** The advertised helper address is `https://` only when the website uses the owner's own certificate (HTTPS design
§7.1). Otherwise it is `http://`. The helper paths join the list of paths served as plain HTTP while HTTPS is on (HTTPS design
§7.3, like `/welcome`). They set no cookies.

The helper calls carry the avatar's secure session id. Over plain HTTP that is as exposed as the rest of the viewer's traffic to
the grid (login, UDP). The money service doesn't take the helper's word for anything (§8.5).

### 8.3 Buy currency

The sequence (`llcurrencyuimanager.cpp`, `llfloaterbuycurrency.cpp`):

1. **The window opens at amount 0** and sends nothing.
2. **The quote:** two seconds after the resident stops typing an amount, the viewer sends `getCurrencyQuote` with `agentId`,
   `secureSessionId`, `language`, `currencyBuy` and the viewer version fields (`:166-179`).
3. **The answer it reads** (`:184-220`):
   - `success`; when false, `errorMessage` and `errorURI`;
   - `currency.estimatedCost`, in US cents, or `currency.estimatedLocalCost`, any text;
   - `currency.currencyBuy`;
   - `confirm`.

   The Buy button stays grey without an estimate.
4. **`buyCurrency`** sends the same fields plus `confirm`, the estimate it received, and a typed password if `confirm` was
   `"password"`.
5. **Errors:** a failed answer shows the `CouldNotBuyCurrencyOS` alert with the message, and its OK button opens `errorURI`
   (`llfloaterbuycurrency.cpp:201-219`). The window closes.
6. **Success:** Firestorm shows Second Life's wording, which mentions US$ and the secondlife.com dashboard
   (`notifications.xml:9513`).

**What GridGate answers (call):**

- **Play coins:** `getCurrencyQuote` answers `success` false. The message says these are play coins that can't be bought and
  how to get them (allowance, gifts, earning), in the resident's `language` where GridGate has it. `errorURI` opens the grid's
  Money help page.
- **Real coins, hand crediting:** the same, with a message saying how to buy, and `errorURI` opening the grid's Buy coins page
  (§9.3).
- **`buyCurrency` always refuses** until the automatic button exists (§9.4). It can't be reached anyway without a quote, but it
  is answered safely.
- So residents never see the Second Life success wording.

### 8.4 Buy land

- **The pre-flight is sent automatically** when a parcel is buyable (`llfloaterbuyland.cpp:662-669`).
  - It sends `preflightBuyLandPrep` with `agentId`, `secureSessionId`, `language`, `billableArea` and `currencyBuy`: the coins
    the buyer is short.
  - It reads `success`, `errorMessage`, `errorURI`, `membership{upgrade, action, levels}`, `landUse{upgrade, action}`,
    `currency{estimatedCost, estimatedLocalCost}` and `confirm` (`:672-715`).
- **The Buy button is enabled only if the pre-flight succeeded** (`:1293`). Without a working helper, **no land can be bought in
  Firestorm at all, not even free land.**
- **After the resident confirms:**
  - If there is no upgrade, no coin shortfall, and `confirm` isn't `"password"`, the viewer skips the helper and sends
    `ParcelBuy` straight to the region (`:717-768`).
  - Otherwise it calls `buyLandPrep` first, and sends `ParcelBuy` only if that succeeds.

**What GridGate answers (call):**

- **Pre-flight:**
  - With no shortfall: `success` true, no membership or land-use upgrade, and `confirm` empty, so the purchase goes straight to
    the region. The region charges through the money service (§7.3).
  - With a shortfall: `success` false, saying how many more coins are needed. In real mode, `errorURI` opens the Buy coins page.
- **`buyLandPrep`:** refuses, with the same message. It is only reached when there is a shortfall or upgrade, and GridGate
  offers no upgrades.
- **When money is Off:** the helper still answers the pre-flight with `success`, so free land keeps working. This is the case
  today: with no helper set, the sample module answers from the region (`SampleMoneyModule.cs:147-154`).

### 8.5 How the helper checks callers

- Every call's `agentId` and `secureSessionId` are checked against `Presence`. A mismatch gets a fixed "please log in again"
  message.
- The helper moves no money, and it tells the money service nothing it would act on.
- It is rate-limited per address like the website's other anonymous endpoints.

### 8.6 Answers Firestorm reads strictly

These come from Firestorm's parser (`llxmlrpctransaction.cpp:262-306`, `llcommon/llsd.cpp:315`, `llxml/llxmlnode.cpp:3432-3447`):

- The response must be exactly `methodResponse/params/param/value/struct`. A fault or anything else reads as a failure with an
  empty message.
- `success` must be `<boolean>1</boolean>` or `<boolean>0</boolean>`. **A string `"false"` counts as true.**
- Integers must be plain digits. `<boolean>true</boolean>` or a non-numeric `<int>` reaches an unguarded `std::stoi`. This may
  crash the viewer; a crash wasn't confirmed, but the helper must never send one.
- Always send a real `errorMessage` and `errorURI`, or residents see Second Life's texts and links.

Tests check the helper's raw XML against these rules.

### 8.7 The currency name

- **Residents:** Firestorm takes the name from the login response's `currency` field. Without it, it shows "OS$"
  (`llstartup.cpp:5292-5317`). OpenSim sends it from Robust's `[LoginService] Currency` (`LLLoginResponse.cs:570`).
- **Visitors:** a region can override it with `OpenSimExtras["currency"]` (`lfsimfeaturehandler.cpp:232-239`).
- Firestorm then replaces "L$" in its texts (`llui/lluistring.cpp:150`).

**(call)** GridGate sets both, from the owner's currency name, so residents and visitors see the same name. Firestorm uses it
as a short symbol before amounts. The settings page asks for a short symbol (for example "C$", up to 4 characters) and a
longer name for GridGate's pages ("coins").

### 8.8 Balance, notices and prices

- **The status bar balance** comes from `MoneyBalanceReply`. The viewer asks:
  - at login;
  - one second after the resident clicks the balance;
  - after Buy currency, group creation and uploads (`llstatusbar.cpp:890`, `llstartup.cpp:2738`).

  Otherwise it relies on replies the region pushes. The module pushes a balance after every movement, to both sides when
  they're online in its regions, and on region entry (§7.3).
- **Payment notices:**
  - A `MoneyBalanceReply` with a `TransactionInfo` block makes Firestorm show its own translated "You paid…/…paid you" notices
    (`llviewermessage.cpp:5807-6079`).
  - An empty `Description` shows no extra popup.
  - A null source and destination is a silent balance update.
  - Firestorm suppresses repeated transaction ids (it keeps the last 30 or so), so every reply needs a fresh id.

  **(call)** The module uses `TransactionInfo` for payments, so notices appear in the resident's viewer language, and silent
  updates for refreshes.
- **Paying:**
  - `MoneyTransferRequest` uses type 5001 for paying an avatar and 5008 for paying an object, whose description is the object
    name.
  - The pay window's buttons come from `RequestPayPrice` / `PayPriceReply` (`llfloaterpay.cpp`).
  - Firestorm checks the balance it knows before paying, and opens Buy currency when it's too low. That's one more reason
    pushed balances must be current.
- **Prices Firestorm shows:**
  - The upload fee comes from `EconomyData.PriceUpload` (`llagentbenefits.cpp:161-166`).
  - Mesh costs come from the upload cap's `upload_price`.
  - **The group creation fee is hard-coded to 0 on OpenSim** (`llagentbenefits.cpp:197-202`), so Firestorm would say "free"
    and then charge. **(call)** GridGate doesn't offer a group creation fee. Group join fees are set per group in-world, and
    Firestorm shows those.
  - The directory listing fee comes from grid info `DirectoryFee`. GridGate leaves it at 0.

### 8.9 Why DTL grids struggle with Firestorm

DTL issue 6 ([MTSGJ/opensim.currency#6](https://github.com/MTSGJ/opensim.currency/issues/6), open since 2025-09-29) is a
developer who couldn't get Firestorm's Buy currency to send anything. The source explains why:

- nothing is sent until a non-zero amount is typed and two seconds pass;
- the Buy button stays grey without an estimate;
- a helper address without a trailing `/`, or with none set, builds a broken address.

§8.1 to §8.6 are written so GridGate avoids each of these.

## 9. The owner's and residents' pages

### 9.1 Money page (staff)

Under the dashboard's features. Admin and owner can change things; supervisors can view (call). Every change is written to the
audit log.

- **Mode** (§10 explains each change before it happens).
- **Settings** (§7.5).
- **Totals:** coins in existence, coins held by residents, by visitors, in Fees, and today's movements.
- **People:** search an avatar and see their balance and history. **Give** and **take back** coins, with a required reason.
  **Freeze** or unfreeze one account.
- **Freeze all payments:** an emergency switch, with the reason shown on the dashboard.
- **Refund an entry:** reverses one movement, with a reason. The original stays in the history.
- **Region Servers** (Grid Asset Server only): pairing code, paired servers, remove.
- **Self-check:** the last result, and a "check now" button.

### 9.2 Turning on real coins (owner only)

Real coins can't be picked until the owner has:

- read a plain statement that selling coins for real money brings duties that GridGate can't handle for them: refunds, taxes,
  consumer law, and in some places rules on stored value; and that games of chance using coins that can be bought with money
  count as gambling in many places, which Second Life banned in 2007;
- ticked "I understand, and I am responsible for selling coins on this grid";
- written a coins section in the grid's Terms (the legal pages gate in the audiences design §4), shown to residents as a Terms
  change they accept.

The tick is recorded in the audit log with the time.

### 9.3 Selling coins by hand (real mode) (call)

- The owner writes the grid's **Buy coins** page text: prices, how to pay (for example, a PayPal link), and how long crediting
  takes.
- The resident pays the owner outside GridGate.
- Staff open the person on the Money page and choose **Credit bought coins**: amount, and a required payment reference (for
  example, the PayPal transaction id). This writes a "bought coins" entry. A reference used before is refused, which stops
  double crediting.
- **Refund bought coins** reverses it, if the coins are still there.

### 9.4 Later: automatic Buy coins

Out of scope here. It will be its own design: a payment provider's checkout and its "payment completed" notice crediting the
same "bought coins" entry, with the payment reference. The ledger and the hand-credit page are built so that nothing changes
underneath.

### 9.5 Residents' My coins page (call)

On the resident's account pages:

- their balance and their history (date, what, who, amount), so people can check what happened for themselves;
- **"My scripts may pay out" (owner):** off by default. Turning it on explains that their objects' scripts (games, rental
  boxes, refunds) can then pay people from their balance, up to the daily limit, which is shown. Turning it on or off is
  recorded.

Visitors have no website account, so they see only the viewer's balance, and their objects can't pay out.

## 10. Changing mode

| From → to | What happens |
|---|---|
| Off → Play or Real | Creates the `money` database the first time. Existing residents get the starting balance the first time they appear. |
| Play → Real | §9.2 first. Balances carry over. A warning repeats that coin games now count as gambling. |
| Real → Play | Allowed, with a strong warning that people paid real money for coins. Balances are kept. |
| Play or Real → Off | Payments stop. All data is kept; turning back on restores everything. |
| Play or Real → Gloebit | Warning: GridGate coin balances don't move to Gloebit. They are kept, not shown, and come back if the owner switches back. |
| Gloebit → Play or Real | Same warning the other way. Gloebit balances belong to Gloebit. |

A mode change takes effect when regions restart. The page says so, and offers to restart them (call).

## 11. Threats and how the design answers them

| Threat | Answer |
|---|---|
| Someone on the internet calls the money service | Not listening there (§7.2); HTTPS; a region key is required. |
| A program on the LAN calls it | A region key is required; keys come only by pairing; Region Servers can be removed. |
| A region program is taken over | Its key acts only for avatars currently in its regions, checked against Robust (§7.2). Script payouts can only come from residents who opted in, up to their daily limit (§7.2); this is the remaining exposure, and the owner accepted it with these limits. The grid-wide switch and the large-payment limit also apply. Every movement is in the ledger and can be refunded. The owner can remove a Region Server's key at once. |
| A forged viewer packet pays from someone else | The module uses `client.AgentId`, never the packet's source (§7.3). |
| A modified viewer pays an object the wrong amount | Fixed pay-price amounts are enforced (§7.3). |
| A script takes money from someone who didn't mean to allow it | The debit permission must come from the object's owner and is never auto-granted (§7.8). |
| Two purchases at once | One guarded database transaction per payment (§7.2). |
| A retry pays twice | Transaction ids (§7.2). |
| Paid but nothing delivered | Purchases use holds that return to the buyer on failure or on their own after a few minutes (§7.2, §7.3). |
| A bug or a bad edit corrupts balances | Ledger-only changes, the daily self-check, and freeze (§7.1). |
| Staff abuse | Required reasons, audit log, the owner sees all; supervisors can't move coins. |
| Double-crediting bought coins | Unique payment references (§9.3). |
| Secrets in logs or errors | §7.2. |
| Database password exposure | The `money` database has its own user, and its password is stored like the others (`DatabaseSecrets`). |

## 12. The safety review gate (owner)

**(owner)** The money code must be reviewed and approved as safe before it ships.

- **What is reviewed:** the money service, the ledger code, the region module, the economy helper, the Money and My coins
  pages, and the OpenSim changes in §7.8.
- **Tests that must exist first (call):**
  - Many payments at once from one account never overdraw it, and the totals still add up.
  - A repeated transaction id never pays twice.
  - Every way of asking without the right key, session or region is refused: no key, a wrong key, another region's avatar, a
    stale session, a visitor's forged session.
  - A failed delivery returns the hold; an unfinished hold returns on its own, even while payments are frozen.
  - The self-check catches a balance changed behind its back.
  - Script payouts are refused without opt-in and above the daily limit.
  - Every group-owned case in §7.3 is refused.
  - Fuzzing of the service's request parsing.
- **The review (owner):**
  - Separate, independent AI review sessions do it. They didn't write the code, and they get the code and this design, not the
    builder's conclusions.
  - They write a security review of all of the above: each finding, its severity and what was done about it.
  - Fixes are re-checked by a fresh review.
  - The review is kept in `docs/superpowers/reviews/`.
- **The approval (owner):** the owner reads the review and approves it. Until then, the Money page offers only Off. Play coins
  and Gloebit are hidden behind one constant, which the approving change flips.
- **Real coins need more (owner):** before real coins can be switched on, a human security reviewer also reviews the code.
  Real coins sit behind a second constant, flipped only after that review and the owner's approval. Play coins can ship
  without waiting for it.
- **Later changes:** any later change to the code in "What is reviewed" gets its own review before it ships.

## 13. Why not the other options

These were considered and set aside. They are written down so they don't have to be rediscovered.

### 13.1 Bundling the DTL/NSL Money Server as it is

The DTL/NSL Money Server ([MTSGJ/opensim.currency](https://github.com/MTSGJ/opensim.currency), BSD-3) is what most self-run
OpenSim currencies are built on, so it was the first choice. Reading its source (HEAD `a53a1e8`, last commit 2025-06-30, read
2026-11-13) showed it can't be shipped:

- **Anyone who can reach its port can create money or move anyone's money.**
  - `ClientLogin` accepts any avatar id and session ids the caller makes up, and creates users with a starting balance of 1000.
    `TransferMoney` then trusts those made-up sessions (`MoneyXmlRpcModule.cs:313-505`, `:597`).
  - `ForceTransferMoney` moves money between any two users with no session check. The sample settings file turns it on
    (`MoneyXmlRpcModule.cs:682-793`; `config/MoneyServer.ini:46`).
  - `AddBankerMoney` mints money with no session check (`:963`).
- **The callback address comes from the caller,** so the server can be made to send session ids to any address
  (`:450`, `:1197`, `:1371`).
- **Two transfers at once can overdraw an account.** The balance is read, then lowered with no `balance >= amount` guard, and
  the withdraw and the deposit run as separate commands that may use different connections (`MoneyDBService.cs:411-415`,
  `MySQLMoneyManager.cs:1037-1039`). The connection pool's flags aren't thread-safe (`MoneyDBService.cs:97-118`).
- **Certificates:**
  - It ships sample certificates with their private keys (`server_cert.p12`, `SineWaveCert.pfx` with password "123").
  - Certificate checks are off by default.
  - The client-certificate check accepts any self-signed certificate (`NSLCertificateTools.cs:153-155`).
  - The certificate name is read from a request position the caller can influence, into a field shared by all requests
    (`MoneyXmlRpcModule.cs:285-298`).
- **The script key** is MD5 with no nonce, with sample key `123456789` (`:840-846`).
- **It leaks:** exception text goes back to callers, the database password ends up in an exception message
  (`MySQLMoneyManager.cs:80`), and debug logging is on by default.
- **Buy currency and buy land aren't written.** `getCurrencyQuote`, `buyCurrency`, `preflightBuyLandPrep` and `buyLandPrep`
  throw "not implemented" (`:256-281`). Grids fill the gap with PHP helper scripts. The common ones
  ([opensim-helpers](https://github.com/GuduleLapointe/opensim-helpers)) build SQL from viewer-supplied ids, so they are open to
  SQL injection (`includes/economy.php:484, 543`), use a default key `1234567883789`, and have a payment step that always
  succeeds (`:414-426`).
- **Its README** says it is "less secure" than the original because the Web Monitor was removed. Its issue tracker says the
  repository is incomplete and won't build (issue 2). It has 3 stars.

### 13.2 Fixing the whole DTL Money Server

Every item above would have to be fixed in code nobody here knows. The result would still need a full review, and GridGate gains
nothing from staying compatible with it: it uses neither the PHP helpers nor any DTL tools. The fixes amount to a rewrite of the
server, without the freedom to design it well.

### 13.3 Keeping DTL's region module with a new server

This was the plan for a while: the region module is the part that has been used on real grids. It has its own problems:

- One shared flag, set by every login, turns money off for every avatar on the region when any one login fails
  (`DTLNSLMoneyModule.cs:639`).
- Its handlers accept a zero secure session id (`:886`).
- It depends on the server's force-transfer method for script payouts when the owner is offline.
- It speaks DTL's protocol, which carries the design mistakes above.

A thin module written for this design (§7.3) is smaller to review than these fixes. DTL's event handling, OpenSim's sample
module and Gloebit's module serve as references for which events matter.

### 13.4 OpenSim's sample money module (BetaGridLikeMoneyModule)

It keeps no balances. Every handler is a stub, it allows only free sales (`SampleMoneyModule.cs:838-872`), and it advertises
prices it never charges. It is a placeholder, not a currency, and it can't become real money.

### 13.5 Putting the money service inside Robust

Robust already knows sessions, but:

- Its private port has no authentication yet (install-and-plans design §10 and §14), and money would make that hole expensive.
- The launcher already has the database provisioning, staff pages, audit log, secrets and HTTPS that the service needs.
- Changing Robust means more OpenSim code to carry through cherry-picks.

### 13.6 A separate money program

A program of its own would isolate money from a website crash. But it is one more thing to start, watch, update and secure,
with no other benefit: the launcher already supervises everything. A separate listener inside the launcher (§7.2) gets the
isolation that matters, a port that isn't the website's.

### 13.7 Paying for each item with PayPal

Paying for each item with PayPal looks simpler than coins, but it fails for exactly what grids do most: small amounts.

- **Fees eat small sales.** PayPal's standard US rate is about 3.49% + $0.49. Its micropayments rate, about 4.99% + $0.09, needs
  PayPal's approval. A $0.50 item would lose $0.51 at the standard rate, or about $0.11 at the micropayments rate.
- **Every purchase or tip would leave the viewer** for a PayPal page.
- **Paying sellers directly** needs PayPal's marketplace program, which a hobby grid may not be approved for.

Coins solve this: one payment buys many purchases. (Rates as published by third-party fee guides in 2026; check PayPal's own
schedule before relying on them.)

### 13.8 Gloebit as the only option

Gloebit is the most used ([Hypergrid Business active grids list](https://www.hypergridbusiness.com/statistics/active-grids/),
updated 2026-10-30): 29 of about 43 grids that list a money system, out of about 330 grids. It is kept as an option (§3). As the
only option it fails the owner's goals:

- it depends on an outside company;
- it had a payment outage in 2021 that damaged trust
  ([Hypergrid Business](https://www.hypergridbusiness.com/2021/05/gloebit-payments-are-back-but-trust-is-damaged/));
- it offers no play money;
- it takes a share of every sale: about 2% per transaction, plus the greater of $1 or delivery cost per cash-out, according to
  its merchant terms. Its other pages give different figures.

### 13.9 Podex and OMC

Both are outside services. Podex lets people buy and sell grid currencies through its website
([podex.info](https://www.podex.info/)); one grid in the active grids list uses it. OMC is an older currency shared by a few
grids. Neither offers what the owner wants that a GridGate ledger plus Gloebit doesn't, and each adds a third party.

### 13.10 Cash-out on self-run grids

Paying artists dollars out of the owner's pocket would make the owner a payment business, with the legal weight that brings.
**(owner)** Self-run grids don't cash out; owners who want that use Gloebit.

## 14. Order of work (call)

1. **The ledger and money service,** including holds, payout opt-in and limits, with the tests in §12, and the Money page in
   its hidden state.
2. **The region module and the OpenSim changes** (§7.8), with live Firestorm checks of every action in §7.3.
3. **The economy helper and the viewer currency name** (§8).
4. **Hypergrid visitor wallets and Region Server pairing.**
5. **Gloebit as a choice:** stage `Gloebit.dll` only when chosen, a settings page for its key and secret (`secret` fields), and
   allow `gloebit.com` in INIs only then.
6. **The AI security review and the owner's approval** (§12). Then flip the first constant: Play coins and Gloebit become
   available.
7. **Real coins:** §9.2 and §9.3, then the human security review and the owner's approval. Then flip the second constant.

## 15. Left to the builder

- The money service's exact API, port default and listener setup in Kestrel.
- Table and column names, indexes and the migration approach (follow what `DatabaseProvisioner` does).
- How the land purchase charge is sequenced, within the rule in §7.3.
- The pairing exchange's details, within §7.7.
- Page layout and wording, following the existing staff and guest pages and their six languages.
- The weekly allowance's timing.
- How a mode change restarts regions.

## 16. Research notes

- **OpenSim hooks** (this tree, read 2026-11-13):
  - Who calls each `IMoneyModule` member: §7.3.
  - `GetBalance` and the void `MoveMoney` overload have no callers.
  - `BuyObject` doesn't check the price or that the object is for sale (`BuySellModule.cs:113-281`).
  - For "contents" sales it returns true even when nothing moved (`:248-274`).
  - Land: `LandBuyArgs` at `EventManager.cs:1013`. The land module transfers only when both `landValidated` and
    `economyValidated` are set (`LandManagementModule.cs:1740-1754`). The order between validators isn't guaranteed.
- **Gloebit's module** (LGPL, read as reference, not copied):
  - It holds, delivers, then consumes or cancels.
  - It pushes the balance on region entry and sends 0 first, in case the visitor came from another economy.
  - It notes that the viewer ignores the balance reply sent right after login, and that Firestorm can send a request twice.
- **Firestorm** (`master` at `6759a401`, read 2026-11-13): see §8. Read from source; nothing was built or run. A live check in Firestorm of every action in §7.3 is part of step 2 of §14.
