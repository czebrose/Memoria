using Memoria.Prime;
using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Memoria.Online.World
{
    /// <summary>
    /// The other players on the world map (docs/online/world-map.md). Every PC sends a <see cref="WorldGhostState"/>
    /// with its field ghost state; this draws each other player on foot or on a chocobo where they are, with their own
    /// model and animation, interpolated, with a world shadow and a name tag.
    ///
    ///  - Timeline: each state carries the sender's clock; the receiver keeps the smallest (receive time - sender time)
    ///    seen as the clock offset, and draws each player <see cref="DelayMs"/> in the past, between the two states
    ///    around that moment. Gaps hold the last pose; after <see cref="StaleMs"/> of silence the ghost goes.
    ///  - Placement: positions are absolute world positions; WMWorld.SetAbsolutePositionOf turns them into this PC's
    ///    Unity positions (the world map wraps and its blocks move under the camera). A jump longer than
    ///    <see cref="SnapDistance"/> (a wrap, a warp) snaps instead of sliding.
    ///  - The model is built as the world builds an actor's (WMWorld.addGameObjectToWMActor): the model under a root
    ///    that carries the actor's transform, turned 180° about z, at 1/256 scale.
    ///  - Airships aren't drawn: every PC has its own ship, which follows the pilot's (<see cref="WorldCoop"/>).
    /// </summary>
    public static class WorldGhosts
    {
        public const Double DelayMs = 110;
        public const Double StaleMs = 2500;
        public const Single SnapDistance = 24f;
        private const Int32 MaxStates = 32;
        private const Double AwaySendMs = 1000; // Away is sent this long after leaving the world map (unreliable channel)
        private const String ShadowPrefab = "EmbeddedAsset/WorldMap_Local/Shadow/WMShadow";

        /// <summary>Name tags over the world ghosts.</summary>
        public static Boolean ShowNames = true;

        private sealed class Sample
        {
            public Double SenderMs;
            public WorldGhostState State;
        }

        private sealed class Peer
        {
            public UInt64 Id;
            public readonly List<Sample> States = new List<Sample>();
            public Double Offset;
            public Boolean HasOffset;
            public Double LastReceivedMs;
            public UInt32 NewestTick;
            public Boolean HasNewest;
            // drawn
            public GameObject Root, Model, Shadow, Tag;
            public Animation Animation;
            public Material ShadowMaterial;
            public UInt16 ShownModel, ShownAnim;
            public Single TagHeight;
            public Boolean TagScaled;
            public Vector3 LastPosition;
            public Boolean Placed;
        }

        private static readonly Dictionary<UInt64, Peer> Peers = new Dictionary<UInt64, Peer>();
        private static Int32 _renderedFrame = -1;
        private static Double _leftWorldAt = -1;
        private static Boolean _wasOnWorld;
        // A battle from the world map: the spot this player left from, sent as Battle until they're back
        private static Boolean _inWorldBattle;
        private static WorldGhostState _battleSpot;
        // Each model's standing animation, learnt while the player stood still (a battle starts while walking)
        private static readonly Dictionary<UInt16, UInt16> _standingAnim = new Dictionary<UInt16, UInt16>();
        private static Vector3 _lastSampled;
        private static Boolean _hasLastSampled;
        private static Double _lastSampledAt;

        /// <summary>This player is in a battle started on the world map (or its result screens), not back yet.</summary>
        public static Boolean InWorldBattle => _inWorldBattle;

        /// <summary>[Online] BattleEntry, when a world map battle takes this player (its own encounter, or another
        /// player's battle it joins): from now until it's back, its state is its spot here, as <see cref="WorldMode.Battle"/>.</summary>
        public static void NoteBattleFromWorld(Double nowMs)
        {
            if (!TrySample(nowMs, out WorldGhostState state) || state.Mode != WorldMode.Foot && state.Mode != WorldMode.Chocobo)
                return; // flying, hidden: nothing to stand there
            if (_standingAnim.TryGetValue(state.Model, out UInt16 standing))
                state.Anim = standing;
            state.Mode = WorldMode.Battle;
            _battleSpot = state;
            _inWorldBattle = true;
        }
        private static Font _tagFont;

        public static Int32 IgnoredBadPacket, Received, Shown;

        // ---- Sending ----------------------------------------------------------------------------------------------

        /// <summary>Whether this PC is on the world map, with a world to draw in.</summary>
        public static Boolean OnWorldMap
        {
            get
            {
                EventEngine engine = PersistenSingleton<EventEngine>.Instance;
                return engine != null && engine.gMode == 3 && ff9.world != null && ff9.world.Blocks != null && ff9.w_moveActorPtr != null && ff9.w_moveActorPtr.World != null;
            }
        }

        /// <summary>This PC's state now, if it should be sent (on the world map, or just after leaving it).</summary>
        public static Boolean TrySample(Double nowMs, out WorldGhostState state)
        {
            state = default(WorldGhostState);
            state.Tick = (UInt32)nowMs;
            state.Control = -1;
            state.Rotation = Quaternion.identity;
            Boolean onWorld = OnWorldMap;
            if (onWorld)
                _inWorldBattle = false; // back
            else if (_inWorldBattle)
            {
                EventEngine engine = PersistenSingleton<EventEngine>.Instance;
                if (engine != null && engine.gMode == 1 || PersistenSingleton<UIManager>.Instance?.State == UIManager.UIState.Title)
                    _inWorldBattle = false; // the battle led to a field, or to the title (game over)
                else
                {
                    state = _battleSpot;
                    state.Tick = (UInt32)nowMs;
                    return true;
                }
            }
            if (onWorld)
                _wasOnWorld = true;
            else if (_wasOnWorld)
            {
                _wasOnWorld = false;
                _leftWorldAt = nowMs;
            }
            if (!onWorld)
            {
                state.Mode = WorldMode.Away;
                return _leftWorldAt >= 0 && nowMs - _leftWorldAt < AwaySendMs;
            }
            WMActor actor = ff9.w_moveActorPtr;
            Actor original = actor.originalActor;
            state.Control = ff9.w_moveCHRControl_No;
            state.Model = original != null ? original.model : (UInt16)0;
            state.Anim = original != null ? original.anim : (UInt16)0;
            state.Position = actor.RealPosition;
            state.Rotation = actor.transform.rotation;
            state.Ground = original != null && ff9.w_moveCHRStatus != null && original.index < ff9.w_moveCHRStatus.Length ? ff9.w_moveCHRStatus[original.index].ground_height : state.Position.y;
            Boolean still = false;
            if (!_hasLastSampled || nowMs - _lastSampledAt >= 40) // two samples apart in time (not an extra call this frame)
            {
                still = _hasLastSampled && (state.Position - _lastSampled).sqrMagnitude < 0.0001f;
                _lastSampled = state.Position;
                _lastSampledAt = nowMs;
                _hasLastSampled = true;
            }
            if (actor == ff9.w_movePlanePtr)
                state.Mode = WorldMode.Airship;
            else if (original != null && original.IsChocoboIndex)
                state.Mode = WorldMode.Chocobo;
            else if (original != null && original.IsPlayerCharacterIndex)
                state.Mode = WorldCoop.Passenger != 0 || !AnyRendererOn(actor) ? WorldMode.Hidden : WorldMode.Foot;
            else
                state.Mode = WorldMode.Vehicle;
            if (still && (state.Mode == WorldMode.Foot || state.Mode == WorldMode.Chocobo))
                _standingAnim[state.Model] = state.Anim;
            return true;
        }

        private static Boolean AnyRendererOn(WMActor actor)
        {
            foreach (Renderer r in actor.GetComponentsInChildren<Renderer>(true))
                if (r.enabled)
                    return true;
            return false;
        }

        // ---- Receiving --------------------------------------------------------------------------------------------

        private static WorldGhostState _scratch;

        public static void OnPacket(UInt64 peer, Byte[] data, Int32 offset, Int32 length, Double nowMs)
        {
            if (!_scratch.Deserialize(data, offset, length))
            {
                IgnoredBadPacket++;
                return;
            }
            Received++;
            if (!Peers.TryGetValue(peer, out Peer p))
                Peers[peer] = p = new Peer { Id = peer };
            Int32 ahead = p.HasNewest ? unchecked((Int32)(_scratch.Tick - p.NewestTick)) : 1;
            if (ahead < -60000 || ahead > 60000)
            {
                p.States.Clear(); // the sender restarted its game (or was away a long time): a new timeline
                p.HasOffset = false;
            }
            else if (ahead <= 0)
                return; // late or repeated (unreliable channel)
            p.NewestTick = _scratch.Tick;
            p.HasNewest = true;
            p.LastReceivedMs = nowMs;
            Double gap = nowMs - _scratch.Tick;
            if (!p.HasOffset || gap < p.Offset)
                p.Offset = gap;
            else
                p.Offset += (gap - p.Offset) * 0.002; // drift (clock rates differ slightly)
            p.HasOffset = true;
            p.States.Add(new Sample { SenderMs = _scratch.Tick, State = _scratch });
            if (p.States.Count > MaxStates)
                p.States.RemoveAt(0);
        }

        public static void Remove(UInt64 peer)
        {
            if (Peers.TryGetValue(peer, out Peer p))
            {
                Destroy(p);
                Peers.Remove(peer);
            }
        }

        /// <summary>A player's interpolated state now (false: nothing fresh from them).</summary>
        internal static Boolean TryGetState(UInt64 peer, Double nowMs, out WorldGhostState state)
        {
            state = default(WorldGhostState);
            return Peers.TryGetValue(peer, out Peer p) && TryInterpolate(p, nowMs, out state);
        }

        /// <summary>The players whose newest state is fresh, with that state's mode.</summary>
        internal static IEnumerable<KeyValuePair<UInt64, WorldMode>> FreshModes(Double nowMs)
        {
            foreach (Peer p in Peers.Values)
                if (p.States.Count > 0 && nowMs - p.LastReceivedMs < StaleMs)
                    yield return new KeyValuePair<UInt64, WorldMode>(p.Id, p.States[p.States.Count - 1].State.Mode);
        }

        private static Boolean TryInterpolate(Peer p, Double nowMs, out WorldGhostState state)
        {
            state = default(WorldGhostState);
            if (p.States.Count == 0 || nowMs - p.LastReceivedMs >= StaleMs)
                return false;
            Double target = nowMs - p.Offset - DelayMs;
            List<Sample> s = p.States;
            if (target <= s[0].SenderMs)
            {
                state = s[0].State;
                return true;
            }
            for (Int32 i = s.Count - 1; i > 0; i--)
            {
                Sample a = s[i - 1], b = s[i];
                if (target < a.SenderMs)
                    continue;
                if (target >= b.SenderMs)
                {
                    state = b.State; // past the newest: hold it (no extrapolation)
                    return true;
                }
                Single t = (Single)((target - a.SenderMs) / (b.SenderMs - a.SenderMs));
                state = t < 0.5f ? a.State : b.State; // mode, model, animation: the nearer one
                if (a.State.Mode == b.State.Mode && a.State.Model == b.State.Model && (a.State.Position - b.State.Position).sqrMagnitude < SnapDistance * SnapDistance)
                {
                    state.Position = Vector3.Lerp(a.State.Position, b.State.Position, t);
                    state.Rotation = Quaternion.Slerp(a.State.Rotation, b.State.Rotation, t);
                    state.Ground = Mathf.Lerp(a.State.Ground, b.State.Ground, t);
                }
                return true;
            }
            state = s[s.Count - 1].State;
            return true;
        }

        // ---- Drawing ----------------------------------------------------------------------------------------------

        /// <summary>Camera.onPreCull (once a frame): places every world ghost.</summary>
        public static void ApplyRender(Double nowMs)
        {
            if (Time.frameCount == _renderedFrame)
                return;
            _renderedFrame = Time.frameCount;
            if (!OnWorldMap)
            {
                DestroyAll();
                return;
            }
            WMWorld world = ff9.world;
            Shown = 0;
            List<UInt64> gone = null;
            foreach (Peer p in Peers.Values)
            {
                if (!TryInterpolate(p, nowMs, out WorldGhostState state))
                {
                    Destroy(p);
                    if (nowMs - p.LastReceivedMs > 60000)
                        (gone = gone ?? new List<UInt64>()).Add(p.Id);
                    continue;
                }
                if (!state.Drawn)
                {
                    Destroy(p);
                    continue;
                }
                try
                {
                    if (Place(world, p, state))
                        Shown++;
                }
                catch (Exception err)
                {
                    Log.Error(err, $"[Online] World ghost of {SteamNative.Tag(p.Id)}: drawing failed; it's removed");
                    Destroy(p);
                    p.States.Clear();
                }
            }
            if (gone != null)
                foreach (UInt64 id in gone)
                    Peers.Remove(id);
        }

        private static Boolean Place(WMWorld world, Peer p, WorldGhostState state)
        {
            if (p.Root == null || p.ShownModel != state.Model)
            {
                Destroy(p);
                if (!Create(world, p, state.Model))
                    return false;
            }
            world.SetAbsolutePositionOf(out Vector3 unity, state.Position);
            if (unity == Vector3.zero && state.Position != Vector3.zero)
            {
                SetActive(p, false); // outside the world's blocks
                return false;
            }
            SetActive(p, true);
            p.Root.transform.position = unity;
            p.Root.transform.rotation = state.Rotation;
            PlayAnimation(p, state.Anim);
            // shadow: on the ground under it, sized and shaded like this PC's own player's
            if (p.Shadow != null)
            {
                p.Shadow.transform.position = new Vector3(unity.x, state.Ground + 0.1f, unity.z);
                WMShadow own = LocalShadow(world);
                if (own != null)
                {
                    p.Shadow.transform.localScale = own.transform.localScale;
                    if (p.ShadowMaterial != null && own.Material != null && own.Material.HasProperty("_Amp"))
                        p.ShadowMaterial.SetFloat("_Amp", own.Material.GetFloat("_Amp"));
                }
            }
            // name tag: above its head, facing the camera
            if (p.Tag != null)
            {
                p.Tag.SetActive(ShowNames);
                if (ShowNames)
                    PlaceTag(world, p, unity);
            }
            p.LastPosition = state.Position;
            p.Placed = true;
            return true;
        }

        private static Boolean Create(WMWorld world, Peer p, UInt16 model)
        {
            if (!FF9BattleDB.GEO.TryGetValue(model, out String name) || String.IsNullOrEmpty(name))
                return false;
            GameObject go = ModelFactory.CreateModel(name, false, true, Configuration.Graphics.WorldSmoothTexture);
            if (go == null)
                return false;
            p.Root = new GameObject($"OnlineWorldGhost {SteamNative.Tag(p.Id)}");
            p.Model = go;
            p.ShownModel = model;
            p.ShownAnim = UInt16.MaxValue;
            go.transform.parent = p.Root.transform;
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.Euler(0f, 0f, 180f);
            go.transform.localScale = Vector3.one * 0.00390625f; // WMActor.SetScale(64, 64, 64)
            p.Animation = go.GetComponent<Animation>();
            Int32 layer = LocalLayer();
            if (layer >= 0)
                SetLayer(go, layer);
            // shadow: the world's own shadow, outside WMWorld's list (that list follows the world's actors)
            GameObject prefab = Resources.Load<GameObject>(ShadowPrefab);
            if (prefab != null)
            {
                p.Shadow = Object.Instantiate(prefab);
                p.Shadow.name = $"OnlineWorldGhost {SteamNative.Tag(p.Id)} shadow";
                p.Shadow.transform.parent = world.WorldMapEffectRoot;
                p.Shadow.transform.localScale = Vector3.one * 0.8545044f;
                Renderer r = p.Shadow.GetComponent<Renderer>();
                p.ShadowMaterial = r != null ? r.material : null;
            }
            // name tag: the player's Steam name (on screen only, never logged)
            if (_tagFont == null)
                _tagFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (_tagFont != null)
            {
                p.Tag = new GameObject($"OnlineWorldGhost {SteamNative.Tag(p.Id)} name");
                TextMesh text = p.Tag.AddComponent<TextMesh>();
                text.font = _tagFont;
                text.fontSize = 48;
                text.characterSize = 1f;
                text.anchor = TextAnchor.LowerCenter;
                text.alignment = TextAlignment.Center;
                text.color = Color.white;
                text.text = SteamNative.GetName(p.Id);
                p.Tag.GetComponent<MeshRenderer>().material = _tagFont.material;
                if (layer >= 0)
                    p.Tag.layer = layer;
                p.TagScaled = false;
            }
            Log.Message($"[Online] World ghost of {SteamNative.Tag(p.Id)} shown (model {model} {name})");
            return true;
        }

        private static void PlayAnimation(Peer p, UInt16 anim)
        {
            if (p.Animation == null || anim == p.ShownAnim)
                return;
            if (!FF9DBAll.AnimationDB.TryGetValue(anim, out String name) || String.IsNullOrEmpty(name))
            {
                p.ShownAnim = anim; // unknown: keep what plays, and don't look it up every frame
                return;
            }
            if (p.Animation[name] == null)
                AnimationFactory.AddAnimWithAnimatioName(p.Model, name);
            AnimationState state = p.Animation[name];
            if (state == null)
                return;
            state.wrapMode = WrapMode.Loop;
            p.Animation.CrossFade(name, p.ShownAnim == UInt16.MaxValue ? 0f : 0.15f);
            p.ShownAnim = anim;
        }

        private static void PlaceTag(WMWorld world, Peer p, Vector3 unity)
        {
            // Height: the top of the model; size: a quarter of the model's height for a line (measured once)
            Bounds? body = ModelBounds(p.Model);
            Single height = body.HasValue ? body.Value.max.y - unity.y : 1f;
            if (height > 0.05f)
                p.TagHeight = height;
            p.Tag.transform.position = unity + Vector3.up * (p.TagHeight + 0.05f);
            Camera camera = world.MainCamera;
            if (camera != null)
                p.Tag.transform.rotation = camera.transform.rotation;
            if (!p.TagScaled)
            {
                Renderer r = p.Tag.GetComponent<Renderer>();
                Single line = r != null ? r.bounds.size.y : 0f;
                if (line > 0f && p.TagHeight > 0f)
                {
                    p.Tag.transform.localScale = Vector3.one * (p.TagHeight * 0.25f / line);
                    p.TagScaled = true;
                }
            }
        }

        private static Bounds? ModelBounds(GameObject go)
        {
            Bounds? result = null;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled)
                    continue;
                if (result.HasValue)
                {
                    Bounds b = result.Value;
                    b.Encapsulate(r.bounds);
                    result = b;
                }
                else
                    result = r.bounds;
            }
            return result;
        }

        private static WMShadow LocalShadow(WMWorld world)
        {
            WMActor human = ff9.w_moveHumanPtr;
            return human != null && human.originalActor != null ? world.GetShadow(human.originalActor) : null;
        }

        private static Int32 LocalLayer()
        {
            WMActor human = ff9.w_moveHumanPtr ?? ff9.w_moveActorPtr;
            Animation a = human != null ? human.Animation : null;
            return a != null ? a.gameObject.layer : -1;
        }

        private static void SetLayer(GameObject go, Int32 layer)
        {
            go.layer = layer;
            foreach (Transform t in go.transform)
                SetLayer(t.gameObject, layer);
        }

        private static void SetActive(Peer p, Boolean on)
        {
            if (p.Root != null && p.Root.activeSelf != on)
                p.Root.SetActive(on);
            if (p.Shadow != null && p.Shadow.activeSelf != on)
                p.Shadow.SetActive(on);
            if (p.Tag != null && !on && p.Tag.activeSelf)
                p.Tag.SetActive(false);
        }

        private static void Destroy(Peer p)
        {
            if (p.Root != null)
                Object.Destroy(p.Root);
            if (p.Shadow != null)
                Object.Destroy(p.Shadow);
            if (p.Tag != null)
                Object.Destroy(p.Tag);
            p.Root = p.Model = p.Shadow = p.Tag = null;
            p.Animation = null;
            p.ShadowMaterial = null;
            p.ShownModel = 0;
            p.Placed = false;
        }

        private static void DestroyAll()
        {
            foreach (Peer p in Peers.Values)
                if (p.Root != null || p.Shadow != null || p.Tag != null)
                    Destroy(p);
        }
    }
}
