using System;
using Random = UnityEngine.Random;

namespace Memoria
{
    public static class GameRandom
    {
        public static Int32 Next8()
        {
            return Online.Battle.BattleTape.Filter(Online.Battle.TapeKind.Random8, Random.Range(0, 256)); // [Online] Spike C. UnityEngine.Random.Range(min [inclusive], max [exclusive])
        }

        public static Int32 Next16()
        {
            return Online.Battle.BattleTape.Filter(Online.Battle.TapeKind.Random16, Random.Range(0, 65536)); // [Online] Spike C
        }

        public static Int32 RandomInt(Int32 minInclusive, Int32 maxExclusive)
        {
            return Online.Battle.BattleTape.Filter(Online.Battle.TapeKind.Range, Random.Range(minInclusive, maxExclusive)); // [Online] Spike C
        }

        public static Single RandomSingle(Single min, Single max)
        {
            return Online.Battle.BattleTape.Filter(Random.Range(min, max)); // [Online] Spike C
        }
    }
}
