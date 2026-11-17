using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace Memoria.Online.Ghosts
{
    /// <summary>
    /// Spike B test driver: a "shadow" ghost that replays your own movement. Attached in SceneDirector.Awake; does
    /// nothing until Ctrl+Shift+G.
    ///
    /// The local leader is sampled at 15 Hz (or 10) into a <see cref="GhostState"/>, serialized to bytes exactly as
    /// it would go to ITransport.Send (channel 1), held back by <see cref="FeedDelayMs"/> (so the ghost walks ~1 s
    /// behind you instead of inside you), then deserialized and fed to <see cref="GhostManager"/> as peer
    /// <see cref="ShadowPeer"/>. On top of that the ghost interpolates <see cref="GhostManager.InterpolationDelayMs"/>
    /// behind its newest state, as it would online.
    ///
    ///   Ctrl+Shift+G  shadow ghost on/off
    ///   Ctrl+Shift+P  pause the feed for 6 s (packets lost, as in a network outage): the ghost hides at 5 s
    ///   Ctrl+Shift+M  next ghost model (Vivi, Garnet, ..., "same as you")
    ///   Ctrl+Shift+R  sample rate 15 / 10 Hz
    ///   Ctrl+Shift+N  interpolation on/off (off = jump from state to state, for comparison)
    ///   Ctrl+Shift+K  network simulation on/off: 10% loss and 0-80 ms jitter (so some states arrive out of order)
    ///   Ctrl+Shift+U  show/hide the status box
    /// (Spike A uses H, I, J, L, O.)
    /// </summary>
    public sealed class GhostSpike : MonoBehaviour
    {
        public const UInt64 ShadowPeer = 0x5348414430574F; // "SHAD0W"
        private const Double FeedDelayMs = 1000;
        private const Double PauseMs = 6000;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static Double NowMs => Clock.Elapsed.TotalMilliseconds;

        private struct InFlight
        {
            public Double DeliverAt;
            public Byte[] Data;
        }

        private Boolean _enabled;
        private Int32 _rateHz = 15;
        private Boolean _netSim;
        private Double _pausedUntil;
        private Int32 _modelIndex = 1; // Vivi; All.Length = "same as you"
        private Boolean _showStatus = true;
        private Double _nextSample;
        private readonly List<InFlight> _inFlight = new List<InFlight>();
        private readonly Byte[] _sendBuffer = new Byte[GhostState.Size];
        private readonly System.Random _random = new System.Random();
        private GhostState _state;
        private Int32 _sampled, _lostInSim, _lostInPause, _clamped;
        private Double _nextSummary;
        private GUIStyle _style;

        private void OnEnable()
        {
            Camera.onPreCull += OnPreCull;
        }

        private void OnDisable()
        {
            Camera.onPreCull -= OnPreCull;
        }

        private static void OnPreCull(Camera camera)
        {
            try
            {
                GhostManager.ApplyRender();
            }
            catch (Exception err)
            {
                Log.Error(err, "[Ghosts] ApplyRender failed");
            }
        }

        private void Update()
        {
            try
            {
                HandleHotkeys();
                Double now = NowMs;
                if (_enabled)
                {
                    Sample(now);
                    Deliver(now);
                }
                GhostManager.Update(now, Time.deltaTime);
                if (_enabled && now >= _nextSummary)
                {
                    if (_nextSummary > 0)
                        LogSummary("SUMMARY");
                    _nextSummary = now + 30000;
                }
            }
            catch (Exception err)
            {
                Log.Error(err, "[Ghosts] Update failed; shadow ghost turned off");
                _enabled = false;
                _inFlight.Clear();
            }
        }

        private void HandleHotkeys()
        {
            Boolean ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            Boolean shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!ctrl || !shift)
                return;
            if (Input.GetKeyDown(KeyCode.G))
            {
                _enabled = !_enabled;
                _inFlight.Clear();
                if (!_enabled)
                {
                    LogSummary("OFF");
                    GhostManager.Remove(ShadowPeer, "shadow ghost turned off");
                }
                else
                {
                    ApplyModel();
                }
                Log.Message($"[Ghosts] Shadow ghost {(_enabled ? "on" : "off")}");
            }
            else if (Input.GetKeyDown(KeyCode.P))
            {
                _pausedUntil = NowMs + PauseMs;
                Log.Message($"[Ghosts] Feed paused for {PauseMs / 1000:0} s");
            }
            else if (Input.GetKeyDown(KeyCode.M))
            {
                _modelIndex = (_modelIndex + 1) % (GhostModels.All.Length + 1);
                ApplyModel();
                GhostManager.Remove(ShadowPeer, "model changed"); // comes back on the next state with the new model
            }
            else if (Input.GetKeyDown(KeyCode.R))
            {
                _rateHz = _rateHz == 15 ? 10 : 15;
                Log.Message($"[Ghosts] Sample rate {_rateHz} Hz");
            }
            else if (Input.GetKeyDown(KeyCode.N))
            {
                GhostManager.Interpolate = !GhostManager.Interpolate;
                Log.Message($"[Ghosts] Interpolation {(GhostManager.Interpolate ? "on" : "off")}");
            }
            else if (Input.GetKeyDown(KeyCode.K))
            {
                _netSim = !_netSim;
                Log.Message($"[Ghosts] Network simulation (10% loss, 0-80 ms jitter) {(_netSim ? "on" : "off")}");
            }
            else if (Input.GetKeyDown(KeyCode.U))
            {
                _showStatus = !_showStatus;
            }
        }

        private void ApplyModel()
        {
            GhostManager.ModelOverride = _modelIndex < GhostModels.All.Length ? GhostModels.All[_modelIndex] : null;
            Log.Message($"[Ghosts] Ghost model: {(GhostManager.ModelOverride != null ? GhostManager.ModelOverride.ToString() : "same as the sender")}");
        }

        /// <summary>Records the local leader like a client would before sending.</summary>
        private void Sample(Double now)
        {
            if (now < _nextSample)
                return;
            _nextSample = Math.Max(_nextSample + 1000.0 / _rateHz, now - 1000.0 / _rateHz);
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            FieldMapActorController leader = engine?.fieldmap?.playerController;
            Actor actor = leader?.originalActor;
            if (engine == null || engine.gMode != 1 || actor == null || leader.IsGhost)
                return;

            _state.Tick = (UInt32)now;
            _state.FieldId = (UInt16)FF9StateSystem.Common.FF9.fldMapNo;
            _state.Model = actor.model;
            _state.X = ToInt16(leader.curPos.x);
            _state.Y = ToInt16(leader.curPos.y);
            _state.Z = ToInt16(leader.curPos.z);
            _state.Tri = (Int16)Mathf.Clamp(leader.activeTri, -1, Int16.MaxValue);
            _state.FacingDegrees = actor.rotAngle[1];
            _state.Anim = actor.anim;
            _state.AnimFrame = actor.animFrame;
            _state.Motion = actor.anim == actor.idle ? GhostMotion.Idle
                : actor.anim == actor.walk ? GhostMotion.Walk
                : actor.anim == actor.run ? GhostMotion.Run
                : GhostMotion.Other;
            _sampled++;

            // What would go to ITransport.Send(peer, TransportChannel.Movement, buffer, length, false)
            Int32 length = _state.Serialize(_sendBuffer, 0);

            if (now < _pausedUntil)
            {
                _lostInPause++;
                return;
            }
            Double delay = FeedDelayMs;
            if (_netSim)
            {
                if (_random.NextDouble() < 0.10)
                {
                    _lostInSim++;
                    return;
                }
                delay += _random.NextDouble() * 80.0;
            }
            Byte[] copy = new Byte[length]; // stands in for the network; the real sender reuses _sendBuffer
            Buffer.BlockCopy(_sendBuffer, 0, copy, 0, length);
            _inFlight.Add(new InFlight { DeliverAt = now + delay, Data = copy });
        }

        private void Deliver(Double now)
        {
            // Delivered in arrival order, which differs from send order when jitter is on
            while (true)
            {
                Int32 next = -1;
                for (Int32 i = 0; i < _inFlight.Count; i++)
                    if (_inFlight[i].DeliverAt <= now && (next < 0 || _inFlight[i].DeliverAt < _inFlight[next].DeliverAt))
                        next = i;
                if (next < 0)
                    break;
                Byte[] data = _inFlight[next].Data;
                _inFlight.RemoveAt(next);
                GhostManager.OnPacket(ShadowPeer, data, 0, data.Length, now);
            }
        }

        private Int16 ToInt16(Single value)
        {
            Int32 rounded = Mathf.RoundToInt(value);
            if (rounded < Int16.MinValue || rounded > Int16.MaxValue)
            {
                if (_clamped++ == 0)
                    Log.Warning($"[Ghosts] Field coordinate {value} doesn't fit in 16 bits (field {FF9StateSystem.Common.FF9.fldMapNo})");
                return (Int16)Mathf.Clamp(rounded, Int16.MinValue, Int16.MaxValue);
            }
            return (Int16)rounded;
        }

        private void LogSummary(String tag)
        {
            StringBuilder line = new StringBuilder($"[Ghosts] {tag}: sampled {_sampled} at {_rateHz} Hz, lost in pause {_lostInPause}, lost in net sim {_lostInSim}, clamped {_clamped}, other-field states ignored {GhostManager.IgnoredOtherField}, spawn failures {GhostManager.SpawnFailures}");
            foreach (GhostActor ghost in GhostManager.All)
                line.Append($" | {ghost}: received {ghost.Received}, dropped as old {ghost.DroppedOld}, snaps {ghost.Snaps}, hidden {ghost.Hides}x, {(ghost.Visible ? "visible" : "hidden")}");
            Log.Message(line.ToString());
        }

        private void OnGUI()
        {
            if (!_showStatus || (!_enabled && GhostManager.Count == 0))
                return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, wordWrap = false };
                _style.normal.textColor = Color.white;
            }
            Double now = NowMs;
            StringBuilder text = new StringBuilder();
            text.Append($"Ghosts  shadow {(_enabled ? "on" : "off")}, model {(GhostManager.ModelOverride != null ? GhostManager.ModelOverride.Name : "same as you")}, {_rateHz} Hz");
            text.Append($", interpolation {(GhostManager.Interpolate ? "on" : "OFF")}, net sim {(_netSim ? "ON" : "off")}");
            text.Append($"\nFeed: {(now < _pausedUntil ? $"PAUSED {(_pausedUntil - now) / 1000:0.0}s" : "live")}, delay {FeedDelayMs / 1000:0.0}s + {GhostManager.InterpolationDelayMs:0} ms");
            foreach (GhostActor ghost in GhostManager.All)
                text.Append($"\n{ghost.Model.Name}: {(ghost.Visible ? "visible" : "HIDDEN")}, recv {ghost.Received}, old {ghost.DroppedOld}, snaps {ghost.Snaps}, last {(now - ghost.LastReceivedMs) / 1000:0.0}s ago");
            text.Append("\nCtrl+Shift: G on/off, P pause 6s, M model, R rate, N interp, K net sim, U hide");
            GUIContent content = new GUIContent(text.ToString());
            Vector2 size = _style.CalcSize(content);
            GUI.Box(new Rect(8, Screen.height - size.y - 12, size.x + 8, size.y + 4), content, _style);
        }
    }
}
