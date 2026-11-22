using FF9;
using Memoria.Data;
using Memoria.Prime;
using System;
using UnityEngine;

namespace Memoria.Online.Battle
{
    /// <summary>
    /// Late join (docs/online/battle-late-join.md, "The joiner's character fights"): a player character added to a
    /// running battle, in a free slot, the same way on the owner and on every passive PC.
    ///
    ///  - Owner (BattleEntry): when a late joiner is in, its character is added at the next moment no command runs, and
    ///    the add goes into the stream as a frame-stamped <see cref="BattleWire.UnitJoin"/> record.
    ///  - Every passive PC (the joiner included, while it catches up) applies the record at that frame
    ///    (<see cref="BattleStreamIn"/>), so the unit appears at the same point of the battle everywhere.
    ///  - Who commands it: the owner's control map follows the session (BattleEntry.ControlMap, a few times a second),
    ///    so once the unit is there it goes to the joiner's PC like any unit it controls (MsgControlUpdate).
    ///
    /// The unit is built as the battle builds its party at start (HonoluluBattleMain.CreateBattleData,
    /// btl_init.InitPlayerData / SetupBattlePlayer) and as Memoria's in-battle party change rebuilds one
    /// (btl_init.SwapPlayerCharacter): model, OrganizePlayerData (shadow, commands, status, weapon, abilities, EXP flag),
    /// battle model, the battle list, its place in the line, the menu's ability lists.
    /// </summary>
    public static class BattleUnitJoin
    {
        private const Int32 Gap = 632; // btl_init.SetupBattlePlayer: the distance between party members

        /// <summary>Whether this character already is a unit of the battle (in it now).</summary>
        public static Boolean InBattle(Int32 character)
        {
            for (BTL_DATA btl = FF9StateSystem.Battle.FF9Battle.btl_list.next; btl != null; btl = btl.next)
                if (btl.bi.player != 0 && btl.bi.slot_no == character)
                    return true;
            return false;
        }

        /// <summary>A free player unit (btl_data 0-3, never used in this battle) and a free party slot, if any.</summary>
        public static Boolean TryFindSlots(out Int32 btlIndex, out Int32 memberIndex)
        {
            btlIndex = memberIndex = -1;
            BTL_DATA[] data = FF9StateSystem.Battle.FF9Battle.btl_data;
            for (Int32 i = 0; i < 4 && btlIndex < 0; i++)
                if (data[i] != null && data[i].btl_id == 0 && data[i].gameObject == null)
                    btlIndex = i;
            PLAYER[] members = FF9StateSystem.Common.FF9.party.member;
            for (Int32 i = 0; i < 4 && memberIndex < 0; i++)
                if (members[i] == null && (battle.btl_bonus.member_flag & (1 << i)) == 0)
                    memberIndex = i;
            return btlIndex >= 0 && memberIndex >= 0;
        }

        /// <summary>Why a character can't be added to the battle now (null: it can).</summary>
        public static String CantAdd(Int32 character)
        {
            FF9StateBattleSystem sys = FF9StateSystem.Battle.FF9Battle;
            if (sys.btl_phase != FF9StateBattleSystem.PHASE_NORMAL)
                return "the battle isn't in its normal phase";
            if (InBattle(character))
                return "already in the battle";
            if (!FF9StateSystem.Common.FF9.player.ContainsKey((CharacterId)character))
                return "not a character of this game";
            if (!TryFindSlots(out _, out _))
                return "the battle party is full";
            return null;
        }

        /// <summary>Owner: true when nothing is in progress that an added unit could disturb (no command running).</summary>
        public static Boolean SafeNow()
        {
            FF9StateBattleSystem sys = FF9StateSystem.Battle.FF9Battle;
            return sys.btl_phase == FF9StateBattleSystem.PHASE_NORMAL && sys.cur_cmd == null;
        }

