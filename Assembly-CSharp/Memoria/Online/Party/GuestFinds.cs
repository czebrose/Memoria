using Memoria.Online.Session;
using Memoria.Prime;
using SimpleJSON;
using System;
using System.Collections.Generic;

namespace Memoria.Online.Party
{
    /// <summary>
    /// Step 2 / M5 tier 1: what a guest's field scripts give or take (chests, NPC gifts, quest items, gil), recorded at
    /// the script opcodes (AddItem / RemoveItem / AddGil / RemoveGil, [Online] hooks in EventEngine.DoEventCode) and
    /// sent to the host with the next party request ("found"). The host replays them with the same game functions, so
    /// items, key items and cards (all "generic" items) and gil land in the host's game and reach everyone.
    ///
    /// Only recorded once the guest has had control on the current field (<see cref="GuestGuard.ActedOnThisField"/>):
    /// a field's opening scripts run on every PC that loads it (and again on a meeting reload), so what they give is
    /// the host's own business, not a guest's find.
    /// </summary>
    public static class GuestFinds
    {
        private const Byte KindItem = 0;
        private const Byte KindGil = 1;

        private struct Find
        {
            public Byte Kind;
            public Int32 Id;
            public Int64 Amount;
        }

        private static readonly List<Find> Pending = new List<Find>();

        public static Int32 Sent { get; private set; }

        /// <summary>[Online] hook: AddItem (<paramref name="count"/> &gt; 0) / RemoveItem (&lt; 0).</summary>
        public static void OnItem(Int32 id, Int32 count)
        {
            if (count == 0 || !ShouldRecord())
                return;
            Pending.Add(new Find { Kind = KindItem, Id = id, Amount = count });
            Log.Message($"[Online] Finds: a script {(count > 0 ? "gave" : "took")} item {id} x{Math.Abs(count)} (sent to the host)");
        }

        /// <summary>[Online] hook: AddGil / RemoveGil, with the actual change.</summary>
        public static void OnGil(Int64 delta)
        {
            if (delta == 0 || !ShouldRecord())
                return;
            Pending.Add(new Find { Kind = KindGil, Amount = delta });
            Log.Message($"[Online] Finds: a script {(delta > 0 ? "gave" : "took")} {Math.Abs(delta)} gil (sent to the host)");
        }

        private static Boolean ShouldRecord() => PartySync.IsGuestNow && GuestGuard.ActedOnThisField;

        internal static Boolean Any => Pending.Count > 0;

        internal static JSONArray Take()
        {
            JSONArray array = new JSONArray();
            foreach (Find find in Pending)
            {
                JSONClass f = new JSONClass();
                f.Add("k", new JSONData(find.Kind));
                f.Add("id", new JSONData(find.Id));
                f.Add("n", new JSONData(find.Amount.ToString()));
                array.Add(f);
            }
            Sent += Pending.Count;
            Pending.Clear();
            return array;
        }

        internal static void Clear() => Pending.Clear();

        /// <summary>Host: replays a guest's finds in the live game.</summary>
        internal static void Apply(JSONArray found)
        {
            FF9StateGlobal ff9 = FF9StateSystem.Common.FF9;
            foreach (JSONNode f in found)
            {
                Int64 n = f["n"].AsLong;
                if (f["k"].AsInt == KindGil)
                {
                    Int64 gil = Math.Max(0, Math.Min(9999999L, (Int64)ff9.party.gil + n));
                    ff9.party.gil = (UInt32)gil;
                }
                else if (n > 0)
                {
                    ff9item.FF9Item_Add_Generic(f["id"].AsInt, (Int32)n);
                }
                else
                {
                    ff9item.FF9Item_Remove_Generic(f["id"].AsInt, (Int32)(-n));
                }
            }
        }

        internal static String Describe(JSONArray found)
        {
            List<String> parts = new List<String>();
            foreach (JSONNode f in found)
                parts.Add(f["k"].AsInt == KindGil ? $"gil {(f["n"].AsLong > 0 ? "+" : "")}{f["n"].AsLong}" : $"item {f["id"].AsInt} {(f["n"].AsLong > 0 ? "+" : "")}{f["n"].AsLong}");
            return $"found: {String.Join(", ", parts.ToArray())}";
        }
    }
}
