using System;
using System.Collections.Generic;
using System.Globalization;

namespace Memoria
{
    public sealed partial class Configuration
    {
        /// <summary>
        /// Step 3 / M2: the Memoria.ini values a session forces (the host's), read and set <b>in memory only</b>
        /// (IniValue.Value; nothing here calls SaveValue, so Memoria.ini on disk never changes). Every value listed is
        /// read where it's used, at the time it's used (checked: none is cached at startup), so setting it in memory
        /// takes effect at once. See docs/online/step3-m2-results.md, settings inventory.
        /// </summary>
        public static class SessionBattle
        {
            private sealed class Key
            {
                public String Name;
                public Func<String> Get;
                public Action<String> Set;
            }

            private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

            private static Key Int(String name, Func<Prime.Ini.IniValue<Int32>> v) => new Key { Name = name, Get = () => v().Value.ToString(Inv), Set = s => v().Value = Int32.Parse(s, Inv) };
            private static Key Bool(String name, Func<Prime.Ini.IniValue<Boolean>> v) => new Key { Name = name, Get = () => v().Value ? "1" : "0", Set = s => v().Value = s == "1" };
            private static Key Float(String name, Func<Prime.Ini.IniValue<Single>> v) => new Key { Name = name, Get = () => v().Value.ToString("R", Inv), Set = s => v().Value = Single.Parse(s, Inv) };
            private static Key Str(String name, Func<Prime.Ini.IniValue<String>> v) => new Key { Name = name, Get = () => v().Value ?? "", Set = s => v().Value = s };

            private static List<Key> _keys;

            private static List<Key> Keys
            {
                get
                {
                    if (_keys != null)
                        return _keys;
                    BattleSection b = Instance._battle;
                    HacksSection h = Instance._hacks;
                    _keys = new List<Key>
                    {
                        Bool("Battle.SFXRework", () => b.SFXRework),
                        Int("Battle.Speed", () => b.Speed),
                        Int("Hacks.BattleSpeed", () => h.BattleSpeed),
                        Float("Battle.ATBSpeedFactor", () => b.ATBSpeedFactor),
                        Bool("Battle.NoAutoTrance", () => b.NoAutoTrance),
                        Int("Battle.EncounterInterval", () => b.EncounterInterval),
                        Int("Battle.EncounterInitial", () => b.EncounterInitial),
                        Bool("Battle.PersistentDangerValue", () => b.PersistentDangerValue),
                        Bool("Battle.PSXEncounterMethod", () => b.PSXEncounterMethod),
                        Int("Battle.AutoPotionOverhealLimit", () => b.AutoPotionOverhealLimit),
                        Bool("Battle.GarnetConcentrate", () => b.GarnetConcentrate),
                        Bool("Battle.SelectBestTarget", () => b.SelectBestTarget),
                        Bool("Battle.BreakDamageLimit", () => b.BreakDamageLimit),
                        Bool("Battle.ViviAutoAttack", () => b.ViviAutoAttack),
                        Bool("Battle.CountersBetterTarget", () => b.CountersBetterTarget),
                        Int("Battle.LockEquippedAbilities", () => b.LockEquippedAbilities),
                        Int("Battle.SummonPriorityCount", () => b.SummonPriorityCount),
                        Bool("Battle.CurseUseWeaponElement", () => b.CurseUseWeaponElement),
                        Int("Battle.FloatEvadeBonus", () => b.FloatEvadeBonus),
                        Int("Battle.AccessMenus", () => b.AccessMenus),
                        new Key { Name = "Battle.AvailableMenus", Get = () => String.Join("|", b.AvailableMenus.Value ?? new String[0]), Set = s => b.AvailableMenus.Value = s.Length == 0 ? new String[0] : s.Split('|') },
                        Int("Battle.CustomBattleFlagsMeaning", () => b.CustomBattleFlagsMeaning),
                        Str("Battle.SpareChangeGilSpentFormula", () => b.SpareChangeGilSpentFormula),
                        Str("Battle.StatusDurationFormula", () => b.StatusDurationFormula),
                        Str("Battle.StatusTickFormula", () => b.StatusTickFormula),
                        Str("Battle.SpeedStatFormula", () => b.SpeedStatFormula),
                        Str("Battle.StrengthStatFormula", () => b.StrengthStatFormula),
                        Str("Battle.MagicStatFormula", () => b.MagicStatFormula),
                        Str("Battle.SpiritStatFormula", () => b.SpiritStatFormula),
                        Str("Battle.MagicStoneStockFormula", () => b.MagicStoneStockFormula),
                        Str("Battle.TranceDecreaseFormula", () => b.TranceDecreaseFormula),
                        Bool("Battle.KeepRestTimeInBattle", () => b.KeepRestTimeInBattle),
                        Int("Hacks.StealingAlwaysWorks", () => h.StealingAlwaysWorks),
                    };
                    return _keys;
                }
            }

            /// <summary>This PC's values of the forced keys, as they are in memory now.</summary>
            public static Dictionary<String, String> Capture()
            {
                Dictionary<String, String> values = new Dictionary<String, String>();
                foreach (Key k in Keys)
                    values[k.Name] = k.Get();
                return values;
            }

            /// <summary>Sets these values in memory (unknown keys are ignored). Returns the keys that changed.</summary>
            public static List<String> Apply(Dictionary<String, String> values)
            {
                List<String> changed = new List<String>();
                foreach (Key k in Keys)
                {
                    if (!values.TryGetValue(k.Name, out String value))
                        continue;
                    String before = k.Get();
                    if (before == value)
                        continue;
                    try
                    {
                        k.Set(value);
                        changed.Add($"{k.Name} {before} -> {value}");
                    }
                    catch (Exception err)
                    {
                        Prime.Log.Warning($"[Online] Session settings: can't set {k.Name} to '{value}': {err.Message}");
                    }
                }
                return changed;
            }

            /// <summary>The raw (unclamped) Battle.Speed, Hacks.BattleSpeed and SFXRework, for the override check.</summary>
            public static Int32 RawSpeed => Math.Max(Instance._battle.Speed, Instance._hacks.BattleSpeed);
            public static Boolean RawSfxRework => Instance._battle.SFXRework;
        }
    }
}
