using Memoria;
using Memoria.Multiplayer;
using Memoria.Prime;
using System;
using UnityEngine;

// [Multiplayer] Fork addition: an extra player's character walking on the field with its own controller.
// It is not the event engine's player character (isPlayer stays false, object 250 is untouched):
// it reuses the leader's walkmesh movement (UpdateActiveTri, ServiceChar, NPC collision push-back)
// but reads its owner's pad, never touches the engine's lock/encounter timers, and is animated by
// EventEngine.AnimateMultiplayerFollower since the event engine does not process it.
public partial class FieldMapActorController
{
    /// <summary>Player (2-4) whose controller moves this character; 0 for every normal field actor.</summary>
    [NonSerialized]
    public Int32 multiplayerOwner;

    public Boolean IsMultiplayerFollower => this.multiplayerOwner > 0;

    /// <summary>Walk/run animation speed (16 = normal), from FieldModels.</summary>
    [NonSerialized]
    public Byte multiplayerMoveAnimSpeed = 16;

    [NonSerialized]
    private Single followerJumpLogTime;

    /// <summary>Diagnostics: log a tick in which this character moved much further than walking/running allows.</summary>
    private void LogFollowerJump(String how, Vector3 startPos, String details)
    {
        Vector3 moved = this.curPos - startPos;
        moved.y = 0f;
        if (moved.magnitude <= 75f || Time.realtimeSinceStartup < this.followerJumpLogTime)
            return;
        this.followerJumpLogTime = Time.realtimeSinceStartup + 1f;
        Log.Message($"[Multiplayer] P{this.multiplayerOwner} moved {moved.magnitude:0} in one tick ({how}; {details}) from {startPos} to {this.curPos}, tri {this.activeTri}, floor {this.activeFloor}, deltaTime {Time.deltaTime:0.000}");
    }

    // Diagnostics: once a second while moving, how far this character went, in how many movement ticks and frames
    [NonSerialized] private Single followerStatsStart = -1f;
    [NonSerialized] private Int32 followerStatsFrame;
    [NonSerialized] private Int32 followerStatsTicks;
    [NonSerialized] private Int32 followerStatsMovingTicks;
    [NonSerialized] private Int32 followerStatsSameFrameTicks;
    [NonSerialized] private Int32 followerStatsLastFrame;
    [NonSerialized] private Single followerStatsDistance;
    [NonSerialized] private Single followerStatsMaxStep;
    [NonSerialized] private Single followerStatsLeaderDistance;
    [NonSerialized] private Vector3 followerStatsLeaderPos;

    private void TrackFollowerStats(Vector3 startPos, Boolean moving)
    {
        Single now = Time.realtimeSinceStartup;
        if (this.followerStatsStart < 0f)
        {
            this.followerStatsStart = now;
            this.followerStatsFrame = Time.frameCount;
        }
        Vector3 moved = this.curPos - startPos;
        moved.y = 0f;
        this.followerStatsTicks++;
        if (Time.frameCount == this.followerStatsLastFrame)
            this.followerStatsSameFrameTicks++;
        this.followerStatsLastFrame = Time.frameCount;
        if (moving)
            this.followerStatsMovingTicks++;
        this.followerStatsDistance += moved.magnitude;
        this.followerStatsMaxStep = Math.Max(this.followerStatsMaxStep, moved.magnitude);
        FieldMapActorController leader = this.fieldMap.playerController;
        if (leader != null)
        {
            Vector3 leaderMoved = leader.curPos - this.followerStatsLeaderPos;
            leaderMoved.y = 0f;
            if (this.followerStatsTicks > 1 && leaderMoved.magnitude < 200f)
                this.followerStatsLeaderDistance += leaderMoved.magnitude;
            this.followerStatsLeaderPos = leader.curPos;
        }
        if (now - this.followerStatsStart < 1f)
            return;
        if (this.followerStatsDistance > 1f)
        {
            Int32 controllers = this.GetComponents<FieldMapActorController>().Length;
            Log.Message($"[Multiplayer] P{this.multiplayerOwner} movement over {now - this.followerStatsStart:0.00}s: {this.followerStatsDistance:0} units in {this.followerStatsTicks} ticks ({this.followerStatsMovingTicks} moving, {this.followerStatsSameFrameTicks} extra in the same frame) over {Time.frameCount - this.followerStatsFrame} frames, biggest step {this.followerStatsMaxStep:0}, speed {this.speed:0}, controllers {controllers}; leader moved {this.followerStatsLeaderDistance:0}");
        }
        this.followerStatsStart = now;
        this.followerStatsFrame = Time.frameCount;
        this.followerStatsTicks = 0;
        this.followerStatsMovingTicks = 0;
        this.followerStatsSameFrameTicks = 0;
        this.followerStatsDistance = 0f;
        this.followerStatsMaxStep = 0f;
        this.followerStatsLeaderDistance = 0f;
    }

