using Memoria.Assets;
using Memoria.Prime;
using System;
using System.Collections.Generic;

namespace Memoria.Online.Bodies
{
    /// <summary>
    /// A playable character's field body: model, head height and one animation per named action
    /// (docs/online/step2-m3-own-leader.md, section 7).
    /// </summary>
    public sealed class CharacterBody
    {
        public Int32 CharacterId;
        public String Name;
        public UInt16 Model;
        public Byte HeadHeight; // SetModel's 2nd argument
        public readonly Dictionary<String, UInt16> Actions = new Dictionary<String, UInt16>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Animation id → action name (the first action declared with that id).</summary>
        public readonly Dictionary<UInt16, String> ActionOf = new Dictionary<UInt16, String>();

        public void SetAction(String action, UInt16 anim)
        {
            Actions[action] = anim;
            if (!ActionOf.ContainsKey(anim))
                ActionOf[anim] = action;
        }

        public UInt16 Get(String action) => Actions.TryGetValue(action, out UInt16 anim) ? anim : Actions[CharacterBodies.Stand];

        public override String ToString() => $"{Name} (character {CharacterId}, model {Model}, {Actions.Count} actions)";
    }

    /// <summary>
    /// The bodies of the playable characters. Vanilla characters have built-in bodies (locomotion only); a mod
    /// declares or replaces bodies in <see cref="File"/>, one character per line:
    ///
    ///   # characterId name: Model m, Head h, Action animId, Action animId, ...
    ///   2 Clove: Model 202, Head 91, Stand 2670, Walk 2655, Run 2672, TurnLeft 2667, TurnRight 2661, KneelDown 60305, ...
    ///
    /// Actions are free names; <see cref="Required"/> lists the ones every character should have (missing ones play
    /// Stand). Model and Stand are mandatory.
    /// </summary>
    public static class CharacterBodies
    {
        public const String File = "Data/Online/CharacterBodies.txt";
        public const String Stand = "Stand";

        /// <summary>Every action the sequel's fields play on the player (measured with tools/online/list_player_animations.py).</summary>
        public static readonly String[] Required =
        {
            "Stand", "Walk", "Run", "TurnLeft", "TurnRight",
            "KneelDown", "Kneeling", "KneelUp", "OpenDoor",
            "LadderOn", "LadderClimb", "LadderOffTop", "LadderOnDown", "LadderOffBottom",
        };

        /// <summary>How many bodies the mod's file declares (0: no file).</summary>
        public static Int32 FromFile
        {
            get { EnsureLoaded(); return _fromFile; }
            private set { _fromFile = value; }
        }
        private static Int32 _fromFile;

        private static Dictionary<Int32, CharacterBody> _byCharacter;
        private static Dictionary<UInt16, CharacterBody> _byModel;

        public static CharacterBody ForCharacter(Int32 character)
        {
            EnsureLoaded();
            return _byCharacter.TryGetValue(character, out CharacterBody body) ? body : null;
        }

        /// <summary>The character whose body uses this model, or null.</summary>
        public static CharacterBody ForModel(UInt16 model)
        {
            EnsureLoaded();
            return _byModel.TryGetValue(model, out CharacterBody body) ? body : null;
        }

        public static IEnumerable<CharacterBody> All
        {
            get
            {
                EnsureLoaded();
                return _byCharacter.Values;
            }
        }

        private static void EnsureLoaded()
        {
            if (_byCharacter != null)
                return;
            _byCharacter = new Dictionary<Int32, CharacterBody>();
            // Vanilla: each character's most common field model and locomotion (same table as the ghosts')
            AddBuiltIn(0, "Zidane", 98, 93, 200, 25, 38, 40, 41);
            AddBuiltIn(1, "Vivi", 8, 61, 148, 571, 419, 917, 918);
            AddBuiltIn(2, "Garnet", 185, 91, 2089, 2086, 2091, 2088, 2084);
            AddBuiltIn(3, "Steiner", 5489, 104, 2001, 1996, 2005, 1986, 2010);
            AddBuiltIn(4, "Freya", 192, 94, 2556, 2553, 2558, 2555, 2551);
            AddBuiltIn(5, "Quina", 273, 92, 3228, 3237, 3230, 3235, 3227);
            AddBuiltIn(6, "Eiko", 443, 63, 7503, 7518, 7506, 7516, 7514);
            AddBuiltIn(7, "Amarant", 509, 122, 8307, 8316, 8312, 8310, 8314);
            AddBuiltIn(8, "Beatrix", 204, 100, 2978, 2975, 2981, 2980, 2974);
            AddBuiltIn(9, "Cinna", 107, 87, 470, 761, 105, 104, 107);
            AddBuiltIn(10, "Marcus", 109, 100, 474, 367, 365, 368, 364);
            AddBuiltIn(11, "Blank", 5467, 87, 462, 5225, 5222, 5223, 5224);
            Int32 fromFile = FromFile = LoadFile();
            _byModel = new Dictionary<UInt16, CharacterBody>();
            foreach (CharacterBody body in _byCharacter.Values)
                if (!_byModel.ContainsKey(body.Model))
                    _byModel[body.Model] = body;
            Log.Message($"[Online] Character bodies: {_byCharacter.Count} ({fromFile} from {File})");
        }

