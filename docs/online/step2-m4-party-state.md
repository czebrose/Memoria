# Step 2 / M4: host-authoritative party state — hook points and proposal

**Status: proposal reviewed, decisions below (2026-11-17); building.**

Brief (M4): guests change only their own character (equipment, abilities); shared inventory and gil are the host's to
arbitrate (two players using the last Potion: first request wins, the second is rejected and the guest's menu shows
the right count); cover equip/unequip, menu item use, shop buy/sell, ability equip/learn, gil; only the host saves;
the M1 blob as a full-resync fallback. Also planned here (decided 2026-11-16): in-game control changes (Take / Swap /
Release / Follow the host).

## 1. Where party state changes (hook points)

"Guest can reach it" assumes M5's rule (guests can walk but not interact: no NPCs, chests, shops, Moogles, save
points). M5 isn't built yet, so until then a guest *can* interact; the protocol below rejects what it can't explain.

| Area | Code | What changes | Guest can reach it | Proposal |
|---|---|---|---|---|
| **Equip / unequip** | `EquipUI` (`OnKeyConfirmInventoryGroupButton`, `OnKeyConfirmEquipmentGroupButton`) | a character's equipment, item counts (old item back, new item out), moonstones | yes (main menu) | **Cover** (own character only) |
| **Optimize / Remove all** | `EquipUI.OnKeyConfirmSubMenuGroupButton` | same, many items at once | yes | **Cover** (own character only) |
| **Support abilities** | `AbilityUI` (`ff9abil.FF9Abil_SetEnableSA`, gem boosts) | a character's equipped support abilities | yes | **Cover** (own character only) |
| **Magic in the menu** | `AbilityUI` (cast: Cure, Life...) | caster's MP, target's HP/status | yes | **Cover** (caster = own character; any target) |
| **Item use** | `ItemUI` (`FF9Item_Remove` + effect) | item count, target's HP/MP/status | yes | **Cover** (any target; the item is the contested resource) |
| **Item arrange / sort** | `ItemUI` arrange | the order of the shared item list | yes | **Block on guests** (the host's order wins anyway) |
| **Order / rows** | `MainMenuUI` Order (`party.member` swap, row) | party order, rows | yes | **Order: block on guests** (the host's; position 1 is the host's character). **Row: own character only** (a property of the character, like equipment) |
| **Card discard** | `CardUI.OnDiscardDialogKeyConfirm` | shared card collection | yes | **Block on guests** |
| **Status / Config** | `MainMenuUI` | nothing shared (Config is per-PC) | yes | nothing to do |
| **Shop buy / sell / synthesis** | `ShopUI` (`FF9Item_Add`, `party.gil -=`), `ff9sell` | items, gil | by talking to a shop | **Anyone**; a guest's change is a shop request (see "Guest shopping") |
| **Chests, gil, items, key items from scripts** | field opcodes (`AddItem`, `AddGil`, ...) | items, gil, key items | only by interacting (host-only from M5) | **Host only** (M5); broadcast |
| **Party changes from scripts** | `EventEngine` party opcodes (`AddParty`, `RemoveParty`) | who's in the party | story (host) | **Host only**; broadcast, may trigger control changes |
| **Party screen** | `PartySettingUI` (a moogle's party menu, a script's `Party()`) | who's in the party | the screen offers only who the story allows | **Anyone**, with guards; a guest's change is a party-screen request (see "Guest party changes") |
| **Rename** | `NameSettingUI` (from scripts) | a character's name | story (host) | **Host only**; broadcast |
| **Moogle: save, tent, Mognet** | `EventService.OpenSaveMenu`, Moogle menu | save file; tent heals the party | interaction (host-only from M5) | **Save blocked on guests** (below); tent host-only |
| **Save menu** | `SaveLoadUI` with `Type = Save` (from `EventService.OpenSaveMenu` and `FieldHUD.OnSaveClickSwitchScene`) | the save file | Moogles, save points, Memoria's field menu | **Blocked on guests** with "Only the host can save" |
| **Card games (Tetra Master)** | `QuadMist`, `CardPool` | card collection, items | interaction | **Host only** |
| **Chocograph / Chocobo** | `ChocographUI`, minigames | items, gil | interaction | **Host only** |
| **Battle results** | `BattleResultUI`, `btl_sys` (EXP, AP, gil, items, learned abilities) | everything | battles are host-only | **M6** (full update after the battle) |
| **Memoria cheats / boosters** | `BoosterSlider` (items to max, gil, etc.) | items, gil, levels | yes (hotkeys) | **Block on guests** (their changes would be rejected anyway) |

## 2. How state flows (proposal)

**Generic, by state difference, not per menu.** Re-implementing each menu's logic on the host (equip rules,
moonstones, gem boosts, item effects) would duplicate a lot of engine code. Instead:

