using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public static class CraftingListLauncher
{
    public static bool TryStart(CraftingListDefinition list, bool disableSkipIfEnough = false)
    {
        if (CraftingGatherBridge.IsQueueRunning)
        {
            GatherBuddy.Log.Warning($"[CraftingListLauncher] Cannot start '{list.Name}': another craft run is in flight");
            Communicator.PrintError("制作队列进行中,请先停止当前制作后再开始新的制作。");
            return false;
        }

        if (list.Recipes.Count == 0)
        {
            GatherBuddy.Log.Warning($"[CraftingListLauncher] Cannot start empty list '{list.Name}'");
            return false;
        }

        if (list.QuickSynthAll)
            GatherBuddy.Log.Debug($"[CraftingListLauncher] Quick Synth All active (PreferNQ={list.QuickSynthAllPreferNQ}, PrecraftsOnly={list.QuickSynthAllPrecraftsOnly})");
        var executionPlan = CraftingExecutionPlan.Create(list, disableSkipIfEnough);

        var missingFoods = CollectMissingFoods(executionPlan.QueueView, list.Consumables);
        if (missingFoods.Count > 0)
        {
            var names = string.Join("、", missingFoods.Select(f => $"{GetItemName(f.ItemId)}{(f.HQ ? " (HQ)" : string.Empty)}"));
            GatherBuddy.Log.Warning($"[CraftingListLauncher] Cannot start crafting list '{list.Name}': configured food not available: {names}");
            Communicator.PrintError($"无法开始清单 \"{list.Name}\":背包中缺少设置的食物 {names},且当前没有对应的食物效果。");
            return false;
        }

        GatherBuddy.Log.Information($"[CraftingListLauncher] Starting crafting list '{list.Name}' with {executionPlan.QueueView.Count} crafts from {executionPlan.ResolvedPlan.Recipes.Count} planned recipes");
        CraftingGatherBridge.StartQueueCraftAndGather(
            executionPlan, list.Consumables, list.Ephemeral ? (int?)list.ID : null);
        return true;
    }

    private static List<(uint ItemId, bool HQ)> CollectMissingFoods(
        IReadOnlyList<CraftingListItem> queue,
        CraftingListConsumableSettings? listConsumables)
    {
        var requiredFoods = new HashSet<(uint ItemId, bool HQ)>();
        foreach (var item in queue)
        {
            if (item.Options.Skipping)
                continue;

            var consumables = CraftingContextResolver.BuildConsumableSettings(item, listConsumables);
            if (consumables?.FoodItemId is { } foodItemId)
                requiredFoods.Add((foodItemId, consumables.FoodHQ));
        }

        return requiredFoods
            .Where(f => !ConsumableChecker.HasFoodBuff(f.ItemId)
                && !ConsumableChecker.HasConfiguredConsumableInInventory(f.ItemId, f.HQ))
            .ToList();
    }

    private static string GetItemName(uint itemId)
    {
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet != null && itemSheet.TryGetRow(itemId, out var item))
            return item.Name.ExtractText();

        return $"物品 {itemId}";
    }
}
