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
| **Shop buy / sell / synthesis** | `ShopUI` (`FF9Item_Add`, `party.gil -=`), `ff9sell` | items, gil | only by talking to a shop (host-only from M5) | **Host only**; the host's changes are broadcast |
| **Chests, gil, items, key items from scripts** | field opcodes (`AddItem`, `AddGil`, ...) | items, gil, key items | only by interacting (host-only from M5) | **Host only** (M5); broadcast |
| **Party changes from scripts** | `PartySettingUI`, `EventEngine` party opcodes | who's in the party | story (host) | **Host only**; broadcast, may trigger control changes |
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
