# Duplicate-code clean-up — design

Date: 2026-11-13. Source: the work list's "Code clean-up — duplicate code (review 2026-11-12)" section
(`docs/superpowers/plans/2026-10-28-wifi-replacement-worklist.md`, items 1–10 and the `[?]` item).
The owner said "no questions, just do your best", so every call below is Claude's unless marked otherwise.

## Why

The launcher grew feature by feature, and the same few blocks were copied into many files. A fix made in one copy
doesn't reach the others (the review found the "Robust didn't answer" check in about 17 versions that don't agree).
The goal is one copy of each, so later work changes one place.

## Goal

Each duplicated block in items 1–10 and the `[?]` item has one shared helper, and the copies call it. The launcher
behaves the same before and after, apart from the one decided change below.

## Non-goals

- No new features, no page or wording changes a resident or staff member would see.
- No changes under `OpenSim/` (upstream code) or in `bin/` templates.
- No renames of public routes, settings keys, cookie names or file formats.
- Not chasing duplicates beyond the review's list. A new one found on the way goes in the PR body as a note.

## Anti-goals

- A "clean-up" that changes what a page shows, what the audit log records, what gets logged at warning level, or which
  error a resident sees. Text in logs and audit entries stays word for word.
- One big helper that hides control flow (for example a generic "run anything with retries" wrapper). Helpers stay small
  and named for what they do.
- Breaking the test suite or loosening a test to get it green.

## The "Robust didn't answer" rule (the `[?]` item)

Investigated 2026-11-13. The copies agree on the exception types and differ only in where the cancel check sits:

- Most copies: `e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or
  InvalidDataException && !ct.IsCancellationRequested` — any of the five types, and our own token is not cancelled.
- `Guests/SignUpApprovals.cs` `IsDown(e)` has no cancel check, but one caller passes `CancellationToken.None` and the
  other adds `&& !ct.IsCancellationRequested` at the catch. Not a bug.
- `Guests/AccountChanges.cs` `Unreachable(e, ct)` applies the cancel check only to `TaskCanceledException`. It differs
  only when the request was already cancelled (the browser went away), where it answers "busy" instead of letting the
  cancellation through. Not a bug a person would see.
- Some copies add `StaffDirectoryUnavailableException` (GridGate's own "staff list couldn't be read").

**Decision (Claude's call):** one helper, `RobustFailure.IsUnreachable(Exception e, CancellationToken ct)`, true when `e`
is one of the five types and `ct` is not cancelled. Callers that also treat the staff list as down say so at the call
site (`e is StaffDirectoryUnavailableException || RobustFailure.IsUnreachable(e, ct)`, keeping each site's current
cancel behaviour for that exception). `AccountChanges` moves to the common rule: the only change is that a request
already cancelled ends as a cancellation instead of a "busy" answer. That is the one behaviour change in this work.

Filters that catch a different set on purpose (calls to something other than Robust's account service, such as email
sending in `Pages/Security/Email*.cshtml.cs`, the region and splash APIs, the support guest lookup, the join page)
stay as they are unless reading them shows they are the same Robust call.

## Items 1–10

As listed in the work list, with the file and line references there. Summary of the intent per item:

1. Database: one way to open a connection to the grid database, one `RunAsync`-style wrapper, one `Command`/`ExecAsync`
   pair, one Guid-column parse (reuse `InventoryStore.IdOf` or move it somewhere shared).
2. Page error/notice boxes: one partial view (or tag helper) for the error box and the ok notice. The Regions pages'
   `notice alert alert-info` style stays its own look, but goes through the same partial if that keeps the HTML identical.
3. Audit-log writes: one helper that builds the `AuditEntry` from the actor, IP, action, target and result. Entries stay
   field for field the same.
4. Two-factor setup: the staff and guest pages share one helper for the enrol/secret/unprotect/lock steps and the
   TurnOn/TurnOff/NewCodes bodies. `Security/Index` `OnPostReset` and `OnPostResetResident` share one body.
5. GridController operation lock: one helper for wait → set activity → try/finally release; `LiveRegion` reused instead of
   re-inlined. Marked medium confidence in the review: if a site's lock handling differs on purpose, it stays.
6. Two-factor sign-in: `TwoFactorPending` and `GuestTwoFactorPending` share their common code; the guest and staff code
   steps share one implementation.
7. Helpers that already exist, written out again: call the existing helper.
8. State files: the shared-read, damaged set-aside and create-folder-then-atomic-write steps go through `JsonStateFile`
   or one small helper next to it.
9. Region background actions: one helper for "requested → background.Run → finished, catch cancel/failure"; the
   Regions page redirect-with-notice and the "still running" text each in one place.
10. Mesh importer: one bounding-box helper and one vertex-subset copy.

## Constraints

- .NET 10, C#, the project's existing style (file-scoped namespaces, `sealed` classes, small static helpers).
- Every item lands with the full unit suite green (about 15 minutes, 3,957 passed / 10 skipped on 2026-11-13). New
  helpers get their own unit tests where they hold logic (the Robust rule, the state-file helper, bounding boxes).
- The CI guards (`check-retired-names.sh`, `check-old-name.sh`, secret scan) stay green.
- Work happens in the worktree `C:\Users\ystud\Desktop\CODE\GridGate-cleanup` on `chore/duplicate-code-cleanup`.

## Done means

- Each item's copies call one helper, or the PR body says why a copy stayed.
- The work list ticks items 1–10 and the `[?]` item, with one line each on what was done.
- The full unit suite and the PR's CI (Windows, Debian 12 and 13, install smoke) pass.
- The PR is opened; merging waits for the owner.

## Left to the builder

Helper names and where they live, the order of items, and whether an item splits into more than one task.
