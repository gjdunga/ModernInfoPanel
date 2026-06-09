// ModernInfoPanel.cs
//
// Copyright (c) 2026 Gabriel Dungan, DunganSoft Technologies (GitHub: gjdunga)
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more
// details. You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.
//
// Part of the DunganSoft Oxide/Rust plugin portfolio. Conforms to the
// DunganSoft Plugin Standard: https://github.com/gjdunga/rust-plugin-standard
// Compatible with both Oxide and Carbon (uses only the shared Rust/CUI APIs).
//
// A modern, security- and performance-minded rebuild of the classic InfoPanel
// concept (originally by Gonzi). Four corner "docks" host configurable panels:
// a clock, a rotating message box, balance/points read-outs, coordinates, a
// compass, online/sleeper counters, and live event indicators (airdrop,
// patrol helicopter, chinook, cargo ship, bradley, radiation). A small,
// reflection-free API lets other plugins register their own panels.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Modern Info Panel", "gjdunga", "2.0.1")]
    [Description("Configurable corner HUD panels: clock, announcements, balance, points, coordinates, compass, player counts and live event indicators. Oxide + Carbon compatible.")]
    public class ModernInfoPanel : RustPlugin
    {
        #region References

        [PluginReference] private Plugin Economics;
        [PluginReference] private Plugin ServerRewards;

        #endregion

        #region Constants & fields

        private const string PermAdmin = "moderninfopanel.admin";
        private const string PermPrefix = "moderninfopanel.";

        private const string Root = "MIP_root";
        private const string AdminRoot = "MIP_admin";
        private const string DataFile = "ModernInfoPanel";

        // Preset background-color swatches offered by the admin editor (R G B A).
        private static readonly string[] AdminPalette =
        {
            "0 0 0 0.45", "0 0 0 0.70", "0.10 0.10 0.12 0.80", "0.15 0.30 0.50 0.80",
            "0.30 0.15 0.15 0.80", "0.15 0.30 0.15 0.80", "0 0 0 0"
        };

        // Built-in panel identifiers.
        private const string PClock = "Clock";
        private const string PMessages = "Messages";
        private const string PBalance = "Balance";
        private const string PPoints = "Points";
        private const string PCoordinates = "Coordinates";
        private const string PCompass = "Compass";
        private const string POnline = "OnlinePlayers";
        private const string PSleepers = "Sleepers";
        private const string PAirdrop = "AirdropEvent";
        private const string PHeli = "HelicopterEvent";
        private const string PChinook = "ChinookEvent";
        private const string PCargo = "CargoShipEvent";
        private const string PBradley = "BradleyEvent";
        private const string PRadiation = "RadiationEvent";
        private const string PServerFps = "ServerFPS";
        private const string PPing = "Ping";
        private const string PWipe = "WipeCountdown";
        private const string PStatus = "Status";

        // Third-party API limits.
        private const int MaxPanelsPerPlugin = 25;
        private const int MaxPanelNameLen = 64;
        private const int MaxTextLen = 256;

        private static readonly string[] TimeFormats = { "H:mm", "HH:mm", "h:mm", "h:mm tt" };
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Expanding backoff (seconds) for deferring panel registration while a freshly
        // connected client is still receiving its world snapshot.
        private static readonly float[] SnapshotRetryDelays = { 2f, 5f, 10f, 15f };

        private Configuration _config;
        private StoredData _data;
        private Timer _tickTimer;
        private bool _ready;
        private long _tick;
        private int _messageIndex;
        private bool _dataDirty;

        // WipeCountdown state.
        private DateTime _wipeAt;          // cached next-wipe time (default = recompute)
        private bool _newSavePending;      // OnNewSave fired before data was ready
        private bool _wipeWarned;          // warn about an unset schedule only once

        // Light per-player command cooldown (Time.realtimeSinceStartup) to blunt console-macro spam.
        private readonly Dictionary<string, float> _lastCmd = new Dictionary<string, float>();
        private const float CommandCooldown = 1f;

        // Per-connected-player render state.
        private readonly Dictionary<string, PlayerView> _views = new Dictionary<string, PlayerView>();

        // Dock name -> panels in that dock, pre-sorted by Order (rebuilt on load/reload).
        private readonly Dictionary<string, List<PanelEntry>> _dockIndex = new Dictionary<string, List<PanelEntry>>();

        // Live state shared by all players.
        private readonly Dictionary<string, string> _eventColor = new Dictionary<string, string>();
        private readonly HashSet<string> _activeEvents = new HashSet<string>();
        private readonly Dictionary<string, HashSet<BaseEntity>> _eventEntities = new Dictionary<string, HashSet<BaseEntity>>();
        private bool _radiationOn;

        // Per-owner "being raided" markers for the Status glow. Value = Time.realtimeSinceStartup
        // up to which the owner's panel glows the raid color (set by OnEntityTakeDamage).
        private readonly Dictionary<ulong, float> _raidUntil = new Dictionary<ulong, float>();

        // Maps a spawned entity type to the event panel it drives.
        private static readonly Dictionary<Type, string> EventTypes = new Dictionary<Type, string>();

        // Third-party panels: plugin title -> panel names it registered.
        private readonly Dictionary<string, List<string>> _pluginPanels = new Dictionary<string, List<string>>();

        // Live configs for third-party panels, re-merged after a config reload.
        private readonly Dictionary<string, PanelConfig> _thirdParty = new Dictionary<string, PanelConfig>();

        // Static/third-party content overrides: panel -> text/color (global and per-player).
        private readonly Dictionary<string, string> _customText = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _customColor = new Dictionary<string, string>();
        // Third-party progress-bar fill levels (0..1), panel -> value, applied to new draws.
        private readonly Dictionary<string, float> _customProgress = new Dictionary<string, float>();

        private readonly HashSet<string> _registeredPerms = new HashSet<string>();

        private sealed class PanelEntry
        {
            public string Name;
            public PanelConfig Cfg;
        }

        private sealed class PlayerView
        {
            public BasePlayer Player;
            public ulong Id;
            public string IdString;
            public bool Visible;                         // currently drawn on the client
            public readonly Dictionary<string, string> Text = new Dictionary<string, string>();
            public readonly Dictionary<string, string> Color = new Dictionary<string, string>();
            public readonly Dictionary<string, float> Progress = new Dictionary<string, float>();

            // AFK detection: last sampled position/yaw and how many ticks (~seconds) unchanged.
            public Vector3 LastPos;
            public float LastYaw;
            public int IdleTicks;
        }

        #endregion

        #region Configuration

        private sealed class Configuration
        {
            [JsonProperty("Config version")]
            public string Version = "1.6.0";

            [JsonProperty("General")]
            public GeneralOptions General = new GeneralOptions();

            [JsonProperty("Docks")]
            public Dictionary<string, DockConfig> Docks = new Dictionary<string, DockConfig>();

            [JsonProperty("Panels")]
            public Dictionary<string, PanelConfig> Panels = new Dictionary<string, PanelConfig>();

            [JsonProperty("Rotating announcement messages")]
            public List<string> Messages = new List<string>();

            [JsonProperty("Wipe schedule")]
            public WipeOptions Wipe = new WipeOptions();
        }

        private sealed class GeneralOptions
        {
            [JsonProperty("Coordinate format (0 = X/Z, 1 = grid, 2 = both)")]
            public int CoordinateType = 2;

            [JsonProperty("Compass shows text direction (false = degrees)")]
            public bool CompassAsText = true;

            [JsonProperty("Message rotation order (normal | random)")]
            public string MessageOrder = "normal";

            [JsonProperty("Show panels to players by default")]
            public bool ShownByDefault = true;

            [JsonProperty("Panel fade-in seconds (0 = off)")]
            public float FadeIn = 0.2f;

            [JsonProperty("Resolve {tokens} via PlaceholderAPI if installed")]
            public bool UsePlaceholderApi = true;
        }

        private sealed class WipeOptions
        {
            [JsonProperty("Interval (auto | weekly | biweekly | monthly | custom)")]
            public string Interval = "auto";
            [JsonProperty("Wipe day of week (server time)")]
            public string DayOfWeek = "Thursday";
            [JsonProperty("Wipe time (HH:mm, server time)")]
            public string Time = "19:00";
            [JsonProperty("Custom interval days (for 'custom')")]
            public int CustomDays = 0;
        }

        private sealed class DockConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Horizontal edge (Left | Right)")] public string AnchorX = "Left";
            [JsonProperty("Vertical edge (Top | Bottom)")] public string AnchorY = "Bottom";
            [JsonProperty("Distance from horizontal edge (0-1)")] public float OffsetX = 0.006f;
            [JsonProperty("Distance from vertical edge (0-1)")] public float OffsetY = 0.02f;
            [JsonProperty("Width (0-1)")] public float Width = 0.3f;
            [JsonProperty("Height (0-1)")] public float Height = 0.03f;
            [JsonProperty("Background color (R G B A)")] public string BackgroundColor = "0 0 0 0";
        }

        private sealed class PanelConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Dock")] public string Dock = "BottomLeftDock";
            [JsonProperty("Order")] public int Order = 1;
            [JsonProperty("Width within dock (0-1)")] public float Width = 0.2f;
            [JsonProperty("Align within dock (Left | Right)")] public string Anchor = "Left";
            [JsonProperty("Background color (R G B A)")] public string BackgroundColor = "0 0 0 0.45";
            [JsonProperty("Permission suffix (null = everyone)", NullValueHandling = NullValueHandling.Ignore)]
            public string Permission;
            [JsonProperty("Refresh interval seconds (0 = static/event-driven)")] public int RefreshInterval;
            [JsonProperty("Run command on click (console cmd, blank = off)", NullValueHandling = NullValueHandling.Ignore)]
            public string Command;
            [JsonProperty("Image", NullValueHandling = NullValueHandling.Ignore)] public ImageElement Image;
            [JsonProperty("Text", NullValueHandling = NullValueHandling.Ignore)] public TextElement Text;
            [JsonProperty("Progress", NullValueHandling = NullValueHandling.Ignore)] public ProgressElement Progress;
            [JsonProperty("Settings", NullValueHandling = NullValueHandling.Ignore)]
            public Dictionary<string, string> Settings;

            // Not serialized: marks panels created by other plugins via the API.
            [JsonIgnore] public bool ThirdParty;

            public string Get(string key, string fallback)
            {
                string v;
                return Settings != null && Settings.TryGetValue(key, out v) && v != null ? v : fallback;
            }
        }

        private sealed class ImageElement
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Url")] public string Url = "";
            [JsonProperty("Color (R G B A)")] public string Color = "1 1 1 1";
            [JsonProperty("Width within panel (0-1)")] public float Width = 0.3f;
            [JsonProperty("Height within panel (0-1)")] public float Height = 0.8f;
        }

        private sealed class TextElement
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Font size")] public int FontSize = 13;
            [JsonProperty("Color (R G B A)")] public string Color = "1 1 1 1";
            [JsonProperty("Alignment (TextAnchor)")] public string Align = "MiddleCenter";
            [JsonProperty("Static content (text/3rd-party panels)")] public string Content = "";
            [JsonProperty("Offset from left within panel (0-1)")] public float Left;
        }

        // Optional horizontal progress bar. The fill width tracks Value (0..1); other plugins
        // drive it live via the SetPanelProgress API. The bar is centered vertically at Height.
        private sealed class ProgressElement
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Fill color (R G B A)")] public string Color = "0.2 0.8 0.2 1";
            [JsonProperty("Track color (R G B A)")] public string BackgroundColor = "0 0 0 0.5";
            [JsonProperty("Height within panel (0-1)")] public float Height = 0.5f;
            [JsonProperty("Initial value (0-1)")] public float Value;
            [JsonProperty("Fill from right edge")] public bool RightToLeft;
        }

        protected override void LoadDefaultConfig()
        {
            _config = BuildDefaultConfig();

            // First-load migration: if this server is coming from the classic InfoPanel
            // and has no MIP config yet, fold its settings into our fresh defaults so the
            // operator keeps their docks/panels. A malformed InfoPanel file never blocks startup.
            // A successful import logs its own console+file report (see LogImport);
            // only announce the plain-default case here.
            if (!TryImportInfoPanel(DefaultInfoPanelPath, _config, out _))
                PrintWarning("Created a new default configuration file.");

            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception("config deserialized to null");
                Normalize(_config);
            }
            catch (Exception ex)
            {
                LogProblem($"Invalid configuration ({ex.Message}); regenerating defaults. The old file is backed up by Oxide.");
                _config = BuildDefaultConfig();
            }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        // Fill in any missing sections so partial/old configs never NRE.
        private void Normalize(Configuration c)
        {
            if (c.General == null) c.General = new GeneralOptions();
            if (c.Docks == null || c.Docks.Count == 0) c.Docks = BuildDefaultConfig().Docks;
            if (c.Panels == null || c.Panels.Count == 0) c.Panels = BuildDefaultConfig().Panels;
            if (c.Messages == null) c.Messages = BuildDefaultConfig().Messages;
        }

        private Configuration BuildDefaultConfig()
        {
            var c = new Configuration
            {
                Messages = new List<string>
                {
                    "Welcome! Type /mipanel for options.",
                    "Be respectful — no harassment or cheating.",
                    "Need help? Ask a moderator in chat."
                },
                Docks = new Dictionary<string, DockConfig>
                {
                    ["TopLeftDock"] = new DockConfig { AnchorX = "Left", AnchorY = "Top", OffsetX = 0.006f, OffsetY = 0.018f, Width = 0.46f, Height = 0.03f },
                    ["TopRightDock"] = new DockConfig { AnchorX = "Right", AnchorY = "Top", OffsetX = 0.006f, OffsetY = 0.018f, Width = 0.30f, Height = 0.03f },
                    ["BottomLeftDock"] = new DockConfig { AnchorX = "Left", AnchorY = "Bottom", OffsetX = 0.006f, OffsetY = 0.175f, Width = 0.30f, Height = 0.03f },
                    ["BottomRightDock"] = new DockConfig { AnchorX = "Right", AnchorY = "Bottom", OffsetX = 0.006f, OffsetY = 0.175f, Width = 0.20f, Height = 0.03f }
                },
                Panels = new Dictionary<string, PanelConfig>
                {
                    [PClock] = new PanelConfig
                    {
                        Dock = "BottomLeftDock", Order = 1, Width = 0.30f, RefreshInterval = 1,
                        Text = new TextElement { FontSize = 14 },
                        Settings = new Dictionary<string, string> { ["Mode"] = "game", ["Format"] = "HH:mm" }
                    },
                    [PBalance] = new PanelConfig
                    {
                        Dock = "BottomLeftDock", Order = 2, Width = 0.35f, RefreshInterval = 5,
                        Image = new ImageElement { Url = "https://i.imgur.com/XgJg7ZC.png", Width = 0.22f },
                        Text = new TextElement { FontSize = 12, Left = 0.24f }
                    },
                    [PPoints] = new PanelConfig
                    {
                        Dock = "BottomLeftDock", Order = 3, Width = 0.35f, RefreshInterval = 5,
                        Image = new ImageElement { Url = "https://i.imgur.com/cdOsBa8.png", Width = 0.22f },
                        Text = new TextElement { FontSize = 12, Left = 0.24f }
                    },
                    [POnline] = new PanelConfig
                    {
                        Dock = "TopLeftDock", Order = 1, Width = 0.16f, RefreshInterval = 5,
                        Image = new ImageElement { Url = "https://i.imgur.com/ogL8T6p.png", Width = 0.34f },
                        Text = new TextElement { FontSize = 13, Left = 0.36f }
                    },
                    [PSleepers] = new PanelConfig
                    {
                        Dock = "TopLeftDock", Order = 2, Width = 0.12f, RefreshInterval = 5,
                        Image = new ImageElement { Url = "https://i.imgur.com/HZobcwc.png", Width = 0.4f },
                        Text = new TextElement { FontSize = 13, Left = 0.42f }
                    },
                    [PCoordinates] = new PanelConfig
                    {
                        Dock = "TopLeftDock", Order = 3, Width = 0.30f, RefreshInterval = 1,
                        Image = new ImageElement { Url = "https://i.imgur.com/nUlXLbO.png", Width = 0.14f },
                        Text = new TextElement { FontSize = 12, Left = 0.16f }
                    },
                    [PAirdrop] = EventPanel("TopLeftDock", 4, "https://i.imgur.com/WUQNWgj.png", "0 1 0 1"),
                    [PHeli] = EventPanel("TopLeftDock", 5, "https://i.imgur.com/PJBCJAv.png", "0.7 0.2 0.2 1"),
                    [PChinook] = EventPanel("TopLeftDock", 6, "https://i.imgur.com/6ES0vIG.png", "0.7 0.2 0.2 1"),
                    [PCargo] = EventPanel("TopLeftDock", 7, "https://i.imgur.com/twAPVF8.png", "0 1 0 1"),
                    [PBradley] = Disabled(EventPanel("TopLeftDock", 8, "https://i.imgur.com/61Gdczt.png", "0.7 0.2 0.2 1")),
                    [PRadiation] = Disabled(EventPanel("TopLeftDock", 9, "https://i.imgur.com/Z0ar6gu.png", "1 1 0 1")),
                    [PMessages] = new PanelConfig
                    {
                        Dock = "TopRightDock", Order = 1, Width = 1f, RefreshInterval = 8,
                        BackgroundColor = "0 0 0 0.45",
                        Text = new TextElement { FontSize = 13 }
                    },
                    [PCompass] = Disabled(new PanelConfig
                    {
                        Dock = "BottomRightDock", Order = 1, Width = 0.5f, RefreshInterval = 1,
                        Image = new ImageElement { Url = "https://i.imgur.com/7brpZTi.png", Width = 0.2f },
                        Text = new TextElement { FontSize = 12, Left = 0.22f }
                    }),
                    [PServerFps] = Disabled(new PanelConfig
                    {
                        Dock = "BottomRightDock", Order = 2, Width = 0.25f, RefreshInterval = 5,
                        Text = new TextElement { FontSize = 12 }
                    }),
                    [PPing] = Disabled(new PanelConfig
                    {
                        Dock = "BottomRightDock", Order = 3, Width = 0.25f, RefreshInterval = 5,
                        Text = new TextElement { FontSize = 12 }
                    }),
                    [PWipe] = Disabled(new PanelConfig
                    {
                        Dock = "TopRightDock", Order = 2, Width = 0.5f, RefreshInterval = 60,
                        Text = new TextElement { FontSize = 12 }
                    }),
                    // Per-player status glow: an icon that tints to reflect the viewer's own live
                    // state. Disabled by default; the placeholder Url should be swapped for a themed
                    // indicator icon. State->color and thresholds live in Settings.
                    [PStatus] = Disabled(new PanelConfig
                    {
                        Dock = "BottomRightDock", Order = 4, Width = 0.07f, RefreshInterval = 1,
                        BackgroundColor = "0 0 0 0.45",
                        Image = new ImageElement { Url = "https://i.imgur.com/nUlXLbO.png", Color = "1 1 1 0.15", Width = 0.85f, Height = 0.85f },
                        Settings = new Dictionary<string, string>
                        {
                            ["SafeColor"] = "0 1 0 1",
                            ["SafeHostileColor"] = "1 0 0 1",
                            ["RaidColor"] = "1 1 0 1",
                            ["AfkColor"] = "0.3 0.5 1 1",
                            ["BuildingPrivColor"] = "1 1 1 1",
                            ["InactiveColor"] = "1 1 1 0.15",
                            ["AfkSeconds"] = "300",
                            ["RaidWindowSeconds"] = "30",
                            ["Priority"] = "SafeHostile,Raid,SafeZone,AFK,BuildingPriv"
                        }
                    })
                }
            };
            return c;
        }

        private static PanelConfig EventPanel(string dock, int order, string url, string activeColor)
        {
            return new PanelConfig
            {
                Dock = dock, Order = order, Width = 0.07f, RefreshInterval = 0,
                BackgroundColor = "0 0 0 0.45",
                Image = new ImageElement { Url = url, Color = "1 1 1 0.15", Width = 0.85f, Height = 0.85f },
                Settings = new Dictionary<string, string> { ["ActiveColor"] = activeColor, ["InactiveColor"] = "1 1 1 0.15" }
            };
        }

        private static PanelConfig Disabled(PanelConfig p) { p.Enabled = false; return p; }

        #endregion

        #region InfoPanel import

        // Where the classic InfoPanel keeps its config. Resolves under Carbon's Oxide-compat
        // layer too. Used by the first-load auto-import and the `mipanel import` command.
        private string DefaultInfoPanelPath => Path.Combine(Interface.Oxide.ConfigDirectory, "InfoPanel.json");

        // True when `path` resolves to a file inside `configDir` (after canonicalising any
        // ".." segments) — used to confine `mipanel import` to the server's config folder.
        private static bool IsUnderConfigDir(string path, string configDir)
        {
            try
            {
                string root = Path.GetFullPath(configDir);
                if (root.Length == 0) return false;
                if (root[root.Length - 1] != Path.DirectorySeparatorChar) root += Path.DirectorySeparatorChar;
                return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // InfoPanel panel names (and common community variants) mapped onto MIP's built-in
        // panel ids. Case-insensitive; names not listed are tried as-is, then skipped.
        private static readonly Dictionary<string, string> InfoPanelAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Clock"] = PClock,
                ["Balance"] = PBalance, ["Economics"] = PBalance, ["Money"] = PBalance,
                ["Points"] = PPoints, ["ServerRewards"] = PPoints, ["RP"] = PPoints,
                ["OnlinePlayers"] = POnline, ["OnlineGamers"] = POnline, ["Online"] = POnline, ["Players"] = POnline,
                ["Sleepers"] = PSleepers,
                ["Coordinates"] = PCoordinates, ["Coord"] = PCoordinates, ["Location"] = PCoordinates, ["Position"] = PCoordinates,
                ["AirdropEvent"] = PAirdrop, ["AirplaneEvent"] = PAirdrop, ["Airdrop"] = PAirdrop, ["Airplane"] = PAirdrop,
                ["HelicopterEvent"] = PHeli, ["Helicopter"] = PHeli, ["Heli"] = PHeli, ["PatrolHelicopter"] = PHeli,
                ["ChinookEvent"] = PChinook, ["Chinook"] = PChinook, ["CH47"] = PChinook,
                ["CargoShipEvent"] = PCargo, ["CargoShip"] = PCargo, ["Cargo"] = PCargo,
                ["BradleyEvent"] = PBradley, ["Bradley"] = PBradley, ["APC"] = PBradley,
                ["RadiationEvent"] = PRadiation, ["Radiation"] = PRadiation, ["Rad"] = PRadiation,
                ["Compass"] = PCompass,
                ["Messages"] = PMessages, ["ServerInfo"] = PMessages, ["Welcome"] = PMessages, ["Announcement"] = PMessages, ["News"] = PMessages
            };

        // Built-in panel ids — third-party registrations may not reuse these (they would
        // shadow a built-in whose content the plugin still renders), so PanelRegister rejects them.
        private static readonly HashSet<string> BuiltInPanels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PClock, PMessages, PBalance, PPoints, PCoordinates, PCompass, POnline, PSleepers,
            PAirdrop, PHeli, PChinook, PCargo, PBradley, PRadiation,
            PServerFps, PPing, PWipe, PStatus
        };

        // Read an InfoPanel config from `path` and overlay everything we recognise onto `cfg`
        // (a merge — MIP defaults survive for anything absent). Unknown panels are logged and
        // skipped. Returns false (reason in `summary`) when the file is missing/malformed/empty.
        private bool TryImportInfoPanel(string path, Configuration cfg, out string summary)
        {
            summary = path;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

            JObject src;
            try { src = JObject.Parse(File.ReadAllText(path)); }
            catch (Exception ex)
            {
                // Keep the raw parser detail server-side only; the player-facing reply
                // gets the generic ImportFailed message (the path), never file contents.
                LogProblem($"InfoPanel import from \"{path}\" failed to parse: {ex.Message}");
                summary = path;
                return false;
            }

            int docks = 0, panels = 0;
            var skipped = new List<string>();

            // Docks — InfoPanel shares MIP's four dock names, so match by key.
            var srcDocks = src["Docks"] as JObject;
            if (srcDocks != null)
                foreach (var prop in srcDocks.Properties())
                {
                    DockConfig dock;
                    var obj = prop.Value as JObject;
                    if (obj == null || !cfg.Docks.TryGetValue(prop.Name, out dock)) continue;
                    ImportDock(obj, dock);
                    docks++;
                }

            // Panels — resolve each name through the alias table (falling back to the raw name),
            // overlay onto the matching MIP panel, or record it as skipped.
            var srcPanels = src["Panels"] as JObject;
            if (srcPanels != null)
                foreach (var prop in srcPanels.Properties())
                {
                    var obj = prop.Value as JObject;
                    if (obj == null) continue;

                    string mip;
                    if (!InfoPanelAliases.TryGetValue(prop.Name, out mip)) mip = prop.Name;

                    PanelConfig panel;
                    if (cfg.Panels.TryGetValue(mip, out panel)) { ImportPanel(obj, panel, cfg); panels++; }
                    else skipped.Add(prop.Name);
                }

            if (docks == 0 && panels == 0) return false;

            summary = $"{docks} dock(s), {panels} panel(s)";
            if (skipped.Count > 0) summary += $", {skipped.Count} skipped";

            // Durable record of what the import did — to the server console and a logfile
            // (oxide/logs/ModernInfoPanel/) — including the coverage caveat so operators
            // understand why some panels may not have come across.
            string report = $"InfoPanel import from \"{path}\": {docks} dock(s), {panels} panel(s) mapped, {skipped.Count} skipped.";
            if (skipped.Count > 0)
                report += $"\n  Skipped (no Modern Info Panel equivalent): {string.Join(", ", skipped.ToArray())}";
            report += "\n  Note: panels InfoPanel registers at runtime (its sub-plugins / oxide/data) are not stored in InfoPanel.json and cannot be imported; only docks and panels present in the file are migrated.";
            LogImport(report);

            return true;
        }

        // Mirror an import report to the server console and a dated logfile under
        // oxide/logs/ModernInfoPanel/ (Carbon writes to its own logs directory).
        private void LogImport(string text)
        {
            Puts(text);
            LogToFile("import", text, this);
        }

        // Emit an operator-facing error to the server console (PrintError) and a durable
        // logfile (oxide/logs/ModernInfoPanel/) — the single path for rejected/invalid
        // third-party input and config problems.
        private void LogProblem(string text)
        {
            PrintError(text);
            LogToFile("errors", text, this);
        }

        private static void ImportDock(JObject src, DockConfig dock)
        {
            dock.Enabled = Bool(src, "Available", dock.Enabled);
            dock.AnchorX = Str(src, "AnchorX", dock.AnchorX);
            dock.AnchorY = Str(src, "AnchorY", dock.AnchorY);
            dock.Width = Float(src, "Width", dock.Width);
            dock.Height = Float(src, "Height", dock.Height);
            dock.BackgroundColor = Str(src, "BackgroundColor", dock.BackgroundColor);

            // InfoPanel positions docks with a CUI margin "left top right bottom"; MIP uses a
            // single distance from each anchored edge. Best-effort: take the anchored sides.
            float[] m = ParseQuad(Str(src, "Margin", null));
            if (m != null)
            {
                dock.OffsetX = string.Equals(dock.AnchorX, "Right", StringComparison.OrdinalIgnoreCase) ? m[2] : m[0];
                dock.OffsetY = string.Equals(dock.AnchorY, "Top", StringComparison.OrdinalIgnoreCase) ? m[1] : m[3];
            }
        }

        private static void ImportPanel(JObject src, PanelConfig panel, Configuration cfg)
        {
            // InfoPanel gates a panel on both Available and Autoload.
            panel.Enabled = Bool(src, "Available", panel.Enabled) && Bool(src, "Autoload", true);

            string dock = Str(src, "Dock", panel.Dock);
            if (!string.IsNullOrEmpty(dock) && cfg.Docks.ContainsKey(dock)) panel.Dock = dock;

            panel.Order = (int)Float(src, "Order", panel.Order);
            panel.Width = Float(src, "Width", panel.Width);
            panel.BackgroundColor = Str(src, "BackgroundColor", panel.BackgroundColor);

            var img = src["Image"] as JObject;
            if (img != null && panel.Image != null)
            {
                panel.Image.Url = Str(img, "Url", panel.Image.Url);
                panel.Image.Color = Str(img, "Color", panel.Image.Color);
                panel.Image.Width = Float(img, "Width", panel.Image.Width);
            }

            var txt = src["Text"] as JObject;
            if (txt != null && panel.Text != null)
            {
                panel.Text.FontSize = (int)Float(txt, "FontSize", panel.Text.FontSize);
                panel.Text.Color = Str(txt, "FontColor", panel.Text.Color);
                panel.Text.Align = Str(txt, "Align", panel.Text.Align);
                panel.Text.Content = Str(txt, "Content", panel.Text.Content);
            }
        }

        // Tolerant JSON readers — never throw on a missing or wrong-typed key.
        private static string Str(JObject o, string key, string fallback)
        {
            JToken t;
            return o != null && o.TryGetValue(key, out t) && t != null && t.Type == JTokenType.String
                ? t.Value<string>() : fallback;
        }

        private static bool Bool(JObject o, string key, bool fallback)
        {
            JToken t;
            return o != null && o.TryGetValue(key, out t) && t != null && t.Type == JTokenType.Boolean
                ? t.Value<bool>() : fallback;
        }

        private static float Float(JObject o, string key, float fallback)
        {
            JToken t;
            if (o == null || !o.TryGetValue(key, out t)) return fallback;
            float f;
            return float.TryParse(t.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : fallback;
        }

        private static float[] ParseQuad(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string[] parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4) return null;
            var v = new float[4];
            for (int i = 0; i < 4; i++)
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return null;
            return v;
        }

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["HelpTitle"] = "<color=#e2a44a>Info Panel</color> commands:",
                ["HelpToggle"] = "<color=#e2a44a>/mipanel hide|show</color> — hide or show your panel.",
                ["HelpClockGame"] = "<color=#e2a44a>/mipanel clock game</color> — use in-game time.",
                ["HelpClockServer"] = "<color=#e2a44a>/mipanel clock server [offset]</color> — use server time (offset -23..23).",
                ["HelpTimeFormat"] = "<color=#e2a44a>/mipanel timeformat [index]</color> — change the clock format.",
                ["PanelShown"] = "Info panel is now <color=#9f9>shown</color>.",
                ["PanelHidden"] = "Info panel is now <color=#f99>hidden</color>.",
                ["ClockGameSet"] = "Clock set to in-game time.",
                ["ClockServerSet"] = "Clock set to server time.",
                ["ClockOffsetSet"] = "Clock offset set to {0}h.",
                ["TimeFormatList"] = "Available time formats:",
                ["TimeFormatEntry"] = "[{0}] {1}",
                ["TimeFormatSet"] = "Clock format updated.",
                ["TimeFormatUsage"] = "Usage: /mipanel timeformat <index>",
                ["InvalidArgs"] = "Invalid arguments. Type <color=#e2a44a>/mipanel</color> for help.",
                ["NoPermission"] = "You don't have permission to do that.",
                ["PlayerOnly"] = "Only the <color=#e2a44a>reload</color> subcommand can be used from the console; the rest are per-player. Run them in-game.",
                ["Reloaded"] = "Modern Info Panel reloaded.",
                ["HelpImport"] = "<color=#e2a44a>/mipanel import [path]</color> — import an existing InfoPanel config.",
                ["ImportOk"] = "Imported InfoPanel config — {0}.",
                ["ImportNone"] = "No InfoPanel config found at {0}.",
                ["ImportFailed"] = "InfoPanel import failed: {0}.",
                ["ImportOutside"] = "Import path must be inside the config directory.",
                ["WipeCountdown"] = "Wipe in {0}d {1}h",
                ["HelpTz"] = "<color=#e2a44a>/mipanel tz <zone|off></color> — set your clock timezone (e.g. America/Denver).",
                ["TzSet"] = "Clock timezone set to {0}.",
                ["TzUnknown"] = "Unknown timezone '{0}'. Use an IANA name like America/Denver.",
                ["HelpAdmin"] = "<color=#e2a44a>/mipanel admin</color> — open the admin editor (toggle/move/recolor panels).",
                ["AdminTitle"] = "Modern Info Panel — admin",
                ["AdminDocks"] = "Docks",
                ["AdminOn"] = "ON",
                ["AdminOff"] = "OFF",
                ["AdminReload"] = "Reload",
                ["AdminClose"] = "Close",
                ["AdminOpened"] = "Opened the admin editor.",
                ["PlayersLabel"] = "{0} / {1}",
                ["DirN"] = "North", ["DirNE"] = "Northeast", ["DirE"] = "East", ["DirSE"] = "Southeast",
                ["DirS"] = "South", ["DirSW"] = "Southwest", ["DirW"] = "West", ["DirNW"] = "Northwest"
            }, this);
        }

        private string L(string key, string id = null, params object[] args)
        {
            string msg = lang.GetMessage(key, this, id);
            return args != null && args.Length > 0 ? string.Format(msg, args) : msg;
        }

        #endregion

        #region Lifecycle hooks

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            // Commands are registered via the [ChatCommand]/[ConsoleCommand]
            // attributes on the handlers below (chat + console/RCON/server console).
        }

        private void OnServerInitialized()
        {
            LoadStoredData();
            ReconcileWipe();
            RebuildIndex();

            EventTypes.Clear();
            EventTypes[typeof(CargoPlane)] = PAirdrop;
            EventTypes[typeof(PatrolHelicopter)] = PHeli;
            EventTypes[typeof(CH47Helicopter)] = PChinook;
            EventTypes[typeof(CargoShip)] = PCargo;
            EventTypes[typeof(BradleyAPC)] = PBradley;

            foreach (string ev in new[] { PAirdrop, PHeli, PChinook, PCargo, PBradley, PRadiation })
            {
                _eventEntities[ev] = new HashSet<BaseEntity>();
                _eventColor[ev] = InactiveColor(ev);
            }

            ScanExistingEvents();
            _radiationOn = ConVar.Server.radiation;
            _eventColor[PRadiation] = _radiationOn ? ActiveColor(PRadiation) : InactiveColor(PRadiation);

            foreach (BasePlayer player in BasePlayer.activePlayerList)
                Register(player);

            _tickTimer = timer.Repeat(1f, 0, OnTick);
            _ready = true;
        }

        private void Unload()
        {
            _tickTimer?.Destroy();
            _tickTimer = null;
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                CuiHelper.DestroyUi(player, Root);
            FlushData();
            _views.Clear();
        }

        private void OnServerSave() => FlushData();

        private void OnPlayerConnected(BasePlayer player) => TryRegisterWhenReady(player, 0);

        // Defer registration until the client has finished receiving its snapshot, retrying
        // on an expanding backoff (2s, 5s, 10s, 15s). After the last attempt, register anyway
        // as a best-effort so a slow client still gets a panel rather than retrying forever.
        private void TryRegisterWhenReady(BasePlayer player, int attempt)
        {
            if (player == null || !player.IsConnected) return;
            if (player.HasPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot) && attempt < SnapshotRetryDelays.Length)
            {
                timer.Once(SnapshotRetryDelays[attempt], () => TryRegisterWhenReady(player, attempt + 1));
                return;
            }
            Register(player);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            _views.Remove(player.UserIDString);
            _lastCmd.Remove(player.UserIDString);
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            if (!_ready || entity == null) return;
            string ev = ResolveEventType(entity);
            if (ev == null || !PanelEnabled(ev)) return;
            var ent = entity as BaseEntity;
            if (ent != null) _eventEntities[ev].Add(ent);
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            var ent = entity as BaseEntity;
            if (ent == null) return;
            string ev = ResolveEventType(entity);
            if (ev != null && _eventEntities.ContainsKey(ev)) _eventEntities[ev].Remove(ent);
        }

        // Marks a structure's owner as "being raided" when their building takes explosive damage,
        // driving the per-player Status glow's raid color. Read-only: never alters the damage
        // (returns null), and does nothing unless the Status panel is enabled.
        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (!_ready || entity == null || info == null || !PanelEnabled(PStatus)) return null;
            if (!(entity is BuildingBlock || entity is Door || entity is BuildingPrivlidge)) return null;
            if (!info.damageTypes.Has(Rust.DamageType.Explosion)) return null;
            ulong owner = entity.OwnerID;
            if (owner != 0)
                _raidUntil[owner] = UnityEngine.Time.realtimeSinceStartup + RaidWindowSeconds();
            return null;
        }

        // Fires on a map wipe (a new world save) — NOT on a blueprint-only wipe. May fire
        // before our data is loaded; OnServerInitialized reconciles that case.
        private void OnNewSave(string filename)
        {
            if (_data == null) { _newSavePending = true; return; }
            RecordWipe(DateTime.UtcNow);
            Puts("Map wipe detected; wipe countdown anchored to now.");
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin == null) return;
            List<string> panels;
            if (!_pluginPanels.TryGetValue(plugin.Title, out panels)) return;

            foreach (string panel in panels)
            {
                foreach (PlayerView view in _views.Values)
                {
                    CuiHelper.DestroyUi(view.Player, NText(panel));
                    CuiHelper.DestroyUi(view.Player, NImage(panel));
                    CuiHelper.DestroyUi(view.Player, NPanel(panel));
                }
                _config.Panels.Remove(panel);
                _thirdParty.Remove(panel);
                _customText.Remove(panel);
                _customColor.Remove(panel);
                _customProgress.Remove(panel);
            }

            _pluginPanels.Remove(plugin.Title);
            RebuildIndex();
            RedrawAll();
        }

        #endregion

        #region Stored data

        private sealed class StoredData
        {
            public Dictionary<string, PlayerPrefs> Players = new Dictionary<string, PlayerPrefs>();
            public DateTime LastWipe;   // UTC time the map was last wiped (OnNewSave / save-file estimate)
            public string MapId;        // World seed:size, to detect a map change across restarts
        }

        private sealed class PlayerPrefs
        {
            public bool Hidden;
            public string ClockMode;   // "game" | "server"
            public int ClockOffset;
            public string ClockFormat;
            public string ClockTz;     // IANA timezone id (e.g. "America/Denver"); overrides mode/offset
        }

        private void LoadStoredData()
        {
            try { _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFile); }
            catch { _data = null; }
            if (_data == null) _data = new StoredData();
            if (_data.Players == null) _data.Players = new Dictionary<string, PlayerPrefs>();
        }

        private void SaveStoredData()
        {
            if (_data != null) Interface.Oxide.DataFileSystem.WriteObject(DataFile, _data);
        }

        // Persistence is debounced: state-changing commands mark the data dirty and a
        // periodic flush (plus OnServerSave / Unload) writes it at most once per minute,
        // so command spam can't drive a full-file disk write per keystroke.
        private void MarkDataDirty() => _dataDirty = true;

        private void FlushData()
        {
            if (!_dataDirty || _data == null) return;
            PrunePrefs();
            SaveStoredData();
            _dataDirty = false;
        }

        // Drop player entries that match the default behaviour, so the file can't grow
        // without bound from players who only ever toggled back to defaults.
        private void PrunePrefs()
        {
            bool defaultHidden = !_config.General.ShownByDefault;
            List<string> drop = null;
            foreach (var kv in _data.Players)
            {
                PlayerPrefs p = kv.Value;
                if (p != null && p.ClockMode == null && p.ClockFormat == null && p.ClockOffset == 0 && p.Hidden == defaultHidden)
                    (drop ?? (drop = new List<string>())).Add(kv.Key);
            }
            if (drop != null) foreach (string k in drop) _data.Players.Remove(k);
        }

        private PlayerPrefs Prefs(string id)
        {
            PlayerPrefs p;
            if (!_data.Players.TryGetValue(id, out p)) _data.Players[id] = p = new PlayerPrefs();
            return p;
        }

        // Read-only lookup: never creates an entry (used on the hot path).
        private PlayerPrefs PrefsRead(string id)
        {
            PlayerPrefs p;
            return _data.Players.TryGetValue(id, out p) ? p : null;
        }

        private bool IsHidden(string id)
        {
            PlayerPrefs p;
            if (_data.Players.TryGetValue(id, out p)) return p.Hidden;
            return !_config.General.ShownByDefault;
        }

        #endregion

        #region Index & player registration

        private void RebuildIndex()
        {
            _dockIndex.Clear();
            foreach (var pair in _config.Panels)
            {
                PanelConfig cfg = pair.Value;
                if (cfg == null || string.IsNullOrEmpty(cfg.Dock)) continue;

                if (!string.IsNullOrEmpty(cfg.Permission))
                {
                    string perm = PermPrefix + cfg.Permission;
                    if (_registeredPerms.Add(perm)) permission.RegisterPermission(perm, this);
                }

                List<PanelEntry> list;
                if (!_dockIndex.TryGetValue(cfg.Dock, out list))
                    _dockIndex[cfg.Dock] = list = new List<PanelEntry>();
                list.Add(new PanelEntry { Name = pair.Key, Cfg = cfg });
            }
            foreach (var list in _dockIndex.Values)
                list.Sort((a, b) => a.Cfg.Order.CompareTo(b.Cfg.Order));
        }

        private void Register(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;

            ulong id;
            if (!ulong.TryParse(player.UserIDString, out id)) id = 0;

            PlayerView view;
            if (!_views.TryGetValue(player.UserIDString, out view))
                _views[player.UserIDString] = view = new PlayerView { IdString = player.UserIDString, Id = id };
            view.Player = player;

            if (IsHidden(player.UserIDString)) { view.Visible = false; return; }
            Draw(view);
        }

        private bool Ready(PlayerView v) => v != null && v.Player != null && v.Player.IsConnected;

        #endregion

        #region Rendering

        private static string NDock(string s) => "MIP_dock_" + s;
        private static string NPanel(string s) => "MIP_panel_" + s;
        private static string NText(string s) => "MIP_text_" + s;
        private static string NImage(string s) => "MIP_image_" + s;
        private static string NButton(string s) => "MIP_btn_" + s;
        private static string NProgress(string s) => "MIP_prog_" + s;
        private static string NProgressFill(string s) => "MIP_progf_" + s;

        private void RedrawAll()
        {
            foreach (PlayerView view in _views.Values)
                if (Ready(view) && view.Visible) Draw(view);
        }

        private void Draw(PlayerView view)
        {
            BasePlayer player = view.Player;
            if (!Ready(view)) return;
            float fade = Mathf.Max(0f, _config.General.FadeIn);

            CuiHelper.DestroyUi(player, Root);
            view.Text.Clear();
            view.Color.Clear();

            var c = new CuiElementContainer();
            // Root is a graphic-less, full-screen container: with no Image component it
            // is NOT a raycast target, so the HUD never swallows mouse input the game
            // needs (notably the map's scroll-to-zoom and drag). Docks parent to it by name.
            c.Add(new CuiElement
            {
                Name = Root,
                Parent = "Hud",
                Components =
                {
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                }
            });

            foreach (var dockPair in _config.Docks)
            {
                DockConfig dock = dockPair.Value;
                if (dock == null || !dock.Enabled) continue;

                List<PanelEntry> entries;
                if (!_dockIndex.TryGetValue(dockPair.Key, out entries)) continue;

                // Filter to what this player may see, keep order.
                var visible = new List<PanelEntry>(entries.Count);
                foreach (PanelEntry e in entries)
                    if (e.Cfg.Enabled && CanSee(view, e.Cfg)) visible.Add(e);
                if (visible.Count == 0) continue;

                Vector4 dr = DockRect(dock);
                c.Add(new CuiPanel
                {
                    Image = { Color = SafeColor(dock.BackgroundColor, "0 0 0 0"), FadeIn = fade },
                    RectTransform = { AnchorMin = Pos(dr.x, dr.y), AnchorMax = Pos(dr.z, dr.w) }
                }, Root, NDock(dockPair.Key));

                float left = 0f, right = 1f;
                foreach (PanelEntry e in visible)
                {
                    float w = Mathf.Clamp(e.Cfg.Width, 0.001f, 1f);
                    float x0, x1;
                    if (string.Equals(e.Cfg.Anchor, "Right", StringComparison.OrdinalIgnoreCase))
                    { x1 = right; x0 = right - w; right = x0; }
                    else
                    { x0 = left; x1 = left + w; left = x1; }

                    // Panels whose widths exceed the dock would otherwise overlap or
                    // produce out-of-bounds anchors — clamp to the dock and drop any
                    // panel with no room left.
                    x0 = Mathf.Clamp01(x0);
                    x1 = Mathf.Clamp01(x1);
                    if (x1 - x0 < 0.001f) continue;

                    string panelName = NPanel(e.Name);
                    c.Add(new CuiPanel
                    {
                        Image = { Color = SafeColor(e.Cfg.BackgroundColor, "0 0 0 0.45"), FadeIn = fade },
                        RectTransform = { AnchorMin = Pos(x0, 0f), AnchorMax = Pos(x1, 1f) }
                    }, NDock(dockPair.Key), panelName);

                    AddContents(c, view, e, panelName);
                }
            }

            CuiHelper.AddUi(player, c.ToJson());
            view.Visible = true;
        }

        private void AddContents(CuiElementContainer c, PlayerView view, PanelEntry e, string parent)
        {
            PanelConfig cfg = e.Cfg;
            float fade = Mathf.Max(0f, _config.General.FadeIn);

            if (cfg.Image != null && cfg.Image.Enabled && !string.IsNullOrEmpty(cfg.Image.Url))
            {
                string color = ImageColorFor(view, e.Name);
                view.Color[e.Name] = color;
                float iw = Mathf.Clamp(cfg.Image.Width, 0.01f, 1f);
                float ih = Mathf.Clamp(cfg.Image.Height, 0.01f, 1f);
                float y0 = (1f - ih) * 0.5f;
                c.Add(new CuiElement
                {
                    Name = NImage(e.Name),
                    Parent = parent,
                    Components =
                    {
                        new CuiRawImageComponent { Url = cfg.Image.Url, Color = color, FadeIn = fade },
                        new CuiRectTransformComponent { AnchorMin = Pos(0.02f, y0), AnchorMax = Pos(iw, y0 + ih) }
                    }
                });
            }

            if (cfg.Progress != null && cfg.Progress.Enabled)
            {
                float value = ProgressValue(view, e.Name, cfg.Progress);
                view.Progress[e.Name] = value;
                float ph = Mathf.Clamp(cfg.Progress.Height, 0.01f, 1f);
                float py0 = (1f - ph) * 0.5f;
                string track = NProgress(e.Name);
                c.Add(new CuiPanel
                {
                    Image = { Color = SafeColor(cfg.Progress.BackgroundColor, "0 0 0 0.5"), FadeIn = fade },
                    RectTransform = { AnchorMin = Pos(0f, py0), AnchorMax = Pos(1f, py0 + ph) }
                }, parent, track);
                AddProgressFill(c, e.Name, cfg.Progress, value, fade);
            }

            if (cfg.Text != null && cfg.Text.Enabled)
            {
                string text = TextValue(view, e);
                view.Text[e.Name] = text;
                float tl = Mathf.Clamp(cfg.Text.Left, 0f, 0.95f);
                c.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = text,
                        FontSize = Mathf.Clamp(cfg.Text.FontSize, 6, 48),
                        Align = ParseAnchor(cfg.Text.Align),
                        Color = SafeColor(cfg.Text.Color, "1 1 1 1"),
                        FadeIn = fade
                    },
                    RectTransform = { AnchorMin = Pos(tl, 0f), AnchorMax = "1 1" }
                }, parent, NText(e.Name));
            }

            // Optional: make the whole panel a click target that runs a console command
            // (run as the clicking player). Placeholders are resolved in the command too.
            if (!string.IsNullOrEmpty(cfg.Command))
            {
                c.Add(new CuiButton
                {
                    Button = { Command = ResolvePlaceholders(cfg.Command, view), Color = "0 0 0 0" },
                    Text = { Text = "" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                }, parent, NButton(e.Name));
            }
        }

        // Lightweight in-place label update (no background/image flicker).
        private void PushText(PlayerView view, string panel, string value)
        {
            string prev;
            if (view.Text.TryGetValue(panel, out prev) && prev == value) return;
            view.Text[panel] = value;
            if (!view.Visible || !Ready(view) || !IsVisibleTo(view, panel)) return;

            PanelConfig cfg;
            if (!_config.Panels.TryGetValue(panel, out cfg)) return;
            CuiHelper.DestroyUi(view.Player, NText(panel));
            var c = new CuiElementContainer();
            float tl = Mathf.Clamp(cfg.Text?.Left ?? 0f, 0f, 0.95f);
            c.Add(new CuiLabel
            {
                Text =
                {
                    Text = value,
                    FontSize = Mathf.Clamp(cfg.Text?.FontSize ?? 13, 6, 48),
                    Align = ParseAnchor(cfg.Text?.Align),
                    Color = SafeColor(cfg.Text?.Color, "1 1 1 1")
                },
                RectTransform = { AnchorMin = Pos(tl, 0f), AnchorMax = "1 1" }
            }, NPanel(panel), NText(panel));
            CuiHelper.AddUi(view.Player, c.ToJson());
        }

        private void PushColor(PlayerView view, string panel, string color)
        {
            string prev;
            if (view.Color.TryGetValue(panel, out prev) && prev == color) return;
            view.Color[panel] = color;
            if (!view.Visible || !Ready(view) || !IsVisibleTo(view, panel)) return;

            PanelConfig cfg;
            if (!_config.Panels.TryGetValue(panel, out cfg) || cfg.Image == null) return;
            float iw = Mathf.Clamp(cfg.Image.Width, 0.01f, 1f);
            float ih = Mathf.Clamp(cfg.Image.Height, 0.01f, 1f);
            float y0 = (1f - ih) * 0.5f;
            CuiHelper.DestroyUi(view.Player, NImage(panel));
            var c = new CuiElementContainer();
            c.Add(new CuiElement
            {
                Name = NImage(panel),
                Parent = NPanel(panel),
                Components =
                {
                    new CuiRawImageComponent { Url = cfg.Image.Url, Color = color },
                    new CuiRectTransformComponent { AnchorMin = Pos(0.02f, y0), AnchorMax = Pos(iw, y0 + ih) }
                }
            });
            CuiHelper.AddUi(view.Player, c.ToJson());
        }

        // Current fill (0..1) for a progress panel: a live API override if set, else its configured value.
        private float ProgressValue(PlayerView view, string panel, ProgressElement prog)
        {
            float v;
            if (_customProgress.TryGetValue(panel, out v)) return Mathf.Clamp01(v);
            return Mathf.Clamp01(prog.Value);
        }

        // Adds the fill rectangle inside an already-present track panel; width tracks `value`.
        private void AddProgressFill(CuiElementContainer c, string panel, ProgressElement prog, float value, float fade)
        {
            float f = Mathf.Clamp01(value);
            string min, max;
            if (prog.RightToLeft) { min = Pos(1f - f, 0f); max = Pos(1f, 1f); }
            else { min = Pos(0f, 0f); max = Pos(f, 1f); }
            c.Add(new CuiPanel
            {
                Image = { Color = SafeColor(prog.Color, "0.2 0.8 0.2 1"), FadeIn = fade },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, NProgress(panel), NProgressFill(panel));
        }

        // Lightweight in-place fill update (no track/text flicker), mirroring PushText/PushColor.
        private void PushProgress(PlayerView view, string panel, float value)
        {
            value = Mathf.Clamp01(value);
            float prev;
            if (view.Progress.TryGetValue(panel, out prev) && Mathf.Approximately(prev, value)) return;
            view.Progress[panel] = value;
            if (!view.Visible || !Ready(view) || !IsVisibleTo(view, panel)) return;

            PanelConfig cfg;
            if (!_config.Panels.TryGetValue(panel, out cfg) || cfg.Progress == null || !cfg.Progress.Enabled) return;
            CuiHelper.DestroyUi(view.Player, NProgressFill(panel));
            var c = new CuiElementContainer();
            AddProgressFill(c, panel, cfg.Progress, value, 0f);
            CuiHelper.AddUi(view.Player, c.ToJson());
        }

        private Vector4 DockRect(DockConfig d)
        {
            float w = Mathf.Clamp(d.Width, 0.01f, 1f);
            float h = Mathf.Clamp(d.Height, 0.005f, 1f);
            float ox = Mathf.Clamp(d.OffsetX, 0f, 1f);
            float oy = Mathf.Clamp(d.OffsetY, 0f, 1f);
            float x0, x1, y0, y1;
            if (string.Equals(d.AnchorX, "Right", StringComparison.OrdinalIgnoreCase)) { x1 = 1f - ox; x0 = x1 - w; }
            else { x0 = ox; x1 = ox + w; }
            if (string.Equals(d.AnchorY, "Top", StringComparison.OrdinalIgnoreCase)) { y1 = 1f - oy; y0 = y1 - h; }
            else { y0 = oy; y1 = oy + h; }
            return new Vector4(x0, y0, x1, y1);
        }

        private bool CanSee(PlayerView view, PanelConfig cfg)
            => string.IsNullOrEmpty(cfg.Permission) || permission.UserHasPermission(view.IdString, PermPrefix + cfg.Permission);

        private bool IsVisibleTo(PlayerView view, string panel)
        {
            PanelConfig cfg;
            if (!_config.Panels.TryGetValue(panel, out cfg) || !cfg.Enabled) return false;
            DockConfig dock;
            if (!_config.Docks.TryGetValue(cfg.Dock, out dock) || !dock.Enabled) return false;
            return CanSee(view, cfg);
        }

        private bool PanelEnabled(string panel)
        {
            PanelConfig cfg;
            return _config.Panels.TryGetValue(panel, out cfg) && cfg != null && cfg.Enabled;
        }

        #endregion

        #region Tick & value providers

        private void OnTick()
        {
            if (!_ready) return;
            _tick++;

            UpdateEvents();
            UpdateRadiation();

            bool dueOnline = Due(POnline);
            bool dueSleepers = Due(PSleepers);
            bool dueMessages = Due(PMessages);

            string onlineText = dueOnline ? OnlineText() : null;
            string sleepersText = dueSleepers ? BasePlayer.sleepingPlayerList.Count.ToString(Inv) : null;

            if (dueMessages && _config.Messages.Count > 0) AdvanceMessage();
            string messageText = dueMessages ? CurrentMessage() : null;

            bool dueClock = Due(PClock);
            bool dueCoord = Due(PCoordinates);
            bool dueCompass = Due(PCompass);
            bool dueBalance = Due(PBalance);
            bool duePoints = Due(PPoints);
            bool dueFps = Due(PServerFps);
            bool duePing = Due(PPing);
            bool dueWipe = Due(PWipe);

            string fpsText = dueFps ? ServerFpsText() : null;
            string wipeText = dueWipe ? WipeText() : null;
            bool statusOn = PanelEnabled(PStatus);

            foreach (PlayerView view in _views.Values)
            {
                if (!Ready(view) || !view.Visible) continue;

                if (statusOn)
                {
                    UpdateIdle(view);
                    PushColor(view, PStatus, StatusColor(view));
                }

                if (onlineText != null) PushText(view, POnline, onlineText);
                if (sleepersText != null) PushText(view, PSleepers, sleepersText);
                if (messageText != null) PushText(view, PMessages, ResolvePlaceholders(messageText, view));

                if (dueClock) PushText(view, PClock, ClockText(view));
                if (dueCoord) PushText(view, PCoordinates, CoordText(view));
                if (dueCompass) PushText(view, PCompass, CompassText(view));
                if (dueBalance) PushText(view, PBalance, BalanceText(view));
                if (duePoints) PushText(view, PPoints, PointsText(view));
                if (fpsText != null) PushText(view, PServerFps, fpsText);
                if (duePing) PushText(view, PPing, PingText(view));
                if (wipeText != null) PushText(view, PWipe, wipeText);
            }

            // Debounced persistence: flush at most ~once a minute when dirty.
            if (_dataDirty && _tick % 60 == 0) FlushData();

            // Drop expired raid markers so the map can't grow without bound over a wipe.
            if (statusOn && _raidUntil.Count > 0 && _tick % 60 == 0) PruneRaidMarkers();
        }

        private void PruneRaidMarkers()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            List<ulong> drop = null;
            foreach (var kv in _raidUntil)
                if (kv.Value <= now) (drop ?? (drop = new List<ulong>())).Add(kv.Key);
            if (drop != null) foreach (ulong k in drop) _raidUntil.Remove(k);
        }

        private bool Due(string panel)
        {
            PanelConfig cfg;
            if (!_config.Panels.TryGetValue(panel, out cfg) || cfg == null || !cfg.Enabled) return false;
            int interval = cfg.RefreshInterval;
            return interval > 0 && _tick % interval == 0;
        }

        // Counts consecutive ticks the player hasn't moved or turned, for the AFK status state.
        private static void UpdateIdle(PlayerView view)
        {
            Vector3 pos = view.Player.transform.position;
            float yaw = view.Player.viewAngles.y;
            if ((pos - view.LastPos).sqrMagnitude < 0.04f && Mathf.Abs(yaw - view.LastYaw) < 1f)
            {
                view.IdleTicks++;
            }
            else
            {
                view.IdleTicks = 0;
                view.LastPos = pos;
                view.LastYaw = yaw;
            }
        }

        private string TextValue(PlayerView view, PanelEntry e)
        {
            switch (e.Name)
            {
                case PClock: return ClockText(view);
                case PMessages: return ResolvePlaceholders(CurrentMessage(), view);
                case PBalance: return BalanceText(view);
                case PPoints: return PointsText(view);
                case PCoordinates: return CoordText(view);
                case PCompass: return CompassText(view);
                case POnline: return OnlineText();
                case PSleepers: return BasePlayer.sleepingPlayerList.Count.ToString(Inv);
                case PServerFps: return ServerFpsText();
                case PPing: return PingText(view);
                case PWipe: return WipeText();
                default:
                    string custom;
                    string raw = _customText.TryGetValue(e.Name, out custom) && custom != null
                        ? custom : (e.Cfg.Text?.Content ?? string.Empty);
                    return ResolvePlaceholders(raw, view);
            }
        }

        private string OnlineText() => L("PlayersLabel", null, BasePlayer.activePlayerList.Count, ConVar.Server.maxplayers);

        private string ServerFpsText() => ((int)Performance.report.frameRate).ToString(Inv) + " FPS";

        private string PingText(PlayerView view)
        {
            try { return Network.Net.sv.GetAveragePing(view.Player.Connection).ToString(Inv) + " ms"; }
            catch { return "0 ms"; }
        }

        // --- Placeholders ---------------------------------------------------------
        // Resolve {tokens} in `template` for one viewer. Tokens are viewer-scoped ({name} is the
        // viewer's own name, shown only to themselves), so there's no cross-player text injection.
        private string ResolvePlaceholders(string template, PlayerView view)
        {
            if (string.IsNullOrEmpty(template) || template.IndexOf('{') < 0) return template;
            string s = template;
            s = Replace(s, "{name}", view.Player?.displayName);
            s = Replace(s, "{online}", BasePlayer.activePlayerList.Count.ToString(Inv));
            s = Replace(s, "{max}", ConVar.Server.maxplayers.ToString(Inv));
            s = Replace(s, "{sleepers}", BasePlayer.sleepingPlayerList.Count.ToString(Inv));
            s = Replace(s, "{server}", ConVar.Server.hostname);
            s = Replace(s, "{wipe}", WipeText());
            s = Replace(s, "{lastwipe}", _data != null && _data.LastWipe != default(DateTime) ? _data.LastWipe.ToString("yyyy-MM-dd", Inv) : "");
            if (view.Player != null)
            {
                s = Replace(s, "{time}", ClockText(view));
                s = Replace(s, "{balance}", BalanceText(view));
                s = Replace(s, "{points}", PointsText(view));
                if (s.IndexOf("{grid}", StringComparison.OrdinalIgnoreCase) >= 0
                    || s.IndexOf("{coords}", StringComparison.OrdinalIgnoreCase) >= 0
                    || s.IndexOf("{x}", StringComparison.OrdinalIgnoreCase) >= 0
                    || s.IndexOf("{z}", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Vector3 pos = view.Player.transform.position;
                    s = Replace(s, "{grid}", Grid(pos));
                    s = Replace(s, "{coords}", "X: " + Mathf.RoundToInt(pos.x).ToString(Inv) + " Z: " + Mathf.RoundToInt(pos.z).ToString(Inv));
                    s = Replace(s, "{x}", Mathf.RoundToInt(pos.x).ToString(Inv));
                    s = Replace(s, "{z}", Mathf.RoundToInt(pos.z).ToString(Inv));
                }
            }

            // Optional PlaceholderAPI passthrough for any remaining {tokens} (best-effort).
            if (_config.General.UsePlaceholderApi && s.IndexOf('{') >= 0 && view.Player != null)
            {
                Plugin papi = plugins.Find("PlaceholderAPI");
                if (papi != null)
                {
                    try { object r = papi.Call("ProcessPlaceholders", s, view.Player); if (r is string rs) s = rs; }
                    catch { }
                }
            }
            return ClampText(s);
        }

        private static string Replace(string s, string token, string value)
        {
            int idx;
            while ((idx = s.IndexOf(token, StringComparison.OrdinalIgnoreCase)) >= 0)
                s = s.Substring(0, idx) + (value ?? string.Empty) + s.Substring(idx + token.Length);
            return s;
        }

        // --- Wipe countdown -------------------------------------------------------
        private static string CurrentMapId()
        {
            try { return World.Seed + ":" + World.Size; } catch { return string.Empty; }
        }

        private static DateTime? SaveFileTimeUtc()
        {
            try
            {
                string f = World.SaveFileName;
                if (!string.IsNullOrEmpty(f) && File.Exists(f)) return File.GetCreationTimeUtc(f);
            }
            catch { }
            return null;
        }

        private void RecordWipe(DateTime whenUtc)
        {
            if (_data == null) return;
            _data.LastWipe = whenUtc;
            _data.MapId = CurrentMapId();
            _wipeAt = default(DateTime);
            MarkDataDirty();
        }

        // Establish/repair the last-wipe anchor at startup: honor a pending OnNewSave, else
        // detect a map change vs. the stored identity, estimating from the save file (or the
        // most recent scheduled day/time, or now).
        private void ReconcileWipe()
        {
            if (_data == null) return;
            if (_newSavePending) { _newSavePending = false; RecordWipe(DateTime.UtcNow); return; }
            string id = CurrentMapId();
            if (_data.LastWipe == default(DateTime) || _data.MapId != id)
                RecordWipe(SaveFileTimeUtc() ?? MostRecentScheduledUtc() ?? DateTime.UtcNow);
        }

        // weekly/biweekly/monthly from explicit config, else auto-detected from server.tags; null = unresolved.
        private string ResolveCadence()
        {
            string iv = (_config.Wipe.Interval ?? "auto").Trim().ToLowerInvariant();
            if (iv == "weekly" || iv == "biweekly" || iv == "monthly" || iv == "custom") return iv;
            if (iv == "auto")
            {
                string tags = (ConVar.Server.tags ?? string.Empty).ToLowerInvariant();
                if (tags.Contains("biweekly")) return "biweekly";
                if (tags.Contains("monthly")) return "monthly";
                if (tags.Contains("weekly")) return "weekly";
            }
            return null;
        }

        private DateTime? MostRecentScheduledUtc()
        {
            DayOfWeek dow;
            if (!Enum.TryParse(_config.Wipe.DayOfWeek, true, out dow)) return null;
            TimeSpan tod;
            if (!TimeSpan.TryParse(_config.Wipe.Time, Inv, out tod)) tod = new TimeSpan(19, 0, 0);
            DateTime now = DateTime.UtcNow;
            DateTime candidate = now.Date + tod;
            for (int i = 0; i < 8; i++)
            {
                if (candidate.DayOfWeek == dow && candidate <= now) return candidate;
                candidate = candidate.AddDays(-1);
            }
            return null;
        }

        // Next wipe (UTC), anchored on the real last map wipe so weekday/time carry over.
        private DateTime NextWipe()
        {
            if (_wipeAt != default(DateTime) && _wipeAt > DateTime.UtcNow) return _wipeAt;
            string cadence = ResolveCadence();
            if (cadence == null) { WarnWipeUnset(); return default(DateTime); }

            DateTime now = DateTime.UtcNow;
            DateTime next = (_data != null && _data.LastWipe != default(DateTime)) ? _data.LastWipe : now;
            if (cadence == "monthly")
                do { next = next.AddMonths(1); } while (next <= now);
            else
            {
                int step = cadence == "biweekly" ? 14 : cadence == "custom" ? Math.Max(1, _config.Wipe.CustomDays) : 7;
                do { next = next.AddDays(step); } while (next <= now);
            }
            _wipeAt = next;
            return next;
        }

        private string WipeText()
        {
            DateTime next = NextWipe();
            if (next == default(DateTime)) return string.Empty;
            TimeSpan left = next - DateTime.UtcNow;
            if (left.Ticks < 0) left = TimeSpan.Zero;
            return L("WipeCountdown", null, left.Days, left.Hours);
        }

        private void WarnWipeUnset()
        {
            if (_wipeWarned) return;
            _wipeWarned = true;
            PrintWarning("Wipe schedule not set: add weekly|biweekly|monthly to server.tags, or set 'Wipe schedule' -> Interval in the config. The WipeCountdown panel stays blank until then.");
        }

        private string ClockText(PlayerView view)
        {
            PanelConfig cfg;
            _config.Panels.TryGetValue(PClock, out cfg);
            PlayerPrefs p = PrefsRead(view.IdString);
            string mode = p?.ClockMode ?? cfg?.Get("Mode", "game") ?? "game";
            string fmt = p?.ClockFormat ?? cfg?.Get("Format", "HH:mm") ?? "HH:mm";

            DateTime dt;
            if (!string.IsNullOrEmpty(p?.ClockTz))
            {
                // Per-player IANA timezone (DST-correct). Falls back to local time if the host's
                // zone database doesn't know the id.
                try { dt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(p.ClockTz)); }
                catch { dt = DateTime.Now; }
            }
            else if (string.Equals(mode, "server", StringComparison.OrdinalIgnoreCase))
                dt = DateTime.Now.AddHours(p?.ClockOffset ?? 0);
            else if (TOD_Sky.Instance != null)
                dt = TOD_Sky.Instance.Cycle.DateTime;
            else
                dt = DateTime.Now;

            try { return dt.ToString(fmt, Inv); }
            catch { return dt.ToString("HH:mm", Inv); }
        }

        private string CoordText(PlayerView view)
        {
            Vector3 pos = view.Player.transform.position;
            int type = _config.General.CoordinateType;
            string xz = "X: " + Mathf.RoundToInt(pos.x).ToString(Inv) + " Z: " + Mathf.RoundToInt(pos.z).ToString(Inv);
            switch (type)
            {
                case 0: return xz;
                case 1: return Grid(pos);
                default: return xz + " | " + Grid(pos);
            }
        }

        private string CompassText(PlayerView view)
        {
            float yaw = view.Player.viewAngles.y;
            yaw -= 360f * Mathf.Floor(yaw / 360f); // normalize 0..360
            if (!_config.General.CompassAsText)
                return Mathf.RoundToInt(yaw).ToString(Inv) + "°";

            string key;
            if (yaw >= 337.5f || yaw < 22.5f) key = "DirN";
            else if (yaw < 67.5f) key = "DirNE";
            else if (yaw < 112.5f) key = "DirE";
            else if (yaw < 157.5f) key = "DirSE";
            else if (yaw < 202.5f) key = "DirS";
            else if (yaw < 247.5f) key = "DirSW";
            else if (yaw < 292.5f) key = "DirW";
            else key = "DirNW";
            return L(key, view.IdString);
        }

        private string BalanceText(PlayerView view)
        {
            if (Economics == null) return "0";
            object result = Economics.Call("Balance", view.Id);
            return ToDouble(result).ToString("N0", Inv);
        }

        private string PointsText(PlayerView view)
        {
            if (ServerRewards == null) return "0";
            object result = ServerRewards.Call("CheckPoints", view.Id);
            return ToInt(result).ToString(Inv);
        }

        private string CurrentMessage()
        {
            if (_config.Messages.Count == 0) return string.Empty;
            int i = _messageIndex % _config.Messages.Count;
            return _config.Messages[i] ?? string.Empty;
        }

        private void AdvanceMessage()
        {
            int count = _config.Messages.Count;
            if (count <= 1) { _messageIndex = 0; return; }
            if (string.Equals(_config.General.MessageOrder, "random", StringComparison.OrdinalIgnoreCase))
            {
                int next = UnityEngine.Random.Range(0, count);
                if (next == _messageIndex) next = (next + 1) % count;
                _messageIndex = next;
            }
            else
            {
                _messageIndex = (_messageIndex + 1) % count;
            }
        }

        #endregion

        #region Events & radiation

        private void ScanExistingEvents()
        {
            ScanType<CargoPlane>(PAirdrop);
            ScanType<PatrolHelicopter>(PHeli);
            ScanType<CH47Helicopter>(PChinook);
            ScanType<CargoShip>(PCargo);
            ScanType<BradleyAPC>(PBradley);
        }

        private void ScanType<T>(string ev) where T : BaseEntity
        {
            if (!PanelEnabled(ev)) return;
            foreach (T e in UnityEngine.Object.FindObjectsByType<T>(UnityEngine.FindObjectsSortMode.None))
                if (e != null && !e.IsDestroyed) _eventEntities[ev].Add(e);
        }

        // Map a spawned/killed entity to its event panel. Exact type is the vanilla fast
        // path; the `is` checks are a second layer so modded subclasses of the vanilla
        // event entities still drive their indicator without affecting vanilla behaviour.
        private static string ResolveEventType(BaseNetworkable entity)
        {
            string ev;
            if (EventTypes.TryGetValue(entity.GetType(), out ev)) return ev;
            if (entity is CargoPlane) return PAirdrop;
            if (entity is PatrolHelicopter) return PHeli;
            if (entity is CH47Helicopter) return PChinook;
            if (entity is CargoShip) return PCargo;
            if (entity is BradleyAPC) return PBradley;
            return null;
        }

        private void UpdateEvents()
        {
            foreach (var pair in _eventEntities)
            {
                string ev = pair.Key;
                if (ev == PRadiation) continue;

                pair.Value.RemoveWhere(e => e == null || e.IsDestroyed || !e.gameObject.activeInHierarchy);
                bool active = pair.Value.Count > 0;
                bool was = _activeEvents.Contains(ev);
                if (active == was) continue;

                if (active) _activeEvents.Add(ev); else _activeEvents.Remove(ev);
                string color = active ? ActiveColor(ev) : InactiveColor(ev);
                _eventColor[ev] = color;
                BroadcastColor(ev, color);
            }
        }

        private void UpdateRadiation()
        {
            bool on = ConVar.Server.radiation;
            if (on == _radiationOn) return;
            _radiationOn = on;
            string color = on ? ActiveColor(PRadiation) : InactiveColor(PRadiation);
            _eventColor[PRadiation] = color;
            BroadcastColor(PRadiation, color);
        }

        private void BroadcastColor(string ev, string color)
        {
            foreach (PlayerView view in _views.Values)
                if (Ready(view) && view.Visible) PushColor(view, ev, color);
        }

        private string ImageColorFor(PlayerView view, string panel)
        {
            string color;
            if (panel == PStatus) return StatusColor(view);
            if (_eventColor.TryGetValue(panel, out color)) return color;
            if (_customColor.TryGetValue(panel, out color)) return color;
            PanelConfig cfg;
            if (_config.Panels.TryGetValue(panel, out cfg)) return SafeColor(cfg.Image?.Color, "1 1 1 1");
            return "1 1 1 1";
        }

        private string ActiveColor(string ev)
        {
            PanelConfig cfg;
            return _config.Panels.TryGetValue(ev, out cfg) ? cfg.Get("ActiveColor", "0 1 0 1") : "0 1 0 1";
        }

        private string InactiveColor(string ev)
        {
            PanelConfig cfg;
            return _config.Panels.TryGetValue(ev, out cfg) ? cfg.Get("InactiveColor", "1 1 1 0.15") : "1 1 1 0.15";
        }

        // Window (seconds) an owner's Status panel glows the raid color after explosive damage.
        private float RaidWindowSeconds()
        {
            PanelConfig cfg;
            float s;
            if (_config.Panels.TryGetValue(PStatus, out cfg)
                && float.TryParse(cfg.Get("RaidWindowSeconds", "30"), NumberStyles.Float, Inv, out s) && s > 0f)
                return s;
            return 30f;
        }

        // The per-viewer Status glow color: walks the configured Priority list and returns the first
        // matching state's color (else InactiveColor). Cheap per-state checks; called once per tick.
        private string StatusColor(PlayerView view)
        {
            PanelConfig cfg;
            if (!_config.Panels.TryGetValue(PStatus, out cfg)) return "1 1 1 0.15";
            BasePlayer p = view.Player;
            string inactive = cfg.Get("InactiveColor", "1 1 1 0.15");
            if (p == null) return inactive;

            string priority = cfg.Get("Priority", "SafeHostile,Raid,SafeZone,AFK,BuildingPriv");
            foreach (string raw in priority.Split(','))
            {
                string state = raw.Trim();
                if (state.Length == 0) continue;
                if (state.Equals("SafeHostile", StringComparison.OrdinalIgnoreCase))
                {
                    if (p.InSafeZone() && p.IsHostile()) return cfg.Get("SafeHostileColor", "1 0 0 1");
                }
                else if (state.Equals("Raid", StringComparison.OrdinalIgnoreCase))
                {
                    float until;
                    if (_raidUntil.TryGetValue(view.Id, out until) && until > UnityEngine.Time.realtimeSinceStartup)
                        return cfg.Get("RaidColor", "1 1 0 1");
                }
                else if (state.Equals("SafeZone", StringComparison.OrdinalIgnoreCase))
                {
                    if (p.InSafeZone()) return cfg.Get("SafeColor", "0 1 0 1");
                }
                else if (state.Equals("AFK", StringComparison.OrdinalIgnoreCase))
                {
                    if (view.IdleTicks >= AfkTicks(cfg)) return cfg.Get("AfkColor", "0.3 0.5 1 1");
                }
                else if (state.Equals("BuildingPriv", StringComparison.OrdinalIgnoreCase))
                {
                    if (p.IsBuildingAuthed()) return cfg.Get("BuildingPrivColor", "1 1 1 1");
                }
            }
            return inactive;
        }

        // AFK threshold expressed in ticks (OnTick runs once per second, so ticks ~= seconds).
        private static int AfkTicks(PanelConfig statusCfg)
        {
            int s;
            return int.TryParse(statusCfg.Get("AfkSeconds", "300"), out s) && s > 0 ? s : 300;
        }

        #endregion

        #region Commands

        // Chat entry point: /mipanel ...
        [ChatCommand("mipanel")]
        private void ChatCmdPanel(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            RunCommand(player, false, args, s => player.ChatMessage(s));
        }

        // Console entry point: covers the in-game F1 console, the server console,
        // and RCON (all as: mipanel ...).
        [ConsoleCommand("mipanel")]
        private void ConsoleCmdPanel(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            BasePlayer player = arg.Player();
            // On current Rust arg.Args/FullString are Facepunch.StringView; GetString(i)
            // returns a plain string for each argument, avoiding any StringView casts.
            int n = arg.Args == null ? 0 : arg.Args.Length;
            string[] args = new string[n];
            for (int i = 0; i < n; i++) args[i] = arg.GetString(i);
            // No player => server console / RCON, which are always authorized.
            RunCommand(player, player == null, args, s => arg.ReplyWith(s));
        }

        // Shared logic. `reply` adapts output to chat or console; when
        // `authorizedConsole` is true (server console / RCON) admin actions are allowed.
        private void RunCommand(BasePlayer player, bool authorizedConsole, string[] args, Action<string> reply)
        {
            string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : null;
            string id = player?.UserIDString;

            if (sub == "reload")
            {
                bool ok = authorizedConsole || (player != null && permission.UserHasPermission(id, PermAdmin));
                if (!ok) { reply(L("NoPermission", id)); return; }
                DoReload();
                reply(L("Reloaded", id));
                return;
            }

            if (sub == "import")
            {
                bool ok = authorizedConsole || (player != null && permission.UserHasPermission(id, PermAdmin));
                if (!ok) { reply(L("NoPermission", id)); return; }

                // Confine imports to the config directory: a relative arg resolves under it,
                // an absolute arg must live inside it, and traversal (..) is canonicalised away.
                string configDir = Interface.Oxide.ConfigDirectory;
                string path = args.Length >= 2
                    ? (Path.IsPathRooted(args[1]) ? args[1] : Path.Combine(configDir, args[1]))
                    : DefaultInfoPanelPath;
                if (!IsUnderConfigDir(path, configDir)) { reply(L("ImportOutside", id)); return; }
                if (!File.Exists(path)) { reply(L("ImportNone", id, path)); return; }

                string summary;
                if (TryImportInfoPanel(path, _config, out summary))
                {
                    SaveConfig();   // persist the merged config...
                    DoReload();     // ...then re-read it from disk and redraw every panel.
                    reply(L("ImportOk", id, summary));
                }
                else reply(L("ImportFailed", id, summary));
                return;
            }

            if (player == null)
            {
                // Server console / RCON: the remaining subcommands are per-player.
                reply(L("PlayerOnly", id));
                return;
            }

            if (sub == null) { reply(HelpText(id)); return; }

            // Light per-player cooldown on the state-changing subcommands so a console macro
            // can't drive repeated saves/redraws. Silently drop while on cooldown.
            float now = UnityEngine.Time.realtimeSinceStartup;
            float last;
            if (_lastCmd.TryGetValue(id, out last) && now - last < CommandCooldown) return;
            _lastCmd[id] = now;

            switch (sub)
            {
                case "hide":
                    if (!IsHidden(id)) { Prefs(id).Hidden = true; MarkDataDirty(); HideFor(id); }
                    reply(L("PanelHidden", id));
                    break;

                case "show":
                    if (IsHidden(id))
                    {
                        Prefs(id).Hidden = false;
                        MarkDataDirty();
                        PlayerView view;
                        if (_views.TryGetValue(id, out view)) Draw(view);
                    }
                    reply(L("PanelShown", id));
                    break;

                case "clock":
                    HandleClock(id, args, reply);
                    break;

                case "timeformat":
                    HandleTimeFormat(id, args, reply);
                    break;

                case "tz":
                    HandleTz(id, args, reply);
                    break;

                case "admin":
                    if (permission.UserHasPermission(id, PermAdmin)) { OpenAdminMenu(player); reply(L("AdminOpened", id)); }
                    else reply(L("NoPermission", id));
                    break;

                default:
                    reply(L("InvalidArgs", id));
                    break;
            }
        }

        private string HelpText(string id)
        {
            var lines = new List<string>
            {
                L("HelpTitle", id), L("HelpToggle", id), L("HelpClockGame", id),
                L("HelpClockServer", id), L("HelpTimeFormat", id), L("HelpTz", id)
            };
            if (id != null && permission.UserHasPermission(id, PermAdmin))
            {
                lines.Add(L("HelpImport", id));
                lines.Add(L("HelpAdmin", id));
            }
            return string.Join("\n", lines);
        }

        // Per-player IANA timezone: `/mipanel tz <zone>` (e.g. America/Denver) or `tz off`.
        private void HandleTz(string id, string[] args, Action<string> reply)
        {
            if (args.Length < 2) { reply(L("HelpTz", id)); return; }
            string zone = args[1];
            if (string.Equals(zone, "off", StringComparison.OrdinalIgnoreCase) || zone == "0")
            {
                if (PrefsRead(id)?.ClockTz != null) { Prefs(id).ClockTz = null; MarkDataDirty(); RefreshPlayer(id); }
                reply(L("TzSet", id, "server"));
                return;
            }
            try
            {
                TimeZoneInfo tzi = TimeZoneInfo.FindSystemTimeZoneById(zone);
                Prefs(id).ClockTz = tzi.Id;
                MarkDataDirty();
                RefreshPlayer(id);
                reply(L("TzSet", id, tzi.Id));
            }
            catch { reply(L("TzUnknown", id, zone)); }
        }

        private void HandleClock(string id, string[] args, Action<string> reply)
        {
            if (args.Length < 2) { reply(L("InvalidArgs", id)); return; }

            if (string.Equals(args[1], "game", StringComparison.OrdinalIgnoreCase))
            {
                PlayerPrefs p = PrefsRead(id);
                if (p == null || !string.Equals(p.ClockMode, "game", StringComparison.OrdinalIgnoreCase))
                {
                    Prefs(id).ClockMode = "game";
                    MarkDataDirty();
                    RefreshPlayer(id);
                }
                reply(L("ClockGameSet", id));
            }
            else if (string.Equals(args[1], "server", StringComparison.OrdinalIgnoreCase))
            {
                PlayerPrefs p = Prefs(id);
                bool changed = !string.Equals(p.ClockMode, "server", StringComparison.OrdinalIgnoreCase);
                p.ClockMode = "server";
                if (args.Length >= 3)
                {
                    int offset;
                    if (int.TryParse(args[2], out offset) && offset > -24 && offset < 24)
                    {
                        if (p.ClockOffset != offset) { p.ClockOffset = offset; changed = true; }
                        if (changed) { MarkDataDirty(); RefreshPlayer(id); }
                        reply(L("ClockOffsetSet", id, offset));
                        return;
                    }
                }
                if (changed) { MarkDataDirty(); RefreshPlayer(id); }
                reply(L("ClockServerSet", id));
            }
            else reply(L("InvalidArgs", id));
        }

        private void HandleTimeFormat(string id, string[] args, Action<string> reply)
        {
            if (args.Length < 2)
            {
                var lines = new List<string> { L("TimeFormatList", id) };
                for (int i = 0; i < TimeFormats.Length; i++)
                    lines.Add(L("TimeFormatEntry", id, i, DateTime.Now.ToString(TimeFormats[i], Inv)));
                lines.Add(L("TimeFormatUsage", id));
                reply(string.Join("\n", lines));
                return;
            }

            int index;
            if (int.TryParse(args[1], out index) && index >= 0 && index < TimeFormats.Length)
            {
                string fmt = TimeFormats[index];
                PlayerPrefs p = PrefsRead(id);
                if (p == null || p.ClockFormat != fmt)
                {
                    Prefs(id).ClockFormat = fmt;
                    MarkDataDirty();
                    RefreshPlayer(id);
                }
                reply(L("TimeFormatSet", id));
            }
            else reply(L("TimeFormatUsage", id));
        }

        private void DoReload()
        {
            LoadConfig();
            foreach (var kv in _thirdParty) _config.Panels[kv.Key] = kv.Value; // survive reload
            RebuildIndex();
            foreach (PlayerView view in _views.Values)
            {
                if (!Ready(view)) continue;
                if (IsHidden(view.IdString)) { CuiHelper.DestroyUi(view.Player, Root); view.Visible = false; }
                else Draw(view);
            }
        }

        private void HideFor(string id)
        {
            PlayerView view;
            if (_views.TryGetValue(id, out view) && Ready(view))
            {
                CuiHelper.DestroyUi(view.Player, Root);
                view.Visible = false;
            }
        }

        private void RefreshPlayer(string id)
        {
            PlayerView view;
            if (_views.TryGetValue(id, out view) && Ready(view) && view.Visible) Draw(view);
        }

        // --- Admin editor (CUI; no ImageLibrary) ----------------------------------
        private static void AdminBtn(CuiElementContainer c, string parent, string min, string max, string color, string label, string command)
        {
            c.Add(new CuiButton
            {
                Button = { Command = command, Color = color },
                Text = { Text = label, FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);
        }

        // Cursor-enabled overlay listing every panel with toggle / dock-cycle / width / color
        // controls, plus per-dock toggles, Reload and Close. Each control runs a mipanel.admin
        // console command (below) that mutates the config, saves, redraws, and re-renders.
        private void OpenAdminMenu(BasePlayer player)
        {
            if (player == null) return;
            string id = player.UserIDString;
            CuiHelper.DestroyUi(player, AdminRoot);

            var c = new CuiElementContainer();
            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.75" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, "Overlay", AdminRoot);

            string win = AdminRoot + "_win";
            c.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.08 0.09 0.98" },
                RectTransform = { AnchorMin = "0.12 0.10", AnchorMax = "0.88 0.92" }
            }, AdminRoot, win);

            c.Add(new CuiLabel
            {
                Text = { Text = L("AdminTitle", id), FontSize = 16, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.02 0.93", AnchorMax = "0.85 0.99" }
            }, win);
            AdminBtn(c, win, "0.90 0.93", "0.99 0.99", "0.7 0.2 0.2 0.95", L("AdminClose", id), "mipanel.admin close");

            // Panel rows, top-down.
            var names = new List<string>(_config.Panels.Keys);
            const float top = 0.90f, rh = 0.044f;
            for (int i = 0; i < names.Count; i++)
            {
                float y1 = top - i * rh, y0 = y1 - rh + 0.006f;
                if (y0 < 0.115f) break;   // leave room for the dock row + footer
                string pn = names[i];
                PanelConfig pc = _config.Panels[pn];
                bool on = pc.Enabled;
                c.Add(new CuiLabel { Text = { Text = pn, FontSize = 11, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = Pos(0.02f, y0), AnchorMax = Pos(0.24f, y1) } }, win);
                AdminBtn(c, win, Pos(0.24f, y0), Pos(0.33f, y1), on ? "0.2 0.6 0.2 0.9" : "0.5 0.2 0.2 0.9",
                    on ? L("AdminOn", id) : L("AdminOff", id), "mipanel.admin toggle " + pn);
                AdminBtn(c, win, Pos(0.335f, y0), Pos(0.37f, y1), "0.25 0.25 0.3 0.9", "<", "mipanel.admin dock " + pn + " prev");
                c.Add(new CuiLabel { Text = { Text = pc.Dock.Replace("Dock", ""), FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                    RectTransform = { AnchorMin = Pos(0.37f, y0), AnchorMax = Pos(0.47f, y1) } }, win);
                AdminBtn(c, win, Pos(0.47f, y0), Pos(0.505f, y1), "0.25 0.25 0.3 0.9", ">", "mipanel.admin dock " + pn + " next");
                AdminBtn(c, win, Pos(0.51f, y0), Pos(0.545f, y1), "0.25 0.25 0.3 0.9", "w-", "mipanel.admin width " + pn + " dec");
                AdminBtn(c, win, Pos(0.55f, y0), Pos(0.585f, y1), "0.25 0.25 0.3 0.9", "w+", "mipanel.admin width " + pn + " inc");
                for (int s = 0; s < AdminPalette.Length; s++)
                {
                    float sx0 = 0.60f + s * 0.038f, sx1 = sx0 + 0.034f;
                    if (sx1 > 0.99f) break;
                    AdminBtn(c, win, Pos(sx0, y0), Pos(sx1, y1), SafeColor(AdminPalette[s], "0 0 0 0.45"), "", "mipanel.admin color " + pn + " " + s);
                }
            }

            // Dock toggles + Reload along the bottom.
            c.Add(new CuiLabel { Text = { Text = L("AdminDocks", id), FontSize = 11, Align = TextAnchor.MiddleLeft, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.02 0.065", AnchorMax = "0.12 0.105" } }, win);
            var docks = new List<string>(_config.Docks.Keys);
            for (int i = 0; i < docks.Count; i++)
            {
                string dn = docks[i];
                bool on = _config.Docks[dn].Enabled;
                float dx0 = 0.02f + i * 0.165f, dx1 = dx0 + 0.155f;
                AdminBtn(c, win, Pos(dx0, 0.015f), Pos(dx1, 0.06f), on ? "0.2 0.6 0.2 0.9" : "0.5 0.2 0.2 0.9",
                    dn.Replace("Dock", "") + " " + (on ? L("AdminOn", id) : L("AdminOff", id)), "mipanel.admin dockenable " + dn);
            }
            AdminBtn(c, win, "0.80 0.015", "0.99 0.06", "0.25 0.4 0.6 0.95", L("AdminReload", id), "mipanel.admin reload");

            CuiHelper.AddUi(player, c.ToJson());
        }

        [ConsoleCommand("mipanel.admin")]
        private void AdminConsole(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg?.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, PermAdmin)) return;
            string action = (arg.GetString(0) ?? string.Empty).ToLowerInvariant();
            if (action == "close") { CuiHelper.DestroyUi(player, AdminRoot); return; }
            if (action == "reload") { DoReload(); OpenAdminMenu(player); return; }

            string target = arg.GetString(1);
            PanelConfig pc;
            switch (action)
            {
                case "toggle":
                    if (_config.Panels.TryGetValue(target, out pc)) pc.Enabled = !pc.Enabled;
                    break;
                case "dock":
                    if (_config.Panels.TryGetValue(target, out pc))
                    {
                        var docks = new List<string>(_config.Docks.Keys);
                        int idx = docks.IndexOf(pc.Dock); if (idx < 0) idx = 0;
                        idx = arg.GetString(2) == "prev" ? (idx - 1 + docks.Count) % docks.Count : (idx + 1) % docks.Count;
                        if (docks.Count > 0) pc.Dock = docks[idx];
                    }
                    break;
                case "width":
                    if (_config.Panels.TryGetValue(target, out pc))
                        pc.Width = Mathf.Clamp(pc.Width + (arg.GetString(2) == "dec" ? -0.02f : 0.02f), 0.02f, 1f);
                    break;
                case "color":
                    int ci;
                    if (_config.Panels.TryGetValue(target, out pc) && int.TryParse(arg.GetString(2), out ci) && ci >= 0 && ci < AdminPalette.Length)
                        pc.BackgroundColor = AdminPalette[ci];
                    break;
                case "dockenable":
                    DockConfig dc;
                    if (_config.Docks.TryGetValue(target, out dc)) dc.Enabled = !dc.Enabled;
                    break;
                default:
                    return;
            }
            SaveConfig();
            RebuildIndex();
            RedrawAll();
            OpenAdminMenu(player);
        }

        #endregion

        // Methods here are invoked by other plugins via plugin.Call("Name", args).
        // Explicit overloads (rather than optional parameters) keep Oxide/Carbon
        // call resolution unambiguous across argument counts.
        #region Third-party API (reflection-free)

        // Register a panel on behalf of another plugin. `json` is a serialized
        // PanelConfig (same shape as one entry under "Panels"). Returns true on success.
        private bool PanelRegister(string pluginName, string panelName, string json)
        {
            if (string.IsNullOrEmpty(pluginName) || string.IsNullOrEmpty(panelName) || string.IsNullOrEmpty(json))
                return false;

            PanelConfig cfg;
            try { cfg = JsonConvert.DeserializeObject<PanelConfig>(json); }
            catch (Exception ex) { LogProblem($"PanelRegister: bad JSON from {pluginName} for {panelName}: {ex.Message}"); return false; }
            if (cfg == null) return false;

            if (BuiltInPanels.Contains(panelName))
            {
                LogProblem($"PanelRegister: '{panelName}' from {pluginName} collides with a built-in panel id; rejected.");
                return false;
            }

            if (panelName.Length > MaxPanelNameLen)
            {
                LogProblem($"PanelRegister: panel name from {pluginName} exceeds {MaxPanelNameLen} chars; rejected.");
                return false;
            }

            List<string> owned;
            _pluginPanels.TryGetValue(pluginName, out owned);
            bool isNew = owned == null || !owned.Contains(panelName);
            if (isNew && owned != null && owned.Count >= MaxPanelsPerPlugin)
            {
                LogProblem($"PanelRegister: {pluginName} reached the {MaxPanelsPerPlugin}-panel limit; '{panelName}' rejected.");
                return false;
            }

            // Sanitize untrusted content: bound text length and drop non-http(s) image URLs.
            if (cfg.Text != null) cfg.Text.Content = ClampText(cfg.Text.Content);
            if (cfg.Image != null && !IsValidImageUrl(cfg.Image.Url))
            {
                LogProblem($"PanelRegister: '{panelName}' from {pluginName} has a non-http(s) image URL ({ClampText(cfg.Image.Url)}); image dropped.");
                cfg.Image = null;
            }

            cfg.ThirdParty = true;
            if (string.IsNullOrEmpty(cfg.Dock) || !_config.Docks.ContainsKey(cfg.Dock)) cfg.Dock = "BottomLeftDock";
            _config.Panels[panelName] = cfg;
            _thirdParty[panelName] = cfg;

            if (owned == null) _pluginPanels[pluginName] = owned = new List<string>();
            if (!owned.Contains(panelName)) owned.Add(panelName);

            RebuildIndex();
            RedrawAll();
            return true;
        }

        private bool PanelUnregister(string pluginName, string panelName)
        {
            List<string> owned;
            if (!_pluginPanels.TryGetValue(pluginName, out owned) || !owned.Remove(panelName)) return false;
            RemovePanel(panelName);
            return true;
        }

        private void RemovePanel(string panelName)
        {
            foreach (PlayerView view in _views.Values)
            {
                if (!Ready(view)) continue;
                CuiHelper.DestroyUi(view.Player, NText(panelName));
                CuiHelper.DestroyUi(view.Player, NImage(panelName));
                CuiHelper.DestroyUi(view.Player, NPanel(panelName));
            }
            _config.Panels.Remove(panelName);
            _thirdParty.Remove(panelName);
            _customText.Remove(panelName);
            _customColor.Remove(panelName);
            _customProgress.Remove(panelName);
            RebuildIndex();
            RedrawAll();
        }

        private bool SetPanelText(string panelName, string text) => SetPanelTextImpl(panelName, text, null);
        private bool SetPanelText(string panelName, string text, string playerId) => SetPanelTextImpl(panelName, text, playerId);

        private bool SetPanelTextImpl(string panelName, string text, string playerId)
        {
            // The API manages third-party panels only — built-ins stay owned by the plugin.
            if (BuiltInPanels.Contains(panelName) || !_config.Panels.ContainsKey(panelName)) return false;
            text = ClampText(text);
            _customText[panelName] = text;
            if (playerId != null)
            {
                PlayerView v;
                if (_views.TryGetValue(playerId, out v)) PushText(v, panelName, text);
                return true;
            }
            foreach (PlayerView view in _views.Values) PushText(view, panelName, text);
            return true;
        }

        private bool SetPanelImage(string panelName, string url, string color) => SetPanelImageImpl(panelName, url, color, null);
        private bool SetPanelImage(string panelName, string url, string color, string playerId) => SetPanelImageImpl(panelName, url, color, playerId);

        private bool SetPanelImageImpl(string panelName, string url, string color, string playerId)
        {
            PanelConfig cfg;
            if (BuiltInPanels.Contains(panelName) || !_config.Panels.TryGetValue(panelName, out cfg)) return false;
            if (!string.IsNullOrEmpty(url) && !IsValidImageUrl(url))
            {
                LogProblem($"SetPanelImage: rejected non-http(s) URL ({ClampText(url)}) for panel '{panelName}'.");
                return false;
            }
            if (cfg.Image == null) cfg.Image = new ImageElement();
            if (!string.IsNullOrEmpty(url)) cfg.Image.Url = url;
            if (!string.IsNullOrEmpty(color)) _customColor[panelName] = SafeColor(color, "1 1 1 1");
            if (playerId != null) RefreshPlayer(playerId); else RedrawAll();
            return true;
        }

        private bool SetPanelProgress(string panelName, object value) => SetPanelProgressImpl(panelName, value, null);
        private bool SetPanelProgress(string panelName, object value, string playerId) => SetPanelProgressImpl(panelName, value, playerId);

        private bool SetPanelProgressImpl(string panelName, object value, string playerId)
        {
            // Third-party panels only, mirroring SetPanelText/SetPanelImage.
            if (BuiltInPanels.Contains(panelName) || !_config.Panels.ContainsKey(panelName)) return false;
            float v = Mathf.Clamp01((float)ToDouble(value));
            _customProgress[panelName] = v;
            if (playerId != null)
            {
                PlayerView pv;
                if (_views.TryGetValue(playerId, out pv)) PushProgress(pv, panelName, v);
                return true;
            }
            foreach (PlayerView view in _views.Values) PushProgress(view, panelName, v);
            return true;
        }

        private bool SetPanelColor(string panelName, string color) => SetPanelColorImpl(panelName, color, null);
        private bool SetPanelColor(string panelName, string color, string playerId) => SetPanelColorImpl(panelName, color, playerId);

        // In-place image-glow update for third-party panels (e.g. flashing a modded-event icon).
        private bool SetPanelColorImpl(string panelName, string color, string playerId)
        {
            if (BuiltInPanels.Contains(panelName) || !_config.Panels.ContainsKey(panelName)) return false;
            color = SafeColor(color, "1 1 1 1");
            _customColor[panelName] = color;
            if (playerId != null)
            {
                PlayerView pv;
                if (_views.TryGetValue(playerId, out pv)) PushColor(pv, panelName, color);
                return true;
            }
            foreach (PlayerView view in _views.Values) PushColor(view, panelName, color);
            return true;
        }

        private bool ShowPanel(string panelName) => SetPanelEnabled(panelName, true, null);
        private bool ShowPanel(string panelName, string playerId) => SetPanelEnabled(panelName, true, playerId);
        private bool HidePanel(string panelName) => SetPanelEnabled(panelName, false, null);
        private bool HidePanel(string panelName, string playerId) => SetPanelEnabled(panelName, false, playerId);

        private bool SetPanelEnabled(string panelName, bool enabled, string playerId)
        {
            PanelConfig cfg;
            if (BuiltInPanels.Contains(panelName) || !_config.Panels.TryGetValue(panelName, out cfg)) return false;
            cfg.Enabled = enabled;
            RebuildIndex();
            if (playerId != null) RefreshPlayer(playerId); else RedrawAll();
            return true;
        }

        private bool RefreshPanel(string panelName) => RefreshPanelImpl(panelName, null);
        private bool RefreshPanel(string panelName, string playerId) => RefreshPanelImpl(panelName, playerId);

        private bool RefreshPanelImpl(string panelName, string playerId)
        {
            if (!_config.Panels.ContainsKey(panelName)) return false;
            if (playerId != null) RefreshPlayer(playerId); else RedrawAll();
            return true;
        }

        private bool IsPlayerGUILoaded(string playerId)
        {
            PlayerView v;
            return _views.TryGetValue(playerId, out v) && v.Visible;
        }

        #endregion

        #region Helpers

        private static string Grid(Vector3 pos)
        {
            const float cell = 146.3f;
            float half = World.Size / 2f;
            int col = Mathf.FloorToInt((pos.x + half) / cell);
            int row = Mathf.FloorToInt((half - pos.z) / cell);
            if (col < 0) col = 0;
            if (row < 0) row = 0;
            return GridColumn(col) + row.ToString(Inv);
        }

        private static string GridColumn(int index)
        {
            string s = string.Empty;
            index++;
            while (index > 0)
            {
                int rem = (index - 1) % 26;
                s = (char)('A' + rem) + s;
                index = (index - 1) / 26;
            }
            return s;
        }

        private static string Pos(float a, float b) => a.ToString(Inv) + " " + b.ToString(Inv);

        private static TextAnchor ParseAnchor(string value)
        {
            TextAnchor anchor;
            if (!string.IsNullOrEmpty(value) && Enum.TryParse(value, true, out anchor)) return anchor;
            return TextAnchor.MiddleCenter;
        }

        // Returns the input if it is a valid "R G B A" string, else the fallback.
        private static string SafeColor(string value, string fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            string[] parts = value.Split(' ');
            if (parts.Length != 4) return fallback;
            for (int i = 0; i < 4; i++)
            {
                float f;
                if (!float.TryParse(parts[i], NumberStyles.Float, Inv, out f)) return fallback;
            }
            return value;
        }

        // Third-party content guards: bound text length and accept only http(s) image URLs.
        private static string ClampText(string s)
            => string.IsNullOrEmpty(s) || s.Length <= MaxTextLen ? s : s.Substring(0, MaxTextLen);

        private static bool IsValidImageUrl(string url)
            => !string.IsNullOrEmpty(url)
               && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        private static double ToDouble(object o)
        {
            if (o == null) return 0d;
            try { return Convert.ToDouble(o, Inv); } catch { return 0d; }
        }

        private static int ToInt(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToInt32(o, Inv); } catch { return 0; }
        }

        #endregion
    }
}
