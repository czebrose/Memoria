using Memoria.Prime;
using System;
using System.Collections.Generic;

namespace Memoria.Online.Ghosts
{
    /// <summary>
    /// Owns the ghosts of the current field, keyed by peer id (ITransport's UInt64). Feed it every received
    /// channel-1 packet with <see cref="OnPacket"/> and call <see cref="Update"/> once per frame and
    /// <see cref="ApplyRender"/> right before rendering.
    ///
    ///  - A ghost is spawned lazily, on the first state for the local field (not on "peer joined": Steam P2P takes
    ///    ~4 s before packets flow).
    ///  - States not newer than the newest one are dropped (unreliable channel: late / out of order).
    ///  - The last pose is held through short gaps; after <see cref="StaleMs"/> of silence the ghost is hidden, and
    ///    shown again (without sliding from the old spot) on the next state.
    ///  - A ghost whose player is in another field is hidden.
    ///  - Leaving the field (or a battle) destroys the field map and with it every ghost; they come back on the next
    ///    state for the new field.
    /// </summary>
    public static class GhostManager
    {
        /// <summary>UIDs used for ghosts: in the custom-object range (128-249), below the 250+ special references.</summary>
        public const Int32 FirstUid = 244;
        public const Int32 MaxGhosts = 4;
        public const Double StaleMs = 5000;

        public static Double InterpolationDelayMs = 100;
        public static Boolean Interpolate = true;
        /// <summary>Debug: draw every ghost with this model instead of its sender's (null = the sender's model).</summary>
        public static GhostModel ModelOverride;

        public static Int32 IgnoredOtherField, IgnoredBadPacket, SpawnFailures;

        private static readonly Dictionary<UInt64, GhostActor> Ghosts = new Dictionary<UInt64, GhostActor>();
        private static readonly List<Pending> PendingRemovals = new List<Pending>();
        private static FieldMap _fieldMap;
        private static GhostState _scratch;

        private struct Pending
        {
            public Int32 Uid;
            public Actor Actor;
            public FieldMapActor FieldActor;
        }

        public static IEnumerable<GhostActor> All => Ghosts.Values;
        public static Int32 Count => Ghosts.Count;

        public static void OnPacket(UInt64 peer, Byte[] data, Int32 offset, Int32 length, Double nowMs)
        {
            if (!_scratch.Deserialize(data, offset, length))
            {
                IgnoredBadPacket++;
                return;
            }
            OnState(peer, _scratch, nowMs);
        }

        public static void OnState(UInt64 peer, GhostState state, Double nowMs)
        {
            if (!IsOnField(out FieldMap fieldMap))
                return;
            CheckFieldMap(fieldMap);
            Ghosts.TryGetValue(peer, out GhostActor ghost);
            if (ghost != null && !ghost.IsAlive)
            {
                Forget(peer, ghost);
                ghost = null;
            }
            if (ghost == null)
            {
                if (state.FieldId != (UInt16)FF9StateSystem.Common.FF9.fldMapNo)
                {
                    IgnoredOtherField++;
                    return;
                }
                if (fieldMap.walkMesh == null || fieldMap.playerController == null)
                    return; // still loading, or a field without a player: nowhere to stand yet
                ghost = TrySpawn(peer, fieldMap, state);
                if (ghost == null)
                    return;
            }
            else if (!ghost.Visible && nowMs - ghost.LastReceivedMs > StaleMs)
            {
                ghost.ResetTimeline(); // back after a silence: appear at the new spot
            }
            ghost.Push(state, nowMs);
        }

        public static void Update(Double nowMs, Single deltaTime)
        {
            ProcessPendingRemovals();
            if (!IsOnField(out FieldMap fieldMap))
            {
                if (Ghosts.Count > 0)
                    ForgetAll("left the field");
                return;
            }
            CheckFieldMap(fieldMap);

            UInt16 localField = (UInt16)FF9StateSystem.Common.FF9.fldMapNo;
            List<UInt64> dead = null;
            foreach (KeyValuePair<UInt64, GhostActor> kv in Ghosts)
            {
                GhostActor ghost = kv.Value;
                if (!ghost.IsAlive)
                {
                    (dead ?? (dead = new List<UInt64>())).Add(kv.Key);
                    continue;
                }
                Boolean stale = nowMs - ghost.LastReceivedMs > StaleMs;
                Boolean otherField = ghost.Newest.FieldId != localField;
                Boolean show = !stale && !otherField;
                if (show != ghost.Visible)
                {
                    ghost.SetVisible(show);
                    Log.Message($"[Ghosts] {ghost} {(show ? "shown again" : stale ? $"hidden: no state for {(nowMs - ghost.LastReceivedMs) / 1000:0.0}s" : $"hidden: its player is in field {ghost.Newest.FieldId}")}");
                }
                if (show)
                    ghost.Update(nowMs, deltaTime, InterpolationDelayMs, Interpolate);
            }
            if (dead != null)
                foreach (UInt64 peer in dead)
                    Forget(peer, Ghosts[peer]);
        }

        /// <summary>Call right before rendering (Camera.onPreCull).</summary>
        public static void ApplyRender()
        {
            foreach (GhostActor ghost in Ghosts.Values)
                ghost.ApplyToTransform();
        }

