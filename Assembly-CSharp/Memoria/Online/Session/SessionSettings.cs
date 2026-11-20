using Memoria.Online.Party;
using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Memoria.Online.Session
{
    /// <summary>
    /// Step 3 / M2, task 1: a session plays by one set of battle rules, the host's.
    ///
    /// - <b>Host</b>, when its lobby opens: its battle settings (the Memoria.ini keys of
    ///   <see cref="Configuration.SessionBattle"/>, the Config menu's battle style / battle speed / battle camera, the
    ///   battle boosters) become the session's, published as lobby data "bset". A value the online code doesn't support
    ///   (Battle Speed 2-5, SFXRework 0, battle style Wait, High Speed Mode) is replaced by the supported default, in
    ///   memory, with a one-line notice. (The launcher and the Config menu don't offer those values any more; this is the
    ///   backstop for a hand-edited Memoria.ini or an old save.)
    /// - <b>Guest</b>, once the session plays: the host's values are set in memory, never written to Memoria.ini or a
    ///   save (guests never save). Its own values come back when it leaves the lobby; on a crash nothing on disk changed.
    ///   The Config menu values are set again every frame (a resync loads the host's save, Config included).
    /// - <b>While playing</b> (everyone): the battle options of the Config menu and the battle boosters are locked
    ///   ("set by the host for this session").
    /// </summary>
    public static class SessionSettings
    {
        private const String LobbyKey = "bset";
        private const Single NoticeSeconds = 8f;

        private static String _notice;
        private static Single _noticeUntil;
        public static String Notice => _notice != null && Time.realtimeSinceStartup < _noticeUntil ? _notice : null;

        private static SessionPhase _phase = SessionPhase.Idle;
        private static Boolean _isHost;
        private static Dictionary<String, String> _ownIni;     // this PC's own ini values (restored on leaving)
        private static UInt64[] _ownCfg;                       // atb, btl_speed, camera
        private static Boolean[] _ownBoosters;
        private static Dictionary<String, String> _session;    // the session's values (host: published; guest: applied)
        private static String _published, _applied;

        private static readonly Int32[] ForcedBoosters = { (Int32)BoosterType.BattleAssistance, (Int32)BoosterType.HighSpeedMode, (Int32)BoosterType.Attack9999, (Int32)BoosterType.NoRandomEncounter, (Int32)BoosterType.MasterSkill };

        public static Boolean Playing => _phase == SessionPhase.Playing;

        private static void Show(String notice)
        {
            _notice = notice;
            _noticeUntil = Time.realtimeSinceStartup + NoticeSeconds;
            Log.Message("[Online] Session settings: " + notice);
        }

        /// <summary>SessionLobby, every update (Idle when not in a lobby).</summary>
        internal static void Update(SessionPhase phase, Boolean isHost, SteamLobby lobby)
        {
            try
            {
                if (phase != _phase)
                {
                    SessionPhase old = _phase;
                    _phase = phase;
                    _isHost = isHost;
                    if (old == SessionPhase.Idle)
                        Enter(lobby);
                    else if (phase == SessionPhase.Idle)
                        Leave();
                }
                if (_phase == SessionPhase.Idle)
                    return;
                if (_isHost)
                {
                    String data = Serialize(_session);
                    if (data != _published)
                    {
                        _published = data;
                        lobby.SetData(LobbyKey, data);
                    }
                }
                else if (_phase == SessionPhase.Playing)
                {
                    String data = lobby.GetData(LobbyKey);
                    if (!String.IsNullOrEmpty(data) && data != _applied)
                    {
                        _applied = data;
                        _session = Parse(data);
                        List<String> changed = Configuration.SessionBattle.Apply(_session);
                        Log.Message($"[Online] Session settings: the host's battle settings applied in memory ({changed.Count} differed{(changed.Count > 0 ? ": " + String.Join(", ", changed.ToArray()) : "")}); Memoria.ini unchanged");
                        Show("Using the host's battle settings for this session.");
                    }
                }
                if (_session != null && (_isHost || _applied != null))
                    EnforceRuntime();
            }
            catch (Exception err)
            {
                Log.Error(err, "[Online] Session settings failed");
            }
        }

        private static void Enter(SteamLobby lobby)
        {
            _ownIni = Configuration.SessionBattle.Capture();
            FF9CFG cfg = FF9StateSystem.Settings.cfg;
            _ownCfg = new[] { cfg.atb, cfg.btl_speed, cfg.camera };
            _ownBoosters = (Boolean[])FF9StateSystem.Settings.IsBoosterButtonActive.Clone();
            _published = _applied = null;
            if (!_isHost)
            {
                _session = null;
                return;
            }
            // The host's settings become the session's; unsupported values get the supported default
            _session = new Dictionary<String, String>(_ownIni);
            List<String> overridden = new List<String>();
            Int32 rawSpeed = Configuration.SessionBattle.RawSpeed;
            if (rawSpeed > Configuration.Battle.MaxSupportedSpeed || rawSpeed < 0)
            {
                overridden.Add($"Battle Speed {rawSpeed} -> 0");
                _session["Battle.Speed"] = "0";
                _session["Hacks.BattleSpeed"] = "0";
            }
            if (!Configuration.SessionBattle.RawSfxRework)
            {
                overridden.Add("SFXRework 0 -> 1");
                _session["Battle.SFXRework"] = "1";
            }
            if (cfg.atb != 0)
                overridden.Add("battle style Wait -> Active");
            _session["cfg.atb"] = "0";
            _session["cfg.btl_speed"] = cfg.btl_speed.ToString();
            _session["cfg.camera"] = cfg.camera.ToString();
            foreach (Int32 b in ForcedBoosters)
            {
                Boolean on = FF9StateSystem.Settings.IsBoosterButtonActive[b];
                if (b == (Int32)BoosterType.HighSpeedMode && on)
                {
                    overridden.Add("High Speed Mode off");
                    on = false;
                }
                _session["booster." + b] = on ? "1" : "0";
            }
            Configuration.SessionBattle.Apply(_session);
            Log.Message($"[Online] Session settings (host): {_session.Count} values are the session's{(overridden.Count > 0 ? "; overridden: " + String.Join(", ", overridden.ToArray()) : "")}");
            if (overridden.Count > 0)
                Show("Session battle settings changed (not supported online): " + String.Join(", ", overridden.ToArray()) + ".");
            if (FF9StateSystem.Settings.IsBoosterButtonActive[(Int32)BoosterType.HighSpeedMode])
                FF9StateSystem.Settings.CallBoosterButtonFuntion(BoosterType.HighSpeedMode, false);
        }

        private static void Leave()
        {
            if (_ownIni != null)
            {
                List<String> changed = Configuration.SessionBattle.Apply(_ownIni);
                FF9CFG cfg = FF9StateSystem.Settings.cfg;
                cfg.atb = Math.Min(_ownCfg[0], 0UL); // Wait is blocked everywhere now
                cfg.btl_speed = _ownCfg[1];
                cfg.camera = _ownCfg[2];
                foreach (Int32 b in ForcedBoosters)
                    if (b != (Int32)BoosterType.HighSpeedMode)
                        FF9StateSystem.Settings.IsBoosterButtonActive[b] = _ownBoosters[b];
                Log.Message($"[Online] Session settings: left the session; this PC's own settings are back ({changed.Count} ini value(s) restored{(changed.Count > 0 ? ": " + String.Join(", ", changed.ToArray()) : "")})");
            }
            _ownIni = null;
            _session = null;
            _published = _applied = null;
        }

        /// <summary>The Config menu values and boosters, set again every frame (a resync loads the host's Config too).</summary>
        private static void EnforceRuntime()
        {
            FF9CFG cfg = FF9StateSystem.Settings.cfg;
            cfg.atb = 0;
            if (_session.TryGetValue("cfg.btl_speed", out String speed) && UInt64.TryParse(speed, out UInt64 s))
                cfg.btl_speed = s;
            if (_session.TryGetValue("cfg.camera", out String camera) && UInt64.TryParse(camera, out UInt64 c))
                cfg.camera = c;
            foreach (Int32 b in ForcedBoosters)
                if (_session.TryGetValue("booster." + b, out String on))
                    FF9StateSystem.Settings.IsBoosterButtonActive[b] = on == "1";
        }

        // ---- Locks (Config menu, boosters) -----------------------------------------------------------------------

        /// <summary>[Online] ConfigUI: true = this option is the session's (locked while playing).</summary>
        public static Boolean IsLocked(ConfigUI.Configurator option)
        {
            if (_phase != SessionPhase.Playing)
                return false;
            switch (option)
            {
                case ConfigUI.Configurator.ATB:
                case ConfigUI.Configurator.BattleSpeed:
                case ConfigUI.Configurator.ATBMode:
                case ConfigUI.Configurator.BattleCamera:
                    PartySync.Toast("Battle options are set by the host for this session.");
                    return true;
            }
            return false;
        }

        /// <summary>[Online] SettingsState.CallBoosterButtonFuntion: true = blocked (a battle booster in a session).</summary>
        public static Boolean BlockBooster(BoosterType type)
        {
            if (_phase != SessionPhase.Playing)
                return false;
            switch (type)
            {
                case BoosterType.BattleAssistance:
                case BoosterType.HighSpeedMode:
                case BoosterType.Attack9999:
                case BoosterType.NoRandomEncounter:
                case BoosterType.MasterSkill:
                case BoosterType.LvMax:
                case BoosterType.GilMax:
                    PartySync.Toast("Boosters that change battles are set by the host for this session.");
                    return true;
            }
            return false;
        }

        // ---- Format: "key=value" lines -------------------------------------------------------------------------

        private static String Serialize(Dictionary<String, String> values)
        {
            if (values == null)
                return String.Empty;
            StringBuilder sb = new StringBuilder();
            List<String> keys = new List<String>(values.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (String k in keys)
                sb.Append(k).Append('=').Append(values[k].Replace("\n", " ")).Append('\n');
            return sb.ToString();
        }

        private static Dictionary<String, String> Parse(String data)
        {
            Dictionary<String, String> values = new Dictionary<String, String>();
            foreach (String line in data.Split('\n'))
            {
                Int32 eq = line.IndexOf('=');
                if (eq > 0)
                    values[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            return values;
        }

        public static String Describe => _phase == SessionPhase.Idle ? null
            : $"settings: {(_isHost ? "host's (published)" : _applied != null ? "the host's, in memory" : "own (not applied yet)")}, Speed {Configuration.Battle.Speed}, SFXRework {(Configuration.Battle.SFXRework ? 1 : 0)}, style {(FF9StateSystem.Settings.cfg.atb == 0 ? "Active" : "Wait")}, battle speed {FF9StateSystem.Settings.cfg.btl_speed}";
    }
}
