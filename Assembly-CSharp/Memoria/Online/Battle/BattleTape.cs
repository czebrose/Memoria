using System;

namespace Memoria.Online.Battle
{
    /// <summary>
    /// Spike C: the random numbers a command draws outside the calculation (CMD_MODE_INSPECTION / SELECT_VFX, the SFX
    /// sequencer, FinishCommand). Hooked into Comn.random8/random16, GameRandom and btl_util.GetRandomBtlID, the game's
    /// battle RNG entry points. While a command is "in context" (<see cref="Enter"/>) and no calculation is running:
    /// the owner appends each value to the command's tape; the passive client returns the taped value instead.
    /// Purely cosmetic randomness calls UnityEngine.Random directly and is left alone.
    /// </summary>
    public static class BattleTape
    {
        [ThreadStatic] private static CMD_DATA _context;

        public static CMD_DATA Context => _context;

        public static CMD_DATA Enter(CMD_DATA cmd)
        {
            CMD_DATA previous = _context;
            _context = cmd;
            return previous;
        }

        public static void Exit(CMD_DATA previous)
        {
            _context = previous;
        }

        public static Int32 Filter(TapeKind kind, Int32 value)
        {
            if (_context == null || BattleSync.CalcDepth > 0)
                return value;
            if (BattleSync.Recorder != null)
                return BattleSync.Recorder.Tape(_context, kind, value);
            if (BattleSync.Replayer != null)
                return BattleSync.Replayer.Tape(_context, kind, value);
            return value;
        }

        public static Single Filter(Single value)
        {
            if (_context == null || BattleSync.CalcDepth > 0)
                return value;
            Int32 bits = BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
            Int32 result = Filter(TapeKind.RangeSingle, bits);
            return result == bits ? value : BitConverter.ToSingle(BitConverter.GetBytes(result), 0);
        }
    }
}
