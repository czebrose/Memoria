using System;
using UnityEngine;

// [Multiplayer] Fork addition: helpers for field characters that are not part of the event script
// (extra players' characters, see Memoria.Multiplayer.MultiplayerField). Such actors are not in the
// active object list, so ProcessEvents/ProcessAnime/ff9fieldCharService never touch them; this applies
// the same walk/run/idle and frame-advance rules to them.
public partial class EventEngine
{
    public void AnimateMultiplayerFollower(Actor actor, Boolean moving, Boolean running)
    {
        if (actor == null || actor.go == null)
            return;
        // Same choice as ProcessEvents for a moving / standing actor
        this.SetAnim(actor, moving ? (running ? actor.run : actor.walk) : actor.idle);
        // Same frame advance as ProcessAnime for an actor without a scripted animation
        if (actor.frameN > 0 && this.NextFrame(actor) >= actor.frameN)
        {
            actor.animFrame = 0;
            if (actor.anim == actor.idle)
                this.idleAnimSpeed(actor);
        }
        // Same rotation as FieldMap.ff9fieldCharService
        actor.go.transform.localRotation = Quaternion.AngleAxis(actor.rotAngle[2], Vector3.back) * Quaternion.AngleAxis(actor.rotAngle[1], Vector3.up) * Quaternion.AngleAxis(actor.rotAngle[0], Vector3.left);
    }

    /// <summary>Start an actor on its idle animation (sets frame count and speed).</summary>
    public void InitMultiplayerFollowerAnimation(Actor actor)
    {
        actor.anim = UInt16.MaxValue;
        this.SetAnim(actor, actor.idle);
        this.idleAnimSpeed(actor);
    }

    // ---- Phase 3c: an extra player talks to an NPC ------------------------------------------------------------
    // During one talk, object 250 ("the player character") means the talker, but only for the NPC being talked
    // to, while its own code runs (gCur): its talk function turns to the talker, measures from them, etc.
    // Every other script (ambient loops that track the player, region triggers that run functions on 250...)
    // and the engine itself (_context.controlUID is never changed) keep seeing the leader.
    // The talker is a detached actor, so GetObjByUID/FindObjByUID also return it for its UID during the talk.

    private Actor _multiplayerTalker;
    private Obj _multiplayerTalkTarget;

    public Boolean IsMultiplayerTalkActive => this._multiplayerTalker != null;
    public Actor MultiplayerTalker => this._multiplayerTalker;
    public Obj MultiplayerTalkTarget => this._multiplayerTalkTarget;

    /// <summary>GetUIDFromLocalUID(250): the talker for the NPC they talk to, the leader for everyone else.</summary>
    private Int32 ResolveMultiplayerPlayerUID(Int32 controlUID)
    {
        if (this._multiplayerTalker != null && this.gCur != null && ReferenceEquals(this.gCur, this._multiplayerTalkTarget))
            return this._multiplayerTalker.uid;
        return controlUID;
    }

    /// <summary>GetObjByUID/FindObjByUID fallback: the talking extra player's character, while (and only while) they talk.</summary>
    private Obj FindMultiplayerTalker(Int32 uid)
    {
        return this._multiplayerTalker != null && this._multiplayerTalker.uid == uid ? this._multiplayerTalker : null;
    }

    /// <summary>
    /// The same check as EventCollision.CheckNPCInput for the leader, for an extra player's character: an NPC in talk
    /// range that it faces. If found, the character becomes object 250 and the NPC's talk function is started.
    /// </summary>
    public Boolean TryStartMultiplayerTalk(Actor talker)
    {
        if (this.gMode != 1 || this._multiplayerTalker != null || talker == null || talker.go == null || !this.GetUserControl())
            return false;
        Single distance = this.nilFloat;
        Obj npc = EventCollision.Collision(this, talker, 4, ref distance);
        if (npc == null || !EventCollision.IsNPCTalkable(npc))
            return false;
        Int32 angle = EventCollision.CollisionAngle(talker, npc);
        if (angle <= -1024 || angle >= 1024)
            return false;

        this._multiplayerTalker = talker;
        this._multiplayerTalkTarget = npc;
        EventCollision.sSysAngle = angle;
        talker.listener = npc.uid;
        if (this.Request(npc, 1, EventEngine.tagTalk, false))
            return true;
        this.EndMultiplayerTalk();
        return false;
    }

    /// <summary>Object 250 means the leader again for everyone. Safe to call when no talk is active.</summary>
    public void EndMultiplayerTalk()
    {
        this._multiplayerTalker = null;
        this._multiplayerTalkTarget = null;
    }

    /// <summary>ProcessEvents: the script is leaving the field; end the talk before the scene changes.</summary>
    private void EndMultiplayerTalkOnSceneChange(Int32 codeFlowState)
    {
        if (this._multiplayerTalker == null)
            return;
        if (codeFlowState == EventEngine.FLOW_STATE_JUMP_BATTLE || codeFlowState == EventEngine.FLOW_STATE_JUMP_FIELD
            || codeFlowState == EventEngine.FLOW_STATE_JUMP_WORLD_MAP || codeFlowState == EventEngine.FLOW_STATE_QUADMIST
            || codeFlowState == EventEngine.FLOW_STATE_GAMEOVER)
        {
            this.EndMultiplayerTalk();
            Memoria.Multiplayer.MultiplayerTalk.OnTalkEnded("the script left the field");
        }
    }
}
