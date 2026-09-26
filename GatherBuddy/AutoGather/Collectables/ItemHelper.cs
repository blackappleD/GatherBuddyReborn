using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace GatherBuddy.AutoGather.Collectables;

public static class ItemHelper
{
    private static DateTime _lastInventoryManagerFallbackLogTime = DateTime.MinValue;
    public static List<GameInventoryItem> GetCurrentInventoryItems()
    {
        ReadOnlySpan<GameInventoryType> inventoriesToFetch = [
            GameInventoryType.Inventory1, GameInventoryType.Inventory2,
            GameInventoryType.Inventory3, GameInventoryType.Inventory4
        ];

        var inventoryItems = new List<GameInventoryItem>(140);
        for (int i = 0; i < inventoriesToFetch.Length; i++)
        {
            inventoryItems.AddRange(Dalamud.GameInventory.GetInventoryItems(inventoriesToFetch[i]));
        }
        return inventoryItems;
    }
    

    private static readonly InventoryType[] InventoryAndArmoryTypes =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.Crystals,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead, InventoryType.ArmoryBody,
        InventoryType.ArmoryHands, InventoryType.ArmoryWaist,
        InventoryType.ArmoryLegs, InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
        InventoryType.ArmorySoulCrystal,
    ];

    // InventoryManager.GetInventoryItemCount does not reliably include armory chest gear,
    // so the containers are scanned slot by slot instead.
    public static unsafe int GetInventoryAndArmoryItemCount(uint itemId, bool includeEquipped = false)
    {
        try
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager != null)
            {
                var baseItemId = itemId >= 1_000_000 ? itemId - 1_000_000 : itemId;
                var total      = CountItemInContainers(inventoryManager, baseItemId, InventoryAndArmoryTypes);
                if (includeEquipped)
                    total += CountItemInContainers(inventoryManager, baseItemId, [InventoryType.EquippedItems]);
                return total;
            }

            LogInventoryManagerFallback($"[物品助手] 计数物品 {itemId} 时 InventoryManager 不可用，回退到仅背包计数");
        }
        catch (Exception ex)
        {
            LogInventoryManagerFallback($"[物品助手] 用 InventoryManager 计数物品 {itemId} 失败: {ex.Message}");
        }

        return GetCurrentInventoryItems()
            .Where(item => item.BaseItemId == itemId)
            .Sum(item => (int)item.Quantity);
    }

    private static unsafe int CountItemInContainers(InventoryManager* inventoryManager, uint baseItemId, ReadOnlySpan<InventoryType> types)
    {
        var hqItemId = baseItemId + 1_000_000;
        var total    = 0;
        foreach (var type in types)
        {
            var container = inventoryManager->GetInventoryContainer(type);
            if (container == null)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    continue;

                if (slot->ItemId == baseItemId || slot->ItemId == hqItemId)
                    total += (int)slot->Quantity;
            }
        }

        return total;
    }

    public static List<Item> GetLuminaItemsFromInventory()
    {
        List<Item> luminaItems = new List<Item>();
        var inventoryItems = GetCurrentInventoryItems();
    
        foreach (var invItem in inventoryItems)
        {
            var luminaItem = Dalamud.GameData.GetExcelSheet<Item>().FirstOrDefault(i => i.RowId == invItem.BaseItemId);
            if (luminaItem.RowId != 0)
                luminaItems.Add(luminaItem);
        }
        return luminaItems;
    }

    private static void LogInventoryManagerFallback(string message)
    {
        if ((DateTime.UtcNow - _lastInventoryManagerFallbackLogTime).TotalSeconds < 5)
            return;

        _lastInventoryManagerFallbackLogTime = DateTime.UtcNow;
        GatherBuddy.Log.Debug(message);
    }
}
