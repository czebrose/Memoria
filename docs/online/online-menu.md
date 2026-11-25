# The Online menus (title-screen lobby, party control in game)

Online play is set up and run from game-style menus:

- **The title screen** has an **Online** button (above the language button, "US"). It hosts or joins a lobby
  and shows the lobby: see [Title screen: hosting and joining](#title-screen-hosting-and-joining).
- **In game**, who plays which party member is changed from the main menu's **Online** entry (the rest of this page).

The old debug-style panels (the title-screen lobby panel and the in-game "Party control" panel, both IMGUI) are gone.
Their F-key shortcuts (title: Ctrl+Shift+F1-F6; in game: Ctrl+Shift+F1-F12) only work with `OnlineDebug = 1`.
Ctrl+Shift+H (host), I (invite), J (join), L (leave) and O (status box) still work for everyone.

## Title screen: hosting and joining

Choose **Online** on the title screen: with the keyboard or a controller, Left from CLOUD DATA, or Up from "US".
It opens a window; Cancel closes it and keeps you in the lobby you're in (Online opens it again). The title's idle
slideshow waits while a window is open.

**Not in a lobby: ONLINE**
- **Host a game**: creates a friends-only Steam lobby (up to 4 players) and shows it.
- **Join a friend**: lists your Steam friends who are in an FF9 lobby; choose one to join. **Refresh** looks again.
  Accepting a Steam invite (from the Steam overlay or the friends list) joins too.

While a lobby is being created or joined the window says so, with **Cancel**.

**In a lobby: LOBBY**
The top of the window lists the players (host, connecting, already in game), the game, and what's missing
before the host can start. Then:

| Who | Rows |
|---|---|
| host | **Game: ...**: New Game and every save slot that has saves (the load screen's 10 slots, and Memoria's extra save pages, as slots 11-20 and up); a slot opens its saves, newest first, 8 at a time (**More**). The newest save is chosen at first; * marks the chosen game's slot. The party shown comes from that game |
| guest | one row per party member, with who plays it: choose a free one to pick it, choose yours to give it back. The first is always the host's |
| everyone | **Invite friends**: the Steam overlay's invite dialog (if the overlay doesn't open, friends can use Join a friend) |
| host | **Start**: greyed until everyone is at the title screen and connected; the reason is in the window |
| everyone | **Leave lobby** |

The host's window also lists the party with who picked what. When the host starts, the guests' windows say
"receiving the host's game" and everyone goes into the game. The window opens by itself when you enter a lobby some
other way (a Steam invite, Ctrl+Shift+H / J). A lobby whose game has already started says so: joining a game in
progress isn't possible yet.

## In game: the Online entry

The rules haven't changed: every action is still a request to the host, as described in
[step2-m4-party-state.md](step2-m4-party-state.md) (section 8) and [step3-m2-results.md](step3-m2-results.md) (Follow).

## Opening it

1. In a field, open the main menu (the Menu button).
2. While an online session is playing, the submenu has an **Online** entry above **Config**. Outside a session the
   entry isn't there and the menu looks as it always did.
3. Choose **Online**.
   - **Host:** the cursor goes straight onto the party (unless a swap offer to the host is open: then the window
     below opens first).
   - **Guest:** a small **ONLINE** window opens first, with:
     - **Party**: goes to the party.
     - **Accept swap** / **Decline swap**: only while someone's swap offer to you is open. The window says who offers
       what.
     - **Join host**: go to the host's field and world (greyed while you're already there).

Cancel steps back one window at a time, then closes the menu as usual. The hand cursor, the confirm / cancel /
buzzer sounds, the help window (Select) and keyboard / controller navigation are the game's own.

## The party view

While the Online entry is in use, each character panel shows who plays that character, in place of the magic
stones:

| Shown | Meaning |
|---|---|
| **You** (yellow) | you play this character |
| a player's name, "(host)" for the host | that player plays it |
| **Follows ...** (blue) | nobody plays it: the host's game drives it, and it joins the named player's battles, commanded from that player's PC |

The help window (Select) on a character says the same in a sentence and mentions an open swap offer.

## A character's commands

Choose a character to open its window (its name is the caption, with one line about who plays it):

| Character | Commands |
|---|---|
| yours (guest) | **Release**: give it back to the host's game and watch |
| yours (host) | **Release**, greyed: the host always plays a character |
| nobody's | **Take**: play it (your old character goes back to the host's game); **Follow**: a list of the players, choose who it follows (the current one is greyed) |
| another player's | **Swap**: offer them your character for theirs. Greyed while you're watching, or as **Swap offered** while your offer waits (offers expire after 30 s) |
| another player's, who offered you a swap | **Accept swap** / **Decline swap** |

A greyed command plays the buzzer and does nothing. After a command the cursor goes back to the party; the labels
change when the host's answer arrives (usually within a second), and every open window redraws if anything changes
meanwhile (another player takes a character, an offer expires, a player leaves).

## In battle: Select

In an online session, **Select** (its keyboard key otherwise) opens a **PARTY** window over the battle. It doesn't
pause: ATB keeps running, as with any battle menu. Each row is a party member with who plays it; choose one for the
same commands as the main menu (Take, Follow, Swap, Accept / Decline, Release), and Follow for the list of players.
Select or Cancel closes it; a turn of yours coming up takes the cursor and closes it. Outside a session Select does
what it always did. (In an online session that means its help toggle isn't available in battle.)

In a shared battle a change moves the character **at its next turn**: at once, unless its turn is open (its command
menu is up on some PC, or it waits in the battle owner's own ready queue); then once that turn is done. Only players
in that battle can command its characters: a character taken by a player who isn't in the battle stays with the
battle's owner until the next battle. A player who never acts on an open turn holds the handover.

How it works: the battle's owner checks the session's control state four times a second
(`BattleControl.OwnerSync`), moves units whose controller changed, and sends every participant the new map
(`MsgControlUpdate`, 40). The next TurnReady for that unit goes to its new PC, which opens its menu as usual; a
TurnReady that arrives before the map is kept until it does.

## Notices on the field

A framed **ONLINE** notice at the top of the field screen shows:

- an open swap offer to you ("*name* offers a swap: answer in the menu, Online.") until it's answered or expires;
- "You now play *character*." / "You're watching (no character)." for a few seconds after your character changes.

The debug overlay (Ctrl+Shift+O, off at start) is every box drawn outside the game's own windows: the connection
status box, the centred notices and toasts (a scene's owner advancing the text, battle joins, blocked actions), and
the battle sync box at the bottom left. The status box also hides while the Online menu has the cursor.

## For developers

- `Assembly-CSharp/Memoria/Online/Menu/OnlineMenu.cs`: the entry, the windows and the owner labels. Everything is
  copied from the main menu's own widgets (Card's submenu row, the Time & Gil window frame, the Location label), so
  it uses the game's atlas, fonts and cursor. Cursor groups: `Online.Command`, `Online.Action`, `Online.Player`.
- `MainMenuUI.Online.cs` (partial of `MainMenuUI`) and the `[Online]` lines in `Global/MainMenuUI.cs`: the hooks
  (Awake, Show, Hide, Update, confirm and cancel). The submenu tween's clip shifts now come from where the entries
  are, so an extra entry doesn't throw them off.
- `IPartyControl.cs`: what the menu reads and does; `SessionLobby.Menu.cs` implements it with the existing requests
  (`take`, `swap`, `release`, `follow`, `accept` / `decline`, Join host).
- `OnlineNotice.cs`: the field notice.
- `MenuWindows.cs`: the window builder both menus use (frame, info text, rows, cursor group, mouse clicks, window
  colour). Copies drop anchors that point outside themselves and get the menu's click handler again (Unity copies
  neither delegates nor outside references).
- `BattleOnline.cs` with `BattleHUD.Online.cs` and two `[Online]` lines in `BattleHUD.Scene.cs`: the battle's
  Online window (Select). `PartyCommands.cs`: the per-character commands both in-game menus show. The mid-battle
  handover is in `Battle/BattleControl.cs` (`OwnerSync`, `OnControlUpdate`) and `BattleEntry.UpdateCurrent`.
- `TitleOnline.cs` with `TitleUI.Online.cs` and the `[Online]` lines in `Global/TitleUI.cs`: the title's Online
  button (a copy of the language button) and its ONLINE / LOBBY / GAME / JOIN A FRIEND windows (cursor groups
  `Online.Title`, `Online.TitleList`). `SessionLobby.Title.cs` exposes the lobby to it; `SteamLobby.FriendLobbies`
  lists friends in FF9 lobbies.
- **A later Inventory / Trade tab:** add a row in `OnlineMenu.BuildCommandRows` (the guest's ONLINE window; the host
  gets that window too as soon as it has more than one row) and a window for it, built like the action window.

### Test driver (one PC, debug only)

With `OnlineDebug = 1`, start `x64\FF9.exe` with `-memoria-online-menu-tour` (plus `-memoria-autoload` to load the
latest save). `MenuTour.cs` presses keys through the game's own input path (`UnityXInput.Input.InjectKeyPress`, so
the window can stay in the background) and saves screenshots to `%TEMP%\FF9OnlineMenuTour`:

- alone, it tours a pretend four-player session (`PreviewPartyControl`): every window and state, no network;
- with `-memoria-online-menu-tour-host` too (and without `-memoria-autoload`), it hosts a lobby alone at the title,
  starts the latest save and tours the real session (host views, the Follow list, a Take);
- with `-memoria-online-menu-tour-title` too (without `-memoria-autoload`), it tours the title screen: keyboard to
  the Online button, Host a game (alone), the GAME list, Leave lobby, Join a friend;
- with `-memoria-online-menu-tour-battle` too and `-memoria-battle 1 -memoria-party 0,1,2,3` (instead of
  `-memoria-autoload`), it opens the battle's Online window over a test battle, with the pretend session.

Nothing of this runs without the flags.

## Two-PC test list

Host on PC 1, a guest on PC 2, a party of at least three; both in the same field unless a step says otherwise.

**Title screen (first):**

- T1. **Button:** both PCs show Online above "US"; keyboard and controller reach it (Left from CLOUD DATA, Up from
  US) and a mouse click opens it. The idle slideshow doesn't start while a window is open.
- T2. **Host:** PC 1 chooses Host a game: the LOBBY window lists PC 1 as host, the latest save as the game and its
  party, all free but the host's character.
- T3. **Join a friend:** PC 2 chooses Join a friend: PC 1's name is in the list; choosing it joins. Both windows now
  list two players (PC 2 "connecting..." for a few seconds).
- T4. **Invite:** PC 1's Invite friends opens the Steam overlay's invite dialog; accepting the invite on PC 2 (after
  leaving) joins and opens PC 2's LOBBY window by itself.
- T5. **Pick:** PC 2 picks a free character (its row and PC 1's party line show PC 2's name), picks another, gives it
  back. The host's character can't be picked.
- T6. **Game:** PC 1 changes the game (New Game, a save); PC 2's window follows (game and party), PC 2's pick of a
  character that isn't in the new party is dropped. With saves in several slots: every slot is listed, a save from
  a slot other than the latest one starts that save.
- T7. **Start:** greyed while PC 2 is connecting or not at the title (the reason shows); Start puts both in the game,
  PC 2 playing its pick.
- T8. **Leave / cancel:** Cancel closes the window but keeps the lobby (Online reopens it); Leave lobby on PC 2 drops
  it from PC 1's list. (Known: if the host leaves first, Steam makes PC 2 the lobby's owner with no game chosen;
  PC 2 should leave too. There's no host-leave handling yet.)

**In game:**

1. **Entry:** outside a session the main menu has no Online entry. In a session it's above Config on both PCs; the
   seven / eight other entries still work (Item, Status, Order, Config...) and the transitions into them look right.
2. **Navigation:** keyboard and controller: up / down wrap through the submenu and every Online window, confirm and
   cancel sound like the rest of the menu, Select shows help on every row and character. Mouse clicks work too.
3. **Party view:** host and guest each see **You** on their own character, the other's name (host marked) on the
   other's, and **Follows ...** (blue) on the rest. Leaving the Online entry brings the magic stones back.
4. **Take (guest):** guest takes a free character. Both PCs' labels update; the guest's field body changes; the
   notice says "You now play ...". The guest's old character shows as free.
5. **Take (host):** the host takes a free character; the host's old one becomes free.
6. **Release (guest):** guest releases; guest watches (no body), labels show the character as free.
   The host's own character shows Release greyed.
7. **Swap offer, accept:** guest offers a swap on the host's character. The guest sees **Swap offered** (greyed).
   The host gets the field notice; in the menu the host's window shows Accept / Decline on the guest's character.
   Accept: both labels and bodies swap; the notice goes away.
8. **Swap offer, decline / expire:** decline: nothing changes, the guest can offer again. Let one expire (30 s): the
   notice and the Accept / Decline rows disappear by themselves, also with the menu open.
9. **Follow:** set a free character to follow the guest (Follow → guest). Its label says "follows *guest*" on both
   PCs; in the next battle it joins the guest's battle and is commanded from the guest's PC. Set it back to the host.
10. **Join host (guest):** with the guest on another field, Online → Join host brings the guest to the host; on the
    same field the row is greyed.
11. **Live redraw:** with a character's window open on one PC, change that character from the other PC: the window
    redraws (or goes back to the party if the command no longer applies), no stuck cursor.
12. **Leaving:** the guest leaves the lobby while the host has the Online menu open: the guest's character shows as
    free. Ending the session with the menu open on a guest closes the Online windows and removes the entry next time.
**In battle (a shared battle, both PCs in it, Active ATB):**

- B1. **Window:** Select opens PARTY on both PCs without pausing; rows show who plays each character; Select / Cancel
  close it and give the cursor back to the battle menu if one was open. A turn coming up closes it.
- B2. **Take mid-battle:** the guest takes a free character while its ATB fills: its next turn opens on the guest's
  PC (not the host's); the host's PC logs "changes hands mid-battle".
- B3. **Open turn:** with a character's command menu open on the host, the guest takes it: the host finishes that
  command; the turn after opens on the guest's PC. No turn lost or doubled.
- B4. **Swap mid-battle:** accept a swap: both characters' next turns open on their new PCs.
- B5. **Follow / Release mid-battle:** a free character set to follow the guest moves to the guest's PC at its next
  turn; the guest releasing moves its character to the host's.
- B6. **Not in the battle:** a player outside the battle taking one of its characters changes nothing in it; after the
  battle the character is theirs.

13. **No leftovers:** no IMGUI lobby or "Party control" panel anywhere; the F-key shortcuts do nothing without
    `OnlineDebug`.
