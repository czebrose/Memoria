using Assets.SiliconSocial;
using Memoria.Prime;
using SimpleJSON;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Memoria.Online.Session
{
    /// <summary>
    /// The whole game state of one save as an in-memory blob, made and loaded with the game's own save code.
    ///
    /// A save slot is a JSON tree: JsonParser.ParseFromFF9StateSystem builds it from the live state (FF9StateSystem),
    /// JsonParser.ParseToFF9StateSystem puts it back. The storage layer (SharedDataBytesStorage) only writes it to disk:
    /// it encrypts the vanilla part into SavedData_ww.dat and writes Memoria's "MemoriaExtraData" subtree to its own
    /// file, and merges both back into one tree when loading. So the tree is the complete state, and the blob is that
    /// tree in SimpleJSON's binary form (JSONNode.SaveToStream / Deserialize). No file is read or written to load it.
    ///
    /// Entering the game after the parse is what TitleUI.LoadAutosave / SaveLoadUI do after a load:
    /// EventEngine.ReplaceLoadMap (field or world map from the tree's mode/fldMapNo/fldLocNo), settings refresh,
    /// ff9.w_frameNewSession.
    /// </summary>
    public static class GameSnapshot
    {
        /// <summary>Subtrees a guest keeps its own copy of: options (volume, language, ATB...) and Steam achievement progress.</summary>
        private static readonly String[] GuestKeepsOwn = { "50000_Setting", "80000_Achievement", "98000_Achievement" };
        /// <summary>Play time; it moves on between a capture and a reload, so a loopback check ignores it.</summary>
        private static readonly String[] TimeKeys = { "00001_time", "time" };

        private static JsonParser Parser => FF9StateSystem.Serializer?.Parser as JsonParser;

        /// <summary>The current game, as a save would store it right now.</summary>
        public static Byte[] CaptureCurrent(out JSONClass tree)
        {
            JsonParser parser = Parser;
            if (parser == null)
                throw new InvalidOperationException("the game's save parser isn't available");
            parser.ParseFromFF9StateSystem();
            tree = parser.RootNodeInParser;
            // Serialize now: SharedDataBytesStorage.Save removes "MemoriaExtraData" from the tree it's given
            return ToBytes(tree);
        }

        /// <summary>The most recently saved slot, from the save file's metadata (FF9StateSystem.latestSlot is only set once the save screen has read it).</summary>
        public static void GetLatestSlot(Action<Int32, Int32> done)
        {
            ISharedDataStorage storage = FF9StateSystem.Serializer?.Storage;
            if (storage == null)
            {
                done(-1, -1);
                return;
            }
            storage.GetLatestSlotAndSave((slot, save) => done(slot, save));
        }

        /// <summary>
        /// The save pages that exist: page 0 (the game's own 10 slots), then Memoria's extra pages (EncryptedSaveData/
        /// ExtraSave_N, made when the load screen first shows that page). An extra page that was never opened has no
        /// saves, so it isn't listed (and its folder isn't created).
        /// </summary>
        public static List<Int32> SavePages()
        {
            List<Int32> pages = new List<Int32> { 0 };
            if (!(FF9StateSystem.Serializer?.Storage is SharedDataBytesStorage) || String.IsNullOrEmpty(SharedDataBytesStorage.MetaData.DirPath))
                return pages;
            for (Int32 page = 1; page < SharedDataBytesStorage.MaximumSavePage; page++)
                if (File.Exists(Path.Combine(Path.Combine(SharedDataBytesStorage.MetaData.DirPath, "ExtraSave_" + page), Path.GetFileName(SharedDataBytesStorage.MetaData.FilePath ?? "SavedData_ww.dat"))))
                    pages.Add(page);
            return pages;
        }

        /// <summary>
        /// Runs <paramref name="act"/> with the storage on save page <paramref name="page"/>, then puts the page the
        /// load screen was on back. The storage opens its file inside the call, so a preview read finishing later
        /// (a coroutine) still reads that page.
        /// </summary>
        public static void OnPage(Int32 page, Action act)
        {
            Int32 current = SharedDataBytesStorage.CurrentSavePage;
            if (page == current || !(FF9StateSystem.Serializer?.Storage is SharedDataBytesStorage))
            {
                act();
                return;
            }
            SharedDataBytesStorage.CurrentSavePage = page;
            SharedDataBytesStorage.UpdatePathForPage();
            try
            {
                act();
            }
            finally
            {
                SharedDataBytesStorage.CurrentSavePage = current;
                SharedDataBytesStorage.UpdatePathForPage();
            }
        }

        /// <summary>A save slot's tree, read through the game's storage (decrypted, Memoria data merged) without loading it.</summary>
        public static void CaptureSlot(Int32 page, Int32 slotId, Int32 saveId, Action<Byte[], JSONClass, String> done)
        {
            OnPage(page, () => CaptureSlot(slotId, saveId, done));
        }

        /// <summary>A save slot's tree on the load screen's current page (see the overload with a page).</summary>
        public static void CaptureSlot(Int32 slotId, Int32 saveId, Action<Byte[], JSONClass, String> done)
        {
            ISharedDataStorage storage = FF9StateSystem.Serializer?.Storage;
            if (storage == null)
            {
                done(null, null, "the game's save storage isn't available");
                return;
            }
            storage.Load(slotId, saveId, (slot, save, rootNode) =>
            {
                if (rootNode == null)
                    done(null, null, $"slot {slotId}/{saveId} could not be read (empty or corrupt?)");
                else
                    done(ToBytes(rootNode), rootNode, null);
            });
        }

        /// <summary>
        /// Debug, title screen only: is loading this slot's blob from memory the same as the game's own load of the
        /// slot? Loads the slot with the game's serializer (FF9StateSystem.Serializer.Load, what Continue does), captures
        /// the resulting state, then parses the blob the way <see cref="Load"/> does and captures again; logs the
        /// differences. Changes the game state, so it's only for the title screen (Continue would replace it anyway).
        /// </summary>
        public static void CompareWithGameLoad(Int32 slotId, Int32 saveId, Byte[] blob)
        {
            JsonParser parser = Parser;
            FF9StateSystem.Serializer.Load(slotId, saveId, null, (errNo, slot, save, success) =>
            {
                if (!success)
                {
                    Log.Warning($"[Online] Load-path check: the game's own load of slot {slotId}/{saveId} failed ({errNo})");
                    return;
                }
                parser.ParseFromFF9StateSystem();
                JSONClass gameLoaded = FromBytes(ToBytes(parser.RootNodeInParser));

                parser.ParseToFF9StateSystem(FromBytes(blob));
                parser.ParseFromFF9StateSystem();
                JSONClass memoryLoaded = FromBytes(ToBytes(parser.RootNodeInParser));

                List<String> differences = Diff(gameLoaded, memoryLoaded, 25);
                Log.Message(differences.Count == 0
                    ? $"[Online] Load-path check: loading slot {slotId}/{saveId} from memory gives exactly the same game state as the game's own load"
                    : $"[Online] Load-path check: {(differences.Count >= 25 ? "25+" : differences.Count.ToString())} difference(s) between the game's own load and the memory load: {String.Join("; ", differences.ToArray())}");
            });
        }

        public static Byte[] ToBytes(JSONNode tree)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                tree.SaveToStream(stream);
                return stream.ToArray();
            }
        }

        public static JSONClass FromBytes(Byte[] blob)
        {
            using (MemoryStream stream = new MemoryStream(blob))
            using (BinaryReader reader = new BinaryReader(stream))
                return JSONNode.Deserialize(reader) as JSONClass;
        }

        /// <summary>Removes what a guest keeps its own copy of (see <see cref="GuestKeepsOwn"/>).</summary>
        public static void PrepareForGuest(JSONClass tree)
        {
            JSONClass data = tree["Data"] as JSONClass;
            if (data == null)
                return;
            foreach (String key in GuestKeepsOwn)
                data.Remove(key);
        }

        /// <summary>
        /// Loads a blob into the game and enters it (from the title screen or from a field).
        /// <paramref name="asGuest"/>: keep the local options and achievements, don't resync Steam achievements.
        /// Returns the tree that was applied (to compare with the state afterwards), or null on failure.
        /// </summary>
        public static JSONClass Load(Byte[] blob, Boolean asGuest, out String error)
        {
            error = null;
            JsonParser parser = Parser;
            if (parser == null)
            {
                error = "the game's save parser isn't available";
                return null;
            }
            JSONClass tree;
            try
            {
                tree = FromBytes(blob);
            }
            catch (Exception err)
            {
                error = $"the blob isn't a save tree ({err.Message})";
                return null;
            }
            if (tree == null || tree["Data"] == null)
            {
                error = "the blob isn't a save tree";
                return null;
            }
            if (asGuest)
            {
                PrepareForGuest(tree);
                GuestGuard.BeginGuestSession(); // before the field loads: its autosave must already be blocked
            }
            Stopwatch watch = Stopwatch.StartNew();
            parser.ParseToFF9StateSystem(tree);
            Log.Message($"[Online] Snapshot parsed into the game state in {watch.ElapsedMilliseconds} ms (mode {PersistenSingleton<FF9StateSystem>.Instance.mode}, field {FF9StateSystem.Common.FF9.fldMapNo}, entrance {FF9StateSystem.Common.FF9.fldLocNo}, world map {FF9StateSystem.Common.FF9.wldMapNo})");

            // Round trip of the parser alone, before any field script runs: capture the state again and compare.
            // (The check once the game is playable also includes what the field's scripts changed on entering.)
            parser.ParseFromFF9StateSystem();
            JSONClass reparsed = FromBytes(ToBytes(parser.RootNodeInParser));
            if (asGuest)
                PrepareForGuest(reparsed);
            List<String> differences = Diff(tree, reparsed, 25);
            Log.Message(differences.Count == 0
                ? "[Online] Snapshot check right after parsing (before the field starts): the game state matches the snapshot exactly"
                : $"[Online] Snapshot check right after parsing: {(differences.Count >= 25 ? "25+" : differences.Count.ToString())} difference(s): {String.Join("; ", differences.ToArray())}");

            // A snapshot is taken mid-field, not at a save point: General_LoadedGame (set by moogle scripts just before the
            // save menu) is 0 and General_FieldEntrance is still the entrance the field was entered by, so the field's
            // Main_Init would replay that entrance (e.g. the mod's field 507 re-ran its New Game party setup and dropped
            // Duskly and Vayle). Enter it as a loaded game does: field scripts turn General_LoadedGame into entrance 10000.
            if (PersistenSingleton<FF9StateSystem>.Instance.mode == 1)
            {
                Log.Message($"[Online] Snapshot load: field entrance {FF9StateSystem.EventState.FieldEntrance} -> 10000 (as a loaded game)");
                FF9StateSystem.EventState.FieldEntrance = 10000;
            }

            UIManager ui = PersistenSingleton<UIManager>.Instance;
            if (ui.State == UIManager.UIState.Title)
                ui.TitleScene.Hide(() => EnterLoadedGame(asGuest));
            else
                EnterLoadedGame(asGuest);
            PersistenSingleton<UIManager>.Instance.MainMenuScene?.EnabledSubMenus?.Clear(); // a menu restriction (from a battle menu) never outlives a reload
            Memoria.Online.Party.PartySync.Instance?.OnGameLoaded(); // a whole-game load: the party sync starts over from it
            return tree;
        }

        private static void EnterLoadedGame(Boolean asGuest)
        {
            // As TitleUI.LoadAutosave / SaveLoadUI.OnFinishedLoadFile_delay
            FF9StateSystem.Settings.SetSound();
            FF9StateSystem.Settings.SetSoundEffect();
            PersistenSingleton<EventEngine>.Instance.ReplaceLoadMap();
            PersistenSingleton<UIManager>.Instance.TitleScene.SplashScreenEnabled = true;
            SoundLib.StopMovieMusic("FMV000", true);
            if (!asGuest)
                AchievementManager.ResyncNormalAchievements();
            FF9StateSystem.Settings.UpdateSetting();
            ff9.w_frameNewSession();
        }

        /// <summary>Paths where two trees differ (play time ignored), at most <paramref name="max"/>.</summary>
        public static List<String> Diff(JSONNode expected, JSONNode actual, Int32 max)
        {
            List<String> differences = new List<String>();
            DiffNode("", expected, actual, differences, max);
            return differences;
        }

        private static void DiffNode(String path, JSONNode a, JSONNode b, List<String> differences, Int32 max)
        {
            if (differences.Count >= max)
                return;
            if (a is JSONClass ca)
            {
                if (!(b is JSONClass cb))
                {
                    differences.Add($"{path}: object vs {(b == null ? "missing" : b.GetType().Name)}");
                    return;
                }
                foreach (KeyValuePair<String, JSONNode> kv in ca)
                {
                    if (Array.IndexOf(TimeKeys, kv.Key) >= 0)
                        continue;
                    DiffNode(path + "/" + kv.Key, kv.Value, cb[kv.Key], differences, max);
                }
                foreach (KeyValuePair<String, JSONNode> kv in cb)
                    if (ca[kv.Key] == null && differences.Count < max)
                        differences.Add($"{path}/{kv.Key}: missing vs present");
                return;
            }
            if (a is JSONArray aa)
            {
                if (!(b is JSONArray ab) || ab.Count != aa.Count)
                {
                    differences.Add($"{path}: array of {aa.Count} vs {(b is JSONArray x ? x.Count.ToString() : "not an array")}");
                    return;
                }
                for (Int32 i = 0; i < aa.Count; i++)
                    DiffNode($"{path}[{i}]", aa[i], ab[i], differences, max);
                return;
            }
            String va = a?.Value;
            String vb = b?.Value;
            if (va != vb)
                differences.Add($"{path}: '{Shorten(va)}' vs '{Shorten(vb)}'");
        }

        private static String Shorten(String value)
        {
            if (value == null)
                return "(none)";
            return value.Length > 40 ? value.Substring(0, 37) + "..." : value;
        }

        public static UInt32 Crc32(Byte[] data, Int32 offset, Int32 length)
        {
            UInt32 crc = 0xFFFFFFFF;
            for (Int32 i = offset; i < offset + length; i++)
            {
                crc ^= data[i];
                for (Int32 k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return ~crc;
        }
    }
}
