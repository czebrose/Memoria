using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Memoria.Online.Bodies
{
    /// <summary>Who a scene shows by default (docs/online/step2-m3-own-leader.md, sections 9 and 11).</summary>
    public enum SceneMode
    {
        /// <summary>The player entry shows the acting character; characters players control are shown once (their ghost or their entry).</summary>
        Player,
        /// <summary>Every entry shows the body the field wrote it with (a story beat with the field's cast).</summary>
        Cast,
    }

    /// <summary>
    /// Step 2 / M3: one shared world (docs/online/step2-m3-own-leader.md, section 11; tools: script-toolkit.md).
    ///
    ///  - Entries are recognised by the model they're created with (<see cref="CharacterBodies.ForModel"/>); the
    ///    scripts keep running the same entries on every PC, only bodies differ.
    ///  - Every such entry shows one character's body: by default the one it was written with ("authored"); the
    ///    player entry (written as the lead, the host's character) shows the <b>acting character</b>, the one doing
    ///    the player's actions (default: this PC's player). A script can bind any entry to any character.
    ///  - A character is shown at most once per PC: an entry written as a character a player controls is hidden
    ///    (Player mode) when that character is already on screen, and a ghost is hidden when an entry shows its
    ///    character. Scripts can force a character shown or hidden.
    ///  - Cast mode: every entry shows its authored body. Mode: the script's choice, else a guess (Cast when, with
    ///    control off, another entry runs one of the player entry's functions by UID; back on EnableMove).
    ///  - Bodies are chosen at creation (SetModel) and changed later with <see cref="BodyRebuild"/>, animations are
    ///    translated by action name.
    /// Hooks ([Online] lines in EventEngine.DoEventCode): SetModel, the animation opcodes, HideObject/ShowObject,
    /// RunScript*, EnableMove.
    /// </summary>
    public static class BodySwap
    {
        // ---- Script dictionary 9900 (docs/online/script-toolkit.md) ---------------------------------------------
        public const Int32 ScriptDictionary = 9900;
        // Read (written by the engine every frame)
        public const Int32 KeyOnline = 0;
        public const Int32 KeyLocalCharacter = 1;
        public const Int32 KeyLocalModel = 2;
        public const Int32 KeyLocalHead = 3;
        public const Int32 KeyActingCharacter = 4;
        public const Int32 KeyLocalFirstAnim = 10;            // + index in CharacterBodies.Required
        public const Int32 KeyCharacterBase = 10000;          // + character * 100 + offset below
        public const Int32 CharController = 0, CharPresent = 1, CharModel = 2, CharHead = 3, CharFirstAnim = 10;
        // Write (by the scripts; reset on each field)
        public const Int32 KeySceneMode = 100;                // 0 automatic, 1 Player, 2 Cast
        public const Int32 KeySceneScope = 101;               // 0 automatic, 1 personal, 2 shared (used from phase 2)
        public const Int32 KeyActor = 102;                    // 0 automatic, else character + 1
        public const Int32 KeyBindBase = 20000;               // + uid: 0 automatic, character + 1, BindAuthored or BindNotCharacter
        public const Int32 KeyVisibilityBase = 30000;         // + character: 0 automatic, 1 show, 2 hide
        public const Int32 BindAuthored = 255;
        public const Int32 BindNotCharacter = 254;
        public const Int32 ControllerNone = 0, ControllerThisPC = 1, ControllerOther = 2;

        public static SceneMode Mode { get; private set; } = SceneMode.Player;
        private static Boolean _modeByScript;

        /// <summary>Debug, offline: pretend to be a guest playing this character (-1 = off).</summary>
        public static Int32 DebugLocalCharacter = -1;

        // ---- Roles ---------------------------------------------------------------------------------------------

        private static Int32 _lead = -1, _local = -1;

        /// <summary>M4: this PC watches (plays no character): the field's player object is hidden here, in Player mode.</summary>
        public static Boolean HideLocalPlayer;
        private static readonly Dictionary<Int32, Int32> Controllers = new Dictionary<Int32, Int32>();
        private static readonly Dictionary<UInt64, Int32> PeerCharacters = new Dictionary<UInt64, Int32>();

        /// <summary>Called every frame by OnlineSession while a session plays (lead = the host's character), else with lead -1.</summary>
        public static void SetSession(Int32 lead, Int32 local, IDictionary<Int32, Int32> controllers, IDictionary<UInt64, Int32> peerCharacters)
        {
            if (lead >= 0 && _lead < 0 && CharacterBodies.FromFile == 0)
                Log.Warning($"[Online] Bodies: no {CharacterBodies.File} in the mods: only vanilla bodies (a mod's characters keep the host's body). Was the mod folder redeployed?");
            _lead = lead;
            _local = local;
            Controllers.Clear();
            PeerCharacters.Clear();
            if (controllers != null)
                foreach (KeyValuePair<Int32, Int32> kv in controllers)
                    Controllers[kv.Key] = kv.Value;
            if (peerCharacters != null)
                foreach (KeyValuePair<UInt64, Int32> kv in peerCharacters)
                    PeerCharacters[kv.Key] = kv.Value;
        }

        private static readonly Dictionary<Int32, Int32> DebugControllers = new Dictionary<Int32, Int32>();

        /// <summary>The lead and local characters and who controls whom (online session, else the offline debug override).</summary>
        private static Boolean TryGetRoles(out Int32 lead, out Int32 local, out Dictionary<Int32, Int32> controllers)
        {
            if (_lead >= 0)
            {
                lead = _lead;
                local = _local;
                controllers = Controllers;
                return true;
            }
            controllers = DebugControllers;
            DebugControllers.Clear();
            lead = local = -1;
            if (DebugLocalCharacter < 0)
                return false;
            lead = PartyLeader();
            if (lead < 0)
                return false;
            local = DebugLocalCharacter;
            DebugControllers[lead] = ControllerOther; // as if the host played the leader
            DebugControllers[local] = ControllerThisPC;
            return true;
        }

        private static Int32 PartyLeader()
        {
            PLAYER leader = FF9StateSystem.Common.FF9.party?.member[0];
            return leader != null ? (Int32)leader.info.slot_no : -1;
        }

        /// <summary>The character doing the player's actions: the script's choice, else this PC's player (else the lead).</summary>
        private static Int32 ActingCharacter(Int32 lead, Int32 local)
        {
            Int32 asked = ReadScriptDictionary(KeyActor);
            if (asked > 0)
                return asked - 1;
            return local >= 0 ? local : lead;
        }

        // ---- Entries -------------------------------------------------------------------------------------------

        private sealed class Entry
        {
            public PosObj Po;
            public CharacterBody Authored;
            /// <summary>The model the script created it with (one of Authored's models).</summary>
            public UInt16 AuthoredModel;
            public CharacterBody Shown;
            /// <summary>The meshes the script hid on the authored body (mesh numbers are per model).</summary>
            public UInt32 AuthoredMeshFlags;
            public Boolean Hidden;
            public Boolean HideApplied;
            public Renderer[] Renderers;
            public GameObject RenderersOf;
            public Boolean LoggedNoBody;

            public Boolean IsSwapped => !ReferenceEquals(Shown, Authored);
            /// <summary>Takes part in "who is who": it shows a party character and no script marked it "not a character".</summary>
            public Boolean IsCharacter => Shown.IsCharacter && ReadScriptDictionary(KeyBindBase + Po.uid) != BindNotCharacter;
            public override String ToString() => $"entry {Po.sid} (uid {Po.uid}) {Authored.Name}{(IsSwapped ? $" as {Shown.Name}" : "")}{(Hidden ? ", hidden" : "")}";
        }

        private static readonly Dictionary<Obj, Entry> Entries = new Dictionary<Obj, Entry>();
        private static readonly HashSet<Int32> HiddenGhostCharacters = new HashSet<Int32>();
        private static readonly HashSet<UInt64> HiddenGhostPeers = new HashSet<UInt64>();
        private static readonly Dictionary<UInt64, Int32> LoggedDrawnAs = new Dictionary<UInt64, Int32>();

        /// <summary>
        /// The character a peer's ghost stands for here: the one it's drawn as. A player whose own player entry acts as
        /// someone else in a scene (the guest's Blank playing Clove in a Cast scene) sends that body; counting it as the
        /// played character hid that character's entry and showed the drawn one twice. Unknown or borrowed models: the
        /// played character.
        /// </summary>
        private static Int32 GhostCharacter(UInt64 peer, Int32 played)
        {
            Int32 drawn = played;
            if (Ghosts.GhostManager.TryGetDrawnModel(peer, out UInt16 model))
            {
                CharacterBody body = CharacterBodies.ForModel(model);
                if (body != null)
                    drawn = body.CharacterId;
            }
            if (drawn != played && (!LoggedDrawnAs.TryGetValue(peer, out Int32 logged) || logged != drawn))
                Log.Message($"[Online] Bodies: {SteamNative.Tag(peer)} plays {NameOf(played)} but is drawn as {NameOf(drawn)} here: counted as {NameOf(drawn)}");
            LoggedDrawnAs[peer] = drawn;
            return drawn;
        }
        private static readonly HashSet<Int32> LoggedUnmapped = new HashSet<Int32>();
        private static FieldMap _fieldMap;
        private static Int32 _lastScriptMode;

        /// <summary>The body this entry should show now.</summary>
        private static CharacterBody DesiredBody(Entry e, Boolean rolesActive, Int32 lead, Int32 acting)
        {
            Int32 bind = ReadScriptDictionary(KeyBindBase + e.Po.uid);
            if (bind == BindAuthored || bind == BindNotCharacter)
                return e.Authored;
            if (bind > 0)
                return BodyOrAuthored(e, bind - 1);
            if (!rolesActive || Mode == SceneMode.Cast || !e.Authored.IsCharacter)
                return e.Authored;
            if (e.Authored.CharacterId == lead && acting >= 0 && acting != lead)
                return BodyOrAuthored(e, acting);
            return e.Authored;
        }

        private static CharacterBody BodyOrAuthored(Entry e, Int32 character)
        {
            CharacterBody body = CharacterBodies.ForCharacter(character);
            if (body != null)
                return body;
            if (!e.LoggedNoBody)
            {
                e.LoggedNoBody = true;
                Log.Message($"[Online] Bodies: no body for character {character} (add it to {CharacterBodies.File}); {e} keeps its own");
            }
            return e.Authored;
        }

        // ---- Hooks ---------------------------------------------------------------------------------------------

        /// <summary>[Online] hook, SetModel (field): after the model and head height are read, before the model is built.</summary>
        public static void OnSetModel(PosObj po, Boolean modelIsExpression)
        {
            try
            {
                EventEngine engine = PersistenSingleton<EventEngine>.Instance;
                if (engine.gMode != 1)
                    return;
                CheckField(engine.fieldmap);
                if (modelIsExpression)
                    return; // the script chose the model itself: never touched
                // Every entry with a model is tracked, so a script can bind it; one whose model isn't a character's
                // (or is borrowed) gets a stand-in body and is left alone unless a script binds it
                CharacterBody authored = CharacterBodies.ForModel(po.model) ?? CharacterBodies.ForUnknownModel(po.model, (Byte)Math.Max(0, Math.Min(255, -po.eye / 4)));
                Entry e = new Entry { Po = po, Authored = authored, AuthoredModel = po.model, Shown = authored };
                Entries[po] = e;
                Boolean rolesActive = TryGetRoles(out Int32 lead, out Int32 local, out _);
                CharacterBody desired = DesiredBody(e, rolesActive, lead, ActingCharacter(lead, local));
                if (!ReferenceEquals(desired, authored))
                {
                    if (!(po is Actor))
                        return;
                    e.Shown = desired;
                    po.model = desired.Model;
                    po.eye = (Int16)(-4 * desired.HeadHeight);
                    Log.Message($"[Online] Bodies: field {FF9StateSystem.Common.FF9.fldMapNo} {e}: created as {desired.Name}");
                }
            }
            catch (Exception err)
            {
                Log.Error(err, "[Online] Bodies: SetModel hook failed");
            }
        }

        /// <summary>[Online] hook, SetModel (field): after the model is built. Gives a swapped body its own locomotion.</summary>
        public static void AfterSetModel(PosObj po)
        {
            try
            {
                if (!Entries.TryGetValue(po, out Entry e) || !e.IsSwapped || !(po is Actor actor) || actor.go == null)
                    return;
                foreach (UInt16 anim in e.Shown.Actions.Values)
                    AnimationFactory.AddAnimWithAnimatioName(actor.go, FF9DBAll.AnimationDB.GetValue(anim));
                actor.idle = e.Shown.Get("Stand");
                actor.walk = e.Shown.Get("Walk");
                actor.run = e.Shown.Get("Run");
                actor.turnl = e.Shown.Get("TurnLeft");
                actor.turnr = e.Shown.Get("TurnRight");
            }
            catch (Exception err)
            {
                Log.Error(err, "[Online] Bodies: SetModel hook (after) failed");
            }
        }

        /// <summary>[Online] hook, every animation opcode: the animation to use on this object.</summary>
        public static UInt16 Anim(Obj obj, UInt16 anim)
        {
            if (obj == null || Entries.Count == 0 || !Entries.TryGetValue(obj, out Entry e) || !e.IsSwapped)
                return anim;
            UInt16 result = BodyRebuild.Translate(e.Authored, e.Shown, anim);
            if (result != anim && !e.Authored.ActionOf.ContainsKey(anim) && LoggedUnmapped.Add(anim))
                Log.Message($"[Online] Bodies: animation {anim} ({FF9DBAll.AnimationDB.GetValue(anim)}) isn't one of {e.Authored.Name}'s actions; {e.Shown.Name} plays Stand");
            return result;
        }

        /// <summary>
        /// [Online] hook, HideObject / ShowObject: mesh numbers belong to the authored model, so on a swapped body they
        /// change the authored body's state (applied when it's shown again) instead of hiding a random part. True = handled.
        /// </summary>
        public static Boolean OnMeshVisibility(PosObj po, Int32 mesh, Boolean hide)
        {
            if (po == null || !Entries.TryGetValue(po, out Entry e) || !e.IsSwapped)
                return false;
            if (hide)
                e.AuthoredMeshFlags |= 1u << mesh;
            else
                e.AuthoredMeshFlags &= ~(1u << mesh);
            return true;
        }

        /// <summary>[Online] hook, RunScript / RunScriptAsync / RunScriptSync: <paramref name="caller"/> runs a function of <paramref name="target"/>.</summary>
        public static void OnRunScript(Obj caller, Obj target, Int32 rawUid)
        {
            try
            {
                if (_modeByScript || Mode != SceneMode.Player || target == null || ReferenceEquals(caller, target) || rawUid == 250)
                    return;
                if (!Entries.TryGetValue(target, out Entry e) || !e.IsSwapped || ReadScriptDictionary(KeyBindBase + e.Po.uid) != 0)
                    return;
                EventEngine engine = PersistenSingleton<EventEngine>.Instance;
                if (engine.gMode != 1 || engine.GetUserControl())
                    return;
                SetMode(SceneMode.Cast, $"entry {caller?.sid} runs a function of the player's entry {target.sid} by its UID while control is off");
            }
            catch (Exception err)
            {
                Log.Error(err, "[Online] Bodies: RunScript hook failed");
            }
        }

        /// <summary>[Online] hook, EnableMove: an automatic Cast scene ends when the player gets control back.</summary>
        public static void OnControlEnabled()
        {
            try
            {
                if (!_modeByScript && Mode == SceneMode.Cast)
                    SetMode(SceneMode.Player, "the player has control again");
            }
            catch (Exception err)
            {
                Log.Error(err, "[Online] Bodies: EnableMove hook failed");
            }
        }

        // ---- Per frame -----------------------------------------------------------------------------------------

        /// <summary>Every frame (OnlineSession.Update): field change, the script dictionary, bodies and visibility.</summary>
        public static void Update()
        {
            try
            {
                EventEngine engine = PersistenSingleton<EventEngine>.Instance;
                if (engine == null || engine.gMode != 1 || engine.fieldmap == null)
                    return;
                CheckField(engine.fieldmap);
                CheckReplaced();
                CheckDictionaryReloaded();
                WriteScriptDictionary();
                Int32 asked = ReadScriptDictionary(KeySceneMode);
                if (asked != _lastScriptMode)
                {
                    _lastScriptMode = asked;
                    _modeByScript = asked == 1 || asked == 2;
                    if (_modeByScript)
                        SetMode(asked == 2 ? SceneMode.Cast : SceneMode.Player, "asked by the script");
                    else if (Mode == SceneMode.Cast && engine.GetUserControl())
                        SetMode(SceneMode.Player, "the script went back to automatic");
                }
                ApplyBodies();
                ComputeVisibility(engine);
            }
            catch (Exception err)
            {
                Log.Error(err, "[Online] Bodies: update failed");
            }
        }

        /// <summary>Rebuilds every entry whose body should change (once the field is on screen: no menu, no pause).</summary>
        private static void ApplyBodies()
        {
            if (Entries.Count == 0 || !CanRebuildNow())
                return;
            Boolean rolesActive = TryGetRoles(out Int32 lead, out Int32 local, out _);
            Int32 acting = ActingCharacter(lead, local);
            foreach (Entry e in Entries.Values)
            {
                if (!(e.Po is Actor actor) || actor.go == null)
                    continue;
                CharacterBody desired = DesiredBody(e, rolesActive, lead, acting);
                if (ReferenceEquals(desired, e.Shown))
                    continue;
                Boolean toAuthored = ReferenceEquals(desired, e.Authored);
                UInt32 meshFlags = actor.meshflags;
                if (!BodyRebuild.Rebuild(actor, e.Shown, desired, toAuthored ? e.AuthoredModel : (UInt16)0))
                    continue;
                if (!e.IsSwapped)
                    e.AuthoredMeshFlags = meshFlags; // leaving the authored body: keep what the script hid on it
                actor.meshflags = toAuthored ? e.AuthoredMeshFlags : 0;
                Log.Message($"[Online] Bodies: field {FF9StateSystem.Common.FF9.fldMapNo} entry {e.Po.sid} (uid {e.Po.uid}): {e.Shown.Name} → {desired.Name}");
                e.Shown = desired;
            }
        }

        /// <summary>
        /// Which entries and ghosts are hidden. A character the script hides is hidden everywhere; otherwise each
        /// character is shown once per PC, in this order of priority:
        ///  1. entries that show a character on purpose: the player entry (the acting character), the entry the field
        ///     made the player, entries the script binds, and in Cast mode every entry (the field's cast);
        ///  2. other players' ghosts on this field;
        ///  3. the field's other entries (extras): hidden only if their character is already shown by 1 or 2;
        ///     otherwise left as the field placed them.
        /// A character the script shows (1) keeps its entries; its ghost is hidden if an entry shows it.
        /// </summary>
        private static void ComputeVisibility(EventEngine engine)
        {
            Boolean rolesActive = TryGetRoles(out Int32 lead, out Int32 local, out _);
            Int32 acting = rolesActive ? ActingCharacter(lead, local) : -1;
            Obj player = engine.fieldmap?.playerController?.originalActor;
            HashSet<Int32> shown = new HashSet<Int32>();
            List<Entry> extras = new List<Entry>();
            foreach (Entry e in Entries.Values)
            {
                if (!e.IsCharacter)
                {
                    e.Hidden = false; // not a party character: shown as the field shows it, never counted
                    continue;
                }
                Int32 character = e.Shown.CharacterId;
                // The player entry of an acting character with no body keeps the lead's body as a stand-in: it stands
                // for the acting character, so the lead's ghost (another player) still shows
                if (rolesActive && Mode == SceneMode.Player && acting >= 0 && ReferenceEquals(e.Po, player)
                    && ReadScriptDictionary(KeyBindBase + e.Po.uid) == 0)
                    character = acting;
                Int32 forced = ReadScriptDictionary(KeyVisibilityBase + character);
                Boolean primary = !rolesActive || Mode == SceneMode.Cast || e.IsSwapped
                                  || ReadScriptDictionary(KeyBindBase + e.Po.uid) != 0
                                  || e.Authored.CharacterId == lead || ReferenceEquals(e.Po, player);
                if (HideLocalPlayer && rolesActive && Mode == SceneMode.Player && ReferenceEquals(e.Po, player) && forced != 1)
                    e.Hidden = true; // watching: no body of our own (the cast still shows in Cast mode)
                else if (forced == 2)
                    e.Hidden = true;
                else if (forced == 1 || primary)
                    e.Hidden = false;
                else
                {
                    extras.Add(e);
                    continue;
                }
                if (!e.Hidden)
                    shown.Add(character);
            }
            HiddenGhostCharacters.Clear();
            HiddenGhostPeers.Clear();
            foreach (KeyValuePair<UInt64, Int32> kv in PeerCharacters)
            {
                Int32 character = GhostCharacter(kv.Key, kv.Value); // [Online] Spike E: as drawn, not as played
                if (shown.Contains(character) || ReadScriptDictionary(KeyVisibilityBase + character) == 2)
                {
                    HiddenGhostCharacters.Add(character);
                    HiddenGhostPeers.Add(kv.Key);
                }
                else if (Ghosts.GhostManager.IsPeerOnLocalField(kv.Key))
                    shown.Add(character);
            }
            // Extras of one character don't hide each other: a field may keep several (one per scene state) and
            // shows the right one itself
            foreach (Entry e in extras)
                e.Hidden = shown.Contains(e.Shown.CharacterId);
        }

        /// <summary>Right before rendering: hidden entries stay hidden, whatever the script shows.</summary>
        public static void ApplyRender()
        {
            if (Entries.Count == 0)
                return;
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            if (engine == null || engine.gMode != 1 || !ReferenceEquals(engine.fieldmap, _fieldMap))
                return;
            foreach (Entry e in Entries.Values)
            {
                PosObj po = e.Po;
                if (po?.go == null || (!e.Hidden && !e.HideApplied))
                    continue;
                if (!ReferenceEquals(e.RenderersOf, po.go))
                {
                    e.Renderers = po.go.GetComponentsInChildren<Renderer>(true);
                    e.RenderersOf = po.go;
                }
                if (e.Hidden)
                {
                    foreach (Renderer renderer in e.Renderers)
                        if (renderer != null && renderer.enabled)
                            renderer.enabled = false;
                    if (!e.HideApplied)
                    {
                        ff9shadow.FF9ShadowOffField(po.uid);
                        e.HideApplied = true;
                    }
                }
                else
                {
                    foreach (Renderer renderer in e.Renderers)
                        if (renderer != null)
                            renderer.enabled = true;
                    if (!po.isShadowOff)
                        ff9shadow.FF9ShadowOnField(po.uid);
                    e.HideApplied = false;
                }
            }
        }

        /// <summary>Ghosts: hidden when their character is already shown by an entry, or the script hides it.</summary>
        public static Boolean HidesGhost(UInt64 peer)
        {
            return HiddenGhostPeers.Contains(peer);
        }

        // ---- Modes ---------------------------------------------------------------------------------------------

        private static void SetMode(SceneMode mode, String reason)
        {
            if (mode == Mode)
                return;
            Mode = mode;
            Log.Message($"[Online] Bodies: {mode} mode on field {FF9StateSystem.Common.FF9.fldMapNo} ({reason})");
            ApplyBodies(); // at once (a function of the player's entry may be about to run), or as soon as the field is on screen
        }

        private static Boolean CanRebuildNow()
        {
            UIManager ui = PersistenSingleton<UIManager>.Instance;
            return ui != null && ui.State == UIManager.UIState.FieldHUD && !ui.IsPause && HonoBehaviorSystem.Exists;
        }

        private sealed class ReplacedBody
        {
            public GameObject Go;
            public FieldMapActor FieldActor;
            public Single Since;
        }

        /// <summary>Bodies replaced by a rebuild, until the behaviour system has disposed of them (then their shadow goes).</summary>
        private static readonly List<ReplacedBody> Replaced = new List<ReplacedBody>();

        internal static void TrackReplaced(GameObject old, FieldMapActor fieldActor)
        {
            Replaced.Add(new ReplacedBody { Go = old, FieldActor = fieldActor, Since = Time.realtimeSinceStartup });
        }

        private static void CheckReplaced()
        {
            for (Int32 i = Replaced.Count - 1; i >= 0; i--)
            {
                ReplacedBody body = Replaced[i];
                if (body.Go == null) // Unity's == null: disposed of; its managed fields are still readable
                {
                    if (!ReferenceEquals(body.FieldActor, null))
                    {
                        try { body.FieldActor.DestroySelfShadow(); }
                        catch (Exception err) { Log.Warning($"[Online] Bodies: destroying a replaced body's shadow failed: {err.Message}"); }
                    }
                    Replaced.RemoveAt(i);
                }
                else if (Time.realtimeSinceStartup - body.Since > 5f)
                {
                    Log.Warning($"[Online] Bodies: the replaced body '{body.Go.name}' is still there after 5 s (left hidden)");
                    Replaced.RemoveAt(i);
                }
            }
        }

        private static void CheckField(FieldMap fieldMap)
        {
            if (ReferenceEquals(fieldMap, _fieldMap))
                return;
            Entries.Clear();
            HiddenGhostCharacters.Clear();
            HiddenGhostPeers.Clear();
            LoggedUnmapped.Clear();
            _fieldMap = fieldMap;
            Mode = SceneMode.Player;
            _modeByScript = false;
            _lastScriptMode = 0;
            ResetScriptRequests();
        }

        // ---- Script dictionary ---------------------------------------------------------------------------------

        /// <summary>Only for mods that declare bodies or while online, so other players' saves get no extra data.</summary>
        private static Boolean ScriptDictionaryInUse() => CharacterBodies.FromFile > 0 || _lead >= 0;

        private static void WriteScriptDictionary()
        {
            if (!ScriptDictionaryInUse())
                return;
            Boolean rolesActive = TryGetRoles(out Int32 lead, out Int32 local, out Dictionary<Int32, Int32> controllers);
            Int32 character = local >= 0 ? local : PartyLeader();
            Dictionary<Int32, Int32> dict = GetScriptDictionary();
            dict[KeyOnline] = _lead >= 0 ? 1 : 0;
            dict[KeyLocalCharacter] = character;
            dict[KeyActingCharacter] = rolesActive ? ActingCharacter(lead, local) : character;
            CharacterBody body = CharacterBodies.ForCharacter(character);
            dict[KeyLocalModel] = body?.Model ?? 0;
            dict[KeyLocalHead] = body?.HeadHeight ?? 0;
            for (Int32 i = 0; i < CharacterBodies.Required.Length; i++)
                dict[KeyLocalFirstAnim + i] = body?.Get(CharacterBodies.Required[i]) ?? 0;

            // Every character: who controls it, whether it's in this field, its body
            HashSet<Int32> characters = new HashSet<Int32>(controllers.Keys);
            foreach (CharacterBody b in CharacterBodies.All)
                characters.Add(b.CharacterId);
            foreach (Int32 c in characters)
            {
                if (c < 0 || c >= 100)
                    continue;
                Int32 baseKey = KeyCharacterBase + c * 100;
                controllers.TryGetValue(c, out Int32 controller);
                dict[baseKey + CharController] = controller;
                dict[baseKey + CharPresent] = IsPresent(c, controller) ? 1 : 0;
                CharacterBody cb = CharacterBodies.ForCharacter(c);
                dict[baseKey + CharModel] = cb?.Model ?? 0;
                dict[baseKey + CharHead] = cb?.HeadHeight ?? 0;
                for (Int32 i = 0; i < CharacterBodies.Required.Length; i++)
                    dict[baseKey + CharFirstAnim + i] = cb?.Get(CharacterBodies.Required[i]) ?? 0;
            }
        }

        /// <summary>A character is in this field: this PC's player, or another player whose last state is on this field.</summary>
        private static Boolean IsPresent(Int32 character, Int32 controller)
        {
            if (controller == ControllerThisPC)
                return true;
            foreach (KeyValuePair<UInt64, Int32> kv in PeerCharacters)
                if (kv.Value == character && Ghosts.GhostManager.IsPeerOnLocalField(kv.Key))
                    return true;
            return false;
        }

        /// <summary>The scripts' requests (mode, scope, actor, bindings, visibility) last for one field.</summary>
        private static void ResetScriptRequests()
        {
            if (!FF9StateSystem.EventState.gScriptDictionary.TryGetValue(ScriptDictionary, out Dictionary<Int32, Int32> dict))
                return;
            dict.Remove(KeySceneMode);
            dict.Remove(KeySceneScope);
            dict.Remove(KeyActor);
            List<Int32> keys = new List<Int32>();
            foreach (Int32 key in dict.Keys)
                if (key >= KeyBindBase)
                    keys.Add(key);
            foreach (Int32 key in keys)
                dict.Remove(key);
        }

        private static Dictionary<Int32, Int32> _dictionary;

        /// <summary>A loaded game (or one saved before the dictionary was excluded from saves) brings its own: the scripts' requests aren't carried over.</summary>
        private static void CheckDictionaryReloaded()
        {
            FF9StateSystem.EventState.gScriptDictionary.TryGetValue(ScriptDictionary, out Dictionary<Int32, Int32> dict);
            if (ReferenceEquals(dict, _dictionary))
                return;
            _dictionary = dict;
            ResetScriptRequests();
            _lastScriptMode = 0;
        }

        private static Dictionary<Int32, Int32> GetScriptDictionary()
        {
            Dictionary<Int32, Dictionary<Int32, Int32>> all = FF9StateSystem.EventState.gScriptDictionary;
            if (!all.TryGetValue(ScriptDictionary, out Dictionary<Int32, Int32> dict))
                all[ScriptDictionary] = _dictionary = dict = new Dictionary<Int32, Int32>();
            return dict;
        }

        private static Int32 ReadScriptDictionary(Int32 key)
        {
            return FF9StateSystem.EventState.gScriptDictionary.TryGetValue(ScriptDictionary, out Dictionary<Int32, Int32> dict) && dict.TryGetValue(key, out Int32 value) ? value : 0;
        }

        // ---- Debug ---------------------------------------------------------------------------------------------

        public static String Describe()
        {
            Boolean rolesActive = TryGetRoles(out Int32 lead, out Int32 local, out _);
            if (!rolesActive)
                return "bodies: off";
            Int32 acting = ActingCharacter(lead, local);
            Int32 scope = ReadScriptDictionary(KeySceneScope);
            StringBuilder text = new StringBuilder();
            if (CharacterBodies.FromFile == 0)
                text.Append($"bodies: NO {CharacterBodies.File} (vanilla bodies only)").Append('\n');
            text.Append($"bodies: lead {NameOf(lead)}, you {NameOf(local)}{(_lead < 0 ? " (debug)" : "")}, acting {NameOf(acting)}{(ReadScriptDictionary(KeyActor) > 0 ? " (script)" : "")}, ");
            text.Append($"{Mode} mode{(_modeByScript ? " (script)" : "")}{(scope != 0 ? $", scope {(scope == 2 ? "shared" : "personal")}" : "")}");
            foreach (Entry e in Entries.Values)
                if (e.IsSwapped || e.Hidden || (e.Authored.IsCharacter && !e.IsCharacter))
                    text.Append($"\n  {e}{(e.Authored.IsCharacter && !e.IsCharacter ? " (not a character: script)" : "")}");
            foreach (Int32 c in HiddenGhostCharacters)
                text.Append($"\n  ghost of {NameOf(c)} hidden");
            return text.ToString();
        }

        private static String NameOf(Int32 character) => CharacterBodies.ForCharacter(character)?.Name ?? (character < 0 ? "-" : $"character {character}");

        /// <summary>Debug: switch between Player and Cast mode by hand (as if the script asked; then back to automatic).</summary>
        public static void DebugToggleMode()
        {
            GetScriptDictionary()[KeySceneMode] = Mode == SceneMode.Player ? 2 : 0;
        }

        /// <summary>Debug: pretend another party member is acting (as if the script asked): automatic → each party member → automatic.</summary>
        public static void DebugCycleActor()
        {
            List<Int32> party = new List<Int32>();
            PLAYER[] members = FF9StateSystem.Common.FF9.party?.member;
            if (members != null)
                foreach (PLAYER member in members)
                    if (member != null && CharacterBodies.ForCharacter((Int32)member.info.slot_no) != null)
                        party.Add((Int32)member.info.slot_no);
            Int32 current = ReadScriptDictionary(KeyActor) - 1;
            Int32 index = party.IndexOf(current);
            Int32 next = index + 1 < party.Count ? party[index + 1] : -1;
            GetScriptDictionary()[KeyActor] = next + 1;
            Log.Message($"[Online] Debug: acting character {(next < 0 ? "automatic" : NameOf(next))}");
        }
    }
}
