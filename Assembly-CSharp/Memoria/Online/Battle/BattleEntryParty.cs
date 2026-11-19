using Memoria.Data;
using Memoria.Online.Party;
using Memoria.Prime;
using SimpleJSON;
using System;
using System.Collections.Generic;
using System.Text;

namespace Memoria.Online.Battle
{
    /// <summary>
    /// Spike D / D2: a battle party that is a subset of the party, without changing the shared party.
    ///
    /// FF9 builds the battle party from the four party slots (<c>FF9StateSystem.Common.FF9.party.member</c>), skipping
    /// empty ones (btl_init.InitPlayerData, HonoluluBattleMain.CreateBattleData, btl_init.SetupBattlePlayer centres the
    /// row on the member count). So for the length of the battle the slots hold only the battle's characters, packed
    /// in party order; the full slot array is put back when the battle scene is left. The character records
    /// (FF9.player) aren't touched: characters left out keep their state, and nothing but the slot array moves.
    /// Memoria's own in-battle main menu does the same trick (BattleHUD.UpdatePlayersForMainMenu).
    ///
    /// D4, results of a battle a guest owned: the guest's party state before and after (PartyState.Diff) goes to the
    /// host as a "BTR1" blob; the host applies the battle characters' records, item counts and gil, trusting the
    /// battle owner, and PartySync broadcasts as usual.
    /// </summary>
    internal static class BattleEntryParty
    {
        private static readonly Byte[] MagicResult = Encoding.ASCII.GetBytes("BTR1");

        private static PLAYER[] _saved;

        /// <summary>The party slots hold only the battle's characters right now (PartySync waits).</summary>
        public static Boolean Altered => _saved != null;

        /// <summary>The characters in the party right now, in party order.</summary>
        public static List<Int32> PartyCharacters()
        {
            List<Int32> result = new List<Int32>();
            PLAYER[] members = FF9StateSystem.Common.FF9.party.member;
            foreach (PLAYER p in members)
                if (p != null)
                    result.Add((Int32)p.info.slot_no);
            return result;
        }

        /// <summary>Puts only <paramref name="characters"/> in the party slots (party order, packed). False if none of them is in the party.</summary>
        public static Boolean Apply(ICollection<Int32> characters)
        {
            PLAYER[] members = FF9StateSystem.Common.FF9.party.member;
            if (_saved != null)
                Restore("a new battle started before the last one was restored");
            PLAYER[] saved = (PLAYER[])members.Clone();
            List<PLAYER> keep = new List<PLAYER>();
            foreach (PLAYER p in saved)
                if (p != null && characters.Contains((Int32)p.info.slot_no))
                    keep.Add(p);
            if (keep.Count == 0)
                return false;
            _saved = saved;
            for (Int32 i = 0; i < members.Length; i++)
                members[i] = i < keep.Count ? keep[i] : null;
            Log.Message($"[Online] Battle party: {Describe(members)} (party {Describe(saved)})");
            return true;
        }

        public static void Restore(String why)
        {
            if (_saved == null)
                return;
            PLAYER[] members = FF9StateSystem.Common.FF9.party.member;
            for (Int32 i = 0; i < members.Length; i++)
                members[i] = i < _saved.Length ? _saved[i] : null;
            _saved = null;
            Log.Message($"[Online] Battle party: the party slots are back ({Describe(members)}; {why})");
        }

        private static String Describe(PLAYER[] members)
        {
            List<String> parts = new List<String>();
            foreach (PLAYER p in members)
                parts.Add(p != null ? ((Int32)p.info.slot_no).ToString() : "-");
            return String.Join(" ", parts.ToArray());
        }

        // ---- Results of a guest's battle ----------------------------------------------------------------------

        public static Boolean IsResultBlob(Byte[] blob)
        {
            if (blob == null || blob.Length < MagicResult.Length)
                return false;
            for (Int32 i = 0; i < MagicResult.Length; i++)
                if (blob[i] != MagicResult[i])
                    return false;
            return true;
        }

        /// <summary>The battle's effect on the party: { "battle": id, "characters": [...], "diff": PartyState.Diff }; null if none.</summary>
        public static Byte[] PackResult(Int32 battleId, ICollection<Int32> characters, JSONClass before, JSONClass after)
        {
            JSONClass diff = PartyState.Diff(before, after);
            if (diff == null)
                return null;
            JSONClass body = new JSONClass();
            body.Add("battle", new JSONData(battleId));
            JSONArray chars = new JSONArray();
            foreach (Int32 c in characters)
                chars.Add(new JSONData(c));
            body.Add("characters", chars);
            body.Add("diff", diff);
            Byte[] payload = Session.GameSnapshot.ToBytes(body);
            Byte[] blob = new Byte[MagicResult.Length + payload.Length];
            Buffer.BlockCopy(MagicResult, 0, blob, 0, MagicResult.Length);
            Buffer.BlockCopy(payload, 0, blob, MagicResult.Length, payload.Length);
            return blob;
        }

        public static JSONClass UnpackResult(Byte[] blob)
        {
            Byte[] payload = new Byte[blob.Length - MagicResult.Length];
            Buffer.BlockCopy(blob, MagicResult.Length, payload, 0, payload.Length);
            return Session.GameSnapshot.FromBytes(payload);
        }

        /// <summary>Host: puts a guest battle's results into the host's game (the battle characters' records, items, gil).</summary>
        public static String ApplyResult(JSONClass body)
        {
            HashSet<Int32> characters = new HashSet<Int32>();
            foreach (JSONNode c in body["characters"].AsArray)
                characters.Add(c.AsInt);
            JSONNode diff = body["diff"];
            JSONClass state = PartyState.Capture();
            Dictionary<Int32, JSONNode> players = PartyState.PlayersById(state[PartyState.Common]);
            List<String> applied = new List<String>(), ignored = new List<String>();
            JSONArray changes = diff["players"].AsArray;
            if (changes != null)
            {
                foreach (JSONNode change in changes)
                {
                    Int32 id = change["id"].AsInt;
                    if (!characters.Contains(id) || !players.TryGetValue(id, out JSONNode player))
                    {
                        ignored.Add($"character {id} (not in the battle)");
                        continue;
                    }
                    List<String> keys = new List<String>();
                    foreach (KeyValuePair<String, JSONNode> key in (JSONClass)change["keys"])
                    {
                        player[key.Key] = PartyState.Clone(key.Value);
                        keys.Add(key.Key);
                    }
                    applied.Add($"character {id}: {String.Join("/", keys.ToArray())}");
                }
            }
            JSONArray items = diff["items"].AsArray;
            if (items != null)
            {
                foreach (JSONNode item in items)
                {
                    PartyState.AddItem(state, item["id"].AsInt, item["d"].AsInt);
                    applied.Add($"item {item["id"].AsInt} {(item["d"].AsInt > 0 ? "+" : "")}{item["d"].AsInt}");
                }
            }
            if (diff["gil"] != null)
            {
                Int64 gil = Math.Max(0, Math.Min(9999999, state[PartyState.Common]["gil"].AsLong + diff["gil"].AsLong));
                state[PartyState.Common]["gil"] = new JSONData(gil.ToString());
                applied.Add($"gil {diff["gil"].AsLong:+0;-0}");
            }
            if (diff["other"] != null && diff["other"].AsArray != null)
                foreach (JSONNode o in diff["other"].AsArray)
                    ignored.Add(o.Value);
            PartyState.Apply(state);
            return $"applied [{String.Join(", ", applied.ToArray())}]{(ignored.Count > 0 ? $", ignored [{String.Join(", ", ignored.ToArray())}]" : "")}";
        }
    }
}
