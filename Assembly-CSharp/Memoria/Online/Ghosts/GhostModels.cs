using System;
using System.Collections.Generic;

namespace Memoria.Online.Ghosts
{
    /// <summary>A playable character's field model and its basic animations, as the field scripts set them up.</summary>
    public sealed class GhostModel
    {
        public readonly String Name;
        public readonly UInt16 Model;
        public readonly Byte HeadHeight; // SetModel's 2nd argument
        public readonly UInt16 Idle, Walk, Run;
        public readonly Byte Size;       // SetObjectLogicalSize's 1st argument

        public GhostModel(String name, UInt16 model, Byte headHeight, UInt16 idle, UInt16 walk, UInt16 run, Byte size)
        {
            Name = name;
            Model = model;
            HeadHeight = headHeight;
            Idle = idle;
            Walk = walk;
            Run = run;
            Size = size;
        }

        public override String ToString() => $"{Name} (model {Model}, anims {Idle}/{Walk}/{Run})";
    }

    /// <summary>
    /// Field models of the playable characters: each one's most common model and animations in the game's own field
    /// scripts (same table as the local-multiplayer branch's FieldModels defaults).
    /// </summary>
    public static class GhostModels
    {
        public static readonly GhostModel[] All =
        {
            new GhostModel("Zidane",  98,   93,  200,  25,   38,   20),
            new GhostModel("Vivi",    8,    61,  148,  571,  419,  20),
            new GhostModel("Garnet",  185,  91,  2089, 2086, 2091, 20),
            new GhostModel("Steiner", 5489, 104, 2001, 1996, 2005, 30),
            new GhostModel("Freya",   192,  94,  2556, 2553, 2558, 26),
            new GhostModel("Quina",   273,  92,  3228, 3237, 3230, 40),
            new GhostModel("Eiko",    443,  63,  7503, 7518, 7506, 20),
            new GhostModel("Amarant", 509,  122, 8307, 8316, 8312, 30),
        };

        private static readonly Dictionary<UInt16, GhostModel> ByModel = new Dictionary<UInt16, GhostModel>();

        static GhostModels()
        {
            foreach (GhostModel model in All)
                ByModel[model.Model] = model;
        }

        /// <summary>
        /// A model the table doesn't know (e.g. the sequel's Clove): drawn with the sender's own animation ids, which
        /// the ghost plays exactly since it uses the same model; its idle/walk/run fallbacks are the current one.
        /// </summary>
        public static GhostModel FromState(GhostState state)
        {
            return new GhostModel($"model {state.Model}", state.Model, 90, state.Anim, state.Anim, state.Anim, 20);
        }

        public static GhostModel Find(UInt16 model)
        {
            return ByModel.TryGetValue(model, out GhostModel result) ? result : null;
        }
    }
}
