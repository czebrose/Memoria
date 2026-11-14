using Memoria.Data;
using Memoria.Prime;
using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Memoria.Multiplayer
{
    /// <summary>
    /// Phase 3a: players 2-4 walk their own character on fields, next to the leader.
    ///
    /// Each extra player gets one "follower": the first party member they own ([Multiplayer] PlayerNCharacters),
    /// shown with its field model (FieldModels) and moved with their controller through the leader's walkmesh
    /// code (FieldMapActorController.Multiplayer.cs). Followers are not event objects: field scripts never see
    /// them, object 250 and isPlayer stay on the leader, and nothing about them is saved. They are rebuilt on
    /// every field (they are children of the field map, which is destroyed on field changes and battles).
    /// </summary>
    public static class MultiplayerField
    {
        private const Int32 FirstFollowerUid = 240; // custom-UID range (128-249), below the 250+ references

        private sealed class Follower
        {
            public Int32 Player;
            public CharacterId Character;
            public Actor Actor;
            public FieldMapActorController Controller;
        }

        private static readonly Dictionary<Int32, Follower> s_followers = new Dictionary<Int32, Follower>();
        private static FieldMap s_fieldMap;
        private static readonly HashSet<String> s_failedSpawns = new HashSet<String>();

        /// <summary>Called every frame from MultiplayerManager.Update (multiplayer on).</summary>
        internal static void Update()
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            FieldMap fieldMap = engine?.fieldmap;
            Boolean onField = engine != null && engine.gMode == 1 && fieldMap != null && FF9StateSystem.Common?.FF9?.party != null;
            if (!onField || !Configuration.Multiplayer.FieldFollowers)
            {
                RemoveAll(onField ? "field followers disabled" : null);
                return;
            }
            if (!ReferenceEquals(fieldMap, s_fieldMap))
            {
                // New field map: the old followers were destroyed with the previous one
                s_followers.Clear();
                s_failedSpawns.Clear();
                s_fieldMap = fieldMap;
            }

            FieldMapActorController leader = fieldMap.playerController;
            if (leader == null || leader.walkMesh == null || leader.activeTri < 0)
                return;

            for (Int32 player = 2; player <= MultiplayerManager.MaxPlayers; player++)
            {
                CharacterId wanted = GetFollowerCharacter(player);
                s_followers.TryGetValue(player, out Follower current);
                if (current != null && (current.Controller == null || current.Character != wanted))
                {
                    Remove(current, current.Controller == null ? null : "party or owner changed");
                    current = null;
                }
                // Only appear once the player can move (not in the middle of the field's opening cutscene).
                // This is the only time a follower is placed next to the leader: within a field it is never moved
                // for the player. The screen edge blocks it while visible (FieldMapActorController.TryFollowerStep);
                // if the camera scrolls away with the leader, it stays where it is and can walk back into view.
                if (current == null && wanted != CharacterId.NONE && engine.GetUserControl())
                    Spawn(engine, fieldMap, leader, player, wanted);
            }
        }

        /// <summary>Whether a character standing at a field position is fully on screen: its feet and its head.</summary>
        public static Boolean IsOnScreen(FieldMap fieldMap, Vector3 position, Actor actor)
        {
            if (!IsOnScreen(fieldMap, position))
                return false;
            // eye = -4 * head height (SetModel); field Y grows upwards (ETb.GetMesPos puts dialogs at y - eye), so the head is at y - eye
            return actor == null || actor.eye == 0 || IsOnScreen(fieldMap, position + new Vector3(0f, -actor.eye, 0f));
        }

        /// <summary>Whether a field position is inside the screen, projected the way the field camera draws it.</summary>
        public static Boolean IsOnScreen(FieldMap fieldMap, Vector3 position)
        {
            try
            {
                Camera camera = fieldMap.GetMainCamera();
                BGCAM_DEF bgCamera = fieldMap.GetCurrentBgCamera();
                if (camera == null || bgCamera == null)
                    return true;
                Vector3 projected = PSX.CalculateGTE_RTPT(position, Matrix4x4.identity, bgCamera.GetMatrixRT(), bgCamera.GetViewDistance(), fieldMap.GetProjectionOffset());
                Vector3 screen = camera.WorldToScreenPoint(projected);
                // Small margin so a character half off the edge counts as hidden
                Single margin = Screen.height * 0.03f;
                return screen.x >= margin && screen.x <= Screen.width - margin && screen.y >= margin && screen.y <= Screen.height - margin;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>The field character of an extra player, or null if they have none on this field.</summary>
        public static Actor GetFollowerActor(Int32 player)
        {
            return s_followers.TryGetValue(player, out Follower follower) && follower.Controller != null ? follower.Actor : null;
        }

        /// <summary>Movement input for a follower: left stick or D-pad of its owner's pad, and whether they hold run (B).</summary>
        public static Vector2 GetMoveInput(Int32 player, out Boolean run)
        {
            run = false;
            // While an extra player has been handed control of the game (F10), their pad drives the leader instead
            if (player == MultiplayerManager.ActivePlayer)
                return Vector2.zero;
            PadState pad = XInputPads.Get(MultiplayerManager.GetPadSlot(player));
            if (!pad.Connected)
                return Vector2.zero;
            Vector2 input = new Vector2(pad.LeftX / 32767f, pad.LeftY / 32767f);
            if (pad.IsDown(XInputPads.DPadLeft)) input.x = -1f;
            if (pad.IsDown(XInputPads.DPadRight)) input.x = 1f;
            if (pad.IsDown(XInputPads.DPadUp)) input.y = 1f;
            if (pad.IsDown(XInputPads.DPadDown)) input.y = -1f;
            // Same rule as the leader: Cancel held toggles walk/run, the game's "always run" setting inverts it
            run = pad.IsDown(XInputPads.B) ^ FF9StateSystem.Settings.cfg.move == 1UL;
            return input;
        }

        private static CharacterId GetFollowerCharacter(Int32 player)
        {
            PLAYER[] members = FF9StateSystem.Common.FF9.party.member;
            if (members == null)
                return CharacterId.NONE;
            foreach (PLAYER member in members)
                if (member != null && MultiplayerManager.GetOwner(member.Index) == player)
                    return member.Index;
            return CharacterId.NONE;
        }

        private static void Spawn(EventEngine engine, FieldMap fieldMap, FieldMapActorController leader, Int32 player, CharacterId character)
        {
            String key = $"{player}:{character}";
            if (s_failedSpawns.Contains(key))
                return;
            FieldModel model = FieldModels.Get(character);
            if (model == null || !FF9BattleDB.GEO.TryGetValue(model.Model, out String modelName))
            {
                s_failedSpawns.Add(key);
                Log.Warning($"[Multiplayer] No field model for {character}; P{player} has no character on this field (set it with FieldModels)");
                return;
            }

            Int32 uid = FirstFollowerUid + player;
            if (engine.FindObjByUID(uid) != null)
            {
                s_failedSpawns.Add(key);
                Log.Warning($"[Multiplayer] Object UID {uid} is used by this field's script; P{player}'s character is not shown here");
                return;
            }

            try
            {
                // A detached actor: the parameterless constructors don't add it to the event engine's object list
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
                    turnl = model.TurnLeft,
                    turnr = model.TurnRight,
                    posField = leader.curPos,
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
                actor.go.name = $"MultiplayerP{player}";

                // Animations, as the Set*Animation opcodes load them
                foreach (UInt16 anim in new[] { model.Idle, model.Walk, model.Run, model.TurnLeft, model.TurnRight })
                    AnimationFactory.AddAnimWithAnimatioName(actor.go, FF9DBAll.AnimationDB.GetValue(anim));

                // Registration the field's renderer, shadows and colour effects need (keyed by UID)
                FF9StateSystem.Common.FF9.charArray[uid] = new FF9Char { geo = actor.go, evt = actor };
                FF9StateSystem.Field.FF9Field.loc.map.charStateArray[uid] = new FF9FieldCharState();
                FF9StateSystem.Field.FF9Field.loc.map.shadowArray[uid] = new FF9Shadow();

                fieldMap.AddFieldChar(actor.go, actor.posField, actor.rotField, false, actor, false);
                FieldMapActorController controller = actor.fieldMapActorController;
                controller.radius = model.Size * 4f;
                controller.multiplayerOwner = player;
                FieldMapActor fieldActor = actor.go.GetComponent<FieldMapActor>();
                if (fieldActor != null)
                    fieldActor.CharRadius = model.Size * 4f;
                String spot = controller.PlaceNextTo(leader, player - 2, pos => IsOnScreen(fieldMap, pos, actor));
                engine.InitMultiplayerFollowerAnimation(actor);
                engine.AnimateMultiplayerFollower(actor, false, false);

                s_followers[player] = new Follower { Player = player, Character = character, Actor = actor, Controller = controller };
                Log.Message($"[Multiplayer] P{player}'s {character} appears on field {FF9StateSystem.Common.FF9.fldMapNo} at {spot}, floor {controller.activeFloor} ({model})");
            }
            catch (Exception err)
            {
                s_failedSpawns.Add(key);
                Log.Error(err, $"[Multiplayer] Failed to show P{player}'s {character} on field {FF9StateSystem.Common.FF9.fldMapNo}");
            }
        }

        internal static void OnMultiplayerDisabled()
        {
            RemoveAll("multiplayer disabled");
        }

        private static void RemoveAll(String reason)
        {
            if (s_followers.Count == 0)
                return;
            foreach (Follower follower in new List<Follower>(s_followers.Values))
                Remove(follower, reason);
            s_followers.Clear();
        }

        private static void Remove(Follower follower, String reason)
        {
            s_followers.Remove(follower.Player);
            Int32 uid = follower.Actor.uid;
            // A character removed in the middle of its talk gives object 250 back to the leader first
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            if (engine != null && ReferenceEquals(engine.MultiplayerTalker, follower.Actor))
            {
                engine.EndMultiplayerTalk();
                MultiplayerTalk.OnTalkEnded("the character was removed");
            }
            // Only remove entries that are still ours: battles and new fields reuse these dictionaries and UIDs
            if (FF9StateSystem.Common.FF9.charArray.TryGetValue(uid, out FF9Char ch) && ReferenceEquals(ch.evt, follower.Actor))
            {
                FF9StateSystem.Common.FF9.charArray.Remove(uid);
                FF9StateSystem.Field.FF9Field.loc.map.charStateArray.Remove(uid);
                FF9StateSystem.Field.FF9Field.loc.map.shadowArray.Remove(uid);
            }
            if (follower.Actor.go != null)
                Object.Destroy(follower.Actor.go);
            if (reason != null)
                Log.Message($"[Multiplayer] P{follower.Player}'s {follower.Character} removed from the field ({reason})");
        }
    }
}
