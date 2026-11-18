using Memoria.Data;
using Memoria.Online.Session;
using SimpleJSON;
using System;
using System.Collections.Generic;

namespace Memoria.Online.Party
{
    /// <summary>
    /// Step 2 / M4: the party state (docs/online/step2-m4-party-state.md): every character record, items, gil, key
    /// items, party order, counters (save section "40000_Common") and the card collection ("30000_MiniGame"), as
    /// Memoria's save writes them (its own, complete copy under "MemoriaExtraData"). Applied through the game's own
    /// loader (JsonParser.ParseToFF9StateSystem), which updates the existing character objects in place.
    /// </summary>
    public static class PartyState
    {
        public const String Common = "40000_Common";
        public const String MiniGame = "30000_MiniGame";

        private static JsonParser Parser => FF9StateSystem.Serializer?.Parser as JsonParser;

        /// <summary>The party state now: { "40000_Common": ..., "30000_MiniGame": ... } (a copy, safe to modify).</summary>
        public static JSONClass Capture()
        {
            JsonParser parser = Parser;
            if (parser == null)
                throw new InvalidOperationException("the game's save parser isn't available");
            parser.ParseFromFF9StateSystem();
            JSONNode memoria = parser.RootNodeInParser["MemoriaExtraData"];
            JSONClass state = new JSONClass();
            state.Add(Common, memoria[Common]);
            state.Add(MiniGame, memoria[MiniGame]);
            return Clone(state);
        }

        /// <summary>Puts a party state into the game, as a load does (only these sections).</summary>
        public static void Apply(JSONClass state)
        {
            JsonParser parser = Parser;
            if (parser == null)
                throw new InvalidOperationException("the game's save parser isn't available");
            JSONClass memoria = new JSONClass();
            memoria.Add(Common, Clone(state[Common]));
            memoria.Add(MiniGame, Clone(state[MiniGame]));
            JSONClass root = new JSONClass();
            root.Add("Data", new JSONClass()); // no vanilla sections: only Memoria's complete copy is read
            root.Add("MemoriaExtraData", memoria);
            parser.ParseToFF9StateSystem(root);
        }

        public static JSONClass Clone(JSONNode node) => GameSnapshot.FromBytes(GameSnapshot.ToBytes(node));

        /// <summary>
        /// A cheap hash of what menus can change, computed every frame: when it moves, the full state is captured and
        /// compared. (A false "changed" only costs a capture; the diff decides.)
        /// </summary>
        public static UInt32 Fingerprint()
        {
            FF9StateGlobal ff9 = FF9StateSystem.Common.FF9;
            UInt32 h = 2166136261;
            void Mix(Int64 v) { unchecked { h = (h ^ (UInt32)v) * 16777619; h = (h ^ (UInt32)(v >> 32)) * 16777619; } }
            foreach (PLAYER p in ff9.PlayerList)
            {
                Mix((Int32)p.info.slot_no);
                Mix(p.level);
                Mix(p.exp);
                Mix(p.cur.hp);
                Mix(p.cur.mp);
                Mix(p.cur.capa);
                Mix((Int64)p.status);
                Mix((Int64)p.permanent_status);
                Mix(p.info.row);
                for (Int32 i = 0; i < PLAYER.EQUIP_MAX; i++)
                    Mix((Int32)p.equip[i]);
                if (p.saExtended != null)
                {
                    Mix(p.saExtended.Count);
                    foreach (SupportAbility sa in p.saExtended)
                        Mix((Int32)sa * 31 + 7);
                }
                String name = p.Name;
                if (name != null)
                    Mix(name.GetHashCode());
            }
            foreach (PLAYER member in ff9.party.member)
                Mix(member != null ? (Int32)member.info.slot_no : -1);
            foreach (FF9ITEM item in ff9.item)
            {
                Mix((Int32)item.id);
                Mix(item.count);
            }
            Mix(ff9.party.gil);
            Mix(ff9.rare_item_obtained?.Count ?? 0);
            Mix(ff9.rare_item_used?.Count ?? 0);
            Mix(FF9StateSystem.MiniGame?.SavedData?.MiniGameCard?.Count ?? 0);
            return h;
        }

        // ---- Differences ---------------------------------------------------------------------------------------

