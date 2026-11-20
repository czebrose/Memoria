using Assets.Scripts.Common;
using Memoria.Data;
using Memoria.Online.Party;
using Memoria.Online.Session;
using Memoria.Prime;
using SimpleJSON;
using System;
using System.Collections.Generic;

namespace Memoria.Online.Battle
{
    /// <summary>
    /// Spike D: shared battle entry and exit (docs/online/spike-d-battle-entry.md).
    ///
    /// The PC whose field starts a battle (random encounter or script) is the battle's <b>owner</b> and runs the real
    /// battle. Players on the same field are <b>participants</b>: their PCs enter the same battle from the owner's
    /// <see cref="BattleStartData"/>, wait in it (held at the end of the intro: no ATB, no menu, no AI) and leave when the
    /// owner's battle ends. The battle party holds only the characters of the players in the battle
    /// (<see cref="BattleEntryParty"/>).
    ///
    /// Flow: owner's trigger → Pending (participants stop moving) → owner enters its battle → Start (after the owner's
    /// battle init: pattern, start type and initial ATB are known) → participants leave their field and load it → each
    /// sends Ready at the end of its intro → the owner, held at the same point, goes on once everyone is ready (or after
    /// <see cref="ReadyTimeoutMs"/>) and sends Go → ... → End (result) → participants exit (escape path, no rewards).
    ///
    /// Engine hooks ([Online] Spike D): HonoluluFieldMain (field flow), HonoluluBattleMain.Start, btl_init.SetupBattleEnemy
    /// (start type), battle.BattleMain (the intro → ATB gate), battle.ff9ShutdownStateBattleResult (battle left),
    /// FieldMapActorController / EventEngine.ProcessEvents (hold the player), EventEngine.OnlineDebugEncounter.
    /// </summary>
    internal sealed class BattleEntry
    {
        public static BattleEntry Instance { get; private set; }

        private const Double PresenceEveryMs = 500;
        private const Double PresenceStaleMs = 3000;
        private const Double PendingTimeoutMs = 10000;   // participant: Pending without a Start
        private const Double JoinTimeoutMs = 8000;       // participant: busy (menu, scene, fade) this long after Start → Decline
        private const Double ReadyTimeoutMs = 20000;     // owner: Start sent, no Ready this long → go on without them
        private const Double AliveEveryMs = 1000;
        private const Double OwnerEndedFallbackMs = 45000; // participant: the owner's End came this long ago, this battle still runs → leave
        private const Double OwnerSilentMs = 6000;       // participant: nothing from the owner this long → leave the battle

        private enum Role { None, Owner, Participant }
        private enum Busy : byte { Control = 0, Menu = 1, NoControl = 2, Battle = 3, WorldMap = 4, Other = 5 }

        private sealed class PeerPresence
        {
            public UInt16 Field;
            public Busy State;
            public Double At;
            public Byte Speed = 255, Sfx = 255;      // the peer's battle settings (Battle.Speed, SFXRework); 255 = unknown
        }

        private sealed class Current
        {
            public Role Role;
            public UInt16 Seq;
            public UInt64 Owner;
            public BattleStartData Data;            // owner: what it sent; participant: what it got
            public List<Int32> Characters = new List<Int32>();
            public List<UInt64> Participants = new List<UInt64>();
            public HashSet<UInt64> Awaiting = new HashSet<UInt64>();
            public Double TriggeredAt, StartSentAt = -1, LastFromOwner, NextAlive;
            public Boolean PartyApplied, StartSent, GoSent, EndSent, ReadySent, InBattle;
            public Byte ExitResult;                  // participant: the owner's result once End arrived (0: none yet)
            public Boolean GoReceived;               // participant: the owner's battle runs; this PC plays it passively
            public Boolean OwnerEnded;               // participant: the owner's End arrived; the passive battle ends by itself
            public Double OwnerEndedAt;
            public Boolean ExitRequested, Exiting;
            public String ExitWhy;
            public JSONClass Before;                 // guest owner: party state before the battle (for the results)
        }

        private sealed class Incoming
        {
            public UInt64 Owner;
            public UInt16 Seq;
            public UInt16 Field, BattleId;
            public Double PendingAt;
            public BattleStartData Start;
            public Double StartAt;
        }

        private readonly SteamTransport _transport;
        private readonly SteamLobby _lobby;
        private readonly BlobTransfer _blobs;
        private readonly Func<Boolean> _playing;
        private readonly Func<Int32> _myCharacter;
        private readonly Func<UInt64, Int32> _characterOf;
        private readonly Func<Int32, Boolean> _isPlayed;
        private readonly Func<Int32, UInt64> _controllerOf;   // M2: who commands a character (its player, or a free character's leader)

        private readonly Dictionary<UInt64, PeerPresence> _presence = new Dictionary<UInt64, PeerPresence>();
        private Double _nextPresence;
        private UInt16 _publishedField = UInt16.MaxValue;
        private Busy _publishedState = Busy.Other;
        private UInt16 _seq;
        private Current _current;
        private Incoming _incoming;
        private Double _now;
        private JSONClass _pendingResult;    // host: a guest battle's results, applied once the host can touch the party
        private UInt64 _pendingResultFrom;
        private readonly Dictionary<String, Boolean> _watched = new Dictionary<String, Boolean>(); // host: "owner/seq" → this PC played it to its own end (rewards applied here)

        /// <summary>Step 3: BattleStart goes out at the trigger, with the enemy group and start type rolled there, so
        /// owner and participants load the battle in parallel. Debug toggle (Ctrl+Shift+7): send it after the owner's
        /// battle init, as spike D did (for measuring).</summary>
        public static Boolean StartAtTrigger = true;

        /// <summary>Settings that change battle code paths (task 3): a shared battle needs the same on every PC.</summary>
        public static Byte LocalSpeed => (Byte)Math.Min(254, Configuration.Battle.Speed);
        public static Byte LocalSfx => (Byte)(Configuration.Battle.SFXRework ? 1 : 0);
        private static Boolean LocalSettingsSupported => Configuration.Battle.Speed < 2 && Configuration.Battle.SFXRework;

        /// <summary>This PC is in (or entering) a shared battle as owner or participant.</summary>
        public static Boolean InSharedBattle => Instance?._current != null && Instance.Online;

        /// <summary>
        /// M2: a party member nobody plays follows a player (the host by default; Follow in the party panel). It joins
        /// the battles its leader is in, and its leader's PC commands it. Debug toggle: Ctrl+Shift+U (off: free
        /// characters sit every shared battle out).
        /// </summary>
        public static Boolean FreeCharactersJoinHost = true;

        /// <summary>Shown on screen (OnlineSession): why this PC waits.</summary>
        public static String Notice { get; private set; }

        public BattleEntry(SteamTransport transport, SteamLobby lobby, BlobTransfer blobs, Func<Boolean> playing, Func<Int32> myCharacter, Func<UInt64, Int32> characterOf, Func<Int32, Boolean> isPlayed, Func<Int32, UInt64> controllerOf)
        {
            _controllerOf = controllerOf;
            BattleControl.OnPeerFled = OnPeerFled;
            BattleControl.OnFleeResult = OnFleeResult;
            _transport = transport;
            _lobby = lobby;
            _blobs = blobs;
            _playing = playing;
            _myCharacter = myCharacter;
            _characterOf = characterOf;
            _isPlayed = isPlayed;
            Instance = this;
        }

        private Boolean Online => _playing() && _transport.IsActive;

        // ---- Static engine hooks ([Online] Spike D) -------------------------------------------------------------

