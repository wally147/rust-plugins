using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Database;
using Oxide.Core.MySql.Libraries;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("TopOnline", "Goldteam", "4.2.0")]
    [Description("Strictly MySQL playtime tracker without debug messages.")]
    public class TopOnline : RustPlugin
    {
        private Dictionary<ulong, double> currentSessionStart = new Dictionary<ulong, double>();

        private MySql _sqlLib;
        private Connection _sqlConn;
        private bool _dbReady;

        private class PlayerData
        {
            public ulong SteamId;
            public string Name;
            public double TotalMinutes;
        }

        #region Config

        private PluginConfig _config;

        private class PluginConfig
        {
            [JsonProperty("MySQL Host")] public string Host = "37.60.246.4";
            [JsonProperty("MySQL Port")] public int Port = 3306;
            [JsonProperty("MySQL Database")] public string Database = "goldteam_rust";
            [JsonProperty("MySQL Username")] public string Username = "goldteam_rust";
            [JsonProperty("MySQL Password")] public string Password = "ScUydc8AZabLyk7QkAT6";
            [JsonProperty("TopOnline Table Name")] public string TableName = "top_online";
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { _config = Config.ReadObject<PluginConfig>(); if (_config == null) throw new Exception(); }
            catch { LoadDefaultConfig(); }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Hooks

        private void Init()
        {
            if (_config == null) LoadDefaultConfig();
        }

        private void OnServerInitialized()
        {
            TryOpenDatabase();
            timer.Every(300f, SaveAllPlayers);
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            currentSessionStart[player.userID] = GetCurrentTimeMinutes();
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player == null) return;
            SyncPlayerTimeWithDatabase(player);
            currentSessionStart.Remove(player.userID);
        }

        private void Unload()
        {
            SaveAllPlayers();
            try { if (_sqlLib != null && _sqlConn != null) _sqlLib.CloseDb(_sqlConn); } catch { }
        }

        #endregion

        #region DB + Core 

        private void TryOpenDatabase()
        {
            _dbReady = false;

            _sqlLib = Interface.Oxide.GetLibrary<MySql>();
            if (_sqlLib == null)
            {
                PrintError("MySQL library is not available.");
                return;
            }

            try
            {
                _sqlConn = _sqlLib.OpenDb(_config.Host, _config.Port, _config.Database, _config.Username, _config.Password, this);
                _dbReady = _sqlConn != null;

                if (_dbReady)
                {
                    Puts($"TopOnline: MySQL connection opened successfully to {_config.Database}");
                    foreach (var player in BasePlayer.activePlayerList)
                    {
                        OnPlayerConnected(player);
                    }
                }
            }
            catch (Exception ex)
            {
                PrintError($"Failed to open MySQL connection: {ex.Message}");
            }
        }

        private double GetCurrentTimeMinutes()
        {
            return DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalMinutes;
        }

        private void SaveAllPlayers()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                SyncPlayerTimeWithDatabase(player);
            }
        }

        private void SyncPlayerTimeWithDatabase(BasePlayer player)
        {
            if (player == null || !currentSessionStart.ContainsKey(player.userID) || !_dbReady) return;

            double start = currentSessionStart[player.userID];
            double now = GetCurrentTimeMinutes();
            double sessionLength = now - start;

            if (sessionLength > 0 && _sqlLib != null && _sqlConn != null)
            {
                var sqlText = $@"
                    INSERT INTO `{_config.TableName}` (`steamid`, `playtime`, `name`) 
                    VALUES (@0, @1, @2) 
                    ON DUPLICATE KEY UPDATE `playtime` = `playtime` + @1, `name` = @2;
                ";

                var sql = Sql.Builder.Append(sqlText, player.userID.ToString(), sessionLength, player.displayName);
                
                _sqlLib.ExecuteNonQuery(sql, _sqlConn, response => { /* success */ });
            }

            currentSessionStart[player.userID] = now;
        }

        #endregion

        #region Command & UI

        [ChatCommand("toponline")]
        private void CmdTopOnline(BasePlayer player, string cmd, string[] args)
        {
            if (player == null) return;

            if (!_dbReady)
            {
                player.ChatMessage("TopOnline: Database connection is not ready.");
                return;
            }

            SyncPlayerTimeWithDatabase(player);

            var sqlText = $"SELECT `steamid`, `playtime`, `name` FROM `{_config.TableName}` ORDER BY `playtime` DESC LIMIT 10;";
            var sql = Sql.Builder.Append(sqlText);
            
            _sqlLib.Query(sql, _sqlConn, list =>
            {
                if (player == null || !player.IsConnected) return;
                if (list == null) return;

                List<PlayerData> top10 = new List<PlayerData>();
                foreach (var row in list)
                {
                    if (ulong.TryParse(row["steamid"].ToString(), out ulong steamId))
                    {
                        top10.Add(new PlayerData
                        {
                            SteamId = steamId,
                            TotalMinutes = Convert.ToDouble(row["playtime"]),
                            Name = row["name"].ToString()
                        });
                    }
                }

                ShowTopOnlineUI(player, top10);
            });
        }

        private void ShowTopOnlineUI(BasePlayer player, List<PlayerData> top10)
        {
            CuiHelper.DestroyUi(player, "TopOnlineUI");

            var container = new CuiElementContainer();

            container.Add(new CuiPanel
            {
                Image = { Color = "0.05 0.05 0.05 0.85", Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat" },
                RectTransform = { AnchorMin = "0.3 0.15", AnchorMax = "0.7 0.85" },
                CursorEnabled = true
            }, "Overlay", "TopOnlineUI");

            container.Add(new CuiPanel
            {
                Image = { Color = "0.15 0.65 0.35 0.9" },
                RectTransform = { AnchorMin = "0 0.9", AnchorMax = "1 1" }
            }, "TopOnlineUI", "HeaderPanel");

            container.Add(new CuiLabel
            {
                Text = { Text = "TOP 10 PLAYTIME", FontSize = 24, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, "HeaderPanel");

            container.Add(new CuiButton
            {
                Button = { Color = "0.8 0.2 0.2 0.9", Command = "toponlineui.close" },
                RectTransform = { AnchorMin = "0.93 0.15", AnchorMax = "0.98 0.85" },
                Text = { Text = "X", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "HeaderPanel");

            container.Add(new CuiLabel { Text = { Text = "#", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "0.7 0.7 0.7 1" }, RectTransform = { AnchorMin = "0.02 0.84", AnchorMax = "0.1 0.88" } }, "TopOnlineUI");
            container.Add(new CuiLabel { Text = { Text = "PLAYER", FontSize = 16, Align = TextAnchor.MiddleLeft, Color = "0.7 0.7 0.7 1" }, RectTransform = { AnchorMin = "0.12 0.84", AnchorMax = "0.6 0.88" } }, "TopOnlineUI");
            container.Add(new CuiLabel { Text = { Text = "PLAYTIME", FontSize = 16, Align = TextAnchor.MiddleRight, Color = "0.7 0.7 0.7 1" }, RectTransform = { AnchorMin = "0.6 0.84", AnchorMax = "0.95 0.88" } }, "TopOnlineUI");

            float rowHeight = 0.075f;
            float startY = 0.83f;
            int rowIndex = 1;

            foreach (var pData in top10)
            {
                string playerName = string.IsNullOrEmpty(pData.Name) ? pData.SteamId.ToString() : pData.Name;

                double hours = Math.Floor(pData.TotalMinutes / 60.0);
                double mins = Math.Floor(pData.TotalMinutes % 60.0);

                float top = startY - (rowIndex - 1) * rowHeight;
                float bottom = top - rowHeight + 0.005f;

                string rowColor = (rowIndex % 2 == 0) ? "1 1 1 0.02" : "1 1 1 0.05";

                container.Add(new CuiPanel
                {
                    Image = { Color = rowColor },
                    RectTransform = { AnchorMin = $"0.02 {bottom}", AnchorMax = $"0.98 {top}" }
                }, "TopOnlineUI", $"Row{rowIndex}");

                container.Add(new CuiLabel
                {
                    Text = { Text = $"{rowIndex}.", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 0.8 0 1" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "0.1 1" }
                }, $"Row{rowIndex}");

                container.Add(new CuiLabel
                {
                    Text = { Text = playerName, FontSize = 16, Align = TextAnchor.MiddleLeft },
                    RectTransform = { AnchorMin = "0.1 0", AnchorMax = "0.6 1" }
                }, $"Row{rowIndex}");

                container.Add(new CuiLabel
                {
                    Text = { Text = $"{hours:F0}h {mins:F0}m", FontSize = 16, Align = TextAnchor.MiddleRight, Color = "0.8 0.8 0.8 1" },
                    RectTransform = { AnchorMin = "0.6 0", AnchorMax = "0.97 1" }
                }, $"Row{rowIndex}");

                rowIndex++;
            }

            if (top10.Count == 0)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "Not enough data available.", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "0.6 0.6 0.6 1" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.8" }
                }, "TopOnlineUI");
            }

            CuiHelper.AddUi(player, container);
        }

        [ConsoleCommand("toponlineui.close")]
        private void CmdUIClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, "TopOnlineUI");
        }

        #endregion
    }
}