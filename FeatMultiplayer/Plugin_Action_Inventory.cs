﻿using BepInEx;
using HarmonyLib;
using MijuTools;
using SpaceCraft;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FeatMultiplayer
{
    public partial class Plugin : BaseUnityPlugin
    {
        /// <summary>
        /// If true, operations affecting the inventory won't send out messages,
        /// avoiding message ping-pong between the parties.
        /// </summary>
        static bool suppressInventoryChange;

        /// <summary>
        /// Helps retarget the client's backpack and equipment ids to the shadow inventory and shadow equipment storages.
        /// </summary>
        /// <param name="hostInventoryId">The id on the host</param>
        /// <param name="clientInventoryId">The shadow id.</param>
        /// <returns>If false, the inventory should not be accessed at all.</returns>
        static bool TryConvertHostToClientInventoryId(int hostInventoryId, out int clientInventoryId)
        {
            // Ignore the Host's own backpack and equipment
            if (hostInventoryId == 1 || hostInventoryId == 2)
            {
                clientInventoryId = 0;
                return false;
            }
            // If it is the shadow inventory/equipment, retarget it to the standard ids for the client
            if (hostInventoryId == shadowInventoryId)
            {
                clientInventoryId = 1;
            }
            else
            if (hostInventoryId == shadowEquipmentId)
            {
                clientInventoryId = 2;
            }
            else
            {
                clientInventoryId = hostInventoryId;
            }
            return true;
        }

        /// <summary>
        /// The vanilla game calls Inventory::AddItem all over the place to try to add to the player's backpack,
        /// equipment, chests, machines, etc. The backpack is at id 1, the equipment is at id 2.
        /// 
        /// On the host, updates to anything but its backpack and inventory is synced back to the client.
        /// The shadow backpack and shadow equipment is renamed to 1 and 2.
        /// </summary>
        /// <param name="__result">Was the original addition successful?</param>
        /// <param name="___worldObjectsInInventory">The full inventory list</param>
        /// <param name="___inventoryId">The id of the inventory being manipulated</param>
        /// <param name="_worldObject">What was added</param>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem))]
        static void Inventory_AddItem(bool __result, List<WorldObject> ___worldObjectsInInventory,
            int ___inventoryId, WorldObject _worldObject)
        {
            if (__result && updateMode != MultiplayerMode.SinglePlayer && !suppressInventoryChange)
            {
                int iid = ___inventoryId;

                if (updateMode == MultiplayerMode.CoopHost)
                {
                    if (!TryConvertHostToClientInventoryId(iid, out iid))
                    {
                        return;
                    }
                }
                var mia = new MessageInventoryAdded()
                {
                    inventoryId = iid,
                    itemId = _worldObject.GetId(),
                    groupId = _worldObject.GetGroup().GetId()
                };
                LogInfo("InventorAddItem: " + iid + ", " + _worldObject.GetId() + ", " + _worldObject.GetGroup().GetId());
                Send(mia);
                Signal();

                // remove from the client's inventory so that only the Host can add it back
                if (updateMode == MultiplayerMode.CoopClient)
                {
                    ___worldObjectsInInventory.Remove(_worldObject);
                }
            }
        }


        /// <summary>
        /// The vanilla game calls Inventory::RemoveItem all over the place to remove items from inventories.
        /// 
        /// On the host, we have things to do after the call, see <see cref="Inventory_RemoveItem(int, WorldObject, bool)"/>.
        /// 
        /// On the client, we have to intercept before the removal so we can retarget and ask the host to remove the item
        /// for us. This avoids issues with the full sync process.
        /// 
        /// We also use the <see cref="suppressInventoryChange"/> so that local inventory changes bounced back from the other side
        /// do not create message ping-ponging.
        /// </summary>
        /// <param name="___inventoryId">The inventory id where the object would be removed</param>
        /// <param name="_worldObject">The world object to remove</param>
        /// <param name="_destroyWorldObject">If true, the world object is destroyed, such as when it was consumed or used for building.</param>
        /// <returns>False if on client and syncing is enabled.</returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem))]
        static bool Inventory_RemoveItem_Pre(int ___inventoryId, WorldObject _worldObject, bool _destroyWorldObject)
        {
            if (updateMode == MultiplayerMode.CoopClient && !suppressInventoryChange)
            {
                var mir = new MessageInventoryRemoved()
                {
                    inventoryId = ___inventoryId,
                    itemId = _worldObject.GetId(),
                    destroy = _destroyWorldObject
                };
                LogInfo("InventoryRemoveItemPre: " + ___inventoryId + ", " + _worldObject.GetId() + ", " + _worldObject.GetGroup().GetId());
                Send(mir);
                Signal();
                return false;
            }
            return true;
        }

        /// <summary>
        /// The vanilla game calls Inventory::RemoveItem all over the place to remove items from inventories.
        /// 
        /// On the host, we let the original method run and then notify the cleint about the inventory change.
        /// The shadow backpack and shadow equipment is renamed to 1 and 2.
        /// 
        /// On the client, we do nothing additional to what <see cref="Inventory_RemoveItem(int, WorldObject, bool)"/> does.
        /// </summary>
        /// <param name="___inventoryId">The inventory id where the object would be removed</param>
        /// <param name="_worldObject">The world object to remove</param>
        /// <param name="_destroyWorldObject">If true, the world object is destroyed, such as when it was consumed or used for building.</param>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem))]
        static void Inventory_RemoveItem(int ___inventoryId, WorldObject _worldObject, bool _destroyWorldObject)
        {
            if (updateMode == MultiplayerMode.CoopHost && !suppressInventoryChange)
            {
                int iid = ___inventoryId;
                if (updateMode == MultiplayerMode.CoopHost)
                {
                    if (!TryConvertHostToClientInventoryId(iid, out iid))
                    {
                        return;
                    }
                }

                var mir = new MessageInventoryRemoved()
                {
                    inventoryId = iid,
                    itemId = _worldObject.GetId(),
                    destroy = _destroyWorldObject
                };
                LogInfo("InventoryRemoveItemPost: " + iid + ", " + _worldObject.GetId() + ", " + _worldObject.GetGroup().GetId());
                Send(mir);
                Signal();
            }
        }

        /// <summary>
        /// The vanilla game uses Inventory::AutoSort when the user clicks on the sort button in the inventory screen.
        /// 
        /// On the host, the shadow backpack and shadow equipment is renamed to 1 and 2.
        ///
        /// On both sides, we let the sort happen, then ask the other party to sort their inventory.
        /// 
        /// This avoids forgetting the sort if the client rejoins or the host changes something in the inventory.
        /// </summary>
        /// <param name="___inventoryId">The target inventory id</param>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.AutoSort))]
        static void Inventory_AutoSort(int ___inventoryId)
        {
            if (!suppressInventoryChange)
            {
                int iid = ___inventoryId;
                if (updateMode == MultiplayerMode.CoopHost) 
                { 
                    if (!TryConvertHostToClientInventoryId(iid, out iid))
                    {
                        return;
                    }
                }

                var msi = new MessageSortInventory()
                {
                    inventoryId = iid
                };
                Send(msi);
                Signal();
            }
        }

        static void ClientConsumeRecipe(GroupConstructible gc)
        {
            var inv = InventoriesHandler.GetInventoryById(shadowInventoryId);

            var toRemove = new List<Group>() { gc };
            if (inv.ContainsItems(toRemove))
            {
                inv.RemoveItems(toRemove, true, false);
            }
            else
            {
                toRemove.Clear();
                toRemove.AddRange(gc.GetRecipe().GetIngredientsGroupInRecipe());
                inv.RemoveItems(toRemove, true, false);
            }
        }
        static void ReceiveMessageInventoryAdded(MessageInventoryAdded mia)
        {
            int targetId = mia.inventoryId;
            if (targetId == 1 && shadowInventoryId != 0)
            {
                targetId = shadowInventoryId;
            }
            else
            if (targetId == 2 && shadowEquipmentId != 0)
            {
                targetId = shadowEquipmentId;
            }
            if (targetId == 2 && updateMode == MultiplayerMode.CoopClient)
            {

                suppressInventoryChange = true;
                try
                {
                    WorldObject wo = WorldObjectsHandler.GetWorldObjectViaId(mia.itemId);
                    if (wo != null)
                    {
                        var _playerController = GetPlayerMainController();
                        _playerController.GetPlayerEquipment()
                            .AddItemInEquipment(wo);
                        LogError("ReceiveMessageInventoryAdded: Add Equipment " + mia.itemId + ", " + mia.groupId);

                        _playerController.GetPlayerEquipment()
                            .GetInventory()
                            .RefreshDisplayerContent();
                    }
                    else
                    {
                        LogError("ReceiveMessageInventoryAdded: Add Equipment missing WorldObject " + mia.itemId + ", " + mia.groupId);
                    }
                }
                finally
                {
                    suppressInventoryChange = false;
                }
                return;
            }
            var inv = InventoriesHandler.GetInventoryById(targetId);
            if (inv != null)
            {
                WorldObject wo = WorldObjectsHandler.GetWorldObjectViaId(mia.itemId);
                if (wo == null)
                {
                    wo = WorldObjectsHandler.CreateNewWorldObject(GroupsHandler.GetGroupViaId(mia.groupId), mia.itemId);
                }
                suppressInventoryChange = updateMode == MultiplayerMode.CoopClient;
                try
                {
                    inv.AddItem(wo);
                    LogInfo("ReceiveMessageInventoryAdded: " + mia.inventoryId + " <= " + mia.itemId + ", " + mia.groupId);
                    inv.RefreshDisplayerContent();
                }
                finally
                {
                    suppressInventoryChange = false;
                }
            }
            else
            {
                LogInfo("ReceiveMessageInventoryAdded: Uknown inventory " + mia.inventoryId + " <= " + mia.itemId + ", " + mia.groupId);
            }
        }

        static void ReceiveMessageInventoryRemoved(MessageInventoryRemoved mia)
        {
            LogInfo("ReceiveMessageInventoryRemoved - Begin");
            int targetId = mia.inventoryId;
            if (targetId == 1 && shadowInventoryId != 0)
            {
                targetId = shadowInventoryId;
            }
            else
            if (targetId == 2 && shadowEquipmentId != 0)
            {
                targetId = shadowEquipmentId;
            }
            if (targetId == 2 && updateMode == MultiplayerMode.CoopClient)
            {

                suppressInventoryChange = true;
                try
                {
                    WorldObject wo = WorldObjectsHandler.GetWorldObjectViaId(mia.itemId);
                    if (wo != null)
                    {
                        var _playerController = GetPlayerMainController();
                        _playerController.GetPlayerEquipment()
                            .RemoveItemFromEquipment(wo);
                        LogInfo("ReceiveMessageInventoryRemoved: Remove Equipment " + mia.itemId + ", " + wo.GetGroup().GetId());
                    }
                    else
                    {
                        LogError("ReceiveMessageInventoryRemoved: Remove Equipment missing WorldObject " + mia.itemId);
                    }
                }
                finally
                {
                    suppressInventoryChange = false;
                }
            }
            else
            {
                var inv = InventoriesHandler.GetInventoryById(targetId);
                if (inv != null)
                {
                    WorldObject wo = WorldObjectsHandler.GetWorldObjectViaId(mia.itemId);
                    if (wo != null)
                    {
                        suppressInventoryChange = updateMode == MultiplayerMode.CoopClient;
                        try
                        {
                            inv.RemoveItem(wo, mia.destroy);
                            LogInfo("ReceiveMessageInventoryRemoved: " + mia.inventoryId + " <= " + mia.itemId + ", " + wo.GetGroup().GetId());
                        }
                        finally
                        {
                            suppressInventoryChange = false;
                        }
                    }
                    else
                    {
                        LogWarning("ReceiveMessageInventoryRemoved: Unknown item " + mia.inventoryId + " <= " + mia.itemId);
                    }
                }
                else
                {
                    LogWarning("ReceiveMessageInventoryRemoved: Unknown inventory " + mia.inventoryId + " <= " + mia.itemId);
                }
            }
            LogInfo("ReceiveMessageInventoryRemoved - End");
        }

        static void ReceiveMessageInventories(MessageInventories minv)
        {
            if (updateMode == MultiplayerMode.CoopClient)
            {
                //LogInfo("Received all inventories");

                HashSet<int> toDelete = new HashSet<int>();
                Dictionary<int, Inventory> localInventories = new Dictionary<int, Inventory>();
                foreach (Inventory inv in InventoriesHandler.GetAllInventories())
                {
                    int id = inv.GetId();
                    if (!localInventories.ContainsKey(id))
                    {
                        localInventories[id] = inv;
                        toDelete.Add(id);
                    }
                }

                suppressInventoryChange = true;
                try
                {
                    foreach (WorldInventory wi in minv.inventories)
                    {
                        toDelete.Remove(wi.id);
                        localInventories.TryGetValue(wi.id, out var inv);
                        if (inv == null)
                        {
                            //LogInfo("ReceiveMessageInventories: Creating new inventory " + wi.id + " of size " + wi.size);
                            inv = InventoriesHandler.CreateNewInventory(wi.size, wi.id);
                        }
                        else
                        {
                            // Don't resize the player's inventory/equipment
                            if (wi.id > 2)
                            {
                                inv.SetSize(wi.size);
                            }
                            //LogInfo("ReceiveMessageInventories: Updating inventory " + wi.id + ", " + wi.size + ", " + wi.itemIds.Count);
                        }

                        SyncInventory(inv, wi);
                    }
                }
                finally
                {
                    suppressInventoryChange = false;
                }

                List<Inventory> inventories = InventoriesHandler.GetAllInventories();
                for (int i = inventories.Count - 1; i >= 0; i--)
                {
                    Inventory inv = inventories[i];
                    if (toDelete.Contains(inv.GetId()))
                    {
                        //LogInfo("ReceiveMessageInventories: Removing inventory " + inv.GetId());
                        inventories.RemoveAt(i);
                    }
                }

                //LogInfo("Received all inventories - Done");
            }
        }

        static void SyncInventory(Inventory inv, WorldInventory wi)
        {
            List<WorldObject> worldObjects = inv.GetInsideWorldObjects();

            // see if there was an inventory composition change
            HashSet<int> currentIds = new HashSet<int>();
            foreach (WorldObject obj in worldObjects)
            {
                currentIds.Add(obj.GetId());
            }
            HashSet<int> newIds = new HashSet<int>();
            foreach (int id in wi.itemIds)
            {
                newIds.Add(id);
            }

            bool changed = false;
            bool once = true;
            for (int i = worldObjects.Count - 1; i >= 0; i--)
            {
                var woi = worldObjects[i];
                int id = woi.GetId();
                if (!newIds.Contains(id))
                {
                    worldObjects.RemoveAt(i);
                    inv.inventoryContentModified?.Invoke(woi, false);
                    currentIds.Remove(id);
                    if (once)
                    {
                        once = false;
                        LogInfo("ReceiveMessageInventories: " + inv.GetId() + " changed");
                    }
                    LogInfo("ReceiveMessageInventories:   Removed " + DebugWorldObject(woi));
                    changed = true;
                }
            }
            foreach (int id in wi.itemIds)
            {
                if (!currentIds.Contains(id))
                {
                    if (worldObjectById.TryGetValue(id, out var wo))
                    {
                        worldObjects.Add(wo);
                        inv.inventoryContentModified?.Invoke(wo, true);
                        if (once)
                        {
                            once = false;
                            LogInfo("ReceiveMessageInventories: " + inv.GetId() + " changed");
                        }
                        LogInfo("ReceiveMessageInventories:   Added " + DebugWorldObject(wo));
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                if (wi.id == 2)
                {
                    var _playerController = GetPlayerMainController();
                    // Reset some equipment stats:

                    // Apply equipment stats
                    HashSet<DataConfig.EquipableType> equipTypes = new HashSet<DataConfig.EquipableType>();
                    foreach (WorldObject wo in worldObjects)
                    {
                        TryApplyEquipment(wo, _playerController, equipTypes);
                    }

                    UnapplyEquipment(equipTypes, _playerController);
                }
                inv.RefreshDisplayerContent();
            }
        }

        /// <summary>
        /// Since we don't go the usual way of equipping and unequipping equimpent when the client joins
        /// or manipulates it equipments, we need to apply the equipment effects manually.
        /// </summary>
        /// <param name="wo">The equipment object</param>
        /// <param name="_playerController">The player's object</param>
        /// <param name="equippables">Output as to what was equipped. Used for disabling certain equipment effects.</param>
        static void TryApplyEquipment(WorldObject wo,
            PlayerMainController _playerController,
            ICollection<DataConfig.EquipableType> equippables)
        {
            if (wo.GetGroup() is GroupItem groupItem)
            {
                var equipType = groupItem.GetEquipableType();
                if (!equippables.Contains(equipType))
                {
                    equippables.Add(equipType);
                }
                switch (equipType)
                {
                    case DataConfig.EquipableType.BackpackIncrease:
                        {
                            _playerController.GetPlayerBackpack()
                                .GetInventory()
                                .SetSize(12 + groupItem.GetGroupValue());
                            break;
                        }
                    case DataConfig.EquipableType.EquipmentIncrease:
                        {
                            _playerController.GetPlayerEquipment()
                                .GetInventory()
                                .SetSize(4 + groupItem.GetGroupValue());
                            break;
                        }
                    case DataConfig.EquipableType.OxygenTank:
                        {
                            _playerController.GetPlayerGaugesHandler()
                                .UpdateGaugesDependingOnEquipment(
                                    _playerController.GetPlayerEquipment()
                                    .GetInventory()
                                );
                            break;
                        }
                    case DataConfig.EquipableType.MultiToolMineSpeed:
                        {
                            _playerController.GetMultitool()
                                .GetMultiToolMine()
                                .SetMineTimeReducer(groupItem.GetGroupValue());
                            break;
                        }
                    case DataConfig.EquipableType.BootsSpeed:
                        {
                            _playerController.GetPlayerMovable()
                                .SetMoveSpeedChangePercentage((float)groupItem.GetGroupValue());
                            break;
                        }
                    case DataConfig.EquipableType.Jetpack:
                        {
                            _playerController.GetPlayerMovable()
                                .SetJetpackFactor((float)groupItem.GetGroupValue() / 100f);
                            break;
                        }
                    case DataConfig.EquipableType.MultiToolLight:
                        {
                            _playerController.GetMultitool()
                                .SetCanUseLight(true, groupItem.GetGroupValue());
                            break;
                        }

                    case DataConfig.EquipableType.MultiToolDeconstruct:
                        {
                            _playerController.GetMultitool().AddEnabledState(DataConfig.MultiToolState.Deconstruct);
                            break;
                        }
                    case DataConfig.EquipableType.MultiToolBuild:
                        {
                            _playerController.GetMultitool().AddEnabledState(DataConfig.MultiToolState.Build);
                            break;
                        }
                    case DataConfig.EquipableType.CompassHUD:
                        {
                            Managers.GetManager<CanvasCompass>().SetStatus(true);
                            break;
                        }
                }
            }
        }

        static void UnapplyEquipment(HashSet<DataConfig.EquipableType> equipTypes, PlayerMainController player)
        {
            var mt = player.GetMultitool();
            // unequip
            if (!equipTypes.Contains(DataConfig.EquipableType.MultiToolBuild))
            {
                if (mt.HasEnabledState(DataConfig.MultiToolState.Build))
                {
                    mt.RemoveEnabledState(DataConfig.MultiToolState.Build);
                }
            }
            if (!equipTypes.Contains(DataConfig.EquipableType.MultiToolDeconstruct))
            {
                if (mt.HasEnabledState(DataConfig.MultiToolState.Deconstruct))
                {
                    mt.RemoveEnabledState(DataConfig.MultiToolState.Deconstruct);
                }
            }
            if (!equipTypes.Contains(DataConfig.EquipableType.MultiToolLight))
            {
                if ((bool)playerMultitoolCanUseLight.GetValue(mt))
                {
                    mt.SetCanUseLight(false, 1);
                }
            }
            if (!equipTypes.Contains(DataConfig.EquipableType.CompassHUD))
            {
                var cc = Managers.GetManager<CanvasCompass>();
                cc.SetStatus(false);
            }
            if (!equipTypes.Contains(DataConfig.EquipableType.OxygenTank))
            {
                player.GetPlayerGaugesHandler()
                                .UpdateGaugesDependingOnEquipment(
                                    player.GetPlayerEquipment()
                                    .GetInventory()
                                );
            }
            if (!equipTypes.Contains(DataConfig.EquipableType.BootsSpeed))
            {
                player.GetPlayerMovable()
                                .SetMoveSpeedChangePercentage(0f);
            }
            if (!equipTypes.Contains(DataConfig.EquipableType.Jetpack))
            {
                player.GetPlayerMovable()
                                .SetJetpackFactor(0f);
            }
            if (!equipTypes.Contains(DataConfig.EquipableType.MultiToolMineSpeed))
            {
                player.GetMultitool()
                                .GetMultiToolMine()
                                .SetMineTimeReducer(0);
            }
            // FIXME backpack and equipment mod unequipping
            /*
            float dropDistance = 0.7f;
            if (!equipTypes.Contains(DataConfig.EquipableType.BackpackIncrease))
            {
                Inventory inv = player.GetPlayerBackpack().GetInventory();
                inv.SetSize(12);
                var point = player.GetAimController().GetAimRay().GetPoint(dropDistance);
                
            }
            if (!equipTypes.Contains(DataConfig.EquipableType.EquipmentIncrease))
            {
                Inventory inv = player.GetPlayerBackpack().GetInventory();
                inv.SetSize(4);
                var point = player.GetAimController().GetAimRay().GetPoint(dropDistance);
            }
            */
        }

        static void ReceiveMessageSortInventory(MessageSortInventory msi)
        {
            int targetId = msi.inventoryId;
            if (targetId != 2)
            {
                // retarget shadow inventory
                if (targetId == 1 && updateMode == MultiplayerMode.CoopHost)
                {
                    targetId = shadowInventoryId;
                }
                Inventory inv = InventoriesHandler.GetInventoryById(targetId);
                if (inv != null)
                {
                    suppressInventoryChange = true;
                    try
                    {
                        inv.AutoSort();
                    }
                    finally
                    {
                        suppressInventoryChange = false;
                    }
                }
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryAssociated), nameof(InventoryAssociated.GetInventory))]
        static bool InventoryAssociated_GetInventory(InventoryAssociated __instance,
            ref Inventory ___inventory,
            ref Inventory __result
        )
        {
            if (updateMode == MultiplayerMode.CoopClient)
            {
                if (___inventory == null)
                {
                    InventoryFromScene component = __instance.GetComponent<InventoryFromScene>();
                    if (component != null)
                    {
                        int iid = component.GetInventoryGeneratedId();
                        Inventory inventoryById = InventoriesHandler.GetInventoryById(iid);
                        if (inventoryById != null)
                        {
                            __instance.SetInventory(inventoryById);
                            __result = ___inventory;
                        }
                        else
                        {
                            Inventory inventory = InventoriesHandler.CreateNewInventory(component.GetSize(), iid);
                            __instance.SetInventory(inventory);
                            __result = ___inventory;
                            var scene = SceneManager.GetActiveScene();
                            LogInfo("InventoryAssociated_GetInventory: Request host spawn " + iid + ", Size = " + component.GetSize());
                            Send(new MessageInventorySpawn() { inventoryId = iid });
                            Signal();
                        }
                    }
                    else
                    {
                        LogInfo("InventoryAssociated_GetInventory: No InventoryFromScene?!");
                    }
                }
                else
                {
                    __result = ___inventory;
                }

                return false;
            }
            return true;
        }

        static void FindAndGenerateInventoryFromScene(int iid)
        {
            foreach (var sceneGo in FindObjectsOfType<GameObject>())
            {
                var sceneInventory = sceneGo.GetComponentInChildren<InventoryFromScene>();
                if (sceneInventory != null)
                {
                    int sid = sceneInventory.GetInventoryGeneratedId();
                    //LogInfo("ReceiveMessageInventorySpawn: Found " + sid);
                    if (sid == iid)
                    {
                        Inventory inventoryById = InventoriesHandler.GetInventoryById(iid);
                        if (inventoryById == null)
                        {
                            List<Group> generatedGroups = sceneInventory.GetGeneratedGroups();
                            LogInfo("ReceiveMessageInventorySpawn: InventoryFromScene generated = " + iid + ", Count = " + generatedGroups.Count);
                            Inventory inventory = InventoriesHandler.CreateNewInventory(sceneInventory.GetSize(), iid);

                            Send(new MessageInventorySize() { inventoryId = iid, size = sceneInventory.GetSize() });
                            Signal();

                            foreach (Group group in generatedGroups)
                            {
                                WorldObject worldObject = WorldObjectsHandler.CreateNewWorldObject(group, 0);
                                SendWorldObject(worldObject, false);
                                inventory.AddItem(worldObject);
                            }
                        }
                        else
                        {
                            LogWarning("ReceiveMessageInventorySpawn: InventoryFromScene already instantiated = " + iid);
                        }
                        return;
                    }
                }
            }
            LogWarning("ReceiveMessageInventorySpawn: InventoryFromScene not found: " + iid + ", (sector not loaded?)");
        }

        /// <summary>
        /// The vanilla game uses SectorEnter::Start to set up a collision box around load boundaries (sectors).
        /// 
        /// On the host, we track the other player and load in the respective sector.
        /// 
        /// On the client, we don't care where the host is?!
        /// </summary>
        /// <param name="__instance">The parent SectorEnter to register a coroutine tracker on.</param>
        /// <param name="___collider">The collision box object around the sector</param>
        /// <param name="___sector">The sector itself so we know what scene to load via <see cref="SceneManager.LoadSceneAsync(int, LoadSceneMode)"/></param>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SectorEnter), "Start")]
        static void SectorEnter_Start(SectorEnter __instance, Collider ___collider, Sector ___sector)
        {
            __instance.StartCoroutine(SectorEnter_TrackOtherPlayer(___collider, ___sector));
        }

        /// <summary>
        /// How often to check the collisions with the other player.
        /// </summary>
        static float sectorTrackDelay = 0.5f;
        /// <summary>
        /// If the player triggered a scene load, defer the spawns.
        /// </summary>
        static bool sceneLoadingForSpawn;
        /// <summary>
        /// What inventory generation requests are pending due to scene loading.
        /// </summary>
        static HashSet<int> sceneSpawnRequest = new();

        static IEnumerator SectorEnter_TrackOtherPlayer(Collider ___collider, Sector ___sector)
        {
            for (; ; )
            {
                if (updateMode == MultiplayerMode.CoopHost && otherPlayer != null)
                {
                    if (___collider.bounds.Contains(otherPlayer.avatar.transform.position))
                    {
                        string name = ___sector.gameObject.name;
                        Scene scene = SceneManager.GetSceneByName(name);
                        if (!scene.IsValid())
                        {
                            sceneLoadingForSpawn = true;
                            LogInfo("SectorEnter_TrackOtherPlayer: Loading Scene " + name);
                            var aop = SceneManager.LoadSceneAsync(name, LoadSceneMode.Additive);
                            aop.completed += (op) => OnSceneLoadedForSpawn(op, ___sector, name);
                        }
                    }
                }
                yield return new WaitForSeconds(sectorTrackDelay);
            }
        }

        static void OnSceneLoadedForSpawn(AsyncOperation op, Sector ___sector, string name)
        {
            try
            {
                sectorSceneLoaded.Invoke(___sector, new object[] { op });    
            } 
            finally
            {
                sceneLoadingForSpawn = false;
            }
            LogInfo("SectorEnter_TrackOtherPlayer: Loading Scene " + name + " done");
            foreach (int spawnId in sceneSpawnRequest)
            {
                FindAndGenerateInventoryFromScene(spawnId);
            }
            sceneSpawnRequest.Clear();
        }

        static void ReceiveMessageInventorySpawn(MessageInventorySpawn mis)
        {
            if (updateMode == MultiplayerMode.CoopHost)
            {
                int iid = mis.inventoryId;

                if (sceneLoadingForSpawn)
                {
                    LogInfo("ReceiveMessageInventorySpawn: Scene loading in progress, resubmit request");
                    //receiveQueue.Enqueue(mis);
                    sceneSpawnRequest.Add(mis.inventoryId);
                }
                else
                {
                    FindAndGenerateInventoryFromScene(iid);
                }
            }
        }

        static void ReceiveMessageInventorySize(MessageInventorySize mis)
        {
            if (updateMode == MultiplayerMode.CoopClient)
            {
                Inventory inv = InventoriesHandler.GetInventoryById(mis.inventoryId);
                if (inv == null)
                {
                    LogInfo("ReceiveMessageInventorySize: Create new inventory " + mis.inventoryId + ", size = " + mis.size);
                    InventoriesHandler.CreateNewInventory(mis.size, mis.inventoryId);
                }
                else
                {
                    LogInfo("ReceiveMessageInventorySize: Update " + mis.inventoryId + ", size = " + inv.GetSize() + " -> " + mis.size);
                    inv.SetSize(mis.size);
                }
            }
        } 
    }
}