        /// <summary>[Online] HonoluluFieldMain, after ServiceEvents: notes the owner's battle, or starts a participant's.</summary>
        public static Int32 OnFieldFlow(Int32 codeFlowState) => Guard(() => Instance?.FieldFlow(codeFlowState) ?? codeFlowState, codeFlowState);

        /// <summary>[Online] HonoluluBattleMain.Start, before InitBattleScene.</summary>
        public static void BeforeBattleInit() => Guard(() => { Instance?.BattleInitStart(); return 0; }, 0);

        /// <summary>[Online] HonoluluBattleMain.Start, after InitBattleScene.</summary>
        public static void AfterBattleInit() => Guard(() => { Instance?.BattleInitEnd(); return 0; }, 0);

        /// <summary>[Online] btl_init.SetupBattleEnemy: the start type (forced on a participant).</summary>
        public static battle_start_type_tags StartType(BTL_SCENE_INFO info)
        {
            Current c = Instance?._current;
            BattleStartData forced = c?.Role == Role.Participant || (c?.Role == Role.Owner && c.Data != null && c.Data.Prerolled) ? c.Data : _debugForced;
            if (forced != null)
                return (battle_start_type_tags)forced.StartType;
            return FF9.btl_sys.StartType(info);
        }

        private static BattleStartData _debugForced;

        /// <summary>The start type is drawn in btl_init.SetupBattleEnemy, during the battle's loading loop (after
        /// InitBattleScene), so a forced value must live until the next battle scene starts.</summary>
        public static void ClearDebugStart() => _debugForced = null;

        /// <summary>Debug record / replay (BattleSync, OnlineDebug): this PC's next battle is forced to a recorded one,
        /// through the same values a participant gets from BattleStart.</summary>
        public static void ForceDebugStart(Int32 battleId, Int32 pattern, Int32 startType)
        {
            FF9StateGlobal ff9 = FF9StateSystem.Common.FF9;
            if (ff9.btlMapNo != battleId)
                Log.Message($"[Online] Battle entry: debug replay forces battle {battleId} (this encounter is {ff9.btlMapNo})");
            _debugForced = new BattleStartData { BattleId = (UInt16)battleId, Pattern = (Byte)pattern, StartType = (Byte)startType };
            ff9.btlMapNo = (Int16)battleId;
            FF9StateSystem.Battle.battleMapIndex = battleId;
            ff9.btlSubMapNo = (SByte)pattern;
        }

        /// <summary>The owner's initial ATB, in btl_list order (a participant's Go, or a debug replay's start).</summary>
        public static Int32 SetInitialAtb(IList<Int16> values)
        {
            Int32 changed = 0, i = 0;
            for (BTL_DATA b = FF9StateSystem.Battle.FF9Battle.btl_list.next; b != null && i < values.Count; b = b.next, i++)
            {
                if (b.cur.at != values[i])
                    changed++;
                b.cur.at = values[i];
            }
            return changed;
        }

        /// <summary>[Online] battle.BattleMain, PHASE_MENU_ON once the intro is done: false holds the battle there.</summary>
        public static Boolean MayLeaveIntro() => Guard(() => Instance == null || Instance.LeaveIntro(), true);

        /// <summary>[Online] battle.ff9ShutdownStateBattleResult: the battle scene is being left (result screen closed).</summary>
        public static void OnBattleLeft() => Guard(() => { Instance?.BattleLeft("battle left"); return 0; }, 0);

        /// <summary>[Online] field player movement and interaction: a participant waits for the owner's battle.</summary>
        public static Boolean HoldPlayer => Instance?._incoming != null;

        /// <summary>PartySync waits while the party slots hold a battle party.</summary>
        public static Boolean PartyAltered => BattleEntryParty.Altered;

        private static T Guard<T>(Func<T> f, T fallback)
        {
            try { return f(); }
            catch (Exception err) { Log.Error(err, "[Online] Battle entry failed"); return fallback; }
        }

        // ---- Per frame ----------------------------------------------------------------------------------------

        public void Update(Double now)
        {
            _now = now;
            Notice = null;
            if (!Online)
            {
                if (_current != null || _incoming != null)
                    Log.Message("[Online] Battle entry: session not playing; forgetting the shared battle");
                _incoming = null;
                if (_current != null && !_current.InBattle)
                    _current = null;
                if (_current == null)
                    BattleEntryParty.Restore("session not playing");
                return;
            }
            PublishPresence(now);
            TrackField();
            UpdateIncoming(now);
            UpdateCurrent(now);
            BattleControl.Flush();
            ApplyPendingResult();
            // Fallback: the battle scene is gone without passing the result screen (game over, title...)
            if (_current != null && _current.InBattle && PersistenSingleton<SceneDirector>.Instance.CurrentScene != SceneDirector.BattleMapSceneName
                && PersistenSingleton<FF9StateSystem>.Instance.mode != 2 && PersistenSingleton<FF9StateSystem>.Instance.mode != 8)
                BattleLeft("left the battle scene without the result screen");
        }

        private void PublishPresence(Double now)
        {
            UInt16 field = (UInt16)FF9StateSystem.Common.FF9.fldMapNo;
            Busy state = LocalBusy();
            if (field == _publishedField && state == _publishedState && now < _nextPresence)
                return;
            _publishedField = field;
            _publishedState = state;
            _nextPresence = now + PresenceEveryMs;
            Byte[] message = { BattleEntryMessages.Presence, (Byte)field, (Byte)(field >> 8), (Byte)state, LocalSpeed, LocalSfx };
            foreach (UInt64 peer in _transport.Peers)
                _transport.Send(peer, TransportChannel.Control, message, message.Length, true);
        }

        private static Busy LocalBusy()
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            UIManager ui = PersistenSingleton<UIManager>.Instance;
            FF9StateSystem sys = PersistenSingleton<FF9StateSystem>.Instance;
            if (engine == null || ui == null)
                return Busy.Other;
            if (sys.mode == 2 || sys.mode == 8 || engine.gMode == 2)
                return Busy.Battle;
            if (engine.gMode == 3)
                return Busy.WorldMap;
            if (engine.gMode != 1)
                return Busy.Other;
            if (ui.State != UIManager.UIState.FieldHUD)
                return Busy.Menu;
            if (engine.fieldmap?.playerController == null || !engine.GetUserControl() || PersistenSingleton<SceneDirector>.Instance.IsFading)
                return Busy.NoControl;
            return Busy.Control;
        }

        // ---- Owner --------------------------------------------------------------------------------------------

        private Int32 FieldFlow(Int32 codeFlowState)
        {
            if (!Online)
                return codeFlowState;
            if (codeFlowState == EventEngine.FLOW_STATE_JUMP_BATTLE)
            {
                if (_incoming != null)
                {
                    Log.Message($"[Online] Battle entry: this PC's own battle started while {SteamNative.Tag(_incoming.Owner)}'s was coming; declining theirs");
                    SendTo(_incoming.Owner, BattleEntryMessages.Short(BattleEntryMessages.Decline, _incoming.Seq, 2));
                    _incoming = null;
                }
                BeginOwned();
                return codeFlowState;
            }
            if (codeFlowState <= EventEngine.FLOW_STATE_WAIT && _incoming?.Start != null && CanJoinNow(out String _))
                return JoinBattle();
            return FleeReturn(codeFlowState);
        }

