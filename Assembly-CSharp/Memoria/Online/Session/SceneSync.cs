using Assets.Scripts.Common;
using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Memoria.Online.Session
{
    /// <summary>
    /// Spike E (docs/online/spike-e-dialog-sync.md): can every PC run a story scene from its own scripts, with the
    /// scene owner's PC pacing the dialog windows? Debug only (OnlineDebug, Ctrl+Shift+2 starts / ends a party scene;
    /// whoever presses it owns it).
    ///
    /// While a scene runs, on every PC:
    /// - **Ready gate per field load.** After a field's opening scripts, its event loop is held
    ///   (<see cref="HoldFieldEvents"/>, HonoluluFieldMain) until the owner sends Go for that field. The owner sends Go
    ///   once every follower has loaded the same field (or after <see cref="GateTimeoutMs"/>) and starts itself half an
    ///   RTT later, so all PCs start the field's scene together. The "scene clock" (ms since Go) is comparable.
    /// - **Windows are numbered** in opening order since the field load (seq). Each open / page / close is logged.
    /// - **The owner paces them.** A follower ignores confirm / cancel / choice input on script windows. When the
    ///   owner's player advances or closes a window, the owner sends (seq, page, choice); the follower applies it to its
    ///   own window with that seq (waiting for its script to open it if behind). Windows closed by a timer ([TIME]) or
    ///   by the script (CloseWindow) close locally on each PC; the owner's notice is only logged for timing.
    /// - **Field jumps and battles** a script starts are logged with the scene clock (owner: also sent), so the doc can
    ///   say how far a follower's script runs ahead. Nothing is blocked.
    /// - **Triggers are the owner's.** What the owner's player starts by talking, checking or walking into a region
    ///   (EventCollision -> EventEngine.Request level 1) is sent (object uid, tag) and started the same way on every
    ///   follower; a follower's own player triggers nothing (<see cref="PlayerRequest"/>). So a scene that begins with a
    ///   talk (the festival: Mogwin) starts on every PC from the owner's talk.
    ///
    /// Control messages (reliable): 40 Start, 41 Loaded (follower -> owner), 42 Go, 43 Window, 44 Jump, 45 End,
    /// 46 Trigger.
    /// </summary>
    public sealed class SceneSync
    {
        public const Byte MsgStart = 40, MsgLoaded = 41, MsgGo = 42, MsgWindow = 43, MsgJump = 44, MsgEnd = 45, MsgTrigger = 46;
        public const Byte KindInput = 1, KindTimer = 2, KindScript = 3, KindOther = 4, KindRemote = 5;
        private const Double GateTimeoutMs = 10000, FollowerGateTimeoutMs = 20000, StuckInputMs = 30000;

        public static SceneSync Instance { get; private set; }

        private readonly SteamTransport _transport;
        private readonly SteamLobby _lobby;
        private readonly Func<UInt64, Double> _rttOf;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static Double NowMs => Clock.Elapsed.TotalMilliseconds;

        // Scene
        private UInt16 _sceneId;
        private UInt64 _owner; // 0: no scene
        private Boolean IsOwner => _owner != 0 && _owner == _lobby.LocalId;
        public static Boolean Active => Instance != null && Instance._owner != 0;
        public static Boolean Following => Active && !Instance.IsOwner;

        // Field and gate
        private System.Object _fieldRef;
        private Int32 _field = -1;
        private UInt16 _fieldIndex; // field loads since the scene started (both sides count the same if they follow)
        private Boolean _gated;
        private Double _gateSince, _fieldT0 = -1, _ownerStartAt = -1;
        private readonly Dictionary<UInt64, Int32> _peerLoaded = new Dictionary<UInt64, Int32>(); // owner: follower -> field it loaded and waits on
        private Int32 _goField = -1; // follower: the last Go not used yet

        // Windows
        private sealed class Win
        {
            public Int32 Seq, WinId, TextId, Pages, Hides;
            public Boolean Choices, Closed;
            public Double OpenMs, CloseMs = -1;
            public Byte CloseKind;
            public Int32 Choice = -1;
            public Dialog Dialog;
            // follower: the owner's view of it
            public Double OwnerOpenMs = -1, OwnerCloseMs = -1;
            public Byte OwnerKind;
            public Boolean Reported;
        }

        private sealed class Pending
        {
            public Int32 Seq, TextId, Page, Choice;
            public Double ArrivedMs;
        }

        private Int32 _seq;
        private readonly Dictionary<Dialog, Win> _byDialog = new Dictionary<Dialog, Win>();
        private readonly Dictionary<Int32, Win> _bySeq = new Dictionary<Int32, Win>();
        private readonly List<Pending> _pending = new List<Pending>();
        private Int32 _ownerMaxSeq = -1;
        private Boolean _applying;
        [ThreadStatic] public static Boolean InInput;

        // Totals for the summary
        private Int32 _windows, _remoteApplied, _choicesApplied, _timerOrScript, _waitedBehind, _droppedRemote, _stuckReleased;
        private readonly List<Double> _inputDrifts = new List<Double>();
        private readonly List<Double> _localDrifts = new List<Double>();

        internal SceneSync(SteamTransport transport, SteamLobby lobby, Func<UInt64, Double> rttOf)
        {
            _transport = transport;
            _lobby = lobby;
            _rttOf = rttOf;
            Instance = this;
        }

        // ---- Start / end ------------------------------------------------------------------------------------------

        /// <summary>Debug key: start a party scene owned by this PC, or end the running one (owner only).</summary>
        public void ToggleDebug()
        {
            if (_owner != 0)
            {
                if (IsOwner)
                    End("ended by the owner (debug key)", true);
                else
                    Log.Message("[Online] Scene: only the owner ends the scene");
                return;
            }
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            if (engine == null || engine.gMode != 1 || _transport.Peers.Count == 0)
            {
                Log.Message("[Online] Scene: start on a field, with another PC in the session");
                return;
            }
            _sceneId = (UInt16)(NowMs % 60000 + 1);
            Begin(_lobby.LocalId);
            Byte[] m = new Byte[5];
            m[0] = MsgStart;
            W16(m, 1, _sceneId);
            W16(m, 3, _field);
            Broadcast(m);
            Log.Message($"[Online] Scene {_sceneId}: started as owner on field {_field}; arm: every PC holds its scripts at each field load until Go");
            // This field is already running: gate it now too, so everyone starts the rest of it together
            Gate("scene start");
        }

        private void Begin(UInt64 owner)
        {
            _owner = owner;
            _fieldIndex = 0;
            _peerLoaded.Clear();
            _goField = -1;
            _windows = _remoteApplied = _choicesApplied = _timerOrScript = _waitedBehind = _droppedRemote = _stuckReleased = 0;
            _triggersSent = _triggersRun = _triggersBlocked = 0;
            _triggers.Clear();
            _inputDrifts.Clear();
            _localDrifts.Clear();
            ResetWindows();
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            _fieldRef = engine?.fieldmap;
            _field = engine != null && engine.gMode == 1 ? FF9StateSystem.Common.FF9.fldMapNo : -1;
        }

        private void End(String why, Boolean send)
        {
            if (_owner == 0)
                return;
            if (send && IsOwner)
            {
                Byte[] m = new Byte[3];
                m[0] = MsgEnd;
                W16(m, 1, _sceneId);
                Broadcast(m);
            }
            ReportOpenWindows();
            Log.Message($"[Online] Scene {_sceneId}: END ({why}). {Summary()}");
            _owner = 0;
            _gated = false;
            _pending.Clear();
            _triggers.Clear();
        }

        public String Summary()
        {
            return $"triggers sent {_triggersSent} / run from owner {_triggersRun} / own ignored {_triggersBlocked}; windows {_windows}, applied from owner {_remoteApplied} (choices {_choicesApplied}, waited for own script {_waitedBehind}, dropped {_droppedRemote}), " +
                   $"timer/script closes {_timerOrScript}, stuck input released {_stuckReleased}; " +
                   $"owner-closed drift ms {Stats(_inputDrifts)}; local-close drift ms {Stats(_localDrifts)}";
        }

        private static String Stats(List<Double> v)
        {
            if (v.Count == 0)
                return "n/a";
            Double min = Double.MaxValue, max = Double.MinValue, sum = 0, absMax = 0;
            foreach (Double d in v)
            {
                min = Math.Min(min, d);
                max = Math.Max(max, d);
                sum += d;
                absMax = Math.Max(absMax, Math.Abs(d));
            }
            return $"n={v.Count} min {min:0} avg {sum / v.Count:0} max {max:0} |max| {absMax:0}";
        }

        public static String StatusLine()
        {
            if (!Active)
                return null;
            SceneSync s = Instance;
            return $"Party scene {s._sceneId} ({(s.IsOwner ? "owner" : "following " + SteamNative.Tag(s._owner))}): field {s._field} #{s._fieldIndex}{(s._gated ? " HELD" : "")}, " +
                   $"windows {s._seq}, from owner {s._remoteApplied}, pending {s._pending.Count}";
        }

        // ---- Per frame ---------------------------------------------------------------------------------------------

        public void Update(Boolean playing)
        {
            if (_owner == 0)
                return;
            if (!playing)
            {
                End("session not playing", false);
                return;
            }
            if (!IsOwner && !_transport.Peers.Contains(_owner))
            {
                End("the owner left", false);
                return;
            }
            CheckField();
            Double now = NowMs;
            if (_gated)
            {
                if (IsOwner)
                {
                    if (_ownerStartAt >= 0)
                    {
                        if (now >= _ownerStartAt)
                            Release("Go sent, after half an RTT");
                    }
                    else
                    {
                        Boolean all = true;
                        foreach (UInt64 p in _transport.Peers)
                            if (!_peerLoaded.TryGetValue(p, out Int32 f) || f != _field)
                                all = false;
                        if (all || now - _gateSince > GateTimeoutMs)
                            SendGo(all ? "everyone loaded" : $"gate timeout ({GateTimeoutMs / 1000:0} s): {MissingPeers()} not here");
                    }
                }
                else
                {
                    if (_goField == _field)
                    {
                        _goField = -1;
                        Release("Go");
                    }
                    else if (now - _gateSince > FollowerGateTimeoutMs)
                    {
                        Release($"no Go after {FollowerGateTimeoutMs / 1000:0} s (released so it isn't stuck)");
                    }
                }
            }
            if (!IsOwner)
            {
                ApplyTriggers(now);
                ApplyPending(now);
            }
        }

        private String MissingPeers()
        {
            List<String> tags = new List<String>();
            foreach (UInt64 p in _transport.Peers)
                if (!_peerLoaded.TryGetValue(p, out Int32 f) || f != _field)
                    tags.Add(SteamNative.Tag(p) + (_peerLoaded.ContainsKey(p) ? $" (on {_peerLoaded[p]})" : ""));
            return String.Join(", ", tags.ToArray());
        }

        /// <summary>A new field (or a field reloaded after a battle): new window numbering, and a gate.</summary>
        private void CheckField()
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            if (engine == null || engine.gMode != 1 || !engine.HasObjectList)
                return;
            if (ReferenceEquals(engine.fieldmap, _fieldRef) || engine.fieldmap == null)
                return;
            _fieldRef = engine.fieldmap;
            Int32 previous = _field;
            _field = FF9StateSystem.Common.FF9.fldMapNo;
            _fieldIndex++;
            ReportOpenWindows();
            ResetWindows();
            Log.Message($"[Online] Scene {_sceneId}: field load #{_fieldIndex}: {previous} -> {_field}");
            if (!IsOwner)
            {
                Byte[] m = new Byte[5];
                m[0] = MsgLoaded;
                W16(m, 1, _sceneId);
                W16(m, 3, _field);
                _transport.Send(_owner, TransportChannel.Control, m, m.Length, true);
            }
            Gate("field load");
        }

        private void Gate(String why)
        {
            _gated = true;
            _gateSince = NowMs;
            _ownerStartAt = -1;
            _fieldT0 = -1;
            Log.Message($"[Online] Scene {_sceneId}: HOLD scripts on field {_field} ({why})");
        }

        private void SendGo(String why)
        {
            Byte[] m = new Byte[7];
            m[0] = MsgGo;
            W16(m, 1, _sceneId);
            W16(m, 3, _field);
            W16(m, 5, _fieldIndex);
            Double halfRtt = 0;
            foreach (UInt64 p in _transport.Peers)
            {
                if (_peerLoaded.TryGetValue(p, out Int32 f) && f == _field)
                {
                    _transport.Send(p, TransportChannel.Control, m, m.Length, true);
                    _peerLoaded.Remove(p);
                    halfRtt = Math.Max(halfRtt, Math.Max(0, _rttOf(p)) / 2);
                }
            }
            _ownerStartAt = NowMs + halfRtt;
            Log.Message($"[Online] Scene {_sceneId}: Go for field {_field} ({why}); owner starts in {halfRtt:0} ms");
        }

        private void Release(String why)
        {
            Double held = NowMs - _gateSince;
            _gated = false;
            _ownerStartAt = -1;
            _fieldT0 = NowMs;
            Log.Message($"[Online] Scene {_sceneId}: RUN field {_field} after {held:0} ms held ({why}); scene clock 0");
        }

        /// <summary>[Online] HonoluluFieldMain, before the field's event loop: true = skip it this frame (ready gate).</summary>
        public static Boolean HoldFieldEvents()
        {
            SceneSync s = Instance;
            if (s == null || s._owner == 0)
                return false;
            s.CheckField();
            return s._gated;
        }

        private Double SceneMs => _fieldT0 >= 0 ? NowMs - _fieldT0 : -1;

        // ---- Field jumps and battles started by a script -------------------------------------------------------------

        /// <summary>[Online] HonoluluFieldMain, the raw flow state of the field's event loop.</summary>
        public static void OnFieldFlow(Int32 flow)
        {
            SceneSync s = Instance;
            if (s == null || s._owner == 0 || s._gated)
                return;
            Byte kind;
            Int32 target = FF9StateSystem.Field.FF9Field.loc.map.nextMapNo; // MAPJUMP and ENCOUNT both set it (EventEngine.FF9ChangeMap)
            if (flow == EventEngine.FLOW_STATE_JUMP_FIELD)
            {
                kind = 1;
            }
            else if (flow == EventEngine.FLOW_STATE_JUMP_BATTLE)
            {
                kind = 2;
            }
            else if (flow == EventEngine.FLOW_STATE_JUMP_WORLD_MAP)
            {
                kind = 3;
            }
            else
            {
                return;
            }
            s.OnJump(kind, target);
        }

        private void OnJump(Byte kind, Int32 target)
        {
            String what = kind == 1 ? $"field jump to {target}" : kind == 2 ? $"battle {target}" : "world map";
            Double t = SceneMs;
            Log.Message($"[Online] Scene {_sceneId}: script {what} on field {_field} #{_fieldIndex} at scene {t:0} ms (windows opened here: {_seq})");
            if (!IsOwner)
                return;
            Byte[] m = new Byte[14];
            m[0] = MsgJump;
            W16(m, 1, _sceneId);
            W16(m, 3, _fieldIndex);
            m[5] = kind;
            W16(m, 6, target);
            W32(m, 8, (Int32)t);
            W16(m, 12, _seq);
            Broadcast(m);
        }

        // ---- Triggers: the owner's player starts things, followers start the same -----------------------------------

        private sealed class Trigger
        {
            public Int32 FieldIndex, Uid, Tag, OwnerMs;
            public Double ArrivedMs;
        }

        private const Double TriggerRetryMs = 3000;
        private readonly List<Trigger> _triggers = new List<Trigger>();
        private Boolean _mirroring;
        private Double _lastBlockedLog;
        private Int32 _triggersSent, _triggersRun, _triggersBlocked;

        /// <summary>
        /// [Online] EventCollision: the player talks to / checks / walks into an object (EventEngine.Request at level 1).
        /// In a party scene the owner's trigger is also sent to every follower; a follower's own player triggers nothing.
        /// </summary>
        public static Boolean PlayerRequest(EventEngine engine, Obj obj, Int32 tag)
        {
            SceneSync s = Instance;
            if (s == null || s._owner == 0 || s._mirroring || obj == null)
                return engine.Request(obj, 1, tag, false);
            if (!s.IsOwner)
            {
                // Only report a trigger that would have started something (a region asks every frame)
                if (engine.GetIP(obj.sid, tag, obj.ebData) != engine.nil && 1 < obj.level)
                {
                    s._triggersBlocked++;
                    if (NowMs - s._lastBlockedLog > 2000)
                    {
                        s._lastBlockedLog = NowMs;
                        Log.Message($"[Online] Scene {s._sceneId}: this player's trigger ignored (uid {obj.uid} tag {tag}): only the owner starts things in a party scene");
                    }
                }
                return false;
            }
            Boolean started = engine.Request(obj, 1, tag, false);
            if (started && !s._gated)
            {
                Double t = s.SceneMs;
                Byte[] m = new Byte[12];
                m[0] = MsgTrigger;
                W16(m, 1, s._sceneId);
                W16(m, 3, s._fieldIndex);
                W16(m, 5, obj.uid);
                m[7] = (Byte)tag;
                W32(m, 8, (Int32)t);
                s.Broadcast(m);
                s._triggersSent++;
                Log.Message($"[Online] Scene {s._sceneId}: TRIGGER uid {obj.uid} tag {tag} ({TagName(tag)}) on field {s._field} at scene {t:0} ms: sent");
            }
            return started;
        }

        private static String TagName(Int32 tag) => tag == 3 ? "talk" : tag == 2 ? "region / push" : tag == 8 ? "special" : "tag " + tag;

        /// <summary>Follower: start the owner's triggers here (once this field runs; retried while the object is busy).</summary>
        private void ApplyTriggers(Double now)
        {
            if (_gated || _triggers.Count == 0)
                return;
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            if (engine == null || engine.gMode != 1 || !engine.HasObjectList)
                return;
            for (Int32 i = 0; i < _triggers.Count; i++)
            {
                Trigger t = _triggers[i];
                if (t.FieldIndex != _fieldIndex)
                {
                    if (t.FieldIndex < _fieldIndex || now - t.ArrivedMs > TriggerRetryMs)
                    {
                        Log.Message($"[Online] Scene {_sceneId}: owner's trigger uid {t.Uid} tag {t.Tag} was for field load #{t.FieldIndex}, here #{_fieldIndex}: dropped");
                        _triggers.RemoveAt(i--);
                    }
                    continue; // the owner is ahead by a field: wait for this PC's load
                }
                Obj obj = engine.FindObjByUID(t.Uid);
                Boolean started = false;
                if (obj != null)
                {
                    if (t.Tag == 3 || t.Tag == 8)
                    {
                        Actor player = engine.getActiveActorByUID(engine.GetControlUID());
                        if (player != null)
                            player.listener = obj.uid;
                    }
                    _mirroring = true;
                    try
                    {
                        started = engine.Request(obj, 1, t.Tag, false);
                    }
                    finally
                    {
                        _mirroring = false;
                    }
                }
                if (started)
                {
                    _triggersRun++;
                    Log.Message($"[Online] Scene {_sceneId}: TRIGGER uid {t.Uid} tag {t.Tag} ({TagName(t.Tag)}) started here at scene {SceneMs:0} ms (owner {t.OwnerMs} ms, {now - t.ArrivedMs:0} ms after it arrived, rtt {RttOwner():0})");
                    _triggers.RemoveAt(i--);
                }
                else if (now - t.ArrivedMs > TriggerRetryMs)
                {
                    Log.Message($"[Online] Scene {_sceneId}: owner's trigger uid {t.Uid} tag {t.Tag} couldn't start here ({(obj == null ? "no such object" : $"object busy, level {obj.level}")}) for {TriggerRetryMs / 1000:0} s: dropped");
                    _triggers.RemoveAt(i--);
                }
            }
        }

        // ---- Windows -----------------------------------------------------------------------------------------------

        private void ResetWindows()
        {
            _seq = 0;
            _ownerMaxSeq = -1;
            _ownerTimes.Clear();
            _byDialog.Clear();
            _bySeq.Clear();
            if (_pending.Count > 0)
            {
                foreach (Pending p in _pending)
                    Log.Message($"[Online] Scene {_sceneId}: owner's close of window {p.Seq} (text {p.TextId}) never applied: this PC never opened it on that field");
                _droppedRemote += _pending.Count;
            }
            _pending.Clear();
        }

        /// <summary>[Online] ETb.NewMesWin, a script window was created.</summary>
        public static void OnWindowOpen(Dialog dialog, Int32 textId, Int32 winId, Int32 flags)
        {
            SceneSync s = Instance;
            if (s == null || s._owner == 0 || dialog == null)
                return;
            s.CheckField();
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            Win w = new Win
            {
                Seq = ++s._seq,
                WinId = winId,
                TextId = textId,
                Pages = Math.Max(1, dialog.SubPageCount),
                Choices = dialog.HasChoices,
                OpenMs = s.SceneMs,
                Dialog = dialog,
            };
            s._byDialog[dialog] = w;
            s._bySeq[w.Seq] = w;
            s._windows++;
            if (!s.IsOwner)
                TakeOwnerTimes(dialog);
            String who = engine?.gCur != null ? $"sid {engine.gCur.sid} uid {engine.gCur.uid} ip {engine.gCur.ip}" : "?";
            Log.Message($"[Online] Scene {s._sceneId}: OPEN  w{w.Seq} win {winId} text {textId} flags {flags} pages {w.Pages}{(w.Choices ? $" choices {dialog.ChoiceNumber}" : "")} " +
                        $"endMode {dialog.EndMode} at scene {w.OpenMs:0} ms ({who}){(s._gated ? " [while HELD]" : "")}");
        }

        /// <summary>[Online] Dialog.OnKeyConfirm / OnKeyCancel / OnItemSelect: true = a follower ignores this input.</summary>
        public static Boolean BlockInput(Dialog dialog)
        {
            SceneSync s = Instance;
            if (s == null || s._owner == 0 || s.IsOwner || s._applying || dialog == null || !dialog.IsETbDialog)
                return false;
            if (!s._byDialog.TryGetValue(dialog, out Win w))
                return false; // opened before the scene: the player closes it
            // Not stuck: if the owner went past this window without closing it by input, or after a long time, input is back
            if (s._ownerMaxSeq > w.Seq || (w.OpenMs >= 0 && s.SceneMs - w.OpenMs > StuckInputMs))
            {
                if (!w.Reported)
                {
                    w.Reported = true;
                    s._stuckReleased++;
                    Log.Message($"[Online] Scene {s._sceneId}: w{w.Seq} (text {w.TextId}) input given back to this player (owner at w{s._ownerMaxSeq}, open {s.SceneMs - w.OpenMs:0} ms)");
                }
                return false;
            }
            return true;
        }

        /// <summary>[Online] Dialog.Hide: a page advance or the close of a window.</summary>
        public static void OnHide(Dialog dialog, Boolean lastPage)
        {
            SceneSync s = Instance;
            if (s == null || s._owner == 0 || dialog == null || !s._byDialog.TryGetValue(dialog, out Win w) || w.Closed)
                return;
            Byte kind = s._applying ? KindRemote : InInput ? KindInput : dialog.IsClosedByScript ? KindScript : dialog.EndMode > 0 ? KindTimer : KindOther;
            Int32 page = w.Hides++;
            Double t = s.SceneMs;
            if (w.Choices && lastPage)
                w.Choice = dialog.SelectChoice;
            Log.Message($"[Online] Scene {s._sceneId}: {(lastPage ? "CLOSE" : "PAGE ")} w{w.Seq} text {w.TextId} page {page} by {KindName(kind)}{(w.Choices && lastPage ? $" choice {w.Choice}" : "")} at scene {t:0} ms (open {t - w.OpenMs:0} ms)");
            if (lastPage)
            {
                w.Closed = true;
                w.CloseMs = t;
                w.CloseKind = kind;
                s._byDialog.Remove(dialog);
                if (kind == KindTimer || kind == KindScript || kind == KindOther)
                    s._timerOrScript++;
            }
            if (s.IsOwner)
            {
                Byte[] m = new Byte[22];
                m[0] = MsgWindow;
                W16(m, 1, s._sceneId);
                W16(m, 3, s._fieldIndex);
                W16(m, 5, w.Seq);
                W16(m, 7, w.TextId);
                m[9] = (Byte)page;
                m[10] = (Byte)(lastPage ? 1 : 0);
                m[11] = (Byte)(SByte)(w.Choices && lastPage ? w.Choice : -1);
                m[12] = kind;
                W32(m, 13, (Int32)w.OpenMs);
                W32(m, 17, (Int32)t);
                s.Broadcast(m);
            }
            else if (lastPage)
            {
                s.ReportWindow(w);
            }
        }

        private static String KindName(Byte k)
        {
            switch (k)
            {
                case KindInput: return "player input";
                case KindTimer: return "timer";
                case KindScript: return "script";
                case KindRemote: return "owner";
                default: return "other";
            }
        }

        /// <summary>Follower: the owner's player advanced or closed window seq; do it here when this PC's script has opened it.</summary>
        private void ApplyPending(Double now)
        {
            for (Int32 i = 0; i < _pending.Count; i++)
            {
                Pending p = _pending[i];
                if (!_bySeq.TryGetValue(p.Seq, out Win w))
                    continue; // this PC's script hasn't opened it yet
                if (w.Closed)
                {
                    Log.Message($"[Online] Scene {_sceneId}: owner closed w{p.Seq} by input, but it's already closed here ({KindName(w.CloseKind)})");
                    _droppedRemote++;
                    _pending.RemoveAt(i--);
                    continue;
                }
                if (w.TextId != p.TextId)
                    Log.Message($"[Online] Scene {_sceneId}: w{p.Seq} DIVERGED: owner text {p.TextId}, here text {w.TextId} (applied anyway)");
                if (w.Hides > p.Page)
                {
                    _pending.RemoveAt(i--);
                    continue;
                }
                if (w.Hides < p.Page)
                    continue; // an earlier page's advance is still queued
                Dialog d = w.Dialog;
                if (d == null || (d.CurrentState != Dialog.State.TextAnimation && d.CurrentState != Dialog.State.CompleteAnimation))
                    continue; // still opening
                Double late = now - p.ArrivedMs;
                if (late > 50)
                    _waitedBehind++;
                _applying = true;
                try
                {
                    if (d.CurrentState == Dialog.State.TextAnimation)
                        d.FinishTextForSync();
                    if (w.Choices && p.Choice >= 0)
                    {
                        d.SelectChoice = p.Choice;
                        _choicesApplied++;
                    }
                    d.ForceClose();
                }
                finally
                {
                    _applying = false;
                }
                _remoteApplied++;
                Log.Message($"[Online] Scene {_sceneId}: applied owner's input to w{p.Seq} page {p.Page}{(p.Choice >= 0 ? $" choice {p.Choice}" : "")} {late:0} ms after it arrived (rtt {RttOwner():0} ms)");
                _pending.RemoveAt(i--);
            }
        }

        private Double RttOwner() => Math.Max(0, _rttOf(_owner));

        /// <summary>Follower: one line per window once both sides' times are known. Drift = here - owner, on the scene clock.</summary>
        private void ReportWindow(Win w)
        {
            if (w.Reported || !w.Closed || w.OwnerCloseMs < 0)
                return;
            w.Reported = true;
            Double openDrift = w.OpenMs - w.OwnerOpenMs, closeDrift = w.CloseMs - w.OwnerCloseMs;
            if (w.CloseKind == KindRemote)
                _inputDrifts.Add(closeDrift);
            else
                _localDrifts.Add(closeDrift);
            Log.Message($"[Online] Scene {_sceneId}: TIMING w{w.Seq} text {w.TextId}: open owner {w.OwnerOpenMs:0} / here {w.OpenMs:0} ({openDrift:+0;-0;0}), " +
                        $"close owner {w.OwnerCloseMs:0} ({KindName(w.OwnerKind)}) / here {w.CloseMs:0} ({KindName(w.CloseKind)}) ({closeDrift:+0;-0;0}) ms; rtt {RttOwner():0}");
        }

        private void ReportOpenWindows()
        {
            foreach (Win w in _bySeq.Values)
                if (!w.Closed)
                    Log.Message($"[Online] Scene {_sceneId}: w{w.Seq} text {w.TextId} still open when the field changed or the scene ended");
        }

        // ---- Messages ----------------------------------------------------------------------------------------------

        public Boolean OnControlMessage(UInt64 peer, Byte[] d)
        {
            if (d.Length < 3 || d[0] < MsgStart || d[0] > MsgTrigger)
                return false;
            UInt16 scene = (UInt16)R16(d, 1);
            switch (d[0])
            {
                case MsgStart:
                {
                    if (d.Length < 5)
                        return true;
                    if (_owner != 0)
                    {
                        Log.Message($"[Online] Scene: {SteamNative.Tag(peer)} started scene {scene} while scene {_sceneId} runs here: ignored (spike: no arbitration)");
                        return true;
                    }
                    _sceneId = scene;
                    Begin(peer);
                    Int32 field = R16(d, 3);
                    Log.Message($"[Online] Scene {_sceneId}: following {SteamNative.Tag(peer)} (its field {field}, here {_field})");
                    // This field is running: hold it, and tell the owner where we are (Go if it's the same field)
                    Byte[] m = new Byte[5];
                    m[0] = MsgLoaded;
                    W16(m, 1, _sceneId);
                    W16(m, 3, _field);
                    _transport.Send(_owner, TransportChannel.Control, m, m.Length, true);
                    Gate("scene start");
                    return true;
                }
                case MsgEnd:
                    if (scene == _sceneId && peer == _owner)
                        End("ended by the owner", false);
                    return true;
            }
            if (_owner == 0 || scene != _sceneId)
                return true;
            switch (d[0])
            {
                case MsgLoaded:
                    if (IsOwner && d.Length >= 5)
                    {
                        Int32 field = R16(d, 3);
                        _peerLoaded[peer] = field;
                        Log.Message($"[Online] Scene {_sceneId}: {SteamNative.Tag(peer)} loaded field {field} (here {_field}{(_gated ? ", held" : $", running for {SceneMs:0} ms")})");
                        if (!_gated && field == _field)
                        {
                            // Late: this field already runs here. Go now; the follower starts behind (logged)
                            Byte[] m = new Byte[7];
                            m[0] = MsgGo;
                            W16(m, 1, _sceneId);
                            W16(m, 3, _field);
                            W16(m, 5, _fieldIndex);
                            _transport.Send(peer, TransportChannel.Control, m, m.Length, true);
                            _peerLoaded.Remove(peer);
                            Log.Message($"[Online] Scene {_sceneId}: late Go to {SteamNative.Tag(peer)}: it starts ~{SceneMs:0} ms behind");
                        }
                    }
                    return true;
                case MsgGo:
                    if (!IsOwner && d.Length >= 7)
                    {
                        Int32 field = R16(d, 3);
                        Log.Message($"[Online] Scene {_sceneId}: Go for field {field} (here {_field}{(_gated ? ", held" : ", not held")})");
                        _goField = field;
                        if (_gated && field == _field)
                        {
                            _goField = -1;
                            Release("Go");
                        }
                    }
                    return true;
                case MsgWindow:
                    if (!IsOwner && d.Length >= 21)
                        OnOwnerWindow(d);
                    return true;
                case MsgTrigger:
                    if (!IsOwner && d.Length >= 12)
                    {
                        _triggers.Add(new Trigger { FieldIndex = R16(d, 3), Uid = R16(d, 5), Tag = d[7], OwnerMs = R32(d, 8), ArrivedMs = NowMs });
                        ApplyTriggers(NowMs);
                    }
                    return true;
                case MsgJump:
                    if (!IsOwner && d.Length >= 14)
                    {
                        Byte kind = d[5];
                        Int32 target = R16(d, 6);
                        Int32 t = R32(d, 8);
                        Int32 seq = R16(d, 12);
                        String what = kind == 1 ? $"field jump to {target}" : kind == 2 ? $"battle {target}" : "world map";
                        Log.Message($"[Online] Scene {_sceneId}: OWNER's script {what} at its scene {t} ms after {seq} windows (here: scene {SceneMs:0} ms, {_seq} windows, field load #{_fieldIndex}, owner's #{R16(d, 3)})");
                    }
                    return true;
            }
            return true;
        }

        private void OnOwnerWindow(Byte[] d)
        {
            Int32 fieldIndex = R16(d, 3);
            Int32 seq = R16(d, 5);
            Int32 textId = R16(d, 7);
            Int32 page = d[9];
            Boolean last = d[10] != 0;
            Int32 choice = (SByte)d[11];
            Byte kind = d[12];
            Int32 openMs = R32(d, 13), closeMs = R32(d, 17);
            if (fieldIndex != _fieldIndex)
            {
                Log.Message($"[Online] Scene {_sceneId}: owner's w{seq} (text {textId}) is for its field load #{fieldIndex}, here #{_fieldIndex}: ignored");
                return;
            }
            _ownerMaxSeq = Math.Max(_ownerMaxSeq, kind == KindInput ? seq - 1 : seq);
            if (_bySeq.TryGetValue(seq, out Win w) && last)
            {
                w.OwnerOpenMs = openMs;
                w.OwnerCloseMs = closeMs;
                w.OwnerKind = kind;
            }
            else if (last)
            {
                // Not opened here yet: keep the owner's times for when it is
                w = new Win { Seq = seq, TextId = textId, OpenMs = -1, OwnerOpenMs = openMs, OwnerCloseMs = closeMs, OwnerKind = kind };
                _ownerTimes[seq] = w;
            }
            if (kind == KindInput)
            {
                _pending.Add(new Pending { Seq = seq, TextId = textId, Page = page, Choice = last ? choice : -1, ArrivedMs = NowMs });
                ApplyPending(NowMs);
            }
            if (_bySeq.TryGetValue(seq, out w))
                ReportWindow(w);
        }

        private readonly Dictionary<Int32, Win> _ownerTimes = new Dictionary<Int32, Win>();

        /// <summary>Follower: a window opened after the owner's notice for it arrived picks up the owner's times.</summary>
        private static void TakeOwnerTimes(Dialog dialog)
        {
            SceneSync s = Instance;
            if (s == null || s._owner == 0 || s.IsOwner || !s._byDialog.TryGetValue(dialog, out Win w))
                return;
            if (s._ownerTimes.TryGetValue(w.Seq, out Win o))
            {
                w.OwnerOpenMs = o.OwnerOpenMs;
                w.OwnerCloseMs = o.OwnerCloseMs;
                w.OwnerKind = o.OwnerKind;
                s._ownerTimes.Remove(w.Seq);
            }
        }

        // ---- Wire helpers ------------------------------------------------------------------------------------------

        private void Broadcast(Byte[] m)
        {
            foreach (UInt64 p in _transport.Peers)
                _transport.Send(p, TransportChannel.Control, m, m.Length, true);
        }

        private static void W16(Byte[] b, Int32 o, Int32 v) { b[o] = (Byte)v; b[o + 1] = (Byte)(v >> 8); }
        private static void W32(Byte[] b, Int32 o, Int32 v) { b[o] = (Byte)v; b[o + 1] = (Byte)(v >> 8); b[o + 2] = (Byte)(v >> 16); b[o + 3] = (Byte)(v >> 24); }
        private static Int32 R16(Byte[] b, Int32 o) => b[o] | (b[o + 1] << 8);
        private static Int32 R32(Byte[] b, Int32 o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
    }
}
