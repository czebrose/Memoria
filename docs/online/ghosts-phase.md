# Ghosts phase: spike A + spike B wired together — results

Branch `online-ghosts` (from `main` at `c716e1fa`, merging `steam-spike-a` and `spike-b-ghosts`). Code:
`Assembly-CSharp/Memoria/Online/`. Rules for contributors: [CONTRIBUTING.md](CONTRIBUTING.md).

**Status: first two-PC session done (2026-11-16): T1 quality, T2, T3, T5 pass; T4 dropped; a 5-minute T1 and the rest of T6 still to run.** Results are in the test table below.

## What changed (from the two spikes)

- **Merge:** both branches merged with no conflicts (B's engine edits were anchored away from A's).
- **One driver:** `OnlineSession` (attached in `SceneDirector.Awake`, marked `[Online]`) owns the lobby, the
  transport and the ghosts and is ticked once per frame. The spike drivers `SteamSpike` and `GhostSpike` are gone;
  the shadow ghost survives as `Ghosts/ShadowGhost.cs`, a debug mode.
- **Wiring:**
  - *Send:* the local field leader is sampled at **15 Hz** (`OnlineSession.SampleLocal`, as `GhostSpike.Sample` did)
    into one reused 23-byte buffer and sent to every peer on channel 1, unreliable. Outside a field (battle, menu,
    world map) the state says `fieldId = 0xFFFF` (`GhostState.NotOnField`) so the other side hides the ghost at once
    instead of after 5 s of silence.
  - *Receive:* every channel-1 message goes to `GhostManager.OnPacket`; unknown message types are counted
    (`bad packets` in the summaries) and ignored.
  - *Peer left / leaving the lobby:* `PeerLeft` → `GhostManager.Remove`; leaving the lobby makes every member leave,
    so all ghosts go and sending stops (no peers).
  - *Model:* the sender's field leader (`Actor.model`); if their leader changes, the ghost is respawned with the new
    model. A model not in `GhostModels` (the sequel's Clove) is drawn with the sender's own animation ids, which it
    can play exactly since it's the same model. No per-player character choice (deferred to the party roster work).
  - *Lobby owner changes:* logged ("no host-leave policy in this phase").
- **Adaptive interpolation delay** (`GhostActor.UpdateTiming`): per ghost, the clock offset is the minimum
  `localNow - senderTick` over the last ~10 s (so clocks needn't agree and slow drift is followed), jitter is the 95th
  percentile of how much later than that the states arrive, and the delay is **one send interval (67 ms) + jitter,
  clamped to 80-250 ms**. Logged every second and in the summaries (delay, jitter, max jitter).
- **Loss measurement:** the movement channel carries ghost states only now (spike A's dummy 48-byte packets are gone),
  so every 5 s each side tells the other on channel 0 how many states it sent them; loss = 1 - received / reported.
- **Status box** (top-left, Ctrl+Shift+O): lobby, host, peers with RTT / relay or direct / loss, ghosts with
  delay / jitter.
- **Debug flag:** `Memoria.ini` `[Debug] Enabled = 1` + `OnlineDebug = 1` enables the shadow ghost and its tools
  (Ctrl+Shift+G / P / K / M / N / D). Off by default.
- **Logs:** prefix `[Online]` (and `[Ghosts]` for ghost lifecycle); players appear as `#1234` (last 4 digits of the
  account number), never by Steam name; the friend scan no longer logs every friend. `SUMMARY` every 30 s, `LEFT`,
  and `FINAL` once on quit (the spike logged it twice).
- **Docs:** this file, [CONTRIBUTING.md](CONTRIBUTING.md), and both spike findings moved to `docs/online/`. Rule
  comments at the two crash-prone spots (`SteamNative` bindings, the callback pump in `OnlineSession.Update`).

## Hotkeys (Ctrl+Shift + key)

| Key | What |
|---|---|
| H | host a lobby (friends-only, 4 players; its ID goes to the clipboard) |
| I | Steam overlay invite dialog |
| J | join: the lobby ID in the clipboard, else a Steam friend's FF9 lobby |
| L | leave the lobby |
| O | status box on/off |
| G P K M N D | debug only (`OnlineDebug`): shadow ghost, pause its feed 6 s, network simulation, ghost model override, interpolation on/off, adaptive/fixed delay |

## Tests

### Single PC (first)

1. In the game's `Memoria.ini`, under `[Debug]`, set `Enabled = 1` and add `OnlineDebug = 1`.
2. Start the game through Steam, load a save in a walkable field.
3. Ctrl+Shift+G: the shadow ghost (your own character, ~1 s behind) walks as in spike B. Status box shows
   `Ghosts: 1 shown`, delay ~80 ms.
4. Ctrl+Shift+K (network simulation): delay rises to ~150 ms (jitter ~80 ms), still smooth.
5. Ctrl+Shift+H: lobby created (status box). Ctrl+Shift+L: left.
6. Quit; `Memoria.log` and `x64\FF9_Data\output_log.txt` have no errors.

### Two PCs, two Steam accounts (T1-T6 in one session)

Setup: both PCs get this build (PC 2: `OnlineGhosts-PC2.zip` with its `INSTALL-FOR-AGENT.md`); `OnlineDebug` not
needed. Both load a save **in the same field**.

1. **Connect:** PC 1 Ctrl+Shift+H, then Ctrl+Shift+I and invite PC 2; PC 2 accepts (overlay or Steam chat).
   Status boxes: `Peers: 1`, then within ~5 s `Ghosts: 1 shown`.
2. **T1 (≥ 5 min):** walk and run around each other. Each sees the other's leader as a ghost, smooth, on the floor,
   correct animations. Status box: no `STALLED`, loss ~0%.
3. **T6a (exact animations):** while there, if the field has a ladder or a jump, use it: the other side's ghost should
   climb / jump the same way.
4. **T2:** PC 2 walks to another field and back, 3 times. On PC 1 the ghost disappears when PC 2 leaves, and is back
   ~1 s after PC 2 is in the same field again.
5. **T3:** PC 2 gets into a battle and finishes it. On PC 1 the ghost disappears at the battle start, comes back after.
   On PC 2, PC 1's ghost is there again after the battle.
6. **T5:** PC 2 presses Ctrl+Shift+L (leave): its ghost disappears on PC 1. PC 2 rejoins (Ctrl+Shift+J). Then PC 1
   (the host) leaves and PC 2 sees itself become host in its log/status box; PC 1 rejoins with Ctrl+Shift+J.
7. **T6b:** walk both into a field with lots of background layers (pillars, foreground objects) and, if handy, an FMV
   or mini-game field; note how the ghost looks.
8. ~~**T4 (relay)**~~: dropped, see the table.
9. Quit both games normally; send both `Memoria.log` files.

| # | Setup | Pass | Result |
|---|---|---|---|
| T1 | same network, ≥ 5 min, same field | ghosts smooth, same model as their leader; no `STALLED`; ping/loss fine | **Pass on quality, short on time.** Tester: "it looked great". Direct connection, RTT 2-7 ms, ghost delay 80 ms (jitter 7 ms), ~3480 states each way with ~0% loss, no `STALLED`, both ghosts the sender's leader (model 202). Longest connection 233 s (not ≥ 5 min), and it included T2/T3, so not 5 min in one field. |
| T2 | field changes ×3 | ghost hides when the other leaves, back ~1 s after both are in the same field; no leftovers | **Pass.** Cargo ship 502/504/505/507 and Alexandria 100/101/112, both directions: hidden as soon as the other's states named another field, dropped with the old map, respawned 1-3 s after both were in the same field. Brief 1-3 s "not on a field" hides at each transition (the sender is between fields). |
| T3 | battle | ghost gone during the battle, back after | **Pass.** PC 2's battle 09:18:39-09:19:33: PC 1 hid PC 2's ghost at once (`NotOnField`) and showed it again after; PC 2's ghost of PC 1 went with PC 2's field map and respawned after the battle. |
| T4 | relay (phone hotspot) | works; relay reported or not; RTT, jitter, adaptive delay; ghost smooth | **Dropped (decision, 2026-11-16).** Not a target for this project: players are expected on decent connections, so a poor-connection / relay test isn't worth running. Untested consequence: relay latency and jitter beyond what the adaptive delay (80-250 ms) hides. The delay and jitter stay logged every second if it ever comes up. |
| T5 | leave / rejoin both ways | ghosts removed on leave, back on rejoin; owner change logged; no crash | **Pass.** Guest left (Ctrl+Shift+L): ghost removed on both sides; rejoined via Ctrl+Shift+J's friend-lobby lookup, ghost back 4 s later. Host left: Steam made the guest owner (logged on both), host rejoined, ghosts back. A P2P request from the departed player was refused (not a lobby member). Found: the setup backlog (~3.5 s of queued states) counted as jitter and pinned the delay at 250 ms for ~10 s after each rejoin; **fixed** after the test (states > 1 s late are ignored for jitter). |
| T6 | ladders/jumps, layered field, FMV/mini-game | noted (documented, not fixed) | **Partly.** Layered fields (Alexandria main street and alleys) looked right. Ladders/jumps and FMV/mini-game fields not checked. Overlay invite (`GameLobbyJoinRequested_t`) and the friend-lobby fallback both used successfully. |

Logs: `Output/logs/PC1-online-2026-11-16-2121.log`, `PC2-online-2026-11-16-2121.log` (not committed). No exceptions in
either PC's `output_log.txt`.

## Known issues (before testing)

- A ghost keeps showing during the *local* player's cutscenes (it's hidden only by silence, another field, or its
  player being out of a field).
- Fields without a player (FMV, cutscene-only, some mini-games) never show ghosts.
- With a model override or an unknown animation, a different model falls back to its own idle/walk/run.
- Joining through an invite while the game is closed (`+connect_lobby`) isn't handled; the log says whether the
  argument arrived.

## Verdict

**Not done yet, close.** Everything run so far passes (T1 quality, T2, T3, T5, layered fields). Missing before the
Ghosts phase can be called done:
- **T1 length**: one uninterrupted ≥ 5 min stretch in one field.
- **T6 rest**: a ladder or jump with the same model; an FMV or mini-game field.

One more two-PC session covers both (with the post-test backlog fix installed on both PCs).