        private void BeginOwned()
        {
            Int32 mine = _myCharacter();
            List<Int32> party = BattleEntryParty.PartyCharacters();
            if (mine < 0 || !party.Contains(mine))
            {
                Log.Message($"[Online] Battle entry: battle {NextBattleId()} with the whole party (this PC plays {(mine < 0 ? "no character" : $"character {mine}, not in the party")})");
                return;
            }
            Current c = new Current { Role = Role.Owner, Seq = ++_seq, Owner = _lobby.LocalId, TriggeredAt = _now };
            UInt16 field = (UInt16)FF9StateSystem.Common.FF9.fldMapNo;
            HashSet<Int32> chars = new HashSet<Int32> { mine };
            Boolean shareable = LocalSettingsSupported;
            if (!shareable)
                Log.Error($"[Online] Battle entry: ASSERTION: this PC's battle settings (Battle Speed {Configuration.Battle.Speed}, SFXRework {LocalSfx}) can't be shared (needs Speed 0/1, SFXRework 1), although sessions force supported ones; nobody joins this battle");
            foreach (UInt64 peer in _transport.Peers)
            {
                if (!shareable)
                    break;
                if (!_presence.TryGetValue(peer, out PeerPresence p) || _now - p.At > PresenceStaleMs || p.Field != field || p.State == Busy.Battle || p.State == Busy.WorldMap)
                    continue;
                Int32 character = _characterOf(peer);
                if (character < 0 || !party.Contains(character))
                    continue;
                if (p.Speed != LocalSpeed || p.Sfx != LocalSfx)
                {
                    // M2: the session forces the host's battle settings on every PC, so this should never happen
                    Log.Error($"[Online] Battle entry: ASSERTION: {SteamNative.Tag(peer)}'s battle settings differ from this PC's although the session forces the host's (Speed {p.Speed} vs {LocalSpeed}, SFXRework {p.Sfx} vs {LocalSfx}); it sits this battle out");
                    PartySync.Toast("A player's battle settings differ from yours: they sit this battle out (see the log).");
                    continue;
                }
                c.Participants.Add(peer);
                c.Awaiting.Add(peer);
                chars.Add(character);
            }
            // M2: a free character joins when its leader is in the battle (the leader's PC commands it)
            if (FreeCharactersJoinHost)
                foreach (Int32 character in party)
                {
                    if (_isPlayed(character))
                        continue;
                    UInt64 leader = _controllerOf(character);
                    if (leader == _lobby.LocalId || c.Participants.Contains(leader))
                        chars.Add(character);
                }
            foreach (Int32 character in party)
                if (chars.Contains(character))
                    c.Characters.Add(character);
            if (!_lobby.IsHost)
                c.Before = PartyState.Capture(); // D4: this guest's results go to the host
            _current = c;
            UInt16 battleId = (UInt16)NextBattleId();
            List<String> who = new List<String>();
            foreach (UInt64 peer in c.Participants)
            {
                who.Add($"{SteamNative.Tag(peer)} ({PresenceOf(peer)})");
                SendTo(peer, BattleEntryMessages.Short(BattleEntryMessages.Pending, c.Seq, (Byte)field, (Byte)(field >> 8), (Byte)battleId, (Byte)(battleId >> 8)));
            }
            if (StartAtTrigger && c.Participants.Count > 0)
            {
                BattleStartData d = PreRoll(c, battleId, out String why);
                if (d != null)
                {
                    c.Data = d;
                    Byte[] message = d.Serialize();
                    foreach (UInt64 peer in c.Participants)
                        SendTo(peer, message);
                    c.StartSent = true;
                    c.StartSentAt = _now;
                    c.NextAlive = _now + AliveEveryMs;
                    Log.Message($"[Online] Battle entry: #{c.Seq} Start sent at the trigger to {c.Participants.Count} participant(s), {message.Length} bytes: {d.Describe()}");
                }
                else
                    Log.Warning($"[Online] Battle entry: #{c.Seq} can't roll the battle at the trigger ({why}); Start goes after this PC's battle init");
            }
            Log.Message($"[Online] Battle entry: battle {battleId} #{c.Seq} on field {field} ({(FF9StateSystem.Battle.isRandomEncounter ? "random encounter" : "scripted")}); this PC owns it; participants: {(who.Count > 0 ? String.Join(", ", who.ToArray()) : "none")}; battle party {String.Join(" ", c.Characters.ConvertAll(x => x.ToString()).ToArray())}");
        }

        private static Int32 NextBattleId() => FF9StateSystem.Field.FF9Field.loc.map.nextMapNo;

        private static Boolean BattlePhaseRunning()
        {
            Byte phase = FF9StateSystem.Battle.FF9Battle.btl_phase;
            return PersistenSingleton<EventEngine>.Instance.gMode == 2 && (phase == FF9StateBattleSystem.PHASE_NORMAL || phase == FF9StateBattleSystem.PHASE_MENU_ON);
        }

        /// <summary>
        /// Owner, at the trigger: what the battle init would draw, drawn now so BattleStart can go out before this PC
        /// loads. Enemy group: HonoluluBattleMain.ChoicePattern's roll on the scene's pattern rates, forced through
        /// btlSubMapNo (ChoicePattern honours it). Start type: btl_sys.StartType with the battle party in the slots,
        /// forced in btl_init.SetupBattleEnemy (StartType above), as on the participants.
        /// </summary>
        private BattleStartData PreRoll(Current c, Int32 battleId, out String why)
        {
            why = null;
            if (FF9StateSystem.Battle.isDebug)
            {
                why = "debug battle";
                return null;
            }
            if (!FF9BattleDB.SceneData.TryGetKey(battleId, out String sceneName) || sceneName.Length <= 4)
            {
                why = $"no scene for battle {battleId}";
                return null;
            }
            BTL_SCENE scene = new BTL_SCENE();
            scene.ReadBattleScene(sceneName.Substring(4));
            Int32 patterns = scene.header.PatCount;
            if (patterns <= 0 || scene.PatAddr == null)
            {
                why = $"battle {battleId} has no enemy groups";
                return null;
            }
            FF9StateGlobal ff9 = FF9StateSystem.Common.FF9;
            Int32 pattern = ff9.btlSubMapNo;
            if (pattern < 0 || pattern >= patterns)
            {
                Int32 roll = UnityEngine.Random.Range(0, 100), sum = scene.PatAddr[0].Rate;
                pattern = 0;
                while (roll >= sum && pattern + 1 < patterns)
                    sum += scene.PatAddr[++pattern].Rate;
                ff9.btlSubMapNo = (SByte)pattern;
            }
            PLAYER[] members = ff9.party.member;
            PLAYER[] saved = (PLAYER[])members.Clone();
            battle_start_type_tags startType;
            try
            {
                List<PLAYER> keep = new List<PLAYER>();
                foreach (PLAYER p in saved)
                    if (p != null && c.Characters.Contains((Int32)p.info.slot_no))
                        keep.Add(p);
                for (Int32 i = 0; i < members.Length; i++)
                    members[i] = i < keep.Count ? keep[i] : null;
                startType = FF9.btl_sys.StartType(scene.Info);
            }
            finally
            {
                for (Int32 i = 0; i < members.Length; i++)
                    members[i] = saved[i];
            }
            BattleStartData d = new BattleStartData
            {
                Seq = c.Seq,
                Seed = Environment.TickCount ^ (c.Seq * 7919),
                BattleId = (UInt16)battleId,
                Pattern = (Byte)pattern,
                StartType = (Byte)startType,
                Flags = (Byte)((FF9StateSystem.Battle.isRandomEncounter ? BattleStartData.FlagRandomEncounter : 0)
                               | (ff9.steiner_state != 0 ? BattleStartData.FlagSteiner : 0)
                               | (FF9StateSystem.Battle.isEncount ? BattleStartData.FlagEncount : 0)),
                Field = (UInt16)ff9.fldMapNo,
                Speed = LocalSpeed,
                Sfx = LocalSfx,
                // A scripted battle's song is playing already (the field script started it); a random encounter's comes
                // with the swirl, which runs on every PC: only Go carries it
                Music = FF9StateSystem.Battle.isRandomEncounter ? (Int16)(-1) : (Int16)FF9Snd.GetCurrentMusicId(),
                Prerolled = true
            };
            foreach (PLAYER p in members)
                if (p != null && c.Characters.Contains((Int32)p.info.slot_no))
                    d.Members.Add(new BattleStartData.Member { Character = (Byte)p.info.slot_no, Hp = p.cur.hp, Mp = p.cur.mp, Status = (UInt64)p.status, Trance = p.trance });
            return d;
        }