    /// <summary>Called by UpdateMovement in place of MovePC/MoveNPC for an extra player's character.</summary>
    private void MoveMultiplayerFollower(Boolean copyLastPos)
    {
        EventEngine engine = PersistenSingleton<EventEngine>.Instance;
        if (this.isPlayer)
        {
            // Never the field's player: HonoUpdate would move it twice per tick whenever player 1 runs
            this.isPlayer = false;
            Log.Warning($"[Multiplayer] P{this.multiplayerOwner}'s character was marked as the field's player; undone");
        }
        Boolean userControl = engine.GetUserControl();
        if (!userControl || copyLastPos)
            this.CopyLastPos();

        // Where the leader went since the last tick (to walk along when a script moves them)
        FieldMapActorController leader = this.fieldMap.playerController;
        Vector3 leaderStep = Vector3.zero;
        if (leader != null && !ReferenceEquals(leader, this))
        {
            if (this.followerLeaderPosValid)
                leaderStep = leader.curPos - this.followerLeaderPos;
            this.followerLeaderPos = leader.curPos;
            this.followerLeaderPosValid = true;
        }

        Boolean canMove = userControl
            && EventInput.IsMovementControl
            && !this.fieldMap.isBattleBackupPos
            && this.originalActor.state == EventEngine.stateRunning;
        if (!canMove && !userControl && !this.fieldMap.isBattleBackupPos && this.originalActor.state == EventEngine.stateRunning)
        {
            this.ReactWhileOthersAct(engine, leaderStep);
            return;
        }
        Boolean wantsRun = false;
        Vector2 input = canMove ? MultiplayerField.GetMoveInput(this.multiplayerOwner, out wantsRun) : Vector2.zero;

        Single threshold = this.stickThreshold > 0f ? this.stickThreshold : 0.2f;
        Boolean moving = input.magnitude > threshold;
        this.isRunning = moving && wantsRun && engine.GetDashInh() != 1;
        this.moveVec = Vector3.zero;
        Vector3 startPos = this.curPos;

        if (moving)
        {
            if (input.sqrMagnitude > 1f)
                input.Normalize();
            this.moveVec = new Vector3(input.x, 0f, input.y);
            // Same camera-relative orientation as MovePC for stick movement
            Single y = Configuration.AnalogControl.UseAbsoluteOrientationStick ? FF9StateSystem.Field.twist.x : FF9StateSystem.Field.twist.y;
            this.moveVec = Quaternion.Euler(0f, y, 0f) * this.moveVec;

            Single walkSpeed = this.analogControlEnabled ? Mathf.Lerp(this.minimumSpeed, this.speed, input.magnitude) : this.speed;
            Vector3 step = this.moveVec.normalized * walkSpeed;
            this.TurnMultiplayerFollower();
            // The leader runs by moving twice at walking speed; do the same in two steps
            Int32 stepCount = this.isRunning ? 2 : 1;
            for (Int32 i = 0; i < stepCount; i++)
            {
                if (Configuration.Control.PSXMovementMethod)
                    step = this.moveVec.normalized * walkSpeed * this.fieldMap.walkMesh.GetTriangleSlopeFactor(this.activeTri);
                // The screen edge is a wall: the camera follows the leader, so an extra player must stay in view.
                // If the full step leaves the screen, slide along the edge using only its X or Z part.
                if (!this.TryFollowerStep(step) && !this.TryFollowerStep(new Vector3(step.x, 0f, 0f)) && !this.TryFollowerStep(new Vector3(0f, 0f, step.z)))
                    break;
            }
        }

        // Walk/run only while actually moving (not while pushing against a wall or the screen edge)
        Vector3 moved = this.curPos - startPos;
        Boolean hasMoved = moving && new Vector2(moved.x, moved.z).sqrMagnitude > 1f;
        this.LogFollowerJump("own movement", startPos, $"input {input.x:0.00},{input.y:0.00}, run {this.isRunning}, speed {this.speed:0}/{this.minimumSpeed:0}, control {userControl}");
        this.TrackFollowerStats(startPos, moving);
        engine.AnimateMultiplayerFollower(this.originalActor, hasMoved, this.isRunning && hasMoved);
    }

