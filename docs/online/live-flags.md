# Live flags on a shared field (Ice Caverns chests and ice)

Branch `online-live-flags` (from `online-step3-m2`, 056cdcd8), 2026-11-19.

## The report

Two PCs in the mod's Ice Cavern (the game's fields 300-311): chests opened and ice walls broken on one PC weren't
opened / broken on the other.

## Cause

Step 2 M5 (see [step2-m5-roaming.md](step2-m5-roaming.md), tier 2) sends event bytes one way only:
- a guest's changes go to the host every 250 ms (`MsgFlags`), and the host applies them to its live game;
- the host's own changes (and other guests' changes) reach a guest **only with its next meeting resync**, which happens
  once per meeting, not while both stay on one field.

A field also draws its chests (open or closed), ice walls, doors and so on **from the flags when it loads**; a flag
set later changes nothing on screen until the field is loaded again.

So with both players in one cave room:
- the host opens a chest → the guest's game doesn't know (its flag is still 0): the chest stays closed there, and **the
  guest can open it again** and get the item a second time (its find goes to the host as a new item);
- the guest opens a chest → the host's flag is set, but its field still shows the chest closed (opening it again
  does nothing, since the script checks the flag).

## Fix

- **Host → guests, live.** Every forwarded event byte (0-1 and 64+, as in M5) the host's own scripts change once it
  has acted on its field goes at once to every guest whose last position is on that field. A guest's changes the host
  applies are passed on the same way to the *other* guests on that guest's field. Same message (`MsgFlags`), same
  format. Log: host `Event bytes: N from <who> on field F sent live to #tag`.
- **The guest applies them live** (`EventWatch.ApplyFromHost`): only bytes that differ; they become the guest's baseline,
  so they aren't sent back as its own. Its own pending changes go out first. Log: `Event bytes: N applied live from
  #tag on field F`. This alone stops the second opening.
- **The guest redraws the field.** When live flags changed something on the guest's field, the guest asks for a
  keep-spot meeting resync once it has had 1.5 s without new flags and has control (not frozen by a host scene). That is
  M5's existing reload: one blink, the guest stays where it stood, the chest is open and the ice is gone. Log:
  `Meet: live flags changed field F; asking for the host's world to redraw it`. If the guest leaves the field first,
  nothing is asked: the next field loads with the new flags.
- **No reload when a guest arrives already in step.** When a guest walks onto the host's field, its meet request now
  carries a CRC32 of its shared event bytes (bytes 0-1 and 64+, `EventWatch.SharedDigest`; `MsgMeet` is 8 bytes then).
  If the host's own CRC matches, the host sends nothing and the guest keeps its field as loaded: no blink. Otherwise
  the host's world is sent as before. Party, items and gil come through PartySync, not this reload. The live-flags
  redraw request above carries no CRC (its flags already match; it asks for the reload to redraw). Logs: guest
  `Meet: ... unless it matches yours (flags XXXXXXXX ...)`; host `arrived on field F with the host's flags already
  (...); no reload` or `flags differ on field F (guest ..., host ...); sending the host's world`.

## Limits

- **The host never redraws.** A chest a guest opens is set in the host's game at once (a second opening does
  nothing), but the host's screen shows it closed until the host loads that field again. A host-side redraw would need
  a reload of its own field (no such path yet).
- Guests elsewhere get the change with their next meeting resync, as before. Two guests on one field get each other's
  changes through the host's relay, wherever the host is.
- The changes a field's exit scene makes are flushed when the field changes; the host relays them to guests on its
  *new* field, so a guest left behind gets them at its next meeting.
- Each relayed change on the guest's field costs one reload blink. If that's too much in practice, the redraw could
  wait for a quiet moment longer, or be done for chest / ice bytes only.
- A host's change that a guest's field script polls can make the guest's own copy of that script react (e.g. start
  its own copy of a scene). Not seen; logged as `applied live`.

## Test (two PCs, the mod's save; Ice Cavern via the bridge pilot's third choice)

1. Both PCs in the same cave room with a chest. **Host** opens it. Guest: within a second `Event bytes: ... applied
   live`, then about 1.5 s later one blink, and the chest is open on the guest's screen. The guest tries the chest:
   nothing (no second item).
2. Same with an ice wall (the host breaks it): it's gone on the guest after the blink.
3. **Guest** opens a chest the host can see: on the host, the flag is applied (`applied from #tag`); a second guest, if
   any, gets it live. The host's own screen redraws only at its next field load (known limit).
4. Guest walks to another room right after the host opens a chest: no reload request; the guest's next room is fine.
5. **Arrival skip:** guest walks into the host's room the normal way (no chests opened elsewhere): host log says
   `no reload`, and the guest's screen doesn't blink. Repeat on a few rooms; any `flags differ` line is worth a look
   (which bytes differ shows in the `Event bytes` lines above it).
6. Send both `Memoria.log` files.