        private String PresenceOf(UInt64 peer) => _presence.TryGetValue(peer, out PeerPresence p) ? p.State.ToString() : "?";

        private void BattleInitStart()
        {
            Current c = _current;
            if (c == null || c.InBattle)
                return;
            c.InBattle = true;
            if (c.Role == Role.Owner)
            {
                if (c.Data == null)
                {
                    Int32 seed = Environment.TickCount ^ (c.Seq * 7919);
                    c.Data = new BattleStartData { Seq = c.Seq, Seed = seed, Speed = LocalSpeed, Sfx = LocalSfx, Music = (Int16)FF9Snd.GetCurrentMusicId() };
                }
                UnityEngine.Random.seed = c.Data.Seed;
            }
            else
            {
                UnityEngine.Random.seed = c.Data.Seed;
                ApplyOwnerStats(c.Data);
                PlayOwnerMusic(c.Data.Music); // from the start of the battle; Go checks again
            }
            c.PartyApplied = BattleEntryParty.Apply(c.Characters);
            if (c.Role == Role.Owner && c.Participants.Count > 0)
                BattleSync.BeginOwner();
            else if (c.Role == Role.Participant)
                BattleSync.BeginPassive();
        }

        private void BattleInitEnd()
        {
            Current c = _current;
            if (c == null || !c.InBattle)
                return;
            FF9StateBattleSystem btl = FF9StateSystem.Battle.FF9Battle;
            if (c.Role == Role.Owner && c.Data.Prerolled)
            {
                // The start type is drawn later (enemy loading): it is forced to the rolled one there
                Log.Message($"[Online] Battle entry: #{c.Seq} battle loaded, {(btl.btl_scene.PatNum == c.Data.Pattern && FF9StateSystem.Battle.battleMapIndex == c.Data.BattleId ? "battle and pattern as rolled at the trigger" : $"DIFFERS from the roll at the trigger: battle {FF9StateSystem.Battle.battleMapIndex}, pattern {btl.btl_scene.PatNum}")}, {_now - c.TriggeredAt:0} ms after the trigger");
            }
            else if (c.Role == Role.Owner)
            {
                BattleStartData d = c.Data;
                d.BattleId = (UInt16)FF9StateSystem.Battle.battleMapIndex;
                d.Pattern = btl.btl_scene.PatNum;
                // The start type isn't drawn yet (btl_init.SetupBattleEnemy, enemy loading): draw it now, with the battle
                // party in the slots, and force it on this PC too
                d.StartType = (Byte)FF9.btl_sys.StartType(btl.btl_scene.Info);
                d.Prerolled = true;
                d.Flags = (Byte)((FF9StateSystem.Battle.isRandomEncounter ? BattleStartData.FlagRandomEncounter : 0)
                                 | (FF9StateSystem.Common.FF9.steiner_state != 0 ? BattleStartData.FlagSteiner : 0)
                                 | (FF9StateSystem.Battle.isEncount ? BattleStartData.FlagEncount : 0));
                d.Field = (UInt16)FF9StateSystem.Common.FF9.fldMapNo;
                foreach (PLAYER p in FF9StateSystem.Common.FF9.party.member)
                    if (p != null)
                        d.Members.Add(new BattleStartData.Member { Character = (Byte)p.info.slot_no, Hp = p.cur.hp, Mp = p.cur.mp, Status = (UInt64)p.status, Trance = p.trance });
                Byte[] message = d.Serialize(); // the units aren't in btl_list yet here: they go with Go
                foreach (UInt64 peer in c.Participants)
                    SendTo(peer, message);
                c.StartSent = true;
                c.StartSentAt = _now;
                c.NextAlive = _now + AliveEveryMs;
                Log.Message($"[Online] Battle entry: #{c.Seq} Start sent to {c.Participants.Count} participant(s), {message.Length} bytes, {_now - c.TriggeredAt:0} ms after the trigger: {d.Describe()}");
            }
            else
            {
                // Same scene? (the units are compared when Go brings the owner's list)
                BattleStartData d = c.Data;
                List<String> diffs = new List<String>();
                if (FF9StateSystem.Battle.battleMapIndex != d.BattleId) diffs.Add($"battle {FF9StateSystem.Battle.battleMapIndex}");
                if (btl.btl_scene.PatNum != d.Pattern) diffs.Add($"pattern {btl.btl_scene.PatNum}");
                // (the start type is drawn later, at enemy load: the replay's start compares it)
                Log.Message($"[Online] Battle entry: #{c.Seq} battle loaded, {(diffs.Count == 0 ? "scene and pattern same as the owner's" : $"DIFFERS from the owner's: {String.Join(", ", diffs.ToArray())}")}");
            }
        }

        /// <summary>Participant, on Go: the same units as the owner's? Then its initial ATB (ATB doesn't run while held).</summary>
        /// <summary>The owner's battle music (a scripted battle's field script started it on the owner's PC only).</summary>
        private static void PlayOwnerMusic(Int32 music)
        {
            Int32 local = FF9Snd.GetCurrentMusicId();
            if (music < 0 || music == local)
                return;
            Log.Message($"[Online] Battle entry: playing the owner's music {music} (here: {local})");
            btlsnd.ff9btlsnd_song_play(music);
        }

        private void ApplyOwnerUnits(Current c, List<KeyValuePair<Byte, Int16>> owner)
        {
            if (!c.InBattle || PersistenSingleton<EventEngine>.Instance.gMode != 2)
            {
                Log.Warning($"[Online] Battle entry: #{c.Seq} Go arrived before this PC's battle was up; units not checked");
                return;
            }
            List<KeyValuePair<Byte, Int16>> units = new List<KeyValuePair<Byte, Int16>>();
            ReadUnits(units);
            List<String> diffs = new List<String>();
            if (units.Count != owner.Count)
                diffs.Add($"{units.Count} units (owner {owner.Count})");
            Int32 atbForced = 0, i = 0;
            for (BTL_DATA b = FF9StateSystem.Battle.FF9Battle.btl_list.next; b != null && i < owner.Count; b = b.next, i++)
            {
                if (units[i].Key != owner[i].Key)
                    diffs.Add($"unit {i}: {UnitName(units[i].Key)} (owner {UnitName(owner[i].Key)})");
                if (b.cur.at != owner[i].Value)
                    atbForced++;
                b.cur.at = owner[i].Value;
            }
            List<String> names = owner.ConvertAll(u => $"{UnitName(u.Key)} at {u.Value}");
            Log.Message($"[Online] Battle entry: #{c.Seq} units {(diffs.Count == 0 ? "same as the owner's" : $"DIFFER from the owner's: {String.Join(", ", diffs.ToArray())}")} [{String.Join(", ", names.ToArray())}]; initial ATB set from the owner on {atbForced} of {units.Count} (the rest already matched)");
        }

        private static String UnitName(Byte key) => (key & 0x80) != 0 ? $"enemy type {key & 0x7F}" : $"char {key}";