        public static void Remove(UInt64 peer, String reason)
        {
            if (!Ghosts.TryGetValue(peer, out GhostActor ghost))
                return;
            Ghosts.Remove(peer);
            FieldMapActor fieldActor = ghost.IsAlive ? ghost.Despawn() : null;
            PendingRemovals.Add(new Pending { Uid = ghost.Uid, Actor = ghost.Actor, FieldActor = fieldActor });
            Log.Message($"[Ghosts] {ghost} removed ({reason}): received {ghost.Received}, dropped as old {ghost.DroppedOld}, snaps {ghost.Snaps}, hidden {ghost.Hides} times");
        }

        public static void RemoveAll(String reason)
        {
            foreach (UInt64 peer in new List<UInt64>(Ghosts.Keys))
                Remove(peer, reason);
        }

        private static GhostActor TrySpawn(UInt64 peer, FieldMap fieldMap, GhostState state)
        {
            if (Ghosts.Count >= MaxGhosts)
                return null;
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            Int32 uid = -1;
            for (Int32 candidate = FirstUid; candidate < FirstUid + MaxGhosts && uid < 0; candidate++)
            {
                if (engine.FindObjByUID(candidate) != null || IsUidUsed(candidate))
                    continue;
                uid = candidate;
            }
            if (uid < 0)
            {
                SpawnFailures++;
                Log.Warning($"[Ghosts] No free UID in {FirstUid}-{FirstUid + MaxGhosts - 1} on field {state.FieldId} (used by the field script?); ghost {peer:X} not shown");
                return null;
            }
            GhostModel model = ModelOverride ?? GhostModels.Find(state.Model) ?? GhostModels.All[0];
            try
            {
                GhostActor ghost = GhostActor.Spawn(peer, uid, model, fieldMap, state);
                Ghosts[peer] = ghost;
                Log.Message($"[Ghosts] {ghost} spawned on field {state.FieldId} at ({state.X},{state.Y},{state.Z}) tri {state.Tri}, floor {ghost.Controller.activeFloor}");
                return ghost;
            }
            catch (Exception err)
            {
                SpawnFailures++;
                Log.Error(err, $"[Ghosts] Spawning ghost {peer:X} ({model}) failed");
                return null;
            }
        }

        private static Boolean IsUidUsed(Int32 uid)
        {
            foreach (GhostActor ghost in Ghosts.Values)
                if (ghost.Uid == uid)
                    return true;
            foreach (Pending pending in PendingRemovals)
                if (pending.Uid == uid)
                    return true;
            return false;
        }

        private static Boolean IsOnField(out FieldMap fieldMap)
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            fieldMap = engine?.fieldmap;
            return engine != null && engine.gMode == 1 && fieldMap != null && HonoBehaviorSystem.Exists;
        }

        private static void CheckFieldMap(FieldMap fieldMap)
        {
            if (ReferenceEquals(fieldMap, _fieldMap))
                return;
            if (Ghosts.Count > 0)
                ForgetAll("new field map");
            _fieldMap = fieldMap;
        }

        /// <summary>
        /// Leaving the field: ghosts destroyed with the field map only lose their bookkeeping; any still alive
        /// (the field ended without destroying the map yet) are despawned properly.
        /// </summary>
        private static void ForgetAll(String reason)
        {
            foreach (KeyValuePair<UInt64, GhostActor> kv in new List<KeyValuePair<UInt64, GhostActor>>(Ghosts))
            {
                if (kv.Value.IsAlive)
                {
                    Remove(kv.Key, reason);
                    continue;
                }
                Log.Message($"[Ghosts] {kv.Value} gone with the field ({reason}): received {kv.Value.Received}, dropped as old {kv.Value.DroppedOld}, snaps {kv.Value.Snaps}, hidden {kv.Value.Hides} times");
                Forget(kv.Key, kv.Value);
            }
            _fieldMap = null;
        }

        private static void Forget(UInt64 peer, GhostActor ghost)
        {
            Ghosts.Remove(peer);
            RemoveCharEntries(ghost.Uid, ghost.Actor);
        }

        private static void ProcessPendingRemovals()
        {
            for (Int32 i = PendingRemovals.Count - 1; i >= 0; i--)
            {
                Pending pending = PendingRemovals[i];
                // Unity's == null: true once HonoBehaviorSystem has destroyed it; its managed fields are still readable
                if (pending.FieldActor != null)
                    continue;
                if (!ReferenceEquals(pending.FieldActor, null))
                {
                    try { pending.FieldActor.DestroySelfShadow(); }
                    catch (Exception err) { Log.Warning($"[Ghosts] Destroying a removed ghost's shadow failed: {err.Message}"); }
                }
                RemoveCharEntries(pending.Uid, pending.Actor);
                PendingRemovals.RemoveAt(i);
            }
        }

        /// <summary>Removes the UID's field registrations, but only if they're still the ghost's (UIDs get reused).</summary>
        private static void RemoveCharEntries(Int32 uid, Actor actor)
        {
            if (FF9StateSystem.Common.FF9.charArray.TryGetValue(uid, out FF9Char ch) && ReferenceEquals(ch.evt, actor))
            {
                FF9StateSystem.Common.FF9.charArray.Remove(uid);
                FF9StateSystem.Field.FF9Field.loc.map.charStateArray.Remove(uid);
                FF9StateSystem.Field.FF9Field.loc.map.shadowArray.Remove(uid);
            }
        }
    }
}
