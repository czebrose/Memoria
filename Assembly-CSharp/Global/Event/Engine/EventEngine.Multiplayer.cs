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

    // ---- Phase 3c: extra players interact with NPCs, chests and regions -----------------------------------------
    // An interaction is one function of a target (NPC, chest, region) that runs on behalf of an extra player:
    //  - a talk (tag 3), started when they press Confirm facing an NPC or standing in a talk region;
    //  - a range check (tag 2), requested every tick for what they stand next to, like CollisionRequest does for the
    //    leader (chests are opened by their range function, which checks the button itself).
    // While a target's own code runs (gCur == target), object 250 means that player's character and the script's
    // button checks (IsButton...) read that player's controller. Every other script and the engine itself
    // (_context.controlUID is never changed) keep seeing the leader and player 1's input.
    // The characters are detached actors, so GetObjByUID/FindObjByUID also return them for their UID meanwhile.

    public sealed class MultiplayerInteraction
    {
        public Int32 Player;
        public Actor Actor;
        public Obj Target;
        public Boolean IsTalk;
        public Boolean HasControl; // the player was given control for this interaction (talk, or a modal range function)
    }

    /// <summary>A function of the leader's entry run on an extra player's behalf (see Request: the item pickup "Vivi_15"...).</summary>
    public sealed class MultiplayerRedirect
    {
        public Int32 Player;
        public Obj Leader;
        public Int32 Level;
    }

    private readonly System.Collections.Generic.List<MultiplayerInteraction> _multiplayerInteractions = new System.Collections.Generic.List<MultiplayerInteraction>();
    private readonly System.Collections.Generic.List<MultiplayerRedirect> _multiplayerRedirects = new System.Collections.Generic.List<MultiplayerRedirect>();

    public System.Collections.Generic.List<MultiplayerInteraction> MultiplayerInteractions => this._multiplayerInteractions;
    public System.Collections.Generic.List<MultiplayerRedirect> MultiplayerRedirects => this._multiplayerRedirects;

    /// <summary>
    /// An extra player is talking or picking something up: the leader waits (MovePC) and cannot start a talk, and no
    /// other extra player starts an interaction.
    /// </summary>
    public Boolean IsMultiplayerTalkActive
    {
        get
        {
            if (this._multiplayerRedirects.Count > 0)
                return true;
            foreach (MultiplayerInteraction interaction in this._multiplayerInteractions)
                if (interaction.IsTalk)
                    return true;
            return false;
        }
    }

    // ---- Press ticks: hidden items are found by code loops (InitCode entries) that check every tick whether
    // "the player" stands near a spot and presses Confirm, then run the player entry's pickup function
    // (RunScriptSync(2, 250, 15) -> "Vivi_15": kneel, AddGil/AddItem, "Received..."). Nothing requests those loops, so
    // they can't be given an interaction. Instead, on a tick where an extra player has just pressed Confirm (and
    // player 1 has not), code entries (except the main entry) see that player's character as object 250 and read that
    // player's buttons. Regions are handled as interactions on those ticks (MultiplayerTalk).

    private Int32 _multiplayerPressPlayer;
    private Actor _multiplayerPressActor;

    /// <summary>MultiplayerTalk, end of a tick: the extra player (or 0) whose Confirm press the next tick's code loops see.</summary>
    public void SetMultiplayerPressTick(Int32 player, Actor actor)
    {
        this._multiplayerPressPlayer = actor != null ? player : 0;
        this._multiplayerPressActor = actor;
    }

    private Boolean IsMultiplayerPressTickFor(Obj cur)
    {
        if (this._multiplayerPressPlayer == 0 || cur == null || cur.cid != 2 || cur.sid == 0)
            return false;
        // Player 1's own press this tick wins
        return (ETb.KeyOn() & (EventInput.Confirm | EventInput.Special)) == 0;
    }

    /// <summary>
    /// EventEngine.Request on an extra player's character: it has no script, so the function is run on the leader's
    /// entry instead, which has the player character's functions (the item pickup with its kneel animation, the chest
    /// "open" pose). The leader plays the animation; the item goes to the shared inventory; the extra player gets
    /// control for the messages (MultiplayerTalk). Returns false if the leader is busy (the caller then continues).
    /// </summary>
    private Boolean RequestForMultiplayerFollower(Obj follower, Int32 level, Int32 tagNumber, Boolean ew)
    {
        Int32 player = Memoria.Multiplayer.MultiplayerField.GetFollowerPlayer(follower);
        Obj leader = this._context != null ? this.FindObjByUID(this._context.controlUID) : null;
        if (player == 0 || leader == null || Memoria.Multiplayer.MultiplayerField.IsFollowerActor(leader))
            return false;
        if (!this.Request(leader, level, tagNumber, ew))
            return false;
        this._multiplayerRedirects.Add(new MultiplayerRedirect { Player = player, Leader = leader, Level = level });
        Memoria.Multiplayer.MultiplayerTalk.OnPlayerFunctionRedirected(player, leader, tagNumber);
        return true;
    }

    public void EndMultiplayerRedirect(MultiplayerRedirect redirect)
    {
        this._multiplayerRedirects.Remove(redirect);
    }

    public MultiplayerInteraction GetMultiplayerInteraction(Obj target)
    {
        if (target == null)
            return null;
        foreach (MultiplayerInteraction interaction in this._multiplayerInteractions)
            if (ReferenceEquals(interaction.Target, target))
                return interaction;
        return null;
    }

    public Boolean HasMultiplayerInteraction(Int32 player)
    {
        foreach (MultiplayerInteraction interaction in this._multiplayerInteractions)
            if (interaction.Player == player)
                return true;
        return false;
    }

    /// <summary>GetUIDFromLocalUID(250): the extra player's character a running script acts for (see GetMultiplayerScriptPlayer), the leader otherwise.</summary>
    private Int32 ResolveMultiplayerPlayerUID(Int32 controlUID)
    {
        Actor actor = this.GetMultiplayerScriptActor();
        return actor != null ? actor.uid : controlUID;
    }

    /// <summary>GetObjByUID/FindObjByUID fallback: an extra player's character, while a script acts for them.</summary>
    private Obj FindMultiplayerTalker(Int32 uid)
    {
        foreach (MultiplayerInteraction interaction in this._multiplayerInteractions)
            if (interaction.Actor.uid == uid)
                return interaction.Actor;
        Actor actor = this.GetMultiplayerScriptActor();
        return actor != null && actor.uid == uid ? actor : null;
    }

    private Actor GetMultiplayerScriptActor()
    {
        if (this.gCur == null)
            return null;
        MultiplayerInteraction interaction = this._multiplayerInteractions.Count > 0 ? this.GetMultiplayerInteraction(this.gCur) : null;
        if (interaction != null)
            return interaction.Actor;
        Int32 player = this.GetMultiplayerScriptPlayer();
        return player != 0 ? Memoria.Multiplayer.MultiplayerField.GetFollowerActor(player) : null;
    }

    /// <summary>
    /// The extra player a script running right now acts for, or 0 for the leader / player 1:
    /// the target of their interaction, a hidden-item loop that looks at them, or a code loop on their press tick.
    /// </summary>
    private Int32 GetMultiplayerScriptPlayer()
    {
        if (this.gCur == null)
            return 0;
        MultiplayerInteraction interaction = this._multiplayerInteractions.Count > 0 ? this.GetMultiplayerInteraction(this.gCur) : null;
        if (interaction != null)
            return interaction.Player;
        if (this.IsMultiplayerItemLoop(this.gCur))
            return this.DecideItemLoopPlayer(this.gCur);
        return this.IsMultiplayerPressTickFor(this.gCur) ? this._multiplayerPressPlayer : 0;
    }

    // ---- Hidden-item loops: code entries whose loop (function 1) shows a bubble (Bubble) and runs a player function
    // (RunScriptSync(_, 250, _)): "is the player standing on my spot, show '!', pick up on Confirm". Each tick such a
    // loop looks at one player: whoever was found near it in the last ticks (the leader first), or else the leader and
    // the extra players in turn, to notice who walks up. Being near is learnt from the loop's own Bubble calls.
    // Other code loops (story triggers, positional sounds...) keep seeing the leader, except on press ticks.

    private sealed class ItemLoopState
    {
        public Int32 LeaderNearTick = -100;
        public readonly Int32[] PlayerNearTick = { -100, -100, -100, -100, -100 };
        public Int32 DecidedTick = -1;
        public Int32 DecidedPlayer;
    }

    private const Int32 MultiplayerNearTicks = 2;
    private readonly System.Collections.Generic.Dictionary<Byte[], Boolean> _multiplayerItemLoopCache = new System.Collections.Generic.Dictionary<Byte[], Boolean>();
    private readonly System.Collections.Generic.Dictionary<Obj, ItemLoopState> _multiplayerItemLoops = new System.Collections.Generic.Dictionary<Obj, ItemLoopState>();
    private FieldMap _multiplayerItemLoopField;
    private Int32 _multiplayerTick;

    /// <summary>MultiplayerTalk, once per field tick.</summary>
    public void AdvanceMultiplayerTick()
    {
        this._multiplayerTick++;
        if (!ReferenceEquals(this._multiplayerItemLoopField, this.fieldmap))
        {
            this._multiplayerItemLoopField = this.fieldmap;
            this._multiplayerItemLoops.Clear();
            this._multiplayerItemLoopCache.Clear();
        }
    }

    private Boolean IsMultiplayerItemLoop(Obj obj)
    {
        if (obj.cid != 2 || obj.sid == 0 || obj.ebData == null || obj.ebData.Length == 0 || this.gMode != 1)
            return false;
        Boolean result;
        if (this._multiplayerItemLoopCache.TryGetValue(obj.ebData, out result))
            return result;
        result = false;
        try
        {
            UInt32 pos = 0;
            EventEngineUtils.BinaryScript.Entry entry = new EventEngineUtils.BinaryScript.Entry(obj.ebData, ref pos, obj.sid, 0, 0, (UInt32)obj.ebData.Length);
            foreach (EventEngineUtils.BinaryScript.Entry.Function function in entry.functions)
            {
                if (function.tagNumber != 1)
                    continue;
                Boolean bubble = false, playerCall = false;
                foreach (EventEngineUtils.BinaryScript.Entry.Function.Code code in function.codes)
                {
                    if (code.opcode == (UInt16)EBin.event_code_binary.FICON)
                        bubble = true;
                    else if (code.opcode == (UInt16)EBin.event_code_binary.REQEW && code.arguments.Count > 1
                        && code.arguments[1] is EventEngineUtils.BinaryScript.Entry.Function.Code.ArgumentConstant obj250
                        && obj250.value == 250)
                        playerCall = true;
                }
                result = bubble && playerCall;
            }
        }
        catch (Exception)
        {
            result = false;
        }
        this._multiplayerItemLoopCache[obj.ebData] = result;
        return result;
    }

    private Int32 DecideItemLoopPlayer(Obj loop)
    {
        ItemLoopState state;
        if (!this._multiplayerItemLoops.TryGetValue(loop, out state))
            this._multiplayerItemLoops[loop] = state = new ItemLoopState();
        if (state.DecidedTick == this._multiplayerTick)
            return state.DecidedPlayer;

        System.Collections.Generic.List<Int32> players = new System.Collections.Generic.List<Int32>(3);
        for (Int32 p = 2; p <= Memoria.Multiplayer.MultiplayerManager.MaxPlayers; p++)
            if (p != Memoria.Multiplayer.MultiplayerManager.ActivePlayer && Memoria.Multiplayer.MultiplayerField.GetFollowerActor(p) != null)
                players.Add(p);

        Int32 decided = 0;
        if (players.Count > 0 && this._multiplayerTick - state.LeaderNearTick > MultiplayerNearTicks)
        {
            decided = -1;
            foreach (Int32 p in players)
                if (this._multiplayerTick - state.PlayerNearTick[p] <= MultiplayerNearTicks)
                {
                    decided = p;
                    break;
                }
            if (decided < 0)
            {
                Int32 turn = this._multiplayerTick % (players.Count + 1);
                decided = turn == 0 ? 0 : players[turn - 1];
            }
        }
        state.DecidedTick = this._multiplayerTick;
        state.DecidedPlayer = decided;
        return decided;
    }

    /// <summary>
    /// The Bubble opcode: remember who the running script found near (hidden-item loops), and show the bubble over the
    /// extra player it runs for instead of the leader. Returns true if handled (the leader's bubble is not polled).
    /// </summary>
    private Boolean HandleMultiplayerBubble(BubbleUI.IconType type)
    {
        if (this.gCur == null || !Memoria.Multiplayer.MultiplayerManager.IsActive)
            return false;
        Int32 player = this.GetMultiplayerScriptPlayer();
        ItemLoopState state;
        if (this._multiplayerItemLoops.TryGetValue(this.gCur, out state))
        {
            if (player == 0)
                state.LeaderNearTick = this._multiplayerTick;
            else
                state.PlayerNearTick[player] = this._multiplayerTick;
        }
        if (player == 0)
            return false;
        Memoria.Multiplayer.MultiplayerBubbles.Poll(player, type);
        return true;
    }

    /// <summary>
    /// What an extra player's character would talk to when pressing Confirm, with the same checks the leader gets
    /// (EventCollision.CheckNPCInput, CheckQuadInput): an NPC in talk range that it faces, or a talk region it stands in.
    /// </summary>
    public Obj FindMultiplayerTalkTarget(Actor actor, out Int32 angle)
    {
        angle = 0;
        Single distance = this.nilFloat;
        Obj npc = EventCollision.Collision(this, actor, 4, ref distance);
        if (npc != null && EventCollision.IsNPCTalkable(npc))
        {
            angle = EventCollision.CollisionAngle(actor, npc);
            if (angle > -1024 && angle < 1024)
                return npc;
        }
        Obj quad = this.TreadQuad(actor, 4);
        if (quad != null && EventCollision.IsQuadTalkable(actor, quad))
            return quad;
        return null;
    }

    /// <summary>
    /// What an extra player's character stands next to, for range functions (tag 2), with the same checks the leader
    /// gets in EventCollision.CollisionRequest: the object it faces in talk range or else the one it touches.
    /// Regions are left out on purpose: field exits are region range functions, and only the leader changes fields.
    /// Talk regions (item spots, "!" spots) still work through a Confirm press (FindMultiplayerTalkTarget).
    /// </summary>
    public void FindMultiplayerRangeTargets(Actor actor, System.Collections.Generic.List<Obj> targets)
    {
        Single distance = this.nilFloat;
        Obj obj = EventCollision.Collision(this, actor, 4, ref distance);
        if (obj != null)
        {
            Int32 angle = EventCollision.CollisionAngle(actor, obj);
            if (angle <= -1024 || angle >= 1024 || !EventCollision.IsNPCTalkable(obj))
                obj = EventCollision.Collision(this, actor, 2, ref distance);
        }
        else
        {
            obj = EventCollision.Collision(this, actor, 2, ref distance);
        }
        if (obj != null && EventCollision.CheckNPCPush((PosObj)obj))
            targets.Add(obj);
    }

    /// <summary>
    /// The "!" an extra player's character gets for what it could talk to, with the leader's rules
    /// (EIcon.PollCollisionIcon): a talk function that is not running, "talkable" for real, Tetra Master if available.
    /// </summary>
    public Boolean GetMultiplayerTalkIcon(Actor actor, out BubbleUI.IconType type)
    {
        type = BubbleUI.IconType.Exclamation;
        if (Memoria.Configuration.Icons.HideSteam || EventHUD.CurrentHUD == MinigameHUD.MogTutorial)
            return false;
        Obj target = this.FindMultiplayerTalkTarget(actor, out Int32 _);
        if (target == null || target.level <= 1)
            return false;
        Boolean talk = this.GetIP(target.sid, EventEngine.tagTalk, target.ebData) != this.nil;
        Boolean duel = this.GetIP(target.sid, 8, target.ebData) != this.nil;
        if (talk && duel)
        {
            type = BubbleUI.IconType.ExclamationAndDuel;
            return true;
        }
        return talk && (target.cid == 3 || this.IsActuallyTalkable(target));
    }

    /// <summary>
    /// On a press tick: the region the character stands in, for its range function (hidden items in regions check the
    /// button there). Only on a press, so walking into a field exit (also a region range function) does nothing.
    /// </summary>
    public Obj FindMultiplayerRegionRangeTarget(Actor actor)
    {
        Obj quad = this.TreadQuad(actor, 2);
        return quad != null && EventCollision.CheckQuadPush(actor, quad) ? quad : null;
    }

    /// <summary>Run a target's function on behalf of an extra player's character. Returns false if the target is busy.</summary>
    public MultiplayerInteraction StartMultiplayerInteraction(Int32 player, Actor actor, Obj target, Int32 tag, Int32 angle)
    {
        if (this.GetMultiplayerInteraction(target) != null)
            return null;
        MultiplayerInteraction interaction = new MultiplayerInteraction { Player = player, Actor = actor, Target = target, IsTalk = tag == EventEngine.tagTalk };
        this._multiplayerInteractions.Add(interaction);
        if (interaction.IsTalk)
        {
            EventCollision.sSysAngle = angle;
            actor.listener = target.uid;
        }
        if (this.Request(target, 1, tag, false))
            return interaction;
        this._multiplayerInteractions.Remove(interaction);
        return null;
    }

    public void EndMultiplayerInteraction(MultiplayerInteraction interaction)
    {
        this._multiplayerInteractions.Remove(interaction);
    }

    /// <summary>Script button checks (B_KEY*): the extra player's controller for their interaction's target, player 1's otherwise.</summary>
    public UInt32 ScriptKeyOn(Boolean japaneseLayout)
    {
        Int32 player = this.GetMultiplayerScriptPlayer();
        if (player == 0)
            return ETb.KeyOn(japaneseLayout);
        UInt32 keys = Memoria.Multiplayer.MultiplayerKeys.KeyOn(player, japaneseLayout);
        if ((keys & (EventInput.Confirm | EventInput.Cancel | EventInput.Special)) != 0)
            Memoria.Multiplayer.MultiplayerTalk.OnScriptReadPress(player); // a message this script opens is theirs
        return keys;
    }

    public UInt32 ScriptKeyOff(Boolean japaneseLayout)
    {
        Int32 player = this.GetMultiplayerScriptPlayer();
        return player == 0 ? ETb.KeyOff(japaneseLayout) : Memoria.Multiplayer.MultiplayerKeys.KeyOff(player, japaneseLayout);
    }

    public UInt32 ScriptKey(Boolean japaneseLayout)
    {
        Int32 player = this.GetMultiplayerScriptPlayer();
        return player == 0 ? ETb.GetInputs(japaneseLayout) : Memoria.Multiplayer.MultiplayerKeys.Key(player, japaneseLayout);
    }

    /// <summary>ProcessEvents: the script is leaving the field; end every interaction before the scene changes.</summary>
    private void EndMultiplayerTalkOnSceneChange(Int32 codeFlowState)
    {
        if (this._multiplayerInteractions.Count == 0)
            return;
        if (codeFlowState == EventEngine.FLOW_STATE_JUMP_BATTLE || codeFlowState == EventEngine.FLOW_STATE_JUMP_FIELD
            || codeFlowState == EventEngine.FLOW_STATE_JUMP_WORLD_MAP || codeFlowState == EventEngine.FLOW_STATE_QUADMIST
            || codeFlowState == EventEngine.FLOW_STATE_GAMEOVER)
            Memoria.Multiplayer.MultiplayerTalk.EndAll(this, "the script left the field");
    }
}