        private static void ReadUnits(List<KeyValuePair<Byte, Int16>> units)
        {
            for (BTL_DATA b = FF9StateSystem.Battle.FF9Battle.btl_list.next; b != null; b = b.next)
                units.Add(new KeyValuePair<Byte, Int16>(b.bi.player != 0 ? (Byte)b.bi.slot_no : (Byte)(0x80 | (b.typeNo & 0x7F)), b.cur.at));
        }

        private Boolean LeaveIntro()
        {
            Current c = _current;
            if (c == null || !c.InBattle)
                return true;
            if (c.Role == Role.Owner)
            {
                if (c.Awaiting.Count > 0 && c.StartSent && _now - c.StartSentAt > ReadyTimeoutMs)
                {
                    foreach (UInt64 peer in c.Awaiting)
                    {
                        Log.Warning($"[Online] Battle entry: #{c.Seq} {SteamNative.Tag(peer)} not ready after {ReadyTimeoutMs / 1000:0} s; going on without them");
                        SendTo(peer, BattleEntryMessages.Short(BattleEntryMessages.End, c.Seq, 0));
                        c.Participants.Remove(peer);
                    }
                    c.Awaiting.Clear();
                }
                if (c.Awaiting.Count > 0)
                    return false;
                if (!c.GoSent)
                {
                    c.GoSent = true;
                    List<KeyValuePair<Byte, Int16>> units = new List<KeyValuePair<Byte, Int16>>();
                    ReadUnits(units); // every unit exists now, and no ATB has run yet: these are the initial values
                    Int32 music = FF9Snd.GetCurrentMusicId();
                    Dictionary<UInt16, UInt64> control = ControlMap(c);
                    Byte[] go = BattleEntryMessages.GoWithUnits(c.Seq, units, music, control);
                    BattleControl.BeginOwner(c.Seq, control, SendTo);
                    foreach (UInt64 peer in c.Participants)
                        SendTo(peer, go);
                    Log.Message($"[Online] Battle entry: #{c.Seq} Go: {go.Length} bytes, music {music}, {units.Count} units [{String.Join(", ", units.ConvertAll(u => $"{UnitName(u.Key)} at {u.Value}").ToArray())}]");
                    Log.Message($"[Online] Battle entry: #{c.Seq} everyone ready ({c.Participants.Count} participant(s)); the battle runs, {_now - c.TriggeredAt:0} ms after the trigger");
                }
                return true;
            }
            if (!c.ReadySent)
            {
                c.ReadySent = true;
                SendTo(c.Owner, BattleEntryMessages.Short(BattleEntryMessages.Ready, c.Seq));
                Log.Message($"[Online] Battle entry: #{c.Seq} ready (intro done); waiting in the battle while {SteamNative.Tag(c.Owner)} runs it");
            }
            // Held until the owner's Go; then the battle runs in passive mode (no ATB filling, no menus, no enemy AI, no
            // calculation: the owner's stream drives it). Without the passive mode it stays held (spike D's behaviour).
            return c.GoReceived && BattleSync.LivePassive;
        }

        /// <summary>M2, owner at Go: each player unit commanded by another PC in this battle (the rest are this PC's).</summary>
        private Dictionary<UInt16, UInt64> ControlMap(Current c)
        {
            Dictionary<UInt16, UInt64> map = new Dictionary<UInt16, UInt64>();
            for (BTL_DATA b = FF9StateSystem.Battle.FF9Battle.btl_list.next; b != null; b = b.next)
            {
                if (b.bi.player == 0)
                    continue;
                UInt64 controller = _controllerOf(b.bi.slot_no);
                if (controller != 0 && controller != _lobby.LocalId && c.Participants.Contains(controller))
                    map[b.btl_id] = controller;
            }
            return map;
        }

        private void UpdateCurrent(Double now)
        {
            Current c = _current;
            if (c == null)
                return;
            FF9StateGlobal ff9 = FF9StateSystem.Common.FF9;
            if (c.Role == Role.Owner)
            {
                if (!c.InBattle && now - c.TriggeredAt > 15000)
                {
                    Log.Warning($"[Online] Battle entry: #{c.Seq} the battle scene never started; forgetting it");
                    _current = null;
                    return;
                }
                if (c.InBattle && !c.GoSent && c.Awaiting.Count > 0)
                    Notice = $"Waiting for {c.Awaiting.Count} player(s) to join the battle...";
                if (c.StartSent && c.Participants.Count > 0 && now >= c.NextAlive)
                {
                    c.NextAlive = now + AliveEveryMs;
                    foreach (UInt64 peer in c.Participants)
                        SendTo(peer, BattleEntryMessages.Short(BattleEntryMessages.Alive, c.Seq));
                }
                if (c.GoSent && c.Participants.Count > 0)
                {
                    Byte[] stream = BattleSync.TakeStream(c.Seq);
                    if (stream != null)
                        foreach (UInt64 peer in c.Participants)
                            SendTo(peer, stream);
                }
                Boolean streamed = BattleSync.OwnerStream != null;
                if (c.StartSent && c.GoSent && !c.EndSent && (streamed ? BattleSync.OwnerStreamEnded : ff9.btl_result != FF9StateGlobal.BTL_RESULT_INITIAL))
                {
                    c.EndSent = true;
                    foreach (UInt64 peer in c.Participants)
                        SendTo(peer, BattleEntryMessages.Short(BattleEntryMessages.End, c.Seq, ff9.btl_result));
                    Log.Message($"[Online] Battle entry: #{c.Seq} ended: {ResultName(ff9.btl_result)}; End sent to {c.Participants.Count} participant(s)");
                }
                return;
            }
            // Participant, in the battle
            String ownerName = SteamNative.GetName(c.Owner);
            Notice = c.ExitRequested || c.GoReceived ? null : $"{ownerName} is running this battle...";
            // The passive plays the owner's battle a few frames behind; once the owner's End is in, it ends through its
            // own screens shortly after. This is the fallback if it doesn't (the owner may have left already: no silence check).
            if (c.OwnerEnded && !c.ExitRequested && now - c.OwnerEndedAt > OwnerEndedFallbackMs && BattlePhaseRunning())
                RequestExit(c, c.ExitResult, $"the owner's battle ended {OwnerEndedFallbackMs / 1000:0} s ago but this one still runs", null);
            if (!c.ExitRequested && !c.OwnerEnded && now - c.LastFromOwner > OwnerSilentMs)
                RequestExit(c, 0, $"nothing from {SteamNative.Tag(c.Owner)} for {OwnerSilentMs / 1000:0} s", "The battle's owner is gone; leaving the battle.");
            if (c.ExitRequested && !c.Exiting)
                TryExit(c);
        }

        // ---- Participant --------------------------------------------------------------------------------------

        private void UpdateIncoming(Double now)
        {
            Incoming inc = _incoming;
            if (inc == null)
                return;
            if (inc.Start == null)
            {
                Notice = "A battle is starting...";
                if (now - inc.PendingAt > PendingTimeoutMs)
                {
                    Log.Warning($"[Online] Battle entry: #{inc.Seq} {SteamNative.Tag(inc.Owner)}'s battle never came ({PendingTimeoutMs / 1000:0} s); carrying on");
                    _incoming = null;
                }
                return;
            }
            if (CanJoinNow(out String why))
            {
                Notice = "Joining the battle...";
                return; // the field flow hook starts it this frame or the next
            }
            if (why == "elsewhere")
            {
                Decline(inc, 1, $"not on field {inc.Start.Field} any more");
                return;
            }
            Notice = $"A battle is starting: {why} ({Math.Max(0, (JoinTimeoutMs - (now - inc.StartAt)) / 1000):0} s)";
            if (now - inc.StartAt > JoinTimeoutMs)
                Decline(inc, 0, $"still busy after {JoinTimeoutMs / 1000:0} s ({why})");
        }