    [NonSerialized]
    private Vector3 followerLeaderPos;
    [NonSerialized]
    private Boolean followerLeaderPosValid;

    /// <summary>
    /// While a script has taken movement away (someone talks, loots, leaves the area...), an extra player's character
    /// reacts like the leader does for them: it walks along when a script walks the leader (an exit, a door), and
    /// otherwise turns to look at whoever is acting (MultiplayerField.GetReactionFocus).
    /// </summary>
    private void ReactWhileOthersAct(EventEngine engine, Vector3 leaderStep)
    {
        Vector3 startPos = this.curPos;
        leaderStep.y = 0f;
        Single stepLength = leaderStep.magnitude;
        // A real walk: more than a nudge, less than a teleport (a script placing the leader elsewhere)
        Boolean leaderWalks = stepLength > 1f && stepLength < 200f && MultiplayerField.ShouldWalkWithLeader(engine);
        Boolean running = false;
        if (leaderWalks)
        {
            this.moveVec = leaderStep / stepLength;
            this.TurnMultiplayerFollower();
            running = stepLength > 45f; // the leader runs at 60/tick, walks at 30
            // No screen-edge wall here: following the leader out of the area is the point
            Vector3 step = this.moveVec * Mathf.Min(stepLength, 60f);
            if (!this.TryFollowerStep(step, false))
            {
                this.TryFollowerStep(new Vector3(step.x, 0f, 0f), false);
                this.TryFollowerStep(new Vector3(0f, 0f, step.z), false);
            }
        }
        else if (MultiplayerField.GetReactionFocus(engine, this.multiplayerOwner, out Vector3 focus))
        {
            Vector3 toFocus = focus - this.curPos;
            toFocus.y = 0f;
            if (toFocus.sqrMagnitude > 1f)
            {
                this.moveVec = toFocus.normalized;
                this.TurnMultiplayerFollower();
            }
        }
        this.moveVec = Vector3.zero;
        this.isRunning = false;
        Vector3 moved = this.curPos - startPos;
        Boolean hasMoved = new Vector2(moved.x, moved.z).sqrMagnitude > 1f;
        this.LogFollowerJump("walking with the leader", startPos, $"leader step {stepLength:0}, walks {leaderWalks}");
        engine.AnimateMultiplayerFollower(this.originalActor, hasMoved, running && hasMoved);
    }

    /// <summary>
    /// Move by one step with the walkmesh and NPC rules; with keepOnScreen, undo it and return false if it would take a
    /// character that is on screen off the screen. A character already off-screen (the camera scrolled away) may move freely.
    /// </summary>
    private Boolean TryFollowerStep(Vector3 step, Boolean keepOnScreen = true)
    {
        if (step.sqrMagnitude < 0.01f)
            return false;
        Boolean wasVisible = keepOnScreen && MultiplayerField.IsOnScreen(this.fieldMap, this.curPos, this.originalActor);
        Vector3 pos = this.curPos;
        Int32 tri = this.activeTri;
        Int32 floor = this.activeFloor;
        this.curPos += step;
        this.PushOutOfObjects(step);
        this.UpdateActiveTri();
        if (!wasVisible || MultiplayerField.IsOnScreen(this.fieldMap, this.curPos, this.originalActor))
            return true;
        this.curPos = pos;
        this.activeTri = tri;
        this.activeFloor = floor;
        return false;
    }

