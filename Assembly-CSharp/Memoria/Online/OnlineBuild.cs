using Memoria.Prime;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Memoria.Online
{
    /// <summary>
    /// Which online build this is: the release it's for (<see cref="Version"/>), and the first 12 hex digits of this
    /// Assembly-CSharp.dll's SHA-256 (<see cref="Hash"/>), the same digits the release zip's name ends with
    /// (tools/multiplayer/package.ps1). Shown on the title screen, and compared with the other PCs' in a lobby
    /// (lobby member data "build"), since two PCs on different builds break in ways that look like bugs.
    /// </summary>
    internal static class OnlineBuild
    {
        /// <summary>The release this build is for (bump it with each release).</summary>
        public const String Version = "0.1.5";

        private static String _hash;

        /// <summary>12 hex digits of this DLL's SHA-256, or "unknown" if the file can't be read.</summary>
        public static String Hash
        {
            get
            {
                if (_hash == null)
                    _hash = ComputeHash();
                return _hash;
            }
        }

        /// <summary>"v0.1.5 1A2B3C4D5E6F": what a PC puts in its lobby member data, and what the title screen shows.</summary>
        public static String Id => $"v{Version} {Hash}";

        private static String ComputeHash()
        {
            try
            {
                String path = typeof(OnlineBuild).Assembly.Location;
                if (String.IsNullOrEmpty(path) || !File.Exists(path))
                    return "unknown";
                using (SHA256 sha = SHA256.Create())
                using (FileStream file = File.OpenRead(path))
                {
                    Byte[] digest = sha.ComputeHash(file);
                    StringBuilder text = new StringBuilder(12);
                    for (Int32 i = 0; i < 6; i++)
                        text.Append(digest[i].ToString("X2"));
                    String hash = text.ToString();
                    Log.Message($"[Online] Build: online co-op v{Version}, Assembly-CSharp.dll {hash}");
                    return hash;
                }
            }
            catch (Exception err)
            {
                Log.Warning($"[Online] Build: couldn't read this build's DLL to name it ({err.Message})");
                return "unknown";
            }
        }
    }
}
