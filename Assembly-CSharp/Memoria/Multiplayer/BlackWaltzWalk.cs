using Memoria.Prime;
using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Memoria.Multiplayer
{
    /// <summary>
    /// Gives the normal Black Waltz No. 3 field model (167, GEO_SUB_F0_BW3), which only floats, the beaten one's
    /// stand and walk (model 207, GEO_SUB_F1_BW3). The two models' bone trees have the same shape but are numbered
    /// differently, so Unity can't play one's clips on the other: a hidden beaten model plays them instead, and every
    /// frame its bone rotations are copied onto the matching bones of the visible model (BonePairs, worked out by tree
    /// shape and rest positions and chosen by eye in game). Standing and walking blend over BlendTime.
    /// Added by MultiplayerField.Spawn to an extra player's character that uses model 167.
    /// </summary>
    public sealed class BlackWaltzWalk : MonoBehaviour
    {
        public const UInt16 NormalModel = 167;
        private const String SourceModel = "GEO_SUB_F1_BW3";
        private const String WalkClip = "ANH_SUB_F1_BW3_WALK";
        private const String IdleClip = "ANH_SUB_F1_BW3_IDLE";
        private const Single BlendTime = 0.2f;
        private static readonly Vector3 FarAway = new Vector3(100000f, 100000f, 100000f);

        /// <summary>Normal model's bone index -> beaten model's bone index.</summary>
        private static readonly Int32[] BonePairs =
        {
            0, 21, 25, 28, 29, 30, 26, 27, 31, 34, 35, 36, 32, 33, 23, 24, 22,
            1, 6, 7, 8, 9, 12, 13, 14, 15, 16, 10, 11, 17, 18, 19, 20, 2, 3, 4, 5,
        };

        private Actor _actor;
        private GameObject _source;
        private Animation _sourceAnimation;
        private AnimationClip _walk;
        private AnimationClip _idle;
        private readonly List<Transform> _targetBones = new List<Transform>();
        private readonly List<Transform> _sourceBones = new List<Transform>();
        private Quaternion[] _idleRot;
        private Vector3 _lastPos;
        private Single _walkTime;
        private Single _idleTime;
        private Single _movingFor;
        private Single _walkWeight;

        public static void AttachTo(Actor actor)
        {
            if (actor?.go == null || actor.model != NormalModel || actor.go.GetComponent<BlackWaltzWalk>() != null)
                return;
            BlackWaltzWalk component = actor.go.AddComponent<BlackWaltzWalk>();
            if (!component.Init(actor))
                Destroy(component);
        }

        private Boolean Init(Actor actor)
        {
            _actor = actor;
            _source = ModelFactory.CreateModel(SourceModel, false, false);
            if (_source == null)
            {
                Log.Warning($"[Multiplayer] Black Waltz walk: could not load {SourceModel}; it floats instead");
                return false;
            }
            _source.name = "MultiplayerBlackWaltzWalkSource";
            _source.transform.position = FarAway;
            foreach (Renderer renderer in _source.GetComponentsInChildren<Renderer>())
                renderer.enabled = false;
            AnimationFactory.AddAnimWithAnimatioName(_source, WalkClip);
            AnimationFactory.AddAnimWithAnimatioName(_source, IdleClip);
            _sourceAnimation = _source.GetComponent<Animation>();
            _walk = _sourceAnimation?.GetClip(WalkClip);
            _idle = _sourceAnimation?.GetClip(IdleClip);

            Dictionary<String, Transform> targetBones = CollectBones(actor.go.transform);
            Dictionary<String, Transform> sourceBones = CollectBones(_source.transform);
            for (Int32 i = 0; i < BonePairs.Length; i++)
            {
                if (targetBones.TryGetValue(BoneName(i), out Transform target) && sourceBones.TryGetValue(BoneName(BonePairs[i]), out Transform source))
                {
                    _targetBones.Add(target);
                    _sourceBones.Add(source);
                }
            }
            if (_walk == null || _idle == null || _targetBones.Count != BonePairs.Length)
            {
                Log.Warning($"[Multiplayer] Black Waltz walk: clips or bones missing ({_targetBones.Count}/{BonePairs.Length} bones); it floats instead");
                Object.Destroy(_source);
                _source = null;
                return false;
            }
            _idleRot = new Quaternion[_sourceBones.Count];
            _lastPos = actor.go.transform.position;
            return true;
        }

        private void LateUpdate()
        {
            if (_actor?.go == null || _source == null)
                return;
            Vector3 position = _actor.go.transform.position;
            Vector3 delta = position - _lastPos;
            delta.y = 0f;
            _lastPos = position;
            // A little hysteresis so the walk doesn't flicker on and off between movement ticks
            _movingFor = delta.sqrMagnitude > 0.01f ? 0.15f : _movingFor - Time.deltaTime;
            _walkWeight = Mathf.MoveTowards(_walkWeight, _movingFor > 0f ? 1f : 0f, Time.deltaTime / BlendTime);

            _idleTime = (_idleTime + Time.deltaTime) % _idle.length;
            Sample(IdleClip, _idleTime);
            for (Int32 i = 0; i < _sourceBones.Count; i++)
                _idleRot[i] = _sourceBones[i].localRotation;
            if (_walkWeight > 0f)
            {
                // Walk cycle speed: the character's walk/run animation speed (FieldModels' %, 16 = 100%)
                Single speed = _actor.fieldMapActorController != null ? _actor.fieldMapActorController.multiplayerMoveAnimSpeed / 16f : 1f;
                _walkTime = (_walkTime + Time.deltaTime * speed) % _walk.length;
                Sample(WalkClip, _walkTime);
            }
            else
            {
                _walkTime = 0f; // each walk starts from the beginning of its cycle
            }
            for (Int32 i = 0; i < _targetBones.Count; i++)
                _targetBones[i].localRotation = _walkWeight <= 0f ? _idleRot[i] : Quaternion.Slerp(_idleRot[i], _sourceBones[i].localRotation, _walkWeight);
        }

        private void Sample(String clipName, Single time)
        {
            AnimationState state = _sourceAnimation[clipName];
            state.enabled = true;
            state.weight = 1f;
            state.time = time;
            _sourceAnimation.Sample();
            state.enabled = false;
        }

        private void OnDestroy()
        {
            if (_source != null)
                Object.Destroy(_source);
        }

        private static String BoneName(Int32 index) => "bone" + index.ToString("D3");

        private static Dictionary<String, Transform> CollectBones(Transform root)
        {
            Dictionary<String, Transform> bones = new Dictionary<String, Transform>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("bone", StringComparison.Ordinal) && !bones.ContainsKey(t.name))
                    bones[t.name] = t;
            return bones;
        }
    }
}
