# Support tickets, PR 1 (guest side) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Guests can send a ticket, confirm it by email, open it by its private link, and read and reply.

**Architecture:**
- A Core `TicketStore` keeps one JSON file per ticket under `<data>/tickets` (owner-only). It holds them in memory behind one lock, as `TakedownStore` does. The in-memory copy is the index: it is rebuilt from the files at start.
- Private links are hashed tokens stored in the ticket itself (`TicketLinks`).
- A web-side `SupportTickets` service holds the rules and limits, and sends the emails.
- Pages live under `Pages/Support`: anonymous, `GuestSideOnly`, guest layout.
- A data-protected `dg-ticket` cookie names the one ticket a link opened.

**Spec:** `docs/superpowers/specs/2026-11-05-support-tickets-design.md` (steps 1 of 3).

## Global Constraints
- **[S]** Text from guests is only ever rendered as text, and evidence links are never made clickable.
- **[S]** The same answer for every address on the confirm and lost-link pages.
- **[S]** Staff-only notes are never sent to guest pages. Notes come in PR 2, but the guest view filters them already.
- **[S]** Limits:
  - 3 open tickets per sender (confirmed ones only);
  - 3 confirm emails and 3 lost-link emails per address a day;
  - 2,000 open tickets in all;
  - 200 messages per ticket;
  - 5 live links per ticket, each for 30 days.
- No inline scripts or styles. English only.

## Review Focus
- **A link or cookie for ticket A must never show ticket B.** Test.
- **A message holding `<script>`** shows as text. Test.
- **An unconfirmed ticket isn't visible and can't be replied to**; its confirm token works once. Test.
- **Email not set up:** anonymous sending is refused with a plain sentence, and signed-in residents can still send. Test.
- **Region Server:** every `/support` page is 404. Test.

## Tasks
1. **Core:** `Tickets/Ticket.cs` (records), `Tickets/TicketLinks.cs` (make, hash, find, prune), `Tickets/TicketStore.cs` (load, create with the next `T-NNNN`, update under lock, delete). Tests: `TicketStoreTests`, `TicketLinksTests`.
2. **Web:**
   - `Guests/SupportTickets.cs`: submit, confirm, open by link, guest reply, lost link, and the limits.
   - `Guests/TicketCookie.cs`: protect and read the `dg-ticket` cookie.
   - `BotGuard.CheckRate` for signed-in residents.
   - Templates `ticket-confirm` and `ticket-access`.
   - Registration in `Program.cs`, and `<data>/tickets` made owner-only.
3. **Pages:**
   - `Support/Index` (form and "Your tickets" for signed-in residents), `Support/Confirm`, `Support/Ticket`, `Support/LostLink`.
   - Links from the guest footer, the Connect page's Help card and the guest sign-in page.
   - Tests: `SupportPagesTests`.
4. **Verify:** the full suite, a review, the PR and the VM.