- **Party state** = every character (the save's character records: equipment, support abilities, HP/MP/status,
  level...), the item counts, gil, key items, cards, party order and rows. Serialized with the game's own save code
  (`JsonParser`, as in M1), so nothing is missed.
- **Guest:** the menus work as normal, locally (*optimistic*). Every frame the guest compares its party state with
  what it expects (the host's last update + its own pending requests). A difference becomes a **Request** (sequence
  number + the differences: changed character records, item count deltas, gil delta).
- **Host:** applies requests in order of arrival, after checking them:
  - character records: **only the guest's own character**, except HP/MP/status, which any item or spell can change;
  - item counts can go down only if the host still has them (**the last Potion**: the first request takes it, the
    second is rejected); they can go up only by unequipping (equipment going back to the bag);
  - gil can't go up from a guest;
  - anything else (party order, cards, other characters' equipment) is rejected.
- **Host → everyone:** a **StateUpdate** after every change (the host's own menu use included): the full party state,
  with the result of each request (accepted / rejected). Party state is a few KB, sent on the reliable channel in
  chunks like M1.
- **Guest on a StateUpdate:** takes the host's state, then re-applies its own requests the host hasn't answered yet.
  A rejected request disappears, so the guest's numbers become the host's (**its menu shows the right count**). The
  open menu is refreshed; a screen that can't be refreshed safely gets the update when it closes (to check per menu
  when building).
- **Resync:** every StateUpdate carries the whole party state, plus a periodic hash every few seconds, so a mismatch
  heals by itself. The M1 blob stays as the full fallback (debug key on the host; after battles in M6).

## 3. Saving

Guests can't save: the save screen (`SaveLoadUI`, Save mode) shows "Only the host can save" instead of opening, from
every path (Moogles, save points, Memoria's field menu). The autosave block from M1 stays.

## 4. Control changes (Take / Swap / Release / Follow the host)

Host-arbitrated, as decided:

| Action | Effect |
|---|---|
| **Take** | a guest takes a character nobody plays (one the host was driving) |
| **Swap** | two players exchange characters (the other one accepts) |
| **Release** | a guest gives their character back to the host; with no character left, they Follow |
| **Follow** | release everything and spectate (follow the host's camera, no body) |

Requests go to the host, which updates the assignments and broadcasts them (already lobby data from M2). A change of
character updates the guest's body (M3) and which character's records they may edit (above). UI: an in-game debug
panel first (like the lobby panel), a real entry in FF9's menu later. If the story removes a guest's character: the
first free character, else Follow.

## 5. Tests (from the brief, plus control changes)

| # | Setup | Pass |
|---|---|---|
| S4 | guest equips gear on their character; host uses a Potion; both try to use the last of an item | both show the same party and inventory; the race resolves (one wins, the other's count corrects) |
| S7 | host saves at a Moogle; guest tries to save | host's file saved; guest gets "Only the host can save", guest's files untouched |
| S4b | guest tries to equip the host's character, change the order, discard a card | blocked or rejected, no change on either side |
| C1 | guest Takes a free character, Swaps with the host, Releases, Follows | assignments, bodies and editing rights follow each change on both PCs |

## 6. Decisions (2026-11-17)

1. **Items and spells on other characters: yes.** A guest can use an item from the shared bag, or cast a spell, on any
   party member (e.g. a Potion on the host's Clove): HP/MP/status of any character may change, the item or the
   caster's MP being the cost. Equipment and abilities stay own-character only.
2. **Order: host only. Row: each player sets their own character's row.**
3. **Item arrange and card discard: blocked on guests** (one shared bag order, decided by the host; card discards
   are permanent and cards are only used by the host's card games).
4. **Control changes are part of M4**, built after the state sync and the save block.

## 7. Two-PC test of the sync (2026-11-18)

Build `35cdd0d6` (host Clove, guest B.Waltz). An earlier run (same day) found two bugs, fixed in that commit: a guest's
request crashed while being packed (copying a non-object JSON value returned null), and the guest lost every menu but
Equip after resyncs (a reload while a menu was open kept its menu restrictions; resyncs now wait for the field and a
load clears them).

| # | Test | Result |
|---|---|---|
| S4 | guest equips B.Waltz, then swaps back | both requests applied by the host (`equip` + the two item counts), shown on the host |
| S4 | host uses a Potion | guest's count and HP updated |
| S4 | guest uses a Potion on Vayle (another player's character) | applied (`cur` + item -1): decision 1 works |
| S4 | last Potion used on both PCs | no conflict happened (the two uses never landed in the same moment); the "rejected" path is untested |
| S4b | guest equips Clove, changes the order, arranges items | blocked with a toast (seen on screen; blocks aren't logged) |
| S7 | guest saves / host saves | guest blocked, host saved |
| R | host resyncs (Ctrl+Shift+Y) three times in 5 s | each snapshot loaded and matched the host exactly; menus intact |

Logs: no errors on the host. On the guest, one `NullReferenceException` in `GhostManager.TrySpawn` during a resync: a
ghost state arrived after the snapshot was parsed but before the field's object list existed. Harmless (the ghost
spawned on the next state); since fixed, ghosts wait for `EventEngine.HasObjectList`.

Round trip guest → host → guest: under 10 ms on a direct connection.

## 8. Control changes, as built (2026-11-18)

Code: `Session/SessionLobby.Control.cs` (+ hooks below). Once the session plays, the lobby's picks are frozen; characters
change hands only through these actions, all arbitrated by the host (first come, first served):

| Action | Who | Effect |
|---|---|---|
| **Take** | anyone | a party member nobody plays becomes yours (yours, if any, goes back to the host's game). The host may Take too: it changes which character the host plays. |
| **Swap** | a player with a character | offers the player of another character an exchange; they **Accept** or **Decline** (offers expire after 30 s). A Swap on a free character is a Take. |
| **Release** | a guest | gives the character back to the host's game; the guest **watches** (this is Follow: with one character per guest, Release and Follow are the same state). |

- The host always plays one character. **The lead** (the character the field scripts make the player) is now the
  party's leader, not "the host's character": if the host plays B.Waltz, the field's Clove entry shows B.Waltz on the
  host's PC and Clove on the PC of whoever plays Clove.
- **Story removes a played character from the party** → that player gets the first free party member, else (a guest)
  watches. Characters assigned in the lobby but not in the party yet (a New Game's party is built by the first field)
  are kept until they have been in the party once.
- **A guest leaves the lobby** → their character is free again (M7's "the host takes over").
- **Watching** (`GuestGuard.Spectating`): the guest's player object is hidden on its PC in Player mode (the cast still
  shows in Cast mode), it can't move (`FieldMapActorController` input) or interact (`EventEngine.CollisionRequest`),
  and it sends no ghost. Following the host's camera comes with M5's tether. The interaction block is the one M5 needs
  for "guests walk but don't interact".
- **Data:** lobby data `assign` (guests' characters, as in the lobby), `hostplays`, `offers`
  (`fromAccount:toAccount|...`); member data `ctl` = `seq:verb:arg`, a guest's latest request; the host acts on each
  new value once.
- **UI** (replaced on 2026-11-21 by the main menu's Online entry, [online-menu.md](online-menu.md); the F-keys below
  are debug-only now): a "Party control" panel at the top right while playing (shown with the status box, Ctrl+Shift+O): the party,
  who plays whom, a Take / Swap / Release button per slot, and an incoming offer with Accept / Decline. Keys:
  Ctrl+Shift+F1-F4 act on party slot 1-4, F5 accepts, F6 declines (the lobby's F-keys only work at the title).
  A toast says "You now play X" / "You're watching" on each change.
- PartySync's editing rights follow the assignments at once (they're read per request).

### C1 test steps (two PCs; host = Clove, guest = B.Waltz from the lobby)

1. Both: in game, status box on. Panel shows: Clove you/host, B.Waltz guest, Duskly/Vayle free.
2. **Guest Take:** guest presses Ctrl+Shift+F for Duskly's slot (not Vayle: no field body yet, she'd show as Clove). Guest becomes Duskly at once; host sees Duskly's
   ghost (not B.Waltz). Guest equips Duskly (allowed), tries B.Waltz (blocked).
3. **Guest offers a Swap to the host:** guest presses Clove's slot key. Host's panel shows the offer; host presses
   Ctrl+Shift+F5. Host now plays Duskly (the host's player body becomes Duskly), guest plays Clove. Both ghosts match.
4. **Host offers, guest Declines:** host presses Clove's slot; guest presses Ctrl+Shift+F6. Nothing changes.
5. **Guest Release:** guest presses their own slot. Guest's body disappears, can't move or talk; host's ghost of the
   guest disappears. Panel: "you play nobody (watching)".
6. **Guest Takes again** a free slot: body back, moves again.
7. Host Takes a free character: host's body switches. Logs: `Control:` lines on both PCs, no exceptions.

### C1 test results (2026-11-18)

Run on two PCs. The guest started out watching (no character picked in the lobby). It then took Duskly (1), took
character 10 while playing Duskly, released, took again, offered swaps with the host's Clove (accepted both ways), and
took and released B.Waltz (11). Every request came back as `Control: this PC plays ...` within about 1 s. On swaps,
the ghost on each PC was rebuilt with the new model. Tester: a success overall.

One bug, fixed after the test. Character 10 (vanilla slot "Blank") has no line in `CharacterBodies.txt`, so the
guest's player entry kept Clove's body as a stand-in (documented). That stand-in counted as "Clove is shown here", so
the host's Clove ghost was hidden on the guest. Now the player entry counts as the acting character. A stand-in body
no longer hides the lead's ghost, and you see two Cloves until the character gets a body. The ghost log now says
"its character is already shown here" for this case, instead of "its player is in field N".
Not covered: the host's own Take (step 7) and the offer Decline (step 4). Neither shows in the guest log.

## Guest shopping

Guests can buy, sell and synthesize in shops; the gil and items are the party's (the host's game).

- Guest: a party change made while its shop screen is open (`UIManager.UIState.Shop`) and carrying gil or items is
  sent with `"shop": 1` (`PartySync.GuestUpdate`). The shop screen itself works as before (optimistic).
- Host (`PartySync.Validate`): a shop request may change gil and gain items. It's rejected if the party's gil
  (the host's, now) can't cover it ("not enough gil"), if an item would go below 0, or if selling brings in more gil
  than the removed items' `selling_price` adds up to. Applied gil is clamped to 0..9,999,999.
- Anything else in the request (other characters' equipment, "other" sections) follows the usual rules.
- Both players shopping at once: requests are applied in order of arrival; the second one is rejected if the gil ran
  out, and the guest's numbers go back to the host's.
- Log: guest `Party: request N sent to the host (item 236 +2, gil -400, in a shop)`; host `... applied` or
  `... rejected: not enough gil (the party has 300, 400 needed)`.

Two-PC test: guest talks to a shop and buys an item (both PCs show the item and the lower gil), sells one (gil
goes up on both), synthesizes one if the shop can; the host spends the gil down first, then the guest tries to buy
more than is left (rejected toast, guest's gil and items back to the host's).

## Guest party changes

Guests can change who's in the party with the game's party screen (`PartySettingUI`): a moogle's party menu, or a
field script's `Party()`. It's the one exception to "only the host can change" the party list. Story changes
(`AddParty` / `RemoveParty`) and the party order stay the host's.

The screen itself carries the story's rules. It offers only characters the story has made available
(`info.party`), and keeps the ones the script locks (the Black Waltz moogles lock Clove, the leader). The host adds
the multiplayer rules.

- Guest (`PartySync.GuestUpdate`): a change to the party list (`slot`) alone, made with the screen open or within 5 s
  of its confirm (`PartySettingUI.OnKeyCancel` calls `PartySync.OnPartyScreenConfirmed` just before it writes the
  party), is sent with `"partyScreen": 1` and the new `slot` list. Changes made by this PC's copy of another player's
  party scene still aren't sent (`GuestGuard.HoldLiveFlags`).
- Host (`PartySync.CheckPartyScreen`) rejects it if:
  - the host's game is in a shared scene, a battle, loading, or a busy menu (its own party screen, a shop...): "try
    again in a moment";
  - a character in it isn't available (`info.party` 0), or appears twice, or the party would be empty;
  - the party's first member changes (it leads on the field for everyone);
  - it removes a character another player plays (the host's, or another guest's). The guest's own character may go:
    the usual rules for a played character leaving the party apply.
- Applying a party state that changes who's in the party also updates the field's party entries
  (`EventEngine.SetupPartyUID`, what a script's `UpdatePartyUID()` does after `Party()`), on every PC it reaches. This
  covers host story changes reaching guests too, which used to wait for the next field load.
- Log: guest `Party: request N sent to the host (party screen: [0,10,11,255], other: slot)`; host `... applied` and
  `Party: the party is now 0 10 11 - (was 0 10 2 -)`, or `... rejected: Vivi is another player's character`.

Two-PC test: on the Cargo Ship (field 451), the guest talks to the moogle and swaps a free character in. Both PCs
show the new party in the menu, the battle party matches, and the swapped-in character's field entry follows. Then the
guest tries to remove the host's character (rejected toast, the party goes back).
