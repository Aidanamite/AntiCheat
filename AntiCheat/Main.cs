using HarmonyLib;
using HMLLibrary;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace AntiCheat
{
    public class Main : Mod
    {
        Harmony harmony;
        public static RuleHandling MissingDropItem = RuleHandling.Ban;
        public static RuleHandling StackExceedDropItem = RuleHandling.Ban;
        public static RuleHandling UsesExceedDropItem = RuleHandling.Kick;
        public static RuleHandling MissingEquipItem = RuleHandling.Ban;
        public static RuleHandling MissingEquipSlot = RuleHandling.Ban;
        public static RuleHandling Teleport = RuleHandling.Ban;
        public static RuleHandling OpenTooFar = RuleHandling.Kick;
        public static RuleHandling CloseMissingItem = RuleHandling.Ban;
        public static RuleHandling PickupTooFar = RuleHandling.Kick;
        public static RuleHandling SpawnerSpawn = RuleHandling.Ban;
        public static RuleHandling TooStrong = RuleHandling.Ban;
        public static RuleHandling AttackWithoutWeapon = RuleHandling.Kick;
        public static RuleHandling ShouldveDied = RuleHandling.Kick;
        public static RuleHandling BadMove = RuleHandling.Kick;
        public static RuleHandling Flying = RuleHandling.Kick;
        public static RuleHandling AttackTooFast = RuleHandling.Kick;
        public static RuleHandling BreakTooFast = RuleHandling.Kick;
        public static RuleHandling BlameOtherPlayer = RuleHandling.Ban;
        public static float damageTolerance = 0.5f; // the amount of variation to allow for when calculating the amount of damage a player should've dealt
        public static float minTimeToFullHealth = 100 / 0.75f; // the min amount of time a player could've taken to regen to full health (if constantly regenerating)
        public static float shouldveDiedAfter = 5; // time in seconds of how long to wait for a player to die before assuming they've cheated
        public static Vector3 AllowedSpeed = new Vector3(3, 20, 3); // max speed in meters per second that a player is allowed to travel
        public static Vector3 AllowedTransitionSpeed = new Vector3(10, 20, 10); // max speed in meters per second that a player is allowed to travel when transitioning to or from being on the raft
        public static int maxBlockBreaks = 5; // max blocks a player is allowed to break per second
        public static int maxAttacks = 5; // max times a player is allowed to deal damage per second
        public static float airTimeBeforeFall = 3; // time in seconds of how long a player is allowed in the air before they should've fallen further than they traveled
        public static float maxAirDistanceFromObjects = 1; // the radius in meters to check around the player to check if they're "in the air"
        public static bool LogCheatsFromTheAdmins = false; // if true, whenever an admin is detected cheating, a message is logged like other players breaking rules (without punishment)
        public static Dictionary<CSteamID, string> blacklist = new Dictionary<CSteamID, string>();
        public static List<CSteamID> admins = new List<CSteamID>() { }; // players stored in here are exempt from cheat punishments
        public static string JSONPath = "AntiCheat.json";
        public static Dictionary<Network_Player, List<(float, float)>> damageLogs = new Dictionary<Network_Player, List<(float, float)>>();
        public static List<(Network_Player, float)> expectedDeaths = new List<(Network_Player, float)>();
        public static bool delayedBreak = false;
        public static Dictionary<Network_Player, List<(Message_NetworkBehaviour, MonoBehaviour_ID_Network, CSteamID, float)>> blockBreaks = new Dictionary<Network_Player, List<(Message_NetworkBehaviour, MonoBehaviour_ID_Network, CSteamID, float)>>();
        public static Dictionary<Network_Player, List<float>> attacks = new Dictionary<Network_Player, List<float>>();
        public void Start()
        {
            LoadJSON();
            (harmony = new Harmony("com.aidanamite.AntiCheat")).PatchAll();
            Log("Mod has been loaded!");
        }

        public void OnModUnload()
        {
            harmony.UnpatchAll(harmony.Id);
            Log("Mod has been unloaded!");
        }
        public static void LoadJSON()
        {
            JSONObject Config;
            try
            {
                Config = new JSONObject(File.ReadAllText(JSONPath));
            }
            catch
            {
                Config = new JSONObject();
            }
            blacklist.Clear();
            admins.Clear();
            if (!Config.IsNull)
            {
                if (Config.HasField("bans"))
                {
                    var f = Config.GetField("bans");
                    if (f.IsObject)
                        foreach (var pair in f.ToDictionary())
                            blacklist.Add(new CSteamID(ulong.Parse(pair.Key)), pair.Value);
                }
                if (Config.HasField("admins"))
                {
                    var f = Config.GetField("admins");
                    if (f.IsArray)
                        for (var i = 0; i < f.Count; i++)  
                            admins.Add(new CSteamID(ulong.Parse(f[i].str)));
                }
            }
        }

        public static void SaveJSON()
        {
            Dictionary<string, string> data = new Dictionary<string, string>();
            foreach (var pair in blacklist)
                data.Add(pair.Key.m_SteamID.ToString(), pair.Value);
            var data2 = new List<JSONObject>();
            foreach (var i in admins)
                data2.Add(JSONObject.Create(i.m_SteamID.ToString()));
            var Config = new JSONObject();
            Config.AddField("bans", new JSONObject(data));
            Config.AddField("admins", new JSONObject(data2.ToArray()));
            try
            {
                File.WriteAllText(JSONPath, Config.ToString());
            }
            catch (Exception err)
            {
                Debug.LogError("An error occured while trying to save settings: " + err.Message);
            }
        }

        public static void LogDamage(Network_Player player, float damage)
        {
            if (expectedDeaths.Exists(x => x.Item1 == player))
                return;
            if (!damageLogs.TryGetValue(player, out var values))
                damageLogs.Add(player, values = new List<(float, float)>());
            var time = Time.time;
            values.Add((damage, time));
            values.RemoveAll(x => time - x.Item2 > minTimeToFullHealth);
            var fake = 0f;
            foreach (var i in values)
                if (i.Item1 >= 100 || (fake += i.Item1) >= 100)
                {
                    damageLogs.Remove(player);
                    expectedDeaths.Add((player, time));
                    return;
                }
        }

        void Update()
        {
            var time = Time.time;
            foreach (var death in expectedDeaths.ToArray())
                if (time  - death.Item2 >= shouldveDiedAfter)
                {
                    expectedDeaths.Remove(death);
                    if (ShouldveDied.Handle(death.Item1.steamID)) // Checks if the amount of time passed since the player should've died is too high. If it has, the player probably blocked the death using a mod
                        Debug.LogError($"User {death.Item1.steamID} should've died");
                }
            delayedBreak = true;
            foreach (var pair in blockBreaks)
                foreach (var message in pair.Value.ToArray())
                    if (time - message.Item4 >= 1)
                        try 
                        {
                            message.Item2.Deserialize(message.Item1, message.Item3);
                        } catch (Exception e) { Debug.LogError(e); }
            delayedBreak = false;
        }

        static FieldInfo _ni = typeof(NetworkIDManager).GetField("networkIDs", ~BindingFlags.Default);
        public static Dictionary<Type, Dictionary<NetworkIDTag, HashSet<MonoBehaviour_ID_Network>>> AllNetworkIds => (Dictionary<Type, Dictionary<NetworkIDTag, HashSet<MonoBehaviour_ID_Network>>>)_ni.GetValue(null);
    }

    static class ExtentionMethods
    {
        public static void DisconnectUser(this Raft_Network network, CSteamID steamID)
        {
            network.SendP2P(steamID, new Message_DisconnectNotify(Messages.Disconnect_Notify, network.LocalSteamID, DisconnectReason.HostDisconnected, true), EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Session);
            network.RPCExclude(new Message_DisconnectNotify(Messages.Disconnect_Notify, steamID, DisconnectReason.SelfDisconnected, true), Target.Other, steamID, EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Session);
            Traverse.Create(network).Method("OnDisconnect", new object[] { steamID, DisconnectReason.SelfDisconnected, true }).GetValue();
        }
        public static string DisconnectUser(this Raft_Network network, string Username)
        {
            var pair = network.GetUser(Username);
            if (pair.Value == null)
                return "";
            network.DisconnectUser(pair.Key);
            return pair.Value.characterSettings.Name;
        }
        public static void BanUser(this Raft_Network network, CSteamID steamID)
        {
            Main.blacklist.Add(steamID, network.GetPlayerFromID(steamID).characterSettings.Name);
            Main.SaveJSON();
            network.DisconnectUser(steamID);
        }
        public static string BanUser(this Raft_Network network, string Username)
        {
            var pair = network.GetUser(Username);
            if (pair.Value == null)
                return "";
            network.BanUser(pair.Key);
            return pair.Value.characterSettings.Name;
        }
        public static KeyValuePair<CSteamID, Network_Player> GetUser(this Raft_Network network, string Username)
        {
            ulong.TryParse(Username, out var id);
            var flags = GetFlags(ref Username);
            foreach (var pair in network.remoteUsers)
                if (id == pair.Key.m_SteamID || Username.Compare(pair.Value.characterSettings.Name, flags.Item1, flags.Item2))
                    return pair;
            return default;
        }
        public static void ClearBans()
        {
            Main.blacklist.Clear();
            Main.SaveJSON();
        }
        public static bool IsBanned(this CSteamID steamID)
        {
            foreach (var iD in Main.blacklist.Keys)
                if (iD.m_SteamID == steamID.m_SteamID)
                    return true;
            return false;
        }
        public static bool Compare(this string str, string checkstring, bool start, bool end)
        {
            if (start && end)
                return str.Contains(checkstring);
            else if (end)
                return str.StartsWith(checkstring);
            else if (start)
                return str.EndsWith(checkstring);
            else
                return str == checkstring;
        }
        public static (bool, bool) GetFlags(ref string str)
        {
            bool flag1 = false;
            bool flag2 = false;
            if (str[0] == '*')
            {
                flag1 = true;
                str = str.Substring(1);
            }
            if (str[str.Length - 1] == '*')
            {
                flag2 = true;
                str = str.Substring(0, str.Length - 1);
            }
            return (flag1, flag2);
        }
        public static string UnbanUser(string name)
        {
            ulong.TryParse(name, out var id);
            (bool, bool) flags = GetFlags(ref name);
            foreach (var pair in Main.blacklist)
                if (id == pair.Key.m_SteamID || pair.Value.Compare(name, flags.Item1, flags.Item2))
                {
                    Main.blacklist.Remove(pair.Key);
                    Main.SaveJSON();
                    return pair.Value;
                }
            return "";
        }

        static FieldInfo _tc = typeof(Throwable_Object).GetField("throwableComponent", ~BindingFlags.Default);
        public static ThrowableComponent ThrowableComponent(this Throwable_Object obj) => (ThrowableComponent)_tc.GetValue(obj);
    }

    public class RuleHandling
    {
        int handling;
        Func<CSteamID,bool> customHandle;
        public RuleHandling(Func<CSteamID, bool> CustomHandle)
        {
            handling = 3;
            customHandle = CustomHandle;
        }
        RuleHandling() { }
        public static RuleHandling DoNothing => new RuleHandling() { handling = 0 };
        public static RuleHandling Kick => new RuleHandling() { handling = 1 };
        public static RuleHandling Ban => new RuleHandling() { handling = 2 };
        public bool Handle(CSteamID user)
        {
            if (user == default || user == ComponentManager<Raft_Network>.Value.HostID)
                return false;
            if (Main.admins.Contains(user))
                return Main.LogCheatsFromTheAdmins;
            if (handling == 0)
                return false;
            if (handling == 1)
            {
                ComponentManager<Raft_Network>.Value.DisconnectUser(user);
                return true;
            }
            if (handling == 2)
            {
                ComponentManager<Raft_Network>.Value.BanUser(user);
                return true;
            }
            if (handling == 3)
            {
                try
                {
                    return customHandle(user);
                } catch { }
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Raft_Network), "CanUserJoinMe")]
    public class Patch_ConnectionAttempt
    {
        static void Postfix(ref InitiateResult __result, CSteamID remoteID)
        {
            if (__result == InitiateResult.Success && remoteID.IsBanned())
                __result = InitiateResult.Fail_NotFriendWithHost;
        }
    }

    [HarmonyPatch(typeof(PickupObjectManager),"Deserialize")]
    static class Patch_PickupObjectManager_Message
    {
        static bool Prefix(PickupObjectManager __instance, Message_NetworkBehaviour msg, CSteamID remoteID)
        {
            if (Raft_Network.IsHost)
            {
                if (msg.Type == Messages.DropItem)
                {
                    var message = msg as Message_DropItem;
                    var item = ItemManager.GetItemByIndex(message.rgdInstance.itemIndex);
                    if (item == null) // Checks if the dropped item exists on the server. If it doesn't, it's probably a modded item
                    {
                        if (Main.MissingDropItem.Handle(remoteID))
                            Debug.LogError($"User {remoteID} dropped an item ({message.rgdInstance.itemIndex}) that does not exist");
                    }
                    else if (item.MaxUses < message.rgdInstance.uses && Main.UsesExceedDropItem.Handle(remoteID)) // Checks if the dropped item has more uses remaining than the max. If it does, the item has probably been modded
                        Debug.LogError($"User {remoteID} dropped an item that has more remaining uses than the item allows");
                    else if ((item.settings_Inventory.StackSize < message.rgdInstance.amount || (!item.settings_Inventory.Stackable && message.rgdInstance.amount > 1)) && Main.StackExceedDropItem.Handle(remoteID)) // Checks if the stack is larger than the max stack size. If it is, it was either spawned or modded
                        Debug.LogError($"User {remoteID} dropped an item stack greater than the amount allowed for the item");
                    else
                        return true;
                    return false;
                }
                else if (msg.Type == Messages.RemoveItem)
                {
                    var message = msg as Message_PickupObjectManager_RemoveItem;
                    var p = ComponentManager<Raft_Network>.Value.remoteUsers[remoteID];
                    var o = NetworkIDManager.GetNetworkIDFromObjectIndex<PickupItem_Networked>(message.itemObjectIndex);
                    if (o != null && (o.transform.position - p.transform.position).magnitude > Mathf.Max(Player.UseDistanceDefault, Player.UseDistanceThirdperson) * 2f && Main.PickupTooFar.Handle(remoteID)) // Checks if the player tried to pickup an item from further than they should be able to
                    {
                        Debug.LogError($"User {remoteID} tried to pickup an item from too far away");
                        return false;
                    }
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(PlayerEquipment), "EquipItemNetwork")]
    static class Patch_PlayerEquipment_Equip
    {
        static void Prefix(PlayerEquipment __instance, Equipment[] ___equipment, int equipmentIndex, Network_Player ___playerNetwork)
        {
            if (Raft_Network.IsHost && equipmentIndex >= ___equipment.Length && Main.MissingEquipSlot.Handle(___playerNetwork.steamID)) // Checks if player equipped an item into a slot they don't have
                Debug.LogError($"User {___playerNetwork.steamID} equipped into a slot that does not exist");
        }
    }

    [HarmonyPatch(typeof(PlayerItemManager), "Deserialize")]
    static class Patch_PlayerItemManager_Message
    {
        static void Prefix(PlayerItemManager __instance, Message_NetworkBehaviour msg, CSteamID remoteID)
        {
            if (Raft_Network.IsHost && msg is Message_SelectItem)
            {
                var message = msg as Message_SelectItem;
                var item = ItemManager.GetItemByIndex(message.itemIndex);
                if (message.itemIndex != -1 && item == null && Main.MissingEquipItem.Handle(remoteID)) // Checks if the player equipped an item that does not exist
                    Debug.LogError($"User {remoteID} equipped an item ({message.itemIndex}) that does not exist");
            }
        }
    }

    [HarmonyPatch(typeof(NetworkIDManager), "Deserialize")]
    static class Patch_NetworkIDManager_Message
    {
        static bool Prefix(NetworkIDManager __instance, Message_NetworkBehaviour msg, CSteamID remoteID)
        {
            if (Raft_Network.IsHost && msg.Type == Messages.Teleport && Main.Teleport.Handle(remoteID)) // Catches if the player tries to teleport, either themself or another player
            {
                var message = msg as Message_Teleport;
                Debug.LogError($"User {remoteID} tried to teleport {(remoteID == message.SteamID ? "theirself" : $"another player ({message.SteamID})")} to {message.Position}");
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Network_Player), "Deserialize")]
    static class Patch_NetworkPlayer_Message
    {
        static Dictionary<Network_Player, (Vector3, bool, float)> lastPos = new Dictionary<Network_Player, (Vector3, bool, float)>();
        static Dictionary<Network_Player, (Vector3, float)> fallStart = new Dictionary<Network_Player, (Vector3, float)>();
        static bool Prefix(Network_Player __instance, Message_NetworkBehaviour msg, CSteamID remoteID)
        {
            if (Raft_Network.IsHost && !Main.delayedBreak)
            {
                if (remoteID != default && remoteID != ComponentManager<Raft_Network>.Value.HostID && remoteID != __instance.steamID && Main.BlameOtherPlayer.Handle(remoteID)) // Checks if one player tries to tell the server that another player did something. Trying to "blame" them
                {
                    Debug.LogError($"User {remoteID} blamed {__instance.steamID} for a {msg.Type} message (Network_Player)");
                    return false;
                }
                if (msg.Type == Messages.PlayerKilled)
                {
                    Main.expectedDeaths.RemoveAll(x => x.Item1 == __instance);
                    Main.damageLogs.Remove(__instance);
                } else if (msg.Type == Messages.Update)
                {
                    var message = msg as Message_Player_Update;
                    if (__instance.PlayerScript.IsDead || message.Anim_FullBodyIndex == (int)PlayerFullBodyAnimation.Ziplining_4 || message.Anim_FullBodyIndex == (int)PlayerFullBodyAnimation.BoarCarried_2)
                        lastPos.Remove(__instance);
                    else
                    {
                        var time = Time.time;
                        if (lastPos.TryGetValue(__instance, out var value) && !PossibleMovement(message.Position, value.Item1, ((message.RaftAsParent == value.Item2) ? Main.AllowedSpeed : Main.AllowedTransitionSpeed) * (time - value.Item3)) && Main.BadMove.Handle(__instance.steamID)) // Checks if the player's movement exceeds the allowed speed
                        {
                            lastPos.Remove(__instance);
                            Debug.LogError($"User {__instance.steamID} moved too quickly");
                            return false;
                        }
                        lastPos[__instance] = (message.Position, message.RaftAsParent, time);
                    }

                    if (__instance.PlayerScript.IsDead || message.ControllerType == ControllerType.Water || message.Anim_FullBodyIndex != (int)PlayerFullBodyAnimation.None_0 || Physics.CheckSphere(__instance.FeetPosition, Main.maxAirDistanceFromObjects, LayerMasks.MASK_GroundMask))
                        fallStart.Remove(__instance);
                    else if (fallStart.TryGetValue(__instance, out var value))
                    {
                        if (Time.time - value.Item2 > Main.airTimeBeforeFall && new Vector2(value.Item1.x, value.Item1.z).magnitude > -value.Item1.y && Main.Flying.Handle(__instance.steamID)) // Checks if the player's movement in the air is invalid (assumed to be "flying"). This works through checking how long they were in the air before they started falling, and their displacement relative to the point where they started falling
                        {
                            fallStart.Remove(__instance);
                            Debug.LogError($"User {__instance.steamID} was determined to be flying");
                            return false;
                        }
                    }
                    else
                        fallStart[__instance] = (__instance.FeetPosition, Time.time);
                }
                else if (msg.Type == Messages.Axe_RemoveBlock)
                {
                    var time = Time.time;
                    if (Main.blockBreaks.TryGetValue(__instance, out var values))
                    {
                        values.Add((msg, __instance, remoteID, time));
                        if (values.Count(x => time - x.Item4 < 1) > Main.maxBlockBreaks && Main.BreakTooFast.Handle(__instance.steamID)) // Checks if the number of blocks the player broke in the last second exceeds a particular value
                        {
                            Main.blockBreaks.Remove(__instance);
                            Debug.LogError($"User {__instance.steamID} broke blocks faster than they should have");
                            return false;
                        }
                    }
                    else
                        Main.blockBreaks[__instance] = new List<(Message_NetworkBehaviour, MonoBehaviour_ID_Network, CSteamID, float)>() { (msg, __instance, remoteID, time) };
                }
            }
            return true;
        }

        static bool PossibleMovement(Vector3 current, Vector3 last, Vector3 allowed)
        {
            var diff = current - last;
            if (diff.x < 0)
                diff.x = -diff.x;
            if (diff.y < 0)
                diff.y = -diff.y;
            if (diff.z < 0)
                diff.z = -diff.z;
            return diff.x <= allowed.x && diff.y <= allowed.y && diff.z <= allowed.z;

        }
    }

    [HarmonyPatch(typeof(BlockCreator), "Deserialize")]
    static class Patch_BlockCreator_Message
    {
        static bool Prefix(BlockCreator __instance, Message_NetworkBehaviour msg, CSteamID remoteID)
        {
            if (Raft_Network.IsHost && !Main.delayedBreak)
            {
                var p = __instance.GetPlayerNetwork();
                if (remoteID != default && remoteID != ComponentManager<Raft_Network>.Value.HostID && p.steamID != remoteID && Main.BlameOtherPlayer.Handle(remoteID)) // Checks if one player tries to tell the server that another player did something. Trying to "blame" them
                {
                    Debug.LogError($"User {remoteID} blamed {p.steamID} for a {msg.Type} message (BlockCreator)");
                    return false;
                }
                if (msg.Type == Messages.BlockCreator_RemoveBlock)
                {
                    var time = Time.time;
                    if (Main.blockBreaks.TryGetValue(p, out var values))
                    {
                        values.Add((msg, __instance, remoteID, time));
                        if (values.Count(x => time - x.Item4 < 1) > Main.maxBlockBreaks && Main.BreakTooFast.Handle(p.steamID)) // Checks if the number of blocks the player broke in the last second exceeds a particular value
                        {
                            Main.blockBreaks.Remove(p);
                            Debug.LogError($"User {p.steamID} broke blocks faster than they should have");
                            return false;
                        }
                    }
                    else
                        Main.blockBreaks[p] = new List<(Message_NetworkBehaviour, MonoBehaviour_ID_Network, CSteamID, float)>() { (msg, __instance, remoteID, time) };
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(StorageManager), "Deserialize")]
    static class Patch_StorageManager_Message
    {
        static bool Prefix(StorageManager __instance, Message_NetworkBehaviour msg, CSteamID remoteID)
        {
            if (Raft_Network.IsHost)
            {
                if (msg.Type == Messages.StorageManager_Open)
                {
                    var message = msg as Message_Storage;
                    var s = __instance.GetStorageByObjectIndex(message.storageObjectIndex);
                    var p = ComponentManager<Raft_Network>.Value.remoteUsers[remoteID];
                    if ((s.transform.position - p.FeetPosition).magnitude > Mathf.Max(Player.UseDistanceDefault, Player.UseDistanceThirdperson) * 2f && Main.OpenTooFar.Handle(remoteID)) // Checks if the player tried to open a storage from outside their reach
                    {
                        Debug.LogError($"User {remoteID} tried to open a {s.buildableItem.settings_Inventory.DisplayName} from too far away");
                        return false;
                    }
                }
                else if (msg.Type == Messages.StorageManager_Close)
                {
                    var message = msg as Message_Storage_Close;
                    var item = message.slots.FirstOrDefault(x => ItemManager.GetItemByIndex(x.itemIndex) == null);
                    if (item != null && Main.CloseMissingItem.Handle(remoteID)) // Checks if the player put an item in a chest that does not exist on the server. If they did, it's probably a modded item
                    {
                        Debug.LogError($"User {remoteID} put an item ({item.itemIndex}) that does not exist in a {__instance.GetStorageByObjectIndex(message.storageObjectIndex).buildableItem.settings_Inventory.DisplayName}");
                        return false;
                    }
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(ObjectSpawnerManager), "Deserialize")]
    static class Patch_ObjectSpawnerManager_Message
    {
        static bool Prefix(ObjectSpawnerManager __instance, Message_NetworkBehaviour msg, CSteamID remoteID)
        {
            if (Raft_Network.IsHost)
            {
                if (msg.Type == Messages.ObjectSpawner_SpawnItem && Main.SpawnerSpawn.Handle(remoteID)) // Catches if a player tries to tell the server that an object spawned. This should never happen
                {
                    Debug.LogError($"User {remoteID} tried to spawn an item using the ObjectSpawnerManager");
                    return false;
                }
                if (msg.Type == Messages.ObjectSpawner_Create && Main.SpawnerSpawn.Handle(remoteID)) // Catches if a player tries to tell the server that an object spawned. This should never happen
                {
                    Debug.LogError($"User {remoteID} tried to spawn multiple items using the ObjectSpawnerManager");
                    return false;
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Network_Host_Entities), "Deserialize")]
    static class Patch_NetworkHostEntities_Message
    {
        static bool Prefix(Network_Host_Entities __instance, Message_NetworkBehaviour msg, CSteamID remoteID)
        {
            if (Raft_Network.IsHost)
            {
                if (msg.Type == Messages.DamageEntity)
                {
                    var message = msg as Message_NetworkEntity_Damage;
                    var entity = NetworkIDManager.GetNetworkIDFromObjectIndex<Network_Entity>(message.entityObjectIndex);
                    var p = ComponentManager<Raft_Network>.Value.remoteUsers[remoteID];
                    if (p.Stats != entity)
                    {
                        var held = Traverse.Create(p.PlayerItemManager.useItemController).Field("activeObject").GetValue<ItemConnection>();
                        var obj = held == null ? null : held.objs.Concat(new[] { held.obj }).FirstOrDefault(x => x && x.activeInHierarchy && x.GetComponentInChildren<MeleeWeapon>())?.GetComponentInChildren<MeleeWeapon>();
                        if (obj)
                        {
                            var cal = GetDamage(entity, Traverse.Create(obj).Field("damage").GetValue<int>(), message.damageInflictorEntityType);
                            if (!CouldBeDealt(cal.Item1, message.damage, cal.Item2) && Main.TooStrong.Handle(remoteID)) // Checks if the player dealt more damage than the melee weapon they're holding can do
                            {
                                Debug.LogError($"User {remoteID} dealt more damage than they should have");
                                return false;
                            }
                            var time = Time.time;
                            if (Main.attacks.TryGetValue(p, out var values))
                            {
                                values.Add(time);
                                values.RemoveAll(x => time - x > 1);
                                if (values.Count > Main.maxAttacks && Main.AttackTooFast.Handle(remoteID)) // Checks if the player is attacking too rapidly. If the number of melee attacks in the last second was too high
                                {
                                    Main.attacks.Remove(p);
                                    Debug.LogError($"User {remoteID} dealt attacks faster than they should have");
                                    return false;
                                }
                            }
                            else
                                Main.attacks[p] = new List<float>() { time };
                        }
                        else if (!Main.AllNetworkIds.Any(
                            x => typeof(Throwable_Object).IsAssignableFrom(x.Key) && x.Value.Any(
                                y => y.Value.Any(
                                    z => z && z.GetComponent<Throwable_Object>()?.ThrowableComponent()?.playerNetwork == entity)))
                            && Main.AttackWithoutWeapon.Handle(remoteID)) // Checks if the player dealt damage without a weapon, either melee or ranged
                        {
                            Debug.LogError($"User {remoteID} dealt damage without a weapon in hand");
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        static bool CouldBeDealt(float calculated, float told, bool allowForMultipliers)
        {
            if (Mathf.Abs(calculated - told) <= Main.damageTolerance)
                return true;
            if (allowForMultipliers)
            {
                if (Mathf.Abs(calculated * 2 - told) <= Main.damageTolerance)
                    return true;
                if (Mathf.Abs(calculated * 3 - told) <= Main.damageTolerance)
                    return true;
            }
            return false;
        }

        static (float, bool) GetDamage(Network_Entity entity, float damage, EntityType damageInflictorEntityType)
        {
            float num = damage;
            var flag = false;
            if (entity.entityType == EntityType.Player && damageInflictorEntityType == EntityType.Player)
            {
                if (!GameManager.FriendlyFire)
                    return default;
                flag = true;
            }
            if (entity.entityType == EntityType.Enemy && GameModeValueManager.GetCurrentGameModeValue().playerSpecificVariables.negateOutgoingPlayerDamage)
                return default;
            if (entity.entityType == EntityType.Player && damageInflictorEntityType == EntityType.Enemy)
                num *= GameModeValueManager.GetCurrentGameModeValue().playerSpecificVariables.damageTakenMultiplier;
            else if (entity.entityType == EntityType.Enemy && damageInflictorEntityType == EntityType.Player)
                num *= GameModeValueManager.GetCurrentGameModeValue().playerSpecificVariables.outgoingDamageMultiplierPVE;
            return (num, flag);
        }
    }

    [HarmonyPatch(typeof(Stat_Health), "ChangeValue")]
    static class Patch_StatHealth_ChangeValue
    {
        static void Prefix(Stat_Health __instance, float diff)
        {
            if (Raft_Network.IsHost)
                foreach(var u in ComponentManager<Raft_Network>.Value.remoteUsers)
                    if (u.Value.Stats.stat_health == __instance)
                        Main.LogDamage(u.Value, diff);
        }
    }
}