using Memoria.Prime;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Memoria.Online.Bodies
{
    /// <summary>Who a scene shows (docs/online/step2-m3-own-leader.md, section 9).</summary>
    public enum SceneMode
    {
        /// <summary>Each PC shows its own character on the player entry.</summary>
        Player,
        /// <summary>Every PC shows the bodies the field authored (a story beat with several characters).</summary>
        Cast,
    }

    /// <summary>
    /// Step 2 / M3: each player plays their own character.
    ///
    /// Engine side (works with the scripts as they are):
    ///  - The scripts keep running the same entries on every PC (object 250 is the same entry everywhere); only the
    ///    body differs. An entry is recognised by the model it's created with (<see cref="CharacterBodies.ForModel"/>).
    ///  - Player mode: an entry created with the lead character's body (the host's character: Clove's player entry)
    ///    is created with the local player's body instead, and every animation the script gives it is translated by
    ///    action name (lead's animation → action → local body's animation; unknown ones play Stand). An entry created
    ///    with the body of a character a player controls is hidden: that character is on screen as its player or
    ///    its ghost.
    ///  - Cast mode: every entry shows its authored body (swapped ones are rebuilt), hidden ones are shown, and
    ///    ghosts whose character is on screen are hidden.
    ///  - The mode: the script's choice if it makes one (<see cref="ScriptDictionary"/>), else a guess: Cast when,
    ///    with the player's control off, another entry runs one of the swapped entry's functions by its UID (scene
    ///    choreography), back to Player when control returns. Reset to Player on every new field.
    ///
    /// Script side (the convention, optional): Memoria script dictionary <see cref="ScriptDictionary"/> holds this
    /// PC's player character, body and animations (read them in expressions), and the scene mode the script asks
    /// for (write it). A SetModel whose model is an expression is the script's own choice: never swapped or hidden.
    ///
    /// Hooks ([Online] lines in EventEngine.DoEventCode): SetModel, the animation opcodes, RunScript*, EnableMove.
    /// </summary>
    public static class BodySwap
    {
        /// <summary>Memoria script dictionary used by the convention (dictionary id, then the keys below).</summary>
        public const Int32 ScriptDictionary = 9900;
        public const Int32 KeyOnline = 0;            // 1 while an online session is playing
        public const Int32 KeyPlayerCharacter = 1;   // this PC's player character (offline: the party leader)
        public const Int32 KeyPlayerModel = 2;       // its SetModel arguments
        public const Int32 KeyPlayerHead = 3;
        public const Int32 KeyFirstAnim = 10;        // 10 + index in CharacterBodies.Required (10 Stand ... 23 LadderOffBottom)
        public const Int32 KeySceneMode = 100;       // written by the script: 0 automatic, 1 Player, 2 Cast (reset to 0 on each field)

        public static SceneMode Mode { get; private set; } = SceneMode.Player;
        private static Boolean _modeByScript;

        /// <summary>Debug, offline: pretend to be a guest playing this character (-1 = off).</summary>
        public static Int32 DebugLocalCharacter = -1;

        private static Int32 _lead = -1, _local = -1;
        private static readonly HashSet<Int32> Controlled = new HashSet<Int32>();

        private sealed class Swap
        {
            public CharacterBody From, To;
        }

        private sealed class Hidden
        {
            public CharacterBody Body;
            public Renderer[] Renderers;
            public GameObject RenderersOf;
            public Boolean Applied;
        }

        /// <summary>Entries showing the local body (Player mode).</summary>
        private static readonly Dictionary<Obj, Swap> Swaps = new Dictionary<Obj, Swap>();
        /// <summary>Entries that show the local body in Player mode but their authored body now (Cast mode).</summary>
        private static readonly Dictionary<Obj, Swap> Suspended = new Dictionary<Obj, Swap>();
        private static readonly Dictionary<Obj, Hidden> Hiddens = new Dictionary<Obj, Hidden>();
        /// <summary>Character body models created on this field (as authored), for hiding ghosts in Cast mode.</summary>
        private static readonly HashSet<UInt16> AuthoredModels = new HashSet<UInt16>();
        private static readonly HashSet<Int32> LoggedUnmapped = new HashSet<Int32>();
        private static FieldMap _fieldMap;
        private static Int32 _lastScriptMode;

        /// <summary>Called every frame by OnlineSession: the session's characters (lead = the host's), or -1 when not playing online.</summary>
        public static void SetSession(Int32 lead, Int32 local, IEnumerable<Int32> controlled)
        {
            _lead = lead;
            _local = local;
            Controlled.Clear();
            if (controlled != null)
                foreach (Int32 character in controlled)
                    Controlled.Add(character);
        }

        /// <summary>The lead and local characters in effect (online session, else the offline debug override).</summary>
        private static Boolean TryGetRoles(out Int32 lead, out Int32 local, out HashSet<Int32> controlled)
        {
            if (_lead >= 0)
            {
                lead = _lead;
                local = _local;
                controlled = Controlled;
                return true;
            }
            lead = local = -1;
            controlled = null;
            if (DebugLocalCharacter < 0)
                return false;
            lead = PartyLeader();
            if (lead < 0)
                return false;
            local = DebugLocalCharacter;
            controlled = new HashSet<Int32> { lead, local };
            return true;
        }

        private static Int32 PartyLeader()
        {
            PLAYER leader = FF9StateSystem.Common.FF9.party?.member[0];
            return leader != null ? (Int32)leader.info.slot_no : -1;
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
                CharacterBody authored = modelIsExpression ? null : CharacterBodies.ForModel(po.model);
                if (authored == null || !TryGetRoles(out Int32 lead, out Int32 local, out HashSet<Int32> controlled))
                    return;
                AuthoredModels.Add(authored.Model);
                if (authored.CharacterId == lead)
                {
                    if (local < 0 || local == lead)
                        return;
                    CharacterBody to = CharacterBodies.ForCharacter(local);
                    if (to == null)
                    {
                        Log.Warning($"[Online] Bodies: no body for character {local} (add it to {CharacterBodies.File}); entry {po.sid} keeps {authored.Name}");
                        return;
                    }
                    Swap swap = new Swap { From = authored, To = to };
                    if (Mode == SceneMode.Cast)
                    {
                        Suspended[po] = swap;
                        return;
                    }
                    Swaps[po] = swap;
                    po.model = to.Model;
                    po.eye = (Int16)(-4 * to.HeadHeight);
                    Log.Message($"[Online] Bodies: field {FF9StateSystem.Common.FF9.fldMapNo} entry {po.sid} (uid {po.uid}) is created as {to.Name} instead of {authored.Name}");
                }
                else if (controlled.Contains(authored.CharacterId))
                {
                    Hiddens[po] = new Hidden { Body = authored };
                    Log.Message($"[Online] Bodies: field {FF9StateSystem.Common.FF9.fldMapNo} entry {po.sid} (uid {po.uid}) is {authored.Name}, played by a player: hidden in Player mode");
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
                if (!Swaps.TryGetValue(po, out Swap swap) || !(po is Actor actor) || actor.go == null)
                    return;
                foreach (UInt16 anim in swap.To.Actions.Values)
                    AnimationFactory.AddAnimWithAnimatioName(actor.go, FF9DBAll.AnimationDB.GetValue(anim));
                actor.idle = swap.To.Get("Stand");
                actor.walk = swap.To.Get("Walk");
                actor.run = swap.To.Get("Run");
                actor.turnl = swap.To.Get("TurnLeft");
                actor.turnr = swap.To.Get("TurnRight");
            }
            catch (Exception err)
            {
                Log.Error(err, "[Online] Bodies: SetModel hook (after) failed");
            }
        }

        /// <summary>[Online] hook, every animation opcode: the animation to use on this object.</summary>
        public static UInt16 Anim(Obj obj, UInt16 anim)
        {
            if (obj == null || Swaps.Count == 0 || !Swaps.TryGetValue(obj, out Swap swap))
                return anim;
            UInt16 result = BodyRebuild.Translate(swap.From, swap.To, anim);
            if (result != anim && !swap.From.ActionOf.ContainsKey(anim) && LoggedUnmapped.Add(anim))
                Log.Message($"[Online] Bodies: animation {anim} ({FF9DBAll.AnimationDB.GetValue(anim)}) isn't one of {swap.From.Name}'s actions; {swap.To.Name} plays Stand");
            return result;
        }

        /// <summary>[Online] hook, RunScript / RunScriptAsync / RunScriptSync: <paramref name="caller"/> runs a function of <paramref name="target"/>.</summary>
        public static void OnRunScript(Obj caller, Obj target, Int32 rawUid)
        {
            try
            {
                if (_modeByScript || Mode != SceneMode.Player || target == null || ReferenceEquals(caller, target) || rawUid == 250 || !Swaps.ContainsKey(target))
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

        /// <summary>Every frame (OnlineSession.Update): field change, the script dictionary, the script's scene mode.</summary>
        public static void Update()
        {
            try
            {
                EventEngine engine = PersistenSingleton<EventEngine>.Instance;
                if (engine == null || engine.gMode != 1 || engine.fieldmap == null)
                    return;
                CheckField(engine.fieldmap);
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
            }
            catch (Exception err)
            {
                Log.Error(err, "[Online] Bodies: update failed");
            }
        }

        /// <summary>Right before rendering: hidden entries stay hidden in Player mode, whatever the script shows (except the player itself).</summary>
        public static void ApplyRender()
        {
            if (Hiddens.Count == 0)
                return;
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            if (engine == null || engine.gMode != 1 || !ReferenceEquals(engine.fieldmap, _fieldMap))
                return;
            Obj player = engine.fieldmap?.playerController?.originalActor;
            foreach (KeyValuePair<Obj, Hidden> kv in Hiddens)
            {
                PosObj po = kv.Key as PosObj;
                if (po?.go == null)
                    continue;
                Hidden hidden = kv.Value;
                if (!ReferenceEquals(hidden.RenderersOf, po.go))
                {
                    hidden.Renderers = po.go.GetComponentsInChildren<Renderer>(true);
                    hidden.RenderersOf = po.go;
                }
                Boolean hide = Mode == SceneMode.Player && !ReferenceEquals(po, player);
                if (hide)
                {
                    foreach (Renderer renderer in hidden.Renderers)
                        if (renderer != null && renderer.enabled)
                            renderer.enabled = false;
                    if (!hidden.Applied)
                    {
                        ff9shadow.FF9ShadowOffField(po.uid);
                        hidden.Applied = true;
                    }
                }
                else if (hidden.Applied)
                {
                    // It became the player (leader switch) or the scene became a Cast scene: show it again
                    foreach (Renderer renderer in hidden.Renderers)
                        if (renderer != null)
                            renderer.enabled = true;
                    if (!po.isShadowOff)
                        ff9shadow.FF9ShadowOnField(po.uid);
                    hidden.Applied = false;
                }
            }
        }

        /// <summary>Ghosts: in a Cast scene, a ghost whose character is on screen (as authored) isn't drawn twice.</summary>
        public static Boolean HidesGhost(UInt16 model)
        {
            return Mode == SceneMode.Cast && AuthoredModels.Contains(model);
        }

        // ---- Modes ---------------------------------------------------------------------------------------------

        private static void SetMode(SceneMode mode, String reason)
        {
            if (mode == Mode)
                return;
            Mode = mode;
            Int32 rebuilt = 0;
            if (mode == SceneMode.Cast)
            {
                foreach (KeyValuePair<Obj, Swap> kv in Swaps)
                {
                    if (kv.Key is Actor actor && actor.go != null && BodyRebuild.Rebuild(actor, kv.Value.To, kv.Value.From))
                        rebuilt++;
                    Suspended[kv.Key] = kv.Value;
                }
                Swaps.Clear();
            }
            else
            {
                foreach (KeyValuePair<Obj, Swap> kv in Suspended)
                {
                    if (kv.Key is Actor actor && actor.go != null)
                    {
                        if (actor.model == kv.Value.From.Model && BodyRebuild.Rebuild(actor, kv.Value.From, kv.Value.To))
                            rebuilt++;
                        Swaps[kv.Key] = kv.Value;
                    }
                }
                Suspended.Clear();
            }
            Log.Message($"[Online] Bodies: {mode} mode on field {FF9StateSystem.Common.FF9.fldMapNo} ({reason}); {rebuilt} bod{(rebuilt == 1 ? "y" : "ies")} rebuilt");
        }

        private static void CheckField(FieldMap fieldMap)
        {
            if (ReferenceEquals(fieldMap, _fieldMap))
                return;
            Swaps.Clear();
            Suspended.Clear();
            Hiddens.Clear();
            AuthoredModels.Clear();
            LoggedUnmapped.Clear();
            _fieldMap = fieldMap;
            Mode = SceneMode.Player;
            _modeByScript = false;
            _lastScriptMode = 0;
            if (ScriptDictionaryInUse())
                SetScriptDictionary(KeySceneMode, 0);
        }

        // ---- Script dictionary (the convention) ----------------------------------------------------------------

        /// <summary>Only for mods that declare bodies or while online, so other players' saves get no extra data.</summary>
        private static Boolean ScriptDictionaryInUse() => CharacterBodies.FromFile > 0 || _lead >= 0;

        private static void WriteScriptDictionary()
        {
            if (!ScriptDictionaryInUse())
                return;
            TryGetRoles(out Int32 lead, out Int32 local, out _);
            Int32 character = local >= 0 ? local : PartyLeader();
            CharacterBody body = CharacterBodies.ForCharacter(character);
            SetScriptDictionary(KeyOnline, _lead >= 0 ? 1 : 0);
            SetScriptDictionary(KeyPlayerCharacter, character);
            if (body == null)
                return;
            SetScriptDictionary(KeyPlayerModel, body.Model);
            SetScriptDictionary(KeyPlayerHead, body.HeadHeight);
            for (Int32 i = 0; i < CharacterBodies.Required.Length; i++)
                SetScriptDictionary(KeyFirstAnim + i, body.Get(CharacterBodies.Required[i]));
        }

        private static Dictionary<Int32, Int32> GetScriptDictionary()
        {
            Dictionary<Int32, Dictionary<Int32, Int32>> all = FF9StateSystem.EventState.gScriptDictionary;
            if (!all.TryGetValue(ScriptDictionary, out Dictionary<Int32, Int32> dict))
                all[ScriptDictionary] = dict = new Dictionary<Int32, Int32>();
            return dict;
        }

        private static void SetScriptDictionary(Int32 key, Int32 value)
        {
            GetScriptDictionary()[key] = value;
        }

        private static Int32 ReadScriptDictionary(Int32 key)
        {
            return FF9StateSystem.EventState.gScriptDictionary.TryGetValue(ScriptDictionary, out Dictionary<Int32, Int32> dict) && dict.TryGetValue(key, out Int32 value) ? value : 0;
        }

        public static String Describe()
        {
            TryGetRoles(out Int32 lead, out Int32 local, out _);
            if (lead < 0)
                return "bodies: off";
            return $"bodies: lead {CharacterBodies.ForCharacter(lead)?.Name ?? lead.ToString()}, you {CharacterBodies.ForCharacter(local)?.Name ?? local.ToString()}" +
                   $"{(_lead < 0 ? " (debug)" : "")}, {Mode} mode{(_modeByScript ? " (script)" : "")}, {Swaps.Count} swapped, {Suspended.Count} suspended, {Hiddens.Count} hidden";
        }

        /// <summary>Debug: switch between Player and Cast mode by hand (as if the script asked; 0 = back to automatic).</summary>
        public static void DebugToggleMode()
        {
            SetScriptDictionary(KeySceneMode, Mode == SceneMode.Player ? 2 : 0);
        }
    }
}
