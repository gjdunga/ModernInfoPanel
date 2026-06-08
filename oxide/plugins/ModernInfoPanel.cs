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
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Modern Info Panel", "gjdunga", "1.0.0")]
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
        private const string DataFile = "ModernInfoPanel";

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

        private static readonly string[] TimeFormats = { "H:mm", "HH:mm", "h:mm", "h:mm tt" };
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private Configuration _config;
        private StoredData _data;
        private Timer _tickTimer;
        private bool _ready;
        private long _tick;
        private int _messageIndex;

        // Per-connected-player render state.
        private readonly Dictionary<string, PlayerView> _views = new Dictionary<string, PlayerView>();

        // Dock name -> panels in that dock, pre-sorted by Order (rebuilt on load/reload).
        private readonly Dictionary<string, List<PanelEntry>> _dockIndex = new Dictionary<string, List<PanelEntry>>();

        // Live state shared by all players.
        private readonly Dictionary<string, string> _eventColor = new Dictionary<string, string>();
        private readonly HashSet<string> _activeEvents = new HashSet<string>();
        private readonly Dictionary<string, HashSet<BaseEntity>> _eventEntities = new Dictionary<string, HashSet<BaseEntity>>();
        private bool _radiationOn;

        // Maps a spawned entity type to the event panel it drives.
        private static readonly Dictionary<Type, string> EventTypes = new Dictionary<Type, string>();

        // Third-party panels: plugin title -> panel names it registered.
        private readonly Dictionary<string, List<string>> _pluginPanels = new Dictionary<string, List<string>>();

        // Live configs for third-party panels, re-merged after a config reload.
        private readonly Dictionary<string, PanelConfig> _thirdParty = new Dictionary<string, PanelConfig>();

        // Static/third-party content overrides: panel -> text/color (global and per-player).
        private readonly Dictionary<string, string> _customText = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _customColor = new Dictionary<string, string>();

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
        }

        #endregion

        #region Configuration

        private sealed class Configuration
        {
            [JsonProperty("Config version")]
            public string Version = "1.0.0";

            [JsonProperty("General")]
            public GeneralOptions General = new GeneralOptions();

            [JsonProperty("Docks")]
            public Dictionary<string, DockConfig> Docks = new Dictionary<string, DockConfig>();

            [JsonProperty("Panels")]
            public Dictionary<string, PanelConfig> Panels = new Dictionary<string, PanelConfig>();

            [JsonProperty("Rotating announcement messages")]
            public List<string> Messages = new List<string>();
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
            [JsonProperty("Image", NullValueHandling = NullValueHandling.Ignore)] public ImageElement Image;
            [JsonProperty("Text", NullValueHandling = NullValueHandling.Ignore)] public TextElement Text;
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

        protected override void LoadDefaultConfig()
        {
            _config = BuildDefaultConfig();
            SaveConfig();
            PrintWarning("Created a new default configuration file.");
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
                PrintError($"Invalid configuration ({ex.Message}); regenerating defaults. The old file is backed up by Oxide.");
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
            SaveStoredData();
            _views.Clear();
        }

        private void OnServerSave() => SaveStoredData();

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            // Defer until the client has finished receiving its snapshot.
            if (player.HasPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot))
            {
                timer.Once(2f, () => OnPlayerConnected(player));
                return;
            }
            Register(player);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            _views.Remove(player.UserIDString);
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            if (!_ready || entity == null) return;
            string ev;
            if (!EventTypes.TryGetValue(entity.GetType(), out ev)) return;
            if (!PanelEnabled(ev)) return;
            var ent = entity as BaseEntity;
            if (ent != null) _eventEntities[ev].Add(ent);
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            var ent = entity as BaseEntity;
            if (ent == null) return;
            string ev;
            if (EventTypes.TryGetValue(entity.GetType(), out ev) && _eventEntities.ContainsKey(ev))
                _eventEntities[ev].Remove(ent);
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
        }

        private sealed class PlayerPrefs
        {
            public bool Hidden;
            public string ClockMode;   // "game" | "server"
            public int ClockOffset;
            public string ClockFormat;
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

        private void RedrawAll()
        {
            foreach (PlayerView view in _views.Values)
                if (Ready(view) && view.Visible) Draw(view);
        }

        private void Draw(PlayerView view)
        {
            BasePlayer player = view.Player;
            if (!Ready(view)) return;

            CuiHelper.DestroyUi(player, Root);
            view.Text.Clear();
            view.Color.Clear();

            var c = new CuiElementContainer();
            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = false
            }, "Hud", Root);

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
                    Image = { Color = SafeColor(dock.BackgroundColor, "0 0 0 0") },
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

                    string panelName = NPanel(e.Name);
                    c.Add(new CuiPanel
                    {
                        Image = { Color = SafeColor(e.Cfg.BackgroundColor, "0 0 0 0.45") },
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
                        new CuiRawImageComponent { Url = cfg.Image.Url, Color = color },
                        new CuiRectTransformComponent { AnchorMin = Pos(0.02f, y0), AnchorMax = Pos(iw, y0 + ih) }
                    }
                });
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
                        Color = SafeColor(cfg.Text.Color, "1 1 1 1")
                    },
                    RectTransform = { AnchorMin = Pos(tl, 0f), AnchorMax = "1 1" }
                }, parent, NText(e.Name));
            }
        }

        // Lightweight in-place label update (no background/image flicker).
        private void PushText(PlayerView view, string panel, string value)
        {
            string prev;
            if (view.Text.TryGetValue(panel, out prev) && prev == value) return;
            view.Text[panel] = value;
            if (!view.Visible || !Ready(view) || !IsVisibleTo(view, panel)) return;

            PanelConfig cfg = _config.Panels[panel];
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

            PanelConfig cfg = _config.Panels[panel];
            if (cfg.Image == null) return;
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

            foreach (PlayerView view in _views.Values)
            {
                if (!Ready(view) || !view.Visible) continue;

                if (onlineText != null) PushText(view, POnline, onlineText);
                if (sleepersText != null) PushText(view, PSleepers, sleepersText);
                if (messageText != null) PushText(view, PMessages, messageText);

                if (dueClock) PushText(view, PClock, ClockText(view));
                if (dueCoord) PushText(view, PCoordinates, CoordText(view));
                if (dueCompass) PushText(view, PCompass, CompassText(view));
                if (dueBalance) PushText(view, PBalance, BalanceText(view));
                if (duePoints) PushText(view, PPoints, PointsText(view));
            }
        }

        private bool Due(string panel)
        {
            PanelConfig cfg;
            if (!_config.Panels.TryGetValue(panel, out cfg) || cfg == null || !cfg.Enabled) return false;
            int interval = cfg.RefreshInterval;
            return interval > 0 && _tick % interval == 0;
        }

        private string TextValue(PlayerView view, PanelEntry e)
        {
            switch (e.Name)
            {
                case PClock: return ClockText(view);
                case PMessages: return CurrentMessage();
                case PBalance: return BalanceText(view);
                case PPoints: return PointsText(view);
                case PCoordinates: return CoordText(view);
                case PCompass: return CompassText(view);
                case POnline: return OnlineText();
                case PSleepers: return BasePlayer.sleepingPlayerList.Count.ToString(Inv);
                default:
                    string custom;
                    if (_customText.TryGetValue(e.Name, out custom)) return custom ?? string.Empty;
                    return e.Cfg.Text?.Content ?? string.Empty;
            }
        }

        private string OnlineText() => L("PlayersLabel", null, BasePlayer.activePlayerList.Count, ConVar.Server.maxplayers);

        private string ClockText(PlayerView view)
        {
            PanelConfig cfg = _config.Panels[PClock];
            PlayerPrefs p = PrefsRead(view.IdString);
            string mode = p?.ClockMode ?? cfg.Get("Mode", "game");
            string fmt = p?.ClockFormat ?? cfg.Get("Format", "HH:mm");

            DateTime dt;
            if (string.Equals(mode, "server", StringComparison.OrdinalIgnoreCase))
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
            foreach (T e in UnityEngine.Object.FindObjectsOfType<T>())
                if (e != null && !e.IsDestroyed) _eventEntities[ev].Add(e);
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
            if (_eventColor.TryGetValue(panel, out color)) return color;
            if (_customColor.TryGetValue(panel, out color)) return color;
            PanelConfig cfg = _config.Panels[panel];
            return SafeColor(cfg.Image?.Color, "1 1 1 1");
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
            // No player => server console / RCON, which are always authorized.
            RunCommand(player, player == null, arg.Args ?? new string[0], s => arg.ReplyWith(s));
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

            if (player == null)
            {
                // Server console / RCON: the remaining subcommands are per-player.
                reply(L("PlayerOnly", id));
                return;
            }

            if (sub == null) { reply(HelpText(id)); return; }

            switch (sub)
            {
                case "hide":
                    Prefs(id).Hidden = true;
                    SaveStoredData();
                    HideFor(id);
                    reply(L("PanelHidden", id));
                    break;

                case "show":
                    Prefs(id).Hidden = false;
                    SaveStoredData();
                    PlayerView view;
                    if (_views.TryGetValue(id, out view)) Draw(view);
                    reply(L("PanelShown", id));
                    break;

                case "clock":
                    HandleClock(id, args, reply);
                    break;

                case "timeformat":
                    HandleTimeFormat(id, args, reply);
                    break;

                default:
                    reply(L("InvalidArgs", id));
                    break;
            }
        }

        private string HelpText(string id) => string.Join("\n", new[]
        {
            L("HelpTitle", id), L("HelpToggle", id), L("HelpClockGame", id),
            L("HelpClockServer", id), L("HelpTimeFormat", id)
        });

        private void HandleClock(string id, string[] args, Action<string> reply)
        {
            if (args.Length < 2) { reply(L("InvalidArgs", id)); return; }

            if (string.Equals(args[1], "game", StringComparison.OrdinalIgnoreCase))
            {
                Prefs(id).ClockMode = "game";
                SaveStoredData();
                RefreshPlayer(id);
                reply(L("ClockGameSet", id));
            }
            else if (string.Equals(args[1], "server", StringComparison.OrdinalIgnoreCase))
            {
                PlayerPrefs p = Prefs(id);
                p.ClockMode = "server";
                if (args.Length >= 3)
                {
                    int offset;
                    if (int.TryParse(args[2], out offset) && offset > -24 && offset < 24)
                    {
                        p.ClockOffset = offset;
                        SaveStoredData();
                        RefreshPlayer(id);
                        reply(L("ClockOffsetSet", id, offset));
                        return;
                    }
                }
                SaveStoredData();
                RefreshPlayer(id);
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
                Prefs(id).ClockFormat = TimeFormats[index];
                SaveStoredData();
                RefreshPlayer(id);
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
            catch (Exception ex) { PrintWarning($"PanelRegister: bad JSON from {pluginName} for {panelName}: {ex.Message}"); return false; }
            if (cfg == null) return false;

            cfg.ThirdParty = true;
            if (string.IsNullOrEmpty(cfg.Dock) || !_config.Docks.ContainsKey(cfg.Dock)) cfg.Dock = "BottomLeftDock";
            _config.Panels[panelName] = cfg;
            _thirdParty[panelName] = cfg;

            List<string> owned;
            if (!_pluginPanels.TryGetValue(pluginName, out owned)) _pluginPanels[pluginName] = owned = new List<string>();
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
            RebuildIndex();
            RedrawAll();
        }

        private bool SetPanelText(string panelName, string text) => SetPanelTextImpl(panelName, text, null);
        private bool SetPanelText(string panelName, string text, string playerId) => SetPanelTextImpl(panelName, text, playerId);

        private bool SetPanelTextImpl(string panelName, string text, string playerId)
        {
            if (!_config.Panels.ContainsKey(panelName)) return false;
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
            if (!_config.Panels.TryGetValue(panelName, out cfg)) return false;
            if (cfg.Image == null) cfg.Image = new ImageElement();
            if (!string.IsNullOrEmpty(url)) cfg.Image.Url = url;
            if (!string.IsNullOrEmpty(color)) _customColor[panelName] = color;
            if (playerId != null) RefreshPlayer(playerId); else RedrawAll();
            return true;
        }

        private bool ShowPanel(string panelName) => SetPanelEnabled(panelName, true, null);
        private bool ShowPanel(string panelName, string playerId) => SetPanelEnabled(panelName, true, playerId);
        private bool HidePanel(string panelName) => SetPanelEnabled(panelName, false, null);
        private bool HidePanel(string panelName, string playerId) => SetPanelEnabled(panelName, false, playerId);

        private bool SetPanelEnabled(string panelName, bool enabled, string playerId)
        {
            PanelConfig cfg;
            if (!_config.Panels.TryGetValue(panelName, out cfg)) return false;
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
