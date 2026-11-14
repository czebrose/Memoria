using Memoria.Data;
using Memoria.Prime;
using System;
using System.Collections.Generic;

namespace Memoria.Multiplayer
{
    /// <summary>Field model and animations used to show a player's character on fields (Phase 3).</summary>
    public sealed class FieldModel
    {
        public UInt16 Model;
        public Byte HeadHeight;   // SetModel's 2nd argument
        public UInt16 Idle;
        public UInt16 Walk;
        public UInt16 Run;
        public UInt16 TurnLeft;
        public UInt16 TurnRight;
        public Byte Size;         // SetObjectLogicalSize's 1st argument (pathing radius / 4)

        public FieldModel(UInt16 model, Byte headHeight, UInt16 idle, UInt16 walk, UInt16 run, UInt16 turnLeft, UInt16 turnRight, Byte size)
        {
            Model = model;
            HeadHeight = headHeight;
            Idle = idle;
            Walk = walk;
            Run = run;
            TurnLeft = turnLeft;
            TurnRight = turnRight;
            Size = size;
        }

        public override String ToString() => $"model {Model}, anims {Idle}/{Walk}/{Run}/{TurnLeft}/{TurnRight}, size {Size}";
    }

    /// <summary>
    /// Which field model each character uses when multiplayer shows them next to the leader.
    /// Defaults are each character's most common model and animations in the game's own field scripts;
    /// [Multiplayer] FieldModels overrides them per character:
    ///   FieldModels = Beatrix=167,87,11676,11676,11676,11676,11676,26; Vivi=...
    /// (model, head height, idle, walk, run, turn left, turn right, size)
    /// </summary>
    public static class FieldModels
    {
        private static readonly Dictionary<CharacterId, FieldModel> Defaults = new Dictionary<CharacterId, FieldModel>
        {
            { CharacterId.Zidane,  new FieldModel(98,   93,  200,  25,   38,   40,   41,   20) },
            { CharacterId.Vivi,    new FieldModel(8,    61,  148,  571,  419,  917,  918,  20) },
            { CharacterId.Garnet,  new FieldModel(185,  91,  2089, 2086, 2091, 2088, 2084, 20) },
            { CharacterId.Steiner, new FieldModel(5489, 104, 2001, 1996, 2005, 1986, 2010, 30) },
            { CharacterId.Freya,   new FieldModel(192,  94,  2556, 2553, 2558, 2555, 2551, 26) },
            { CharacterId.Quina,   new FieldModel(273,  92,  3228, 3237, 3230, 3235, 3227, 40) },
            { CharacterId.Eiko,    new FieldModel(443,  63,  7503, 7518, 7506, 7516, 7514, 20) },
            { CharacterId.Amarant, new FieldModel(509,  122, 8307, 8316, 8312, 8310, 8314, 30) },
            { CharacterId.Cinna,   new FieldModel(107,  87,  470,  761,  105,  104,  107,  20) },
            { CharacterId.Marcus,  new FieldModel(109,  100, 474,  367,  365,  368,  364,  20) },
            { CharacterId.Blank,   new FieldModel(5467, 87,  462,  5225, 5222, 5223, 5224, 20) },
            { CharacterId.Beatrix, new FieldModel(204,  100, 2978, 2975, 2981, 2980, 2974, 20) },
        };

        private static String s_overrideSource;
        private static Dictionary<CharacterId, FieldModel> s_overrides = new Dictionary<CharacterId, FieldModel>();

        public static FieldModel Get(CharacterId character)
        {
            RefreshOverrides();
            if (s_overrides.TryGetValue(character, out FieldModel custom))
                return custom;
            return Defaults.TryGetValue(character, out FieldModel model) ? model : null;
        }

        private static void RefreshOverrides()
        {
            String text = Configuration.Multiplayer.FieldModels ?? "";
            if (text == s_overrideSource)
                return;
            s_overrideSource = text;
            s_overrides = new Dictionary<CharacterId, FieldModel>();
            foreach (String entry in text.Split(';'))
            {
                String trimmed = entry.Trim().Trim('"').Trim();
                if (trimmed.Length == 0)
                    continue;
                Int32 eq = trimmed.IndexOf('=');
                String[] values = eq > 0 ? trimmed.Substring(eq + 1).Split(',') : new String[0];
                CharacterId character;
                if (eq <= 0 || !MultiplayerManager.TryParseCharacter(trimmed.Substring(0, eq).Trim(), out character) || values.Length != 8)
                {
                    Log.Warning($"[Multiplayer] FieldModels: cannot read '{trimmed}' (expected Name=model,height,idle,walk,run,turnL,turnR,size)");
                    continue;
                }
                try
                {
                    FieldModel model = new FieldModel(
                        UInt16.Parse(values[0].Trim()), Byte.Parse(values[1].Trim()),
                        UInt16.Parse(values[2].Trim()), UInt16.Parse(values[3].Trim()), UInt16.Parse(values[4].Trim()),
                        UInt16.Parse(values[5].Trim()), UInt16.Parse(values[6].Trim()), Byte.Parse(values[7].Trim()));
                    if (!FF9BattleDB.GEO.TryGetValue(model.Model, out String _))
                    {
                        Log.Warning($"[Multiplayer] FieldModels: model {model.Model} for {character} does not exist");
                        continue;
                    }
                    s_overrides[character] = model;
                    Log.Message($"[Multiplayer] Field model for {character}: {model}");
                }
                catch (Exception)
                {
                    Log.Warning($"[Multiplayer] FieldModels: cannot read the numbers in '{trimmed}'");
                }
            }
        }
    }
}