    private void TurnMultiplayerFollower()
    {
        // Same smoothed turn as MovePC
        Single moveAngle = Mathf.Atan2(-this.moveVec.x, -this.moveVec.z) * 57.29578f;
        Single current = this.actor.actor.rotAngle[1];
        if (Mathf.Abs(moveAngle - current) > 180f)
            moveAngle += moveAngle > current ? -360f : 360f;
        Single rot = Mathf.Lerp(current, moveAngle, 0.4f);
        while (rot > 180f)
            rot -= 360f;
        while (rot < -180f)
            rot += 360f;
        this.actor.actor.rotAngle[1] = rot;
    }

    /// <summary>MovePC's push-back from NPCs and objects, without the leader's lock timers.</summary>
    private void PushOutOfObjects(Vector3 step)
    {
        PosObj posObj = this.walkMesh.Collision(this, 0, out Single collDist);
        if (posObj == null || collDist > 0f)
            return;
        Vector3 collObjPos = new Vector3(posObj.pos[0], posObj.pos[1], posObj.pos[2]);
        Int32 fixedPointAngle = PersistenSingleton<EventEngine>.Instance.eBin.CollisionAngle(this.actor.actor, posObj, this.actor.actor.rotAngle[1]);
        Single degree = EventEngineUtils.ConvertFixedPointAngleToDegree((Int16)fixedPointAngle);
        if (degree < -90f || degree > 90f)
            return;
        Single safeDist = this.radius + 4 * posObj.collRad;
        Vector3 away = this.curPos - collObjPos;
        Vector3 moveToSafe = (safeDist - away.magnitude) * away.normalized;
        this.curPos += moveToSafe;
        posObj = this.walkMesh.Collision(this, 0, out collDist);
        if (posObj != null && collDist < 0f)
        {
            this.curPos -= moveToSafe;
            this.curPos -= step;
        }
    }

    /// <summary>
    /// Put this character on the walkmesh next to another one (the leader), on the leader's floor and, when possible,
    /// where isVisible says it can be seen. Returns a short description of the spot chosen (for the log).
    /// </summary>
    public String PlaceNextTo(FieldMapActorController leader, Int32 slotIndex, Func<Vector3, Boolean> isVisible)
    {
        Single facing = leader.actor != null && leader.actor.actor != null ? leader.actor.actor.rotAngle[1] : 0f;
        Single baseDistance = Math.Max(leader.radius, 40f) + Math.Max(this.radius, 40f);
        // Beside, then behind/in front of the leader, at two distances; each extra player starts at a different angle
        Single[] angles = { 90f, -90f, 0f, 45f, -45f, 135f, -135f, 180f };
        Single[] distances = { baseDistance, baseDistance * 0.6f, baseDistance * 1.6f };
        // First pass: same floor and visible; second pass: same floor only
        for (Int32 pass = 0; pass < 2; pass++)
        {
            foreach (Single distance in distances)
            {
                for (Int32 i = 0; i < angles.Length; i++)
                {
                    Single angle = (facing + angles[(i + slotIndex * 2) % angles.Length]) * Mathf.Deg2Rad;
                    // A model faces -Z at rotation 0 (see MovePC), so this offset turns with the leader
                    Vector3 candidate = leader.curPos + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * distance;
                    Int32 tri = this.GetTriIdxAtPos(candidate);
                    if (tri < 0 || this.walkMesh.tris[tri].floorIdx != leader.activeFloor)
                        continue;
                    if (pass == 0 && isVisible != null && !isVisible(candidate))
                        continue;
                    if (this.SetPosition(candidate, true, true))
                    {
                        this.actor.actor.rotAngle[1] = facing;
                        return $"{(pass == 0 ? "visible" : "off-screen")} spot {distance:0} from the leader";
                    }
                }
            }
        }
        // Fall back to the leader's own spot, which is on the walkmesh
        this.SetPosition(leader.curPos, true, true);
        this.actor.actor.rotAngle[1] = facing;
        return "the leader's own spot (no free spot around)";
    }
}