        /// <summary>Adds the character as player unit <paramref name="btlIndex"/>, party slot <paramref name="memberIndex"/>.
        /// Null and a log line if it can't (the slot is taken here: the PCs' battles differ).</summary>
        public static BTL_DATA Add(Int32 character, Int32 btlIndex, Int32 memberIndex)
        {
            FF9StateBattleSystem sys = FF9StateSystem.Battle.FF9Battle;
            FF9StateGlobal ff9 = FF9StateSystem.Common.FF9;
            if (btlIndex < 0 || btlIndex > 3 || memberIndex < 0 || memberIndex > 3)
                return Fail(character, $"slot {btlIndex}/{memberIndex} out of range");
            BTL_DATA btl = sys.btl_data[btlIndex];
            if (btl == null || btl.btl_id != 0 || btl.gameObject != null)
                return Fail(character, $"unit {btlIndex} is taken here");
            if (ff9.party.member[memberIndex] != null)
                return Fail(character, $"party slot {memberIndex} is taken here");
            if (InBattle(character))
                return Fail(character, "already in the battle here");
            if (!ff9.player.TryGetValue((CharacterId)character, out PLAYER player) || player == null)
                return Fail(character, "no such character here");

            // Where it stands: past the end of the line (the others keep their places)
            Int32 posX = 0;
            Boolean any = false;
            for (BTL_DATA b = sys.btl_list.next; b != null; b = b.next)
            {
                if (b.bi.player == 0)
                    continue;
                posX = any ? Math.Min(posX, (Int32)b.original_pos[0]) : (Int32)b.original_pos[0];
                any = true;
            }
            posX = any ? posX - Gap : 0;

            // HonoluluBattleMain.CreateBattleData: the model
            btl.typeNo = Byte.MaxValue;
            btl.weaponModels.Clear();
            BattlePlayerCharacter.CreatePlayer(btl, player);
            Int32 meshCount = 0;
            foreach (Transform t in btl.gameObject.transform)
                if (t.name.Contains("mesh"))
                    meshCount++;
            btl.meshCount = meshCount;
            btl.meshIsRendering = new Boolean[meshCount];
            for (Int32 i = 0; i < meshCount; i++)
                btl.meshIsRendering[i] = true;
            btl.animation = btl.gameObject.GetComponent<Animation>();

            // btl_init.InitPlayerData for one member
            ff9.party.member[memberIndex] = player;
            btl.dms_geo_id = btl_init.GetModelID(player.info.serial_no);
            btl_init.OrganizePlayerData(player, btl, (UInt16)memberIndex, (UInt16)btlIndex);
            btl_init.SetBattleModel(btl);
            if (btl_stat.CheckStatus(btl, BattleStatus.Death))
            {
                GeoTexAnim.geoTexAnimStop(btl.texanimptr, 2);
                GeoTexAnim.geoTexAnimPlayOnce(btl.texanimptr, 0);
                GeoTexAnim.geoTexAnimStop(btl.tranceTexanimptr, 2);
                GeoTexAnim.geoTexAnimPlayOnce(btl.tranceTexanimptr, 0);
            }
            else
            {
                GeoTexAnim.geoTexAnimPlay(btl.texanimptr, 2);
            }
            btl_sys.AddCharacter(btl);
            btl_init.SetupBattlePlayerSingle(btl, posX, battle_start_type_tags.BTL_START_NORMAL_ATTACK);
            btl.cur.at = 0;
            SFX.InitBattleParty();
            UIManager.Battle?.OnPlayerUnitAdded(new BattleUnit(btl));
            BattleSync.Note($"Unit join: {player.Name} (character {character}) added to the battle as unit {btlIndex}, party slot {memberIndex}");
            return btl;
        }

        private static BTL_DATA Fail(Int32 character, String why)
        {
            BattleSync.Note($"Unit join: character {character} not added: {why}");
            Log.Warning($"[Online] Battle join: character {character} couldn't be added to the battle here: {why}");
            return null;
        }

        /// <summary>Passive: the owner's stream says this character joined (applied at the owner's frame).</summary>
        public static void ApplyFromStream(UInt16 character, Byte slots)
        {
            BattleSync.Applying++;
            try { Add(character, slots & 0x0F, slots >> 4); }
            catch (Exception err) { Log.Error(err, $"[Online] Battle join: adding character {character} failed"); }
            finally { BattleSync.Applying--; }
        }
    }
}