        /// <summary>
        /// What changed between two party states, as a request body:
        /// { "players": [ { "id": c, "keys": { key: value, ... }, "row": r? } ], "items": [ { "id": i, "d": n } ],
        ///   "gil": d, "other": [ section keys ] }. Null when nothing changed.
        /// </summary>
        public static JSONClass Diff(JSONClass from, JSONClass to)
        {
            JSONClass result = new JSONClass();
            Boolean any = false;
            JSONNode a = from[Common], b = to[Common];

            // Characters, by character id
            Dictionary<Int32, JSONNode> before = PlayersById(a);
            JSONArray players = new JSONArray();
            foreach (KeyValuePair<Int32, JSONNode> kv in PlayersById(b))
            {
                if (!before.TryGetValue(kv.Key, out JSONNode old))
                    continue; // a character appearing is a story change: "other" below catches the array
                JSONClass keys = new JSONClass();
                Int32 row = -1;
                foreach (KeyValuePair<String, JSONNode> field in (JSONClass)kv.Value)
                {
                    if (field.Key == "info")
                    {
                        if (field.Value["row"].AsInt != old["info"]["row"].AsInt)
                            row = field.Value["row"].AsInt;
                        if (WithoutRow(field.Value) != WithoutRow(old["info"]))
                            keys.Add("info", Clone(field.Value));
                        continue;
                    }
                    if (field.Value.ToString() != old[field.Key].ToString())
                        keys.Add(field.Key, Clone(field.Value));
                }
                if (keys.Count == 0 && row < 0)
                    continue;
                JSONClass change = new JSONClass();
                change.Add("id", new JSONData(kv.Key));
                change.Add("keys", keys);
                if (row >= 0)
                    change.Add("row", new JSONData(row));
                players.Add(change);
            }
            if (players.Count > 0)
            {
                result.Add("players", players);
                any = true;
            }
            if (PlayersById(a).Count != PlayersById(b).Count)
            {
                AddOther(result, "players");
                any = true;
            }

            // Items: count changes by id (the list order is the host's)
            Dictionary<Int32, Int32> itemsA = ItemCounts(a), itemsB = ItemCounts(b);
            JSONArray items = new JSONArray();
            HashSet<Int32> ids = new HashSet<Int32>(itemsA.Keys);
            ids.UnionWith(itemsB.Keys);
            foreach (Int32 id in ids)
            {
                itemsA.TryGetValue(id, out Int32 ca);
                itemsB.TryGetValue(id, out Int32 cb);
                if (ca != cb)
                {
                    JSONClass item = new JSONClass();
                    item.Add("id", new JSONData(id));
                    item.Add("d", new JSONData(cb - ca));
                    items.Add(item);
                }
            }
            if (items.Count > 0)
            {
                result.Add("items", items);
                any = true;
            }
            else if (a["items"].ToString() != b["items"].ToString())
            {
                AddOther(result, "items order"); // same counts, another order (arranging)
                any = true;
            }

            Int64 gil = b["gil"].AsLong - a["gil"].AsLong;
            if (gil != 0)
            {
                result.Add("gil", new JSONData(gil.ToString()));
                any = true;
            }

            // Anything else in the common section, and the cards
            foreach (KeyValuePair<String, JSONNode> field in (JSONClass)b)
            {
                if (field.Key == "players" || field.Key == "items" || field.Key == "gil")
                    continue;
                if (field.Value.ToString() != a[field.Key].ToString())
                {
                    AddOther(result, field.Key);
                    any = true;
                }
            }
            if (from[MiniGame].ToString() != to[MiniGame].ToString())
            {
                AddOther(result, "cards");
                any = true;
            }
            return any ? result : null;
        }

        private static void AddOther(JSONClass result, String what)
        {
            if (result["other"] == null || result["other"].AsArray == null)
                result.Add("other", new JSONArray());
            result["other"].Add(new JSONData(what));
        }

        private static String WithoutRow(JSONNode info)
        {
            JSONClass copy = Clone(info);
            copy.Remove("row");
            return copy.ToString();
        }

        public static Dictionary<Int32, JSONNode> PlayersById(JSONNode common)
        {
            Dictionary<Int32, JSONNode> result = new Dictionary<Int32, JSONNode>();
            JSONArray players = common["players"].AsArray;
            if (players == null)
                return result;
            for (Int32 i = 0; i < players.Count; i++)
                result[players[i]["info"]["slot_no"].AsInt] = players[i];
            return result;
        }

        public static Dictionary<Int32, Int32> ItemCounts(JSONNode common)
        {
            Dictionary<Int32, Int32> result = new Dictionary<Int32, Int32>();
            JSONArray items = common["items"].AsArray;
            if (items == null)
                return result;
            for (Int32 i = 0; i < items.Count; i++)
            {
                Int32 id = items[i]["id"].AsInt;
                result.TryGetValue(id, out Int32 count);
                result[id] = count + items[i]["count"].AsInt;
            }
            return result;
        }

        /// <summary>Changes one item's count in a state (adds the item at the end, removes it at 0).</summary>
        public static void AddItem(JSONClass state, Int32 id, Int32 delta)
        {
            JSONArray items = state[Common]["items"].AsArray;
            for (Int32 i = 0; i < items.Count; i++)
            {
                if (items[i]["id"].AsInt != id)
                    continue;
                Int32 count = items[i]["count"].AsInt + delta;
                if (count <= 0)
                    items.Remove(i);
                else
                    items[i]["count"] = new JSONData(Math.Min(count, ff9item.FF9ITEM_COUNT_MAX));
                return;
            }
            if (delta > 0)
            {
                JSONClass item = new JSONClass();
                item.Add("id", new JSONData(id));
                item.Add("count", new JSONData(Math.Min(delta, ff9item.FF9ITEM_COUNT_MAX)));
                items.Add(item);
            }
        }
    }
}
