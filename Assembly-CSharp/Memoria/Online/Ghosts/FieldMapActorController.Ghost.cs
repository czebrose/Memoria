using System;

// [Ghosts] The ghost side of FieldMapActorController (Spike B). Its only engine hook is in UpdateMovement:
// a ghost's controller doesn't move it; Memoria.Online.Ghosts.GhostActor sets its position, floor and animation.
public partial class FieldMapActorController
{
    /// <summary>Set when this controller belongs to another player's ghost.</summary>
    public Memoria.Online.Ghosts.GhostActor ghost;

    public Boolean IsGhost => this.ghost != null;

    public void SetGhostMoving(Boolean moving)
    {
        this.isMoving = moving;
    }
}
