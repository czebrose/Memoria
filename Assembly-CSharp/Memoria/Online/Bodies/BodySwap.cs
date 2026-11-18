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
    /// Step 2 / M3: each player plays their own character, without changing the field scripts.
    ///
    ///  - The scripts keep running the same entries on every PC (object 250 is the same entry everywhere); only the
    ///    body differs. An entry is recognised by the model it's created with (<see cref="CharacterBodies.ForModel"/>).
    ///  - An entry created with the lead character's body (the host's character: Clove's player entry) is created
    ///    with the local player's body instead (<see cref="OnSetModel"/>), and every animation the script gives it
    ///    is translated through the action names: lead's animation → action → local body's animation
    ///    (<see cref="Anim"/>; unknown ones play Stand).
    ///  - An entry created with the body of a character another player controls (or the local one, on a guest) is
    ///    hidden in Player mode: that character is already on screen as its player or its ghost.
    ///  - Bodies are chosen when the object is created (SetModel runs once), so a change of local character applies
    ///    from the next field.
    /// Hooks: SetModel and the animation opcodes in EventEngine.DoEventCode ([Online] lines), and OnPreCull.
    /// </summary>
    public static class BodySwap
    {
        public static SceneMode Mode = SceneMode.Player;

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
            public Boolean ShadowOff;
        }

        private static readonly Dictionary<Obj, Swap> Swaps = new Dictionary<Obj, Swap>();
        private static readonly Dictionary<Obj, Hidden> Hiddens = new Dictionary<Obj, Hidden>();
        private static readonly HashSet<Int32> LoggedUnmapped = new HashSet<Int32>();
        private static FieldMap _fieldMap;

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

        public static Int32 SwappedCount => Swaps.Count;
        public static Int32 HiddenCount => Hiddens.Count;

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
            PLAYER leader = FF9StateSystem.Common.FF9.party?.member[0];
            if (leader == null)
                return false;
            lead = (Int32)leader.info.slot_no;
            local = DebugLocalCharacter;
            controlled = new HashSet<Int32> { lead, local };
            return true;
        }

        /// <summary>[Online] hook, SetModel (field): after the model and head height are read, before the model is built.</summary>
        public static void OnSetModel(PosObj po)
        {
            try
            {
                EventEngine engine = PersistenSingleton<EventEngine>.Instance;
                if (engine.gMode != 1)
                    return;
                CheckField(engine.fieldmap);
                if (Mode != SceneMode.Player || !TryGetRoles(out Int32 lead, out Int32 local, out HashSet<Int32> controlled))
                    return;
                CharacterBody authored = CharacterBodies.ForModel(po.model);
                if (authored == null)
                    return;
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
                    Swaps[po] = new Swap { From = authored, To = to };
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
            if (swap.From.ActionOf.TryGetValue(anim, out String action))
                return swap.To.Get(action);
            if (swap.To.ActionOf.ContainsKey(anim))
                return anim; // already one of the local body's
            UInt16 stand = swap.To.Get(CharacterBodies.Stand);
            if (LoggedUnmapped.Add(anim))
                Log.Message($"[Online] Bodies: animation {anim} ({FF9DBAll.AnimationDB.GetValue(anim)}) isn't one of {swap.From.Name}'s actions; {swap.To.Name} plays Stand");
            return stand;
        }

        /// <summary>Right before rendering: hidden entries stay hidden, whatever the script shows (except the player itself).</summary>
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
                Boolean hide = Mode == SceneMode.Player && !ReferenceEquals(po, player);
                Hidden hidden = kv.Value;
                if (hidden.Renderers == null)
                    hidden.Renderers = po.go.GetComponentsInChildren<Renderer>(true);
                if (hide)
                {
                    foreach (Renderer renderer in hidden.Renderers)
                        if (renderer != null && renderer.enabled)
                            renderer.enabled = false;
                    if (!hidden.ShadowOff)
                    {
                        ff9shadow.FF9ShadowOffField(po.uid);
                        hidden.ShadowOff = true;
                    }
                }
                else if (hidden.ShadowOff)
                {
                    // It became the player (leader switch) or the scene became a Cast scene: show it again
                    foreach (Renderer renderer in hidden.Renderers)
                        if (renderer != null)
                            renderer.enabled = true;
                    ff9shadow.FF9ShadowOnField(po.uid);
                    hidden.ShadowOff = false;
                }
            }
        }

        private static void CheckField(FieldMap fieldMap)
        {
            if (ReferenceEquals(fieldMap, _fieldMap))
                return;
            Swaps.Clear();
            Hiddens.Clear();
            LoggedUnmapped.Clear();
            _fieldMap = fieldMap;
        }

        public static String Describe()
        {
            TryGetRoles(out Int32 lead, out Int32 local, out _);
            if (lead < 0)
                return "bodies: off";
            return $"bodies: lead {CharacterBodies.ForCharacter(lead)?.Name ?? lead.ToString()}, you {CharacterBodies.ForCharacter(local)?.Name ?? local.ToString()}" +
                   $"{(_lead < 0 ? " (debug)" : "")}, {Mode} mode, {Swaps.Count} swapped, {Hiddens.Count} hidden";
        }
    }
}
