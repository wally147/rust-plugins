using System;
using System.Collections;
using System.Collections.Generic;
using Oxide.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace Oxide.Plugins
{
    [Info("WallPoster", "GoldTeam", "1.1.0")]
    [Description("Places an image on a wall. Max 3 per player, only in authorized bases.")]
    public class WallPoster : RustPlugin
    {
        // --- DATA STORAGE FOR LIMITS ---
        private StoredData data;

        private class StoredData
        {
            // Key: Player SteamID, Value: List of Poster Entity IDs
            public Dictionary<ulong, List<ulong>> PlayerPosters = new Dictionary<ulong, List<ulong>>();
        }

        private void Init()
        {
            try
            {
                data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>("WallPosterData") ?? new StoredData();
            }
            catch
            {
                data = new StoredData();
            }
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject("WallPosterData", data);
        }

        // -------------------------------

        [ChatCommand("poster")]
        private void CmdPoster(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0)
            {
                player.ChatMessage("<color=red>Error!</color> Usage: /poster <image_link.jpg/.png>");
                return;
            }

            // --- 1. TC PRIVILEGE CHECK (OWN BASE) ---
            BuildingPrivlidge tc = player.GetBuildingPrivilege();
            if (tc == null || !tc.IsAuthed(player))
            {
                player.ChatMessage("<color=red>Error!</color> You must be authorized on the Tool Cupboard to place a poster here!");
                return;
            }

            // --- 2. LIMIT CHECK (MAX 3) ---
            ulong userId = player.userID;
            if (!data.PlayerPosters.ContainsKey(userId))
            {
                data.PlayerPosters[userId] = new List<ulong>();
            }

            // Clean up any posters that were destroyed while the server was offline
            data.PlayerPosters[userId].RemoveAll(id => BaseNetworkable.serverEntities.Find(new NetworkableId(id)) == null);
            SaveData();

            if (data.PlayerPosters[userId].Count >= 3)
            {
                player.ChatMessage("<color=red>Limit Reached!</color> You can only have a maximum of 3 posters. Break an old one to place a new one.");
                return;
            }

            // --- 3. RAYCAST (LOOKING AT WALL) ---
            string url = args[0];
            RaycastHit hit;
            if (!Physics.Raycast(player.eyes.HeadRay(), out hit, 5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                player.ChatMessage("You need to stand closer and look directly at a wall!");
                return;
            }

            BuildingBlock block = hit.GetEntity() as BuildingBlock;
            if (block == null)
            {
                player.ChatMessage("<color=red>Error!</color> You must look at a building wall!");
                return;
            }

            player.ChatMessage("Pasting poster... Please wait!");

            // --- 4. SPAWN FRAME ---
            Vector3 spawnPos = hit.point + (hit.normal * 0.02f);
            Quaternion spawnRot = Quaternion.LookRotation(hit.normal);

            string prefab = "assets/prefabs/deployable/signs/sign.pictureframe.landscape.prefab";
            BaseEntity entity = GameManager.server.CreateEntity(prefab, spawnPos, spawnRot);
            
            if (entity == null)
            {
                player.ChatMessage("Error generating the object!");
                return;
            }

            entity.OwnerID = player.userID; // Set ownership
            entity.Spawn();

            Signage sign = entity as Signage;
            if (sign != null)
            {
                sign.SetFlag(BaseEntity.Flags.Locked, true); 
                sign.SendNetworkUpdate();

                // Add to player's poster count and save
                data.PlayerPosters[userId].Add(sign.net.ID.Value);
                SaveData();

                // --- 5. DOWNLOAD IMAGE ---
                ServerMgr.Instance.StartCoroutine(DownloadImage(url, sign, player));
            }
        }

        // --- IMAGE DOWNLOAD COROUTINE ---
        private IEnumerator DownloadImage(string url, Signage sign, BasePlayer player)
        {
            using (UnityWebRequest www = UnityWebRequest.Get(url))
            {
                www.downloadHandler = new DownloadHandlerBuffer();
                yield return www.SendWebRequest();

                if (www.result != UnityWebRequest.Result.Success || www.downloadHandler.data == null || www.downloadHandler.data.Length == 0)
                {
                    if (player != null && player.IsConnected)
                        player.ChatMessage("<color=red>Error!</color> Failed to download the image. Check the link.");
                    
                    if (sign != null && !sign.IsDestroyed)
                        sign.Kill(); // This will trigger OnEntityKill and remove it from the limit
                    
                    yield break;
                }

                if (sign != null && !sign.IsDestroyed)
                {
                    byte[] bytes = www.downloadHandler.data;
                    uint crc = FileStorage.server.Store(bytes, FileStorage.Type.png, sign.net.ID);
                    
                    if (sign.textureIDs == null) 
                    {
                        sign.textureIDs = new uint[1];
                    }
                    
                    sign.textureIDs[0] = crc;
                    sign.SendNetworkUpdate();

                    if (player != null && player.IsConnected)
                        player.ChatMessage("<color=green>Poster successfully pasted!</color> (" + data.PlayerPosters[player.userID].Count + "/3)");
                }
            }
        }

        // --- AUTOMATIC LIMIT CLEANUP WHEN POSTER IS DESTROYED ---
        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity is Signage && data != null)
            {
                ulong id = entity.net.ID.Value;
                
                // Find and remove the poster from whichever player owned it
                foreach (var kvp in data.PlayerPosters)
                {
                    if (kvp.Value.Contains(id))
                    {
                        kvp.Value.Remove(id);
                        SaveData();
                        break;
                    }
                }
            }
        }
    }
}