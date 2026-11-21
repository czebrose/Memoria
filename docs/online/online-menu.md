# The Online menu (party control in game)

Who plays which party member is now changed from the game's own main menu. The old debug-style "Party control"
panel (top right, IMGUI) is gone; its Ctrl+Shift+F1-F12 shortcuts only work with `OnlineDebug = 1`.

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
| **Free, follows ...** (blue) | nobody plays it: the host's game drives it, and it joins the named player's battles, commanded from that player's PC |

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

## Notices on the field

A framed **ONLINE** notice at the top of the field screen shows:

- an open swap offer to you ("*name* offers a swap: answer in the menu, Online.") until it's answered or expires;
- "You now play *character*." / "You're watching (no character)." for a few seconds after your character changes.

The connection status box (Ctrl+Shift+O) hides while the Online menu has the cursor.

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
- **A later Inventory / Trade tab:** add a row in `OnlineMenu.BuildCommandRows` (the guest's ONLINE window; the host
  gets that window too as soon as it has more than one row) and a window for it, built like the action window.

### Test driver (one PC, debug only)

With `OnlineDebug = 1`, start `x64\FF9.exe` with `-memoria-online-menu-tour` (plus `-memoria-autoload` to load the
latest save). `MenuTour.cs` presses keys through the game's own input path (`UnityXInput.Input.InjectKeyPress`, so
the window can stay in the background) and saves screenshots to `%TEMP%\FF9OnlineMenuTour`:

- alone, it tours a pretend four-player session (`PreviewPartyControl`): every window and state, no network;
- with `-memoria-online-menu-tour-host` too (and without `-memoria-autoload`), it hosts a lobby alone at the title,
  starts the latest save and tours the real session (host views, the Follow list, a Take).

Nothing of this runs without the flags.

## Two-PC test list

Host on PC 1, a guest on PC 2, a party of at least three; both in the same field unless a step says otherwise.

1. **Entry:** outside a session the main menu has no Online entry. In a session it's above Config on both PCs; the
   seven / eight other entries still work (Item, Status, Order, Config...) and the transitions into them look right.
2. **Navigation:** keyboard and controller: up / down wrap through the submenu and every Online window, confirm and
   cancel sound like the rest of the menu, Select shows help on every row and character. Mouse clicks work too.
3. **Party view:** host and guest each see **You** on their own character, the other's name (host marked) on the
   other's, and **Free, follows ...** on the rest. Leaving the Online entry brings the magic stones back.
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
13. **No leftovers:** no IMGUI "Party control" panel anywhere; Ctrl+Shift+F1-F12 do nothing without `OnlineDebug`.
