# Support tickets, step 3: notices and keeping

Design: [2026-11-05-support-tickets-design.md](../specs/2026-11-05-support-tickets-design.md), "Notices" and "Keeping".

## What gets built

1. **The switch.** `TicketNotices` (`<data>/guests/ticket-notices.json`) lists the staff who switched on "New support tickets". For each, it keeps when their last email went out and how far it covered. It holds account ids and times only. It's off by default, and switching on counts from that moment, so nothing older is sent.
   - The Email notices page shows the switch to every staff member on a grid with staff pages. The sign-up switch still shows only to those who may approve sign-ups.
   - The design put the opt-in list next to the sign-up opt-out list. It's a separate file in the same folder instead, so each switch has its own simple rules.
2. **The emails.** `ticket-new` and `ticket-reply-guest` hold the count, the numbers, and one line per ticket (number, category, subject), plus a link to the Tickets list. They never hold a message or the guest's email. At most 20 tickets are named; the rest are counted.
3. **The job.** `TicketSweep` (a background service, every 5 minutes, the first run 2 minutes after start):
   - It deletes unconfirmed tickets after 24 hours.
   - It deletes closed tickets 365 days after closing. A ticket linked to a takedown case waits until the case is closed and 365 days have passed since then. While the case is open, or can't be read, the ticket stays.
   - Each deletion is audited as done by DreamGrid, with the number only.
   - Then the notices go out. The job works out what is new from the tickets themselves (when each opened, and the guest's messages since the person's last email). Nothing about a ticket is copied into the notices file.
   - A guest reply goes to whoever took the ticket. On a ticket nobody holds, or one held by someone who is no longer staff, it goes to everyone with the switch on.
   - The emails go out at most once an hour per person; whatever arrives meanwhile goes in the next one. With no email set up, nothing is marked as sent, so nothing is lost.
   - The job runs every 5 minutes so the first email comes soon; the hourly limit is counted per person.

## Tests

- `TicketSweepTests` checks these cases:
  - only staff with the switch on get an email, with no message text;
  - the switch is off by default;
  - one email an hour, with the rest batched;
  - who gets a guest reply;
  - no email set up;
  - unconfirmed tickets deleted after 24 hours;
  - closed and linked tickets kept and deleted on the right days.
- `ProfileEmailsPageTests` checks that a supervisor can switch it on, and that it starts off.
- The template list and site facts are updated.
