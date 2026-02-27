# Ticket Context Workflow

## Goal
Keep ticket execution context small, deterministic, and repeatable.

## Start a new ticket pack
1. Create context/closeout files:
   - `dotnet run --project Tools/TicketKit -- init TXXXX "Ticket Title"`
2. Optional ADR stub:
   - `dotnet run --project Tools/TicketKit -- init TXXXX "Ticket Title" --adr "ADR Title"`
3. Verify pack quality:
   - `dotnet run --project Tools/TicketKit -- verify TXXXX`

## Codex prompt bundle
Use this minimum bundle when starting a ticket:
- Ticket spec section in `Docs/TICKETS.md`
- `Docs/Tickets/TXXXX.context.md`
- Links from `## Canonical links (do not paste content)` in the context file

## Prompt template (short form)
```text
Implement TXXXX exactly as written.
Use:
- Docs/Tickets/TXXXX.context.md
- Docs/Tickets/TXXXX.closeout.md
Follow canonical links instead of pasting large docs.
Stay offline and keep scope to this ticket only.
```

## Closeout
1. Update `Docs/Tickets/TXXXX.closeout.md`.
2. Re-run verification:
   - `dotnet run --project Tools/TicketKit -- verify TXXXX --strict`
3. Add completion entry in `Docs/TICKETS.md` when acceptance criteria are met.
