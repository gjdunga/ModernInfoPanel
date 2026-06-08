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
// Compatible with both Oxide and Carbon (uses only the shared Rust/Cui APIs).

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core.Libraries;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Modern Info Panel", "gjdunga", "1.0.0")]
    [Description("Configurable on-screen information panel (clock, players online, rotating announcements). Oxide + Carbon compatible.")]
    public class ModernInfoPanel : RustPlugin
    {
        #region Constants & fields

        private const string PermUse = "moderninfopanel.use";
        private const string PermAdmin = "moderninfopanel.admin";

        // Root CUI element; per-block backgrounds and labels are derived from it.
        private const string GuiRoot = "moderninfopanel.root";

        private Configuration _config;
        private Timer.TimerInstance _refreshTimer;
        private Timer.TimerInstance _rotatorTimer;
        private int _rotatorIndex;

        // Players who explicitly hid their panel with /infopanel off.
        private readonly HashSet<ulong> _hidden = new HashSet<ulong>();

        #endregion

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Update interval (seconds)")]
            public float UpdateInterval = 1f;

            [JsonProperty("Rotator interval (seconds)")]
            public float RotatorInterval = 8f;

            [JsonProperty("Show panel to everyone (no permission required)")]
            public bool ShowToEveryone = true;

            [JsonProperty("Clock mode (realtime | gametime)")]
            public string ClockMode = "realtime";

            [JsonProperty("Clock format (.NET date/time format string)")]
            public string ClockFormat = "HH:mm";

            [JsonProperty("Rotating announcement messages")]
            public List<string> RotatorMessages = new List<string>();

            [JsonProperty("Blocks")]
            public List<Block> Blocks = new List<Block>();
        }

        private class Block
        {
            [JsonProperty("Id")]
            public string Id = "block";

            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Type (clock | players | rotator | text)")]
            public string Type = "text";

            [JsonProperty("Anchor min (x y, 0-1)")]
            public string AnchorMin = "0.0 0.95";

            [JsonProperty("Anchor max (x y, 0-1)")]
            public string AnchorMax = "0.2 1.0";

            [JsonProperty("Background color (R G B A, 0-1)")]
            public string BackgroundColor = "0.10 0.10 0.10 0.75";

            [JsonProperty("Text color (R G B A, 0-1)")]
            public string TextColor = "1 1 1 1";

            [JsonProperty("Font size")]
            public int FontSize = 14;

            [JsonProperty("Text alignment (TextAnchor)")]
            public string Align = "MiddleCenter";

            [JsonProperty("Static text (text blocks only; empty = localized welcome)")]
            public string Text = "";
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                RotatorMessages = new List<string>
                {
                    "Welcome! Type /infopanel to toggle this panel.",
                    "Be respectful - no harassment or cheating.",
                    "Need help? Ask a moderator in chat."
                },
                Blocks = new List<Block>
                {
                    new Block
                    {
                        Id = "clock", Type = "clock",
                        AnchorMin = "0.0 0.96", AnchorMax = "0.10 1.0",
                        FontSize = 14, Align = "MiddleCenter"
                    },
                    new Block
                    {
                        Id = "players", Type = "players",
                        AnchorMin = "0.10 0.96", AnchorMax = "0.27 1.0",
                        FontSize = 14, Align = "MiddleCenter"
                    },
                    new Block
                    {
                        Id = "rotator", Type = "rotator",
                        AnchorMin = "0.27 0.96", AnchorMax = "0.73 1.0",
                        BackgroundColor = "0.10 0.10 0.10 0.60",
                        FontSize = 14, Align = "MiddleCenter"
                    }
                }
            };
        }

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                    throw new Exception("configuration deserialized to null");
            }
            catch (Exception ex)
            {
                PrintError($"Invalid config ({ex.Message}); writing a fresh default config.");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["ClockLabel"] = "{0}",
                ["PlayersLabel"] = "{0} / {1} online",
                ["Welcome"] = "Welcome to our Rust server!",
                ["PanelShown"] = "Info panel <color=#9f9>shown</color>.",
                ["PanelHidden"] = "Info panel <color=#f99>hidden</color>.",
                ["Reloaded"] = "ModernInfoPanel reloaded.",
                ["NoPermission"] = "You don't have permission to use that command."
            }, this);
        }

        private string Lang(string key, BasePlayer player, params object[] args)
        {
            string msg = lang.GetMessage(key, this, player?.UserIDString);
            return args != null && args.Length > 0 ? string.Format(msg, args) : msg;
        }

        private void Reply(BasePlayer player, string key, params object[] args)
        {
            if (player != null)
                player.ChatMessage(Lang(key, player, args));
        }

        #endregion

        #region Lifecycle hooks

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);
        }

        private void OnServerInitialized()
        {
            StartTimers();
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                Show(player);
        }

        private void Unload()
        {
            StopTimers();
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                CuiHelper.DestroyUi(player, GuiRoot);
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            // Give the client a moment to finish loading before drawing the HUD.
            timer.Once(2f, () => Show(player));
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player != null)
                _hidden.Remove(player.userID);
        }

        #endregion

        #region Timers

        private void StartTimers()
        {
            StopTimers();

            float refresh = Mathf.Max(0.25f, _config.UpdateInterval);
            _refreshTimer = timer.Every(refresh, RefreshAll);

            float rotate = Mathf.Max(1f, _config.RotatorInterval);
            _rotatorTimer = timer.Every(rotate, () => _rotatorIndex++);
        }

        private void StopTimers()
        {
            _refreshTimer?.Destroy();
            _refreshTimer = null;
            _rotatorTimer?.Destroy();
            _rotatorTimer = null;
        }

        private void RefreshAll()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                RefreshText(player);
        }

        #endregion

        #region Commands

        [ChatCommand("infopanel")]
        private void CmdInfoPanel(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "toggle";

            if (sub == "reload")
            {
                if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
                {
                    Reply(player, "NoPermission");
                    return;
                }
                ReloadAndRedraw();
                Reply(player, "Reloaded");
                return;
            }

            if (!CanSee(player))
            {
                Reply(player, "NoPermission");
                return;
            }

            switch (sub)
            {
                case "on":
                    _hidden.Remove(player.userID);
                    Show(player);
                    Reply(player, "PanelShown");
                    break;
                case "off":
                    _hidden.Add(player.userID);
                    Hide(player);
                    Reply(player, "PanelHidden");
                    break;
                default: // toggle
                    if (_hidden.Remove(player.userID))
                    {
                        Show(player);
                        Reply(player, "PanelShown");
                    }
                    else
                    {
                        _hidden.Add(player.userID);
                        Hide(player);
                        Reply(player, "PanelHidden");
                    }
                    break;
            }
        }

        [ConsoleCommand("moderninfopanel.reload")]
        private void CcmdReload(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg?.Player();
            if (player != null && !permission.UserHasPermission(player.UserIDString, PermAdmin))
                return;

            ReloadAndRedraw();

            if (player != null) Reply(player, "Reloaded");
            else Puts("Configuration reloaded; panels redrawn.");
        }

        private void ReloadAndRedraw()
        {
            LoadConfig();
            StartTimers();
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, GuiRoot);
                Show(player);
            }
        }

        #endregion

        #region UI rendering

        private bool CanSee(BasePlayer player)
            => _config.ShowToEveryone || permission.UserHasPermission(player.UserIDString, PermUse);

        private void Show(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            if (!CanSee(player) || _hidden.Contains(player.userID)) return;

            CuiHelper.DestroyUi(player, GuiRoot);

            var container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = false
            }, "Hud", GuiRoot);

            foreach (Block block in _config.Blocks)
            {
                if (block == null || !block.Enabled) continue;

                string bg = BgName(block.Id);
                container.Add(new CuiPanel
                {
                    Image = { Color = block.BackgroundColor },
                    RectTransform = { AnchorMin = block.AnchorMin, AnchorMax = block.AnchorMax }
                }, GuiRoot, bg);

                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = BuildText(player, block),
                        FontSize = block.FontSize,
                        Align = ParseAnchor(block.Align),
                        Color = block.TextColor
                    },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                }, bg, LabelName(block.Id));
            }

            CuiHelper.AddUi(player, container);
        }

        // Updates only the dynamic labels in place so static backgrounds don't flicker.
        private void RefreshText(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            if (!CanSee(player) || _hidden.Contains(player.userID)) return;

            var container = new CuiElementContainer();
            bool any = false;

            foreach (Block block in _config.Blocks)
            {
                if (block == null || !block.Enabled || !IsDynamic(block)) continue;

                CuiHelper.DestroyUi(player, LabelName(block.Id));
                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = BuildText(player, block),
                        FontSize = block.FontSize,
                        Align = ParseAnchor(block.Align),
                        Color = block.TextColor
                    },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                }, BgName(block.Id), LabelName(block.Id));
                any = true;
            }

            if (any)
                CuiHelper.AddUi(player, container);
        }

        private void Hide(BasePlayer player)
        {
            if (player != null)
                CuiHelper.DestroyUi(player, GuiRoot);
        }

        private string BuildText(BasePlayer player, Block block)
        {
            switch (block.Type?.ToLowerInvariant())
            {
                case "clock":
                    return Lang("ClockLabel", player, FormatClock());
                case "players":
                    return Lang("PlayersLabel", player, BasePlayer.activePlayerList.Count, ConVar.Server.maxplayers);
                case "rotator":
                    if (_config.RotatorMessages == null || _config.RotatorMessages.Count == 0)
                        return string.Empty;
                    return _config.RotatorMessages[_rotatorIndex % _config.RotatorMessages.Count];
                default: // text
                    return string.IsNullOrEmpty(block.Text) ? Lang("Welcome", player) : block.Text;
            }
        }

        private string FormatClock()
        {
            DateTime now;
            if (string.Equals(_config.ClockMode, "gametime", StringComparison.OrdinalIgnoreCase)
                && TOD_Sky.Instance != null)
                now = TOD_Sky.Instance.Cycle.DateTime;
            else
                now = DateTime.Now;

            try { return now.ToString(_config.ClockFormat); }
            catch { return now.ToString("HH:mm"); }
        }

        #endregion

        #region Helpers

        private static string BgName(string id) => GuiRoot + ".bg." + id;
        private static string LabelName(string id) => GuiRoot + ".lbl." + id;

        private static bool IsDynamic(Block block)
        {
            switch (block.Type?.ToLowerInvariant())
            {
                case "clock":
                case "players":
                case "rotator":
                    return true;
                default:
                    return false;
            }
        }

        private static TextAnchor ParseAnchor(string value)
        {
            if (!string.IsNullOrEmpty(value) && Enum.TryParse(value, true, out TextAnchor anchor))
                return anchor;
            return TextAnchor.MiddleCenter;
        }

        #endregion
    }
}
