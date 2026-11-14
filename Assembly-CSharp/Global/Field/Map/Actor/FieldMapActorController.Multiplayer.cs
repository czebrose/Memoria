using Memoria;
using Memoria.Multiplayer;
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

    /// <summary>Called by UpdateMovement in place of MovePC/MoveNPC for an extra player's character.</summary>
    private void MoveMultiplayerFollower(Boolean copyLastPos)
    {
        EventEngine engine = PersistenSingleton<EventEngine>.Instance;
        Boolean userControl = engine.GetUserControl();
        if (!userControl || copyLastPos)
            this.CopyLastPos();

        Boolean canMove = userControl
            && EventInput.IsMovementControl
            && !this.fieldMap.isBattleBackupPos
            && this.originalActor.state == EventEngine.stateRunning;
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
        engine.AnimateMultiplayerFollower(this.originalActor, hasMoved, this.isRunning && hasMoved);
    }

    /// <summary>
    /// Move by one step with the walkmesh and NPC rules; undo it and return false if it would take a character that is
    /// on screen off the screen. A character already off-screen (the camera scrolled away) may move freely.
    /// </summary>
    private Boolean TryFollowerStep(Vector3 step)
    {
        if (step.sqrMagnitude < 0.01f)
            return false;
        Boolean wasVisible = MultiplayerField.IsOnScreen(this.fieldMap, this.curPos, this.originalActor);
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