        private void Decline(Incoming inc, Byte reason, String why)
        {
            SendTo(inc.Owner, BattleEntryMessages.Short(BattleEntryMessages.Decline, inc.Seq, reason));
            Log.Message($"[Online] Battle entry: #{inc.Seq} declined {SteamNative.Tag(inc.Owner)}'s battle: {why}");
            PartySync.Toast("You were left out of the battle.");
            _incoming = null;
        }

        /// <summary>D3 rules: join from a field this PC can move in, with no menu, scene, fade or field change going on.</summary>
        private Boolean CanJoinNow(out String why)
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            UIManager ui = PersistenSingleton<UIManager>.Instance;
            why = null;
            if (engine == null || engine.gMode != 1 || PersistenSingleton<FF9StateSystem>.Instance.mode != 1)
                why = "elsewhere";
            else if (FF9StateSystem.Common.FF9.fldMapNo != _incoming.Start.Field)
                why = "elsewhere";
            else if (ui.State != UIManager.UIState.FieldHUD)
                why = "close the menu to join";
            else if (engine.fieldmap?.playerController == null || (!engine.GetUserControl() && !Memoria.Online.Session.SceneSync.WaitingForOwnerBattle)) // [Online] Spike E: a party scene follower whose script reached its battle joins the owner's
                why = "waiting for the scene to end";
            else if (PersistenSingleton<SceneDirector>.Instance.IsFading || (PersistenSingleton<FF9StateSystem>.Instance.attr & 15u) != 0)
                why = "waiting for the screen change";
            return why == null;
        }

        private Int32 JoinBattle()
        {
            Incoming inc = _incoming;
            BattleStartData d = inc.Start;
            _incoming = null;
            _current = new Current { Role = Role.Participant, Seq = inc.Seq, Owner = inc.Owner, Data = d, TriggeredAt = _now, LastFromOwner = _now };
            foreach (BattleStartData.Member m in d.Members)
                _current.Characters.Add(m.Character);
            // As the field's own encounter / ENCOUNT opcode would set them; the pattern is forced through btlSubMapNo
            FF9StateSystem.Field.FF9Field.loc.map.nextMapNo = (Int16)d.BattleId;
            FF9StateSystem.Common.FF9.btlSubMapNo = (SByte)d.Pattern;
            FF9StateSystem.Common.FF9.steiner_state = (Byte)((d.Flags & BattleStartData.FlagSteiner) != 0 ? 1 : 0);
            FF9StateSystem.Battle.isRandomEncounter = (d.Flags & BattleStartData.FlagRandomEncounter) != 0;
            FF9StateSystem.Battle.isEncount = (d.Flags & BattleStartData.FlagEncount) != 0;
            Log.Message($"[Online] Battle entry: #{inc.Seq} joining {SteamNative.Tag(inc.Owner)}'s battle {d.BattleId} from field {d.Field}, {_now - inc.StartAt:0} ms after its Start");
            return EventEngine.FLOW_STATE_JUMP_BATTLE;
        }

        private static void ApplyOwnerStats(BattleStartData d)
        {
            List<String> changed = new List<String>();
            foreach (BattleStartData.Member m in d.Members)
            {
                PLAYER p = FF9StateSystem.Common.FF9.GetPlayer((CharacterId)m.Character);
                if (p == null)
                    continue;
                if (p.cur.hp != m.Hp || p.cur.mp != m.Mp || (UInt64)p.status != m.Status)
                    changed.Add($"{m.Character}: hp {p.cur.hp}->{m.Hp}, mp {p.cur.mp}->{m.Mp}{((UInt64)p.status != m.Status ? $", status 0x{(UInt64)p.status:X}->0x{m.Status:X}" : "")}");
                p.cur.hp = Math.Min(m.Hp, p.max.hp);
                p.cur.mp = Math.Min(m.Mp, p.max.mp);
                p.status = (BattleStatus)m.Status;
                p.trance = m.Trance;
            }
            Log.Message($"[Online] Battle entry: starting HP/MP/status from the owner{(changed.Count > 0 ? $"; changed {String.Join("; ", changed.ToArray())}" : " (all already equal)")}");
        }

        private void RequestExit(Current c, Byte result, String why, String toast)
        {
            if (c.ExitRequested)
                return;
            c.ExitRequested = true;
            c.ExitResult = result;
            c.ExitWhy = why;
            BattleSync.AbortLive();
            if (toast != null)
                PartySync.Toast(toast);
            Log.Message($"[Online] Battle entry: #{c.Seq} leaving the battle ({why})");
        }

        /// <summary>
        /// D4, participant: leave the battle as an escape (no rewards, no result screen), whatever the owner's result;
        /// the owner's result is put back in btl_result once the battle is left. Ending through the normal victory
        /// screen needs spike C's passive replay (the participant's battle then reaches victory on its own).
        /// </summary>
        private void TryExit(Current c)
        {
            FF9StateBattleSystem btl = FF9StateSystem.Battle.FF9Battle;
            FF9StateGlobal ff9 = FF9StateSystem.Common.FF9;
            if (!c.InBattle)
            {
                // Start arrived but the jump hasn't happened: nothing to leave
                if (PersistenSingleton<FF9StateSystem>.Instance.mode == 1)
                    _current = null;
                return;
            }
            if (PersistenSingleton<EventEngine>.Instance.gMode != 2)
                return;
            switch (btl.btl_phase)
            {
                case FF9StateBattleSystem.PHASE_EVENT:
                case FF9StateBattleSystem.PHASE_MENU_ON:
                case FF9StateBattleSystem.PHASE_NORMAL:
                    break;
                case FF9StateBattleSystem.PHASE_ENTER:
                    return; // still loading: leave right after
                default:
                    c.Exiting = true; // already ending on its own
                    return;
            }
            c.Exiting = true;
            UIManager.Battle.FF9BMenu_EnableMenu(false);
            btl_cmd.KillAllCommand(btl);
            ff9.btl_result = FF9StateGlobal.BTL_RESULT_ESCAPE;
            btl.btl_phase = FF9StateBattleSystem.PHASE_CLOSE;
            btl.btl_seq = FF9StateBattleSystem.SEQ_DEFEATCLOSE_FADEOUT;
            Log.Message($"[Online] Battle entry: #{c.Seq} exiting the battle (owner's result {ResultName(c.ExitResult)})");
        }

        private void BattleLeft(String how)
        {
            Current c = _current;
            BattleControl.Reset();
            BattleEntryParty.Restore(how);
            if (c == null)
                return;
            _current = null;
            Notice = null;
            if (c.Role == Role.Participant && c.ExitRequested && c.ExitResult != 0)
                FF9StateSystem.Common.FF9.btl_result = c.ExitResult; // left early: the field sees the owner's result
            if (c.Role == Role.Participant && _lobby.IsHost)
                _watched[$"{c.Owner}/{c.Seq}"] = c.GoReceived && !c.ExitRequested; // played to its own end: rewards applied here
            if (!_lobby.IsHost)
            {
                if (c.Role == Role.Owner && c.Before != null)
                {
                    Byte[] blob = BattleEntryParty.PackResult(c.Data?.BattleId ?? 0, c.Seq, c.Characters, c.Before, PartyState.Capture());
                    if (blob != null)
                    {
                        _blobs.Send(_lobby.OwnerId, blob);
                        Log.Message($"[Online] Battle entry: #{c.Seq} results sent to the host ({blob.Length} bytes)");
                    }
                }
                PartySync.Instance?.OnGameLoaded(); // a guest's battle changes aren't party requests: take the host's next update as is
            }
            Log.Message($"[Online] Battle entry: #{c.Seq} {(c.Role == Role.Owner ? "owned" : $"{SteamNative.Tag(c.Owner)}'s")} battle left ({how}), {_now - c.TriggeredAt:0} ms after the trigger");
        }

        private void ApplyPendingResult()
        {
            if (_pendingResult == null || !_lobby.IsHost || BattleEntryParty.Altered)
                return;
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            UIManager.UIState state = PersistenSingleton<UIManager>.Instance.State;
            if (engine == null || (engine.gMode != 1 && engine.gMode != 3) || (state != UIManager.UIState.FieldHUD && state != UIManager.UIState.WorldHUD))
                return;
            JSONClass body = _pendingResult;
            _pendingResult = null;
            String key = $"{_pendingResultFrom}/{body["seq"].AsInt}";
            Boolean playedHere = body["seq"] != null && _watched.TryGetValue(key, out Boolean ended) && ended;
            _watched.Remove(key);
            // This PC watched that battle to its end: the rewards (items, gil) are already in; the characters' records
            // are absolute values, applied again to match the owner's
            String what = BattleEntryParty.ApplyResult(body, playedHere);
            Log.Message($"[Online] Battle entry: results of {SteamNative.Tag(_pendingResultFrom)}'s battle {body["battle"].AsInt} {what}");
        }

        // ---- M2: per-player flee ---------------------------------------------------------------------------------

        /// <summary>Owner: that PC fled; its units are out of this battle, stop streaming to it.</summary>
        private void OnPeerFled(UInt64 peer)
        {
            Current c = _current;
            if (c == null || c.Role != Role.Owner)
                return;
            c.Participants.Remove(peer);
            c.Awaiting.Remove(peer);
            Log.Message($"[Online] Battle entry: #{c.Seq} {SteamNative.Tag(peer)} fled; {c.Participants.Count} participant(s) left");
        }

        /// <summary>Participant: the owner's answer to this PC's flee (0: its units left the battle).</summary>
        private void OnFleeResult(Byte result)
        {
            Current c = _current;
            if (c == null || c.Role != Role.Participant || result != 0)
                return;
            _fleeReturn = _prevField >= 0 ? new KeyValuePair<Int32, Int32>(_prevField, _prevEntrance) : new KeyValuePair<Int32, Int32>(_curField, _curEntrance);
            Log.Message($"[Online] Battle entry: #{c.Seq} this PC fled; back to field {_fleeReturn.Value.Key} (entrance {_fleeReturn.Value.Value}, {(_prevField >= 0 ? "the field it came from" : "the way into this field: the previous one is unknown")})");
            RequestExit(c, FF9StateGlobal.BTL_RESULT_ESCAPE, "this PC fled", "You fled.");
        }

        private Int32 _curField = -1, _curEntrance = -1, _prevField = -1, _prevEntrance = -1;
        private KeyValuePair<Int32, Int32>? _fleeReturn;

        /// <summary>The field this PC is on and the one it came from, each with the entrance it was entered by.</summary>
        private void TrackField()
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            if (engine == null || engine.gMode != 1 || PersistenSingleton<FF9StateSystem>.Instance.mode != 1)
                return;
            Int32 field = FF9StateSystem.Common.FF9.fldMapNo;
            if (field == _curField)
                return;
            if (_curField >= 0)
            {
                _prevField = _curField;
                _prevEntrance = _curEntrance;
            }
            _curField = field;
            _curEntrance = FF9StateSystem.EventState.FieldEntrance;
        }

        /// <summary>[Online] field flow (OnFieldFlow): after fleeing, go back to the field this PC came from.</summary>
        private Int32 FleeReturn(Int32 codeFlowState)
        {
            if (_fleeReturn == null || _current != null || codeFlowState > EventEngine.FLOW_STATE_WAIT)
                return codeFlowState;
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            if (engine == null || engine.gMode != 1 || PersistenSingleton<UIManager>.Instance.State != UIManager.UIState.FieldHUD
                || engine.fieldmap?.playerController == null || !engine.GetUserControl() || PersistenSingleton<SceneDirector>.Instance.IsFading)
                return codeFlowState;
            KeyValuePair<Int32, Int32> target = _fleeReturn.Value;
            _fleeReturn = null;
            if (target.Key < 0)
                return codeFlowState;
            FF9StateSystem.EventState.FieldEntrance = Math.Max(0, target.Value);
            engine.SetNextMap(target.Key);
            Log.Message($"[Online] Battle entry: fled: field {FF9StateSystem.Common.FF9.fldMapNo} -> field {target.Key}, entrance {target.Value}");
            return EventEngine.FLOW_STATE_JUMP_FIELD;
        }

        // ---- Messages -----------------------------------------------------------------------------------------

        public Boolean OnControlMessage(UInt64 peer, Byte[] d)
        {
            if ((d.Length == 4 || d.Length == 6) && d[0] == BattleEntryMessages.Presence)
            {
                PeerPresence p = new PeerPresence { Field = (UInt16)(d[1] | (d[2] << 8)), State = (Busy)d[3], At = _now };
                if (d.Length == 6)
                {
                    p.Speed = d[4];
                    p.Sfx = d[5];
                }
                _presence[peer] = p;
                return true;
            }
            if (d.Length >= 3 && d[0] == BattleWire.MsgStream)
            {
                UInt16 streamSeq = BattleEntryMessages.Seq(d);
                if (Online && IsFromMyOwner(peer, streamSeq))
                {
                    _current.LastFromOwner = _now;
                    BattleSync.OnStream(d);
                }
                return true;
            }
            if (BattleControl.IsControlMessage(d))
            {
                if (Online && _current != null && _current.Seq == BattleEntryMessages.Seq(d))
                    BattleControl.OnMessage(peer, d);
                return true;
            }
            if (!BattleEntryMessages.IsBattleEntry(d))
                return false;
            if (!Online)
                return true;
            UInt16 seq = BattleEntryMessages.Seq(d);
            switch (d[0])
            {
                case BattleEntryMessages.Pending:
                    OnPending(peer, seq, d);
                    break;
                case BattleEntryMessages.Start:
                    OnStart(peer, BattleStartData.Parse(d), d.Length);
                    break;
                case BattleEntryMessages.Ready:
                case BattleEntryMessages.Decline:
                    if (_current?.Role == Role.Owner && _current.Seq == seq && _current.Awaiting.Remove(peer))
                    {
                        if (d[0] == BattleEntryMessages.Decline)
                        {
                            _current.Participants.Remove(peer);
                            BattleControl.ReleasePeer(peer, "declined");
                        }
                        Log.Message($"[Online] Battle entry: #{seq} {SteamNative.Tag(peer)} {(d[0] == BattleEntryMessages.Ready ? $"ready, {_now - _current.StartSentAt:0} ms after Start" : $"declined (reason {d[3]})")}; {_current.Awaiting.Count} still awaited");
                    }
                    break;
                case BattleEntryMessages.Go:
                case BattleEntryMessages.Alive:
                    if (IsFromMyOwner(peer, seq))
                    {
                        _current.LastFromOwner = _now;
                        if (d[0] == BattleEntryMessages.Go)
                        {
                            Log.Message($"[Online] Battle entry: #{seq} the owner's battle runs (Go, {d.Length} bytes, {_now - _current.TriggeredAt:0} ms after this PC joined)");
                            ApplyOwnerUnits(_current, BattleEntryMessages.ReadUnits(d));
                            PlayOwnerMusic(BattleEntryMessages.ReadMusic(d));
                            List<UInt16> mine = new List<UInt16>();
                            foreach (KeyValuePair<UInt16, UInt32> kv in BattleEntryMessages.ReadControl(d))
                                if (kv.Value == (UInt32)(_lobby.LocalId & 0xFFFFFFFF))
                                    mine.Add(kv.Key);
                            BattleControl.BeginPassive(seq, peer, mine, SendTo);
                            _current.GoReceived = true;
                        }
                    }
                    break;
                case BattleEntryMessages.End:
                    if (_incoming != null && _incoming.Owner == peer && _incoming.Seq == seq)
                    {
                        Log.Message($"[Online] Battle entry: #{seq} {SteamNative.Tag(peer)}'s battle ended before this PC joined ({ResultName(d[3])})");
                        _incoming = null;
                    }
                    else if (IsFromMyOwner(peer, seq))
                    {
                        _current.LastFromOwner = _now;
                        if (d[3] != 0 && _current.GoReceived && BattleSync.LivePassive)
                        {
                            // Its last events are in: the passive battle ends through its own screens
                            _current.OwnerEnded = true;
                            _current.OwnerEndedAt = _now;
                            _current.ExitResult = d[3];
                            Log.Message($"[Online] Battle entry: #{seq} the owner's battle ended ({ResultName(d[3])}); this one ends through its own screens");
                        }
                        else
                            RequestExit(_current, d[3], d[3] == 0 ? "the owner went on without this PC" : $"the owner's battle ended: {ResultName(d[3])}", null);
                    }
                    break;
            }
            return true;
        }

        private Boolean IsFromMyOwner(UInt64 peer, UInt16 seq) => _current?.Role == Role.Participant && _current.Owner == peer && _current.Seq == seq;

        private void OnPending(UInt64 peer, UInt16 seq, Byte[] d)
        {
            UInt16 field = (UInt16)(d[3] | (d[4] << 8)), battleId = (UInt16)(d[5] | (d[6] << 8));
            if (_current != null)
            {
                Log.Message($"[Online] Battle entry: #{seq} {SteamNative.Tag(peer)}'s battle {battleId}: this PC is already in a battle; declining");
                SendTo(peer, BattleEntryMessages.Short(BattleEntryMessages.Decline, seq, 2));
                return;
            }
            _incoming = new Incoming { Owner = peer, Seq = seq, Field = field, BattleId = battleId, PendingAt = _now };
            Log.Message($"[Online] Battle entry: #{seq} {SteamNative.Tag(peer)} starts battle {battleId} on field {field}; this PC takes part ({LocalBusy()}); holding the player");
        }

        private void OnStart(UInt64 peer, BattleStartData data, Int32 bytes)
        {
            if (_incoming == null || _incoming.Owner != peer || _incoming.Seq != data.Seq)
            {
                if (_current == null)
                    _incoming = new Incoming { Owner = peer, Seq = data.Seq, Field = data.Field, BattleId = data.BattleId, PendingAt = _now };
                else
                {
                    SendTo(peer, BattleEntryMessages.Short(BattleEntryMessages.Decline, data.Seq, 2));
                    return;
                }
            }
            if (data.Speed != LocalSpeed || data.Sfx != LocalSfx)
            {
                Decline(_incoming, 3, $"battle settings differ (owner Speed {data.Speed} SFXRework {data.Sfx}, this PC {LocalSpeed} / {LocalSfx})");
                PartySync.Toast("Your battle settings (Battle Speed / SFXRework) differ from the battle owner's: you sit this battle out.");
                return;
            }
            _incoming.Start = data;
            _incoming.StartAt = _now;
            Log.Message($"[Online] Battle entry: #{data.Seq} Start from {SteamNative.Tag(peer)} ({bytes} bytes, {_now - _incoming.PendingAt:0} ms after Pending): {data.Describe()}");
        }

        public void OnBlob(BlobTransfer.Received received)
        {
            if (!BattleEntryParty.IsResultBlob(received.Blob))
                return;
            try
            {
                if (!_lobby.IsHost)
                    return;
                _pendingResult = BattleEntryParty.UnpackResult(received.Blob);
                _pendingResultFrom = received.Peer;
                Log.Message($"[Online] Battle entry: results of {SteamNative.Tag(received.Peer)}'s battle {_pendingResult["battle"].AsInt} received ({received.Blob.Length} bytes)");
            }
            catch (Exception err)
            {
                Log.Error(err, "[Online] Battle entry: battle results couldn't be read");
            }
        }

        public void OnPeerLeft(UInt64 peer)
        {
            _presence.Remove(peer);
            if (_incoming != null && _incoming.Owner == peer)
            {
                Log.Message($"[Online] Battle entry: #{_incoming.Seq} {SteamNative.Tag(peer)} left before its battle started here");
                _incoming = null;
            }
            Current c = _current;
            if (c == null)
                return;
            if (c.Role == Role.Owner && (c.Participants.Remove(peer) | c.Awaiting.Remove(peer)))
            {
                Log.Message($"[Online] Battle entry: #{c.Seq} participant {SteamNative.Tag(peer)} left; its character stays in this battle (driven here)");
                BattleControl.ReleasePeer(peer, "left the session");
            }
            else if (c.Role == Role.Participant && c.Owner == peer)
                RequestExit(c, 0, $"the owner {SteamNative.Tag(peer)} left the session", "The battle's owner left; leaving the battle.");
        }

        private void SendTo(UInt64 peer, Byte[] message) => _transport.Send(peer, TransportChannel.Control, message, message.Length, true);

        public static Boolean IsResultBlob(Byte[] blob) => BattleEntryParty.IsResultBlob(blob);

        public String Describe()
        {
            Current c = _current;
            String state = c != null ? $"{c.Role} #{c.Seq}{(c.Role == Role.Owner ? $", awaiting {c.Awaiting.Count}/{c.Participants.Count}" : "")}" : _incoming != null ? $"incoming #{_incoming.Seq}" : "idle";
            String stream = BattleSync.StatusLine;
            String who = c == null ? "" : c.Role == Role.Owner ? $", owner this PC, participants [{String.Join(" ", c.Participants.ConvertAll(p => SteamNative.Tag(p)).ToArray())}]" : $", owner {SteamNative.Tag(c.Owner)}";
            return $"Battle: {state}{who}{(stream != null ? $"\n  {stream}" : "")}\n  U free characters join the host's battles {(FreeCharactersJoinHost ? "ON" : "off")}, 1 encounter now, 7 Start {(StartAtTrigger ? "at the trigger" : "after init")}; settings Speed {LocalSpeed} SFXRework {LocalSfx}";
        }

        private static String ResultName(Byte result)
        {
            switch (result)
            {
                case 0: return "cancelled";
                case FF9StateGlobal.BTL_RESULT_VICTORY: return "victory";
                case FF9StateGlobal.BTL_RESULT_VICTORY_NO_POSE: return "victory (no pose)";
                case FF9StateGlobal.BTL_RESULT_DEFEAT: return "defeat";
                case FF9StateGlobal.BTL_RESULT_ESCAPE: return "fled";
                case FF9StateGlobal.BTL_RESULT_INTERRUPTION: return "interrupted";
                case FF9StateGlobal.BTL_RESULT_GAMEOVER: return "game over";
                case FF9StateGlobal.BTL_RESULT_ENEMY_FLEE: return "enemies fled";
                default: return $"result {result}";
            }
        }
    }
}