        private static void AddBuiltIn(Int32 id, String name, UInt16 model, Byte head, UInt16 stand, UInt16 walk, UInt16 run, UInt16 turnLeft, UInt16 turnRight)
        {
            CharacterBody body = new CharacterBody { CharacterId = id, Name = name, Model = model, HeadHeight = head };
            body.SetAction("Stand", stand);
            body.SetAction("Walk", walk);
            body.SetAction("Run", run);
            body.SetAction("TurnLeft", turnLeft);
            body.SetAction("TurnRight", turnRight);
            _byCharacter[id] = body;
        }

        private static Int32 LoadFile()
        {
            String text;
            try { text = AssetManager.LoadString(File, true); }
            catch (Exception err) { Log.Warning($"[Online] {File}: {err.Message}"); return 0; }
            if (String.IsNullOrEmpty(text))
                return 0;
            Int32 count = 0;
            foreach (String rawLine in text.Split('\n'))
            {
                String line = rawLine;
                Int32 comment = line.IndexOf('#');
                if (comment >= 0)
                    line = line.Substring(0, comment);
                line = line.Trim();
                if (line.Length == 0)
                    continue;
                CharacterBody body = ParseLine(line, out String error);
                if (body == null)
                {
                    Log.Warning($"[Online] {File}: '{line}': {error}");
                    continue;
                }
                List<String> missing = new List<String>();
                foreach (String action in Required)
                    if (!body.Actions.ContainsKey(action))
                        missing.Add(action);
                if (missing.Count > 0)
                    Log.Message($"[Online] {File}: {body.Name} has no {String.Join(", ", missing.ToArray())} (those play Stand)");
                _byCharacter[body.CharacterId] = body;
                count++;
            }
            return count;
        }

        private static CharacterBody ParseLine(String line, out String error)
        {
            error = null;
            Int32 colon = line.IndexOf(':');
            if (colon <= 0)
            {
                error = "expected 'characterId name: Model m, Head h, Action animId, ...'";
                return null;
            }
            String[] head = line.Substring(0, colon).Trim().Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (head.Length == 0 || !Int32.TryParse(head[0], out Int32 id))
            {
                error = "no character id before ':'";
                return null;
            }
            CharacterBody body = new CharacterBody { CharacterId = id, Name = head.Length > 1 ? head[1].Trim() : $"character {id}", HeadHeight = 90 };
            Boolean hasModel = false;
            foreach (String pair in line.Substring(colon + 1).Split(','))
            {
                String[] kv = pair.Trim().Split(new[] { ' ', '\t', '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (kv.Length == 0)
                    continue;
                if (kv.Length != 2 || !UInt16.TryParse(kv[1], out UInt16 value))
                {
                    error = $"'{pair.Trim()}' isn't 'Name number'";
                    return null;
                }
                if (String.Equals(kv[0], "Model", StringComparison.OrdinalIgnoreCase))
                {
                    body.Model = value;
                    hasModel = true;
                }
                else if (String.Equals(kv[0], "Head", StringComparison.OrdinalIgnoreCase))
                {
                    body.HeadHeight = (Byte)Math.Min(value, (UInt16)255);
                }
                else
                {
                    body.SetAction(kv[0], value);
                }
            }
            if (!hasModel || !body.Actions.ContainsKey(Stand))
            {
                error = "Model and Stand are required";
                return null;
            }
            if (!FF9BattleDB.GEO.ContainsKey(body.Model))
            {
                error = $"model {body.Model} doesn't exist";
                return null;
            }
            return body;
        }
    }
}
