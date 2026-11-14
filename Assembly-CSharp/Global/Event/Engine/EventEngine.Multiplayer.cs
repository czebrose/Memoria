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
}
