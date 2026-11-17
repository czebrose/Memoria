using Memoria.Prime;
using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Memoria.Online.Ghosts
{
    /// <summary>
    /// Another player's character in the local field: a detached field actor (no event-script entry) whose position,
    /// facing and animation come from received <see cref="GhostState"/>s, interpolated on a timeline delayed by
    /// <see cref="DelayMs"/> (one send interval plus measured jitter, 80-250 ms).
    ///
    /// Spawned like the local-multiplayer branch's field followers (MultiplayerField.Spawn): the parameterless Actor
    /// constructor keeps it out of the event engine's object list, so the field script can't see, run or move it,
    /// it doesn't collide with anyone, and the engine's per-object loops (ProcessEvents, ff9fieldCharService, the
    /// 60 FPS smoothing) skip it. Everything those loops would do for it, it does itself:
    ///   - movement: FieldMapActorController.UpdateMovement returns early for ghosts (marked engine edit);
    ///   - pose: applied every rendered frame from Camera.onPreCull (<see cref="ApplyToTransform"/>), so it's smooth
    ///     at any frame rate without Memoria's smoothing;
    ///   - animation: its own frame clock, sampled at render time;
    ///   - lighting: fldmcf lights detached actors whose controller is a ghost (marked engine edit).
    /// </summary>
    public sealed class GhostActor
    {
        /// <summary>A jump between two consecutive states longer than this is a teleport (warp, ladder): snap, don't slide.</summary>
        public const Single TeleportDistance = 400f;
        private const Int32 MaxBuffered = 64;
        /// <summary>Arrival history kept for the clock offset and jitter (~10 s at 15 Hz).</summary>
        private const Int32 TimingWindow = 150;
        public const Double MinDelayMs = 80;
        public const Double MaxDelayMs = 250;
        /// <summary>States later than this are connection-setup backlog, not jitter (see UpdateTiming).</summary>
        public const Double BacklogMs = 1000;

        public readonly UInt64 Peer;
        public readonly Int32 Uid;
        public readonly GhostModel Model;
        public readonly FieldMap FieldMap;
        public Actor Actor { get; private set; }
        public FieldMapActorController Controller { get; private set; }

        public Boolean Visible { get; private set; } = true;
        public Boolean IsMoving { get; private set; }
        public GhostState Newest { get; private set; }
        public Double LastReceivedMs { get; private set; }

        // Statistics
        public Int32 Received, DroppedOld, Snaps, Hides;
        /// <summary>95th percentile of how late states arrive compared with the earliest one in the window.</summary>
        public Double JitterMs { get; private set; }
        /// <summary>How far behind its newest state the ghost is drawn (adaptive unless a fixed delay is set).</summary>
        public Double DelayMs { get; private set; } = 100;
        public Double MaxJitterMs { get; private set; }

        private readonly List<GhostState> _buffer = new List<GhostState>(MaxBuffered);
        private Boolean _hasTick;
        private Double _clockOffset; // localMs - senderMs, lowest in the window (= least delayed packet)
        private Boolean _hasOffset;
        private readonly List<Double> _offsets = new List<Double>(TimingWindow + 1);
        private readonly Double[] _lateness = new Double[TimingWindow + 1];
        private UInt32 _lastSnapTick;

        private Vector3 _pose;
        private Single _facing;
        private UInt16 _anim = UInt16.MaxValue;
        private Double _animClock;
        private Int32 _frameCount = 1;
        private Animation _animation;
        private Renderer[] _renderers;
        private readonly HashSet<UInt16> _loadedAnims = new HashSet<UInt16>();

        private GhostActor(UInt64 peer, Int32 uid, GhostModel model, FieldMap fieldMap)
        {
            Peer = peer;
            Uid = uid;
            Model = model;
            FieldMap = fieldMap;
        }

        /// <summary>Unity-null-aware: false once the field map (and with it the ghost's objects) is destroyed.</summary>
        public Boolean IsAlive => Actor != null && Actor.go != null && Controller != null;

        public static GhostActor Spawn(UInt64 peer, Int32 uid, GhostModel model, FieldMap fieldMap, GhostState first)
        {
            if (!FF9BattleDB.GEO.TryGetValue(model.Model, out String modelName))
                throw new Exception($"no geometry name for model {model.Model}");

            GhostActor ghost = new GhostActor(peer, uid, model, fieldMap);
            Vector3 position = new Vector3(first.X, first.Y, first.Z);
            Actor actor = new Actor
            {
                uid = (Byte)uid,
                sid = 0,
                model = model.Model,
                eye = (Int16)(-4 * model.HeadHeight),
                flags = 5,
                state = EventEngine.stateRunning,
                speed = 30,
                speedth = 31,
                aspeed = 16,
                aspeed0 = 16,
                outFrame = Byte.MaxValue,
                omega = 16,
                collRad = model.Size,
                talkRad = model.Size,
                bgiRad = model.Size,
                idle = model.Idle,
                walk = model.Walk,
                run = model.Run,
                turnl = model.Idle,
                turnr = model.Idle,
                posField = position,
                rotField = Quaternion.identity,
            };
            for (Int32 i = 0; i < actor.idleSpeed.Length; i++)
                actor.idleSpeed[i] = 16;

            // Model, as the SetModel opcode builds it
            actor.go = ModelFactory.CreateModel(modelName, false, true, Configuration.Graphics.ElementsSmoothTexture);
            if (actor.go == null)
                throw new Exception($"model {modelName} could not be created");
            GeoTexAnim.addTexAnim(actor.go, modelName);
            if (ModelFactory.garnetShortHairTable.Contains(modelName))
            {
                actor.garnet = true;
                actor.shortHair = FF9StateSystem.EventState.ScenarioCounter >= 10300;
            }
            Int32 meshCount = 0;
            foreach (Transform child in actor.go.transform)
                if (child.name.Contains("mesh"))
                    meshCount++;
            if (actor.garnet)
                meshCount++;
            actor.meshIsRendering = new Boolean[meshCount];
            for (Int32 i = 0; i < meshCount; i++)
                actor.meshIsRendering[i] = true;
            actor.go.name = $"Ghost_{peer:X}";
            ghost.Actor = actor;
            foreach (UInt16 anim in new[] { model.Idle, model.Walk, model.Run })
                ghost.EnsureAnimLoaded(anim);

            // Registration the field's renderer, shadows and lighting need (keyed by UID)
            FF9StateSystem.Common.FF9.charArray[uid] = new FF9Char { geo = actor.go, evt = actor };
            FF9StateSystem.Field.FF9Field.loc.map.charStateArray[uid] = new FF9FieldCharState();
            FF9StateSystem.Field.FF9Field.loc.map.shadowArray[uid] = new FF9Shadow();

            fieldMap.AddFieldChar(actor.go, actor.posField, actor.rotField, false, actor, false);
            FieldMapActorController controller = actor.fieldMapActorController;
            controller.radius = model.Size * 4f;
            controller.ghost = ghost;
            FieldMapActor fieldActor = actor.go.GetComponent<FieldMapActor>();
            if (fieldActor != null)
                fieldActor.CharRadius = model.Size * 4f;
            ghost.Controller = controller;
            ghost._animation = actor.go.GetComponent<Animation>();
            ghost._renderers = actor.go.GetComponentsInChildren<Renderer>(true);

            ghost._pose = position;
            ghost._facing = first.FacingDegrees;
            ghost.SetTriangle(first.Tri);
            ghost.SetAnim(model.Idle, 0);
            ghost.WriteToEngine();
            return ghost;
        }

        /// <summary>Adds a received state. Returns false (and counts it) if it's not newer than the newest one.</summary>
        public Boolean Push(GhostState state, Double nowMs)
        {
            if (_hasTick && (Int32)(state.Tick - Newest.Tick) <= 0)
            {
                DroppedOld++;
                return false;
            }
            _hasTick = true;
            Newest = state;
            LastReceivedMs = nowMs;
            Received++;
            UpdateTiming(nowMs - state.Tick);
            _buffer.Add(state);
            if (_buffer.Count > MaxBuffered)
                _buffer.RemoveAt(0);
            return true;
        }

        /// <summary>Forget the buffered states (after a silence): the next state is shown where it is, without sliding there.</summary>
        public void ResetTimeline()
        {
            _buffer.Clear();
            _offsets.Clear();
            _hasOffset = false;
        }

        /// <summary>
        /// Clock offset = the lowest (localNow - senderTick) of the last ~10 s: the least delayed state, so sender and
        /// receiver clocks needn't agree, and a windowed minimum follows slow clock drift. Jitter = 95th percentile of
        /// how much later than that the others arrived, ignoring states more than <see cref="BacklogMs"/> late: those are
        /// the backlog queued while a P2P connection was being set up (~3.5 s, two-PC test), not jitter, and counting
        /// them pinned the delay at its 250 ms maximum for ~10 s after every (re)join.
        /// </summary>
        private void UpdateTiming(Double offset)
        {
            _offsets.Add(offset);
            if (_offsets.Count > TimingWindow)
                _offsets.RemoveAt(0);
            Double min = Double.MaxValue;
            foreach (Double o in _offsets)
                min = Math.Min(min, o);
            _clockOffset = min;
            _hasOffset = true;
            Int32 n = 0;
            foreach (Double o in _offsets)
                if (o - min <= BacklogMs)
                    _lateness[n++] = o - min;
            Array.Sort(_lateness, 0, n);
            JitterMs = n > 0 ? _lateness[Math.Min(n - 1, (Int32)(n * 0.95))] : 0;
            if (n >= 30)
                MaxJitterMs = Math.Max(MaxJitterMs, JitterMs);
        }

        /// <summary>Computes the pose for this frame from the buffered states.</summary>
        /// <param name="fixedDelayMs">Negative: adaptive delay (one send interval + jitter, 80-250 ms).</param>
        public void Update(Double nowMs, Single deltaTime, Double sendIntervalMs, Double fixedDelayMs, Boolean interpolate)
        {
            if (!IsAlive || _buffer.Count == 0)
                return;

            DelayMs = fixedDelayMs >= 0 ? fixedDelayMs : Math.Max(MinDelayMs, Math.Min(MaxDelayMs, sendIntervalMs + JitterMs));
            Double renderTick = nowMs - _clockOffset - DelayMs;
            // Keep one state at or before the render time, drop the older ones
            while (_buffer.Count >= 2 && _buffer[1].Tick <= renderTick)
                _buffer.RemoveAt(0);

            GhostState a = _buffer[0];
            GhostState b = _buffer.Count >= 2 ? _buffer[1] : a;
            Single t = 0f;
            if (_buffer.Count >= 2 && renderTick > a.Tick && b.Tick > a.Tick)
                t = Mathf.Clamp01((Single)((renderTick - a.Tick) / (b.Tick - a.Tick)));
            Vector3 posA = new Vector3(a.X, a.Y, a.Z);
            Vector3 posB = new Vector3(b.X, b.Y, b.Z);
            if (!interpolate)
            {
                t = 0f;
            }
            else if ((posB - posA).sqrMagnitude > TeleportDistance * TeleportDistance)
            {
                t = 0f; // hold, then jump when b becomes the current state
                if (_lastSnapTick != b.Tick)
                {
                    _lastSnapTick = b.Tick;
                    Snaps++;
                }
            }

            _pose = Vector3.Lerp(posA, posB, t);
            _facing = Mathf.LerpAngle(a.FacingDegrees, b.FacingDegrees, t);
            SetTriangle(t < 0.5f ? a.Tri : b.Tri);
            GhostState current = t < 0.5f ? a : b;
            IsMoving = current.Motion != GhostMotion.Idle;
            UpdateAnimation(current, deltaTime);
            WriteToEngine();
        }

        /// <summary>Called right before cameras render: puts the model exactly at this frame's pose and animation frame.</summary>
        public void ApplyToTransform()
        {
            if (!Visible || !IsAlive)
                return;
            Transform transform = Actor.go.transform;
            transform.localPosition = _pose;
            transform.localRotation = Quaternion.AngleAxis(_facing, Vector3.up);
            // Some fields shrink their characters to fit the background (SetObjectSize, opcode 0x9F, e.g. the cargo
            // ship's engine room). The script only does it to its own objects; a ghost takes the local leader's size,
            // since both players are in the same field.
            transform.localScale = GhostManager.LeaderScale;
            if (_animation == null)
                return;
            String name = FF9DBAll.AnimationDB.GetValue(_anim);
            AnimationState state = name != null ? _animation[name] : null;
            if (state == null)
                return;
            if (!_animation.IsPlaying(name))
            {
                _animation.clip = state.clip;
                _animation.Play(name);
            }
            state.speed = 0f;
            state.time = (Single)(_animClock / _frameCount) * state.length;
            _animation.Sample();
        }

        public void SetVisible(Boolean visible)
        {
            if (visible == Visible || !IsAlive)
                return;
            Visible = visible;
            if (!visible)
                Hides++;
            if (_renderers != null)
                foreach (Renderer renderer in _renderers)
                    if (renderer != null)
                        renderer.enabled = visible;
            if (FF9StateSystem.Common.FF9.charArray.ContainsKey(Uid) && FF9StateSystem.Field.FF9Field.loc.map.shadowArray.ContainsKey(Uid))
            {
                if (visible)
                    ff9shadow.FF9ShadowOnField(Uid);
                else
                    ff9shadow.FF9ShadowOffField(Uid);
            }
        }

        private void UpdateAnimation(GhostState current, Single deltaTime)
        {
            // Same model as the sender: play its exact animation (covers ladders, jumps, scripted poses).
            // Different model: the sender's animation ids mean nothing for it, use its own idle/walk/run.
            UInt16 wanted;
            Boolean exact = current.Model == Model.Model && current.Anim != 0 && current.Anim != UInt16.MaxValue;
            if (exact && EnsureAnimLoaded(current.Anim))
            {
                wanted = current.Anim;
            }
            else
            {
                exact = false;
                switch (current.Motion)
                {
                    case GhostMotion.Walk: wanted = Model.Walk; break;
                    case GhostMotion.Run: wanted = Model.Run; break;
                    default: wanted = Model.Idle; break;
                }
            }

            if (wanted != _anim)
            {
                SetAnim(wanted, exact ? current.AnimFrame : 0);
                return;
            }
            // Same frame rate as the engine's animation ticks (aspeed 16 = one frame per field tick)
            _animClock += deltaTime * Configuration.Graphics.FieldTPS * (Actor.aspeed / 16.0);
            if (_animClock >= _frameCount)
                _animClock %= _frameCount;
        }

        private void SetAnim(UInt16 anim, Int32 frame)
        {
            _anim = anim;
            _frameCount = Math.Max(1, EventEngineUtils.GetCharAnimFrame(Actor.go, anim));
            _animClock = Math.Min(frame, _frameCount - 1);
            Actor.anim = anim;
            Actor.frameN = (Byte)Math.Min(_frameCount, Byte.MaxValue);
            Actor.frameDif = 0;
            Actor.aspeed = Actor.aspeed0;
        }

        private Boolean EnsureAnimLoaded(UInt16 anim)
        {
            if (_loadedAnims.Contains(anim))
                return true;
            String name = FF9DBAll.AnimationDB.GetValue(anim);
            if (String.IsNullOrEmpty(name))
                return false;
            AnimationFactory.AddAnimWithAnimatioName(Actor.go, name);
            Animation animation = Actor.go.GetComponent<Animation>();
            if (animation == null || animation.GetClip(name) == null)
                return false;
            _loadedAnims.Add(anim);
            return true;
        }

        private void SetTriangle(Int16 tri)
        {
            if (Controller == null)
                return;
            WalkMesh walkMesh = Controller.walkMesh ?? FieldMap.walkMesh;
            if (walkMesh != null && tri >= 0 && tri < walkMesh.tris.Count)
            {
                Controller.activeTri = tri;
                Controller.activeFloor = walkMesh.tris[tri].floorIdx;
            }
        }

        /// <summary>Keeps the engine-side copies (controller position, actor fields) equal to the pose.</summary>
        private void WriteToEngine()
        {
            if (Controller == null)
                return;
            Controller.curPos = _pose;
            Controller.lastPos = _pose;
            Controller.SetGhostMoving(IsMoving);
            Actor.pos[0] = _pose.x;
            Actor.pos[1] = _pose.y;
            Actor.pos[2] = _pose.z;
            Actor.rotAngle[1] = _facing;
            Actor.animFrame = (Byte)Math.Min((Int32)_animClock, Byte.MaxValue);
        }

        /// <summary>
        /// Removes the ghost in the middle of a field, the way EventEngine.DisposeObj does: unregistering from
        /// HonoBehaviorSystem with dispose drops the behaviours from its update list and destroys the object at a safe
        /// point. (The local-multiplayer branch found that a plain Destroy leaves the dead controller in that list and
        /// makes HonoBehaviorSystem.Update throw every frame.) The UID's charArray entries are kept, as DisposeObj
        /// does, until the object is gone; <see cref="GhostManager"/> removes them afterwards.
        /// Returns the FieldMapActor whose shadow must be destroyed once it's gone (null if none).
        /// </summary>
        public FieldMapActor Despawn()
        {
            GameObject go = Actor?.go;
            if (go == null)
                return null;
            FieldMapActorController controller = go.GetComponent<FieldMapActorController>();
            FieldMapActor fieldActor = go.GetComponent<FieldMapActor>();
            if (controller != null)
                controller.ghost = null;
            if (HonoBehaviorSystem.Exists && (controller != null || fieldActor != null))
            {
                if (fieldActor != null)
                    fieldActor.UnregisterHonoBehavior(true);
                if (controller != null)
                    controller.UnregisterHonoBehavior(true);
                return fieldActor;
            }
            Object.Destroy(go);
            return null;
        }

        public override String ToString() => $"ghost {Peer:X} ({Model.Name}, UID {Uid})";
    }
}
