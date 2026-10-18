using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Crafting;
using GatherBuddy.Interfaces;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.Plugin;

public sealed class GatherBuddyIpc : IDisposable
{
    public const int IpcVersion = 8;

    /// <summary> Returned by <see cref="VendorBuyListStart"/> when no list matches the requested name. </summary>
    public const int VendorBuyListNotFound = -1;

    /// <summary> Returned by <see cref="VendorBuyListReplaceByName"/> when the list name is empty. </summary>
    public const int VendorBuyListInvalidArgument = -2;

    /// <summary> Returned by <see cref="VendorBuyListReplaceByName"/> while a list is running; nothing is changed. </summary>
    public const int VendorBuyListBusy = -3;

    /// <summary> Returned by <see cref="VendorBuyListReplaceByName"/> while vendor data is still loading; nothing is changed. </summary>
    public const int VendorBuyListNotReady = -4;

    /// <summary> Returned by the AutoGatherList methods when no list matches the requested name. </summary>
    public const int AutoGatherListNotFound = -1;

    /// <summary> Returned by <see cref="AutoGatherListSet"/> when the name or the item string is invalid. </summary>
    public const int AutoGatherListInvalidArgument = -2;

    /// <summary> Returned by <see cref="CraftingListReplaceByName"/> when the list name is empty. </summary>
    public const int CraftingListInvalidArgument = -2;

    /// <summary> Returned by <see cref="CraftingListReplaceByName"/> while the crafting list queue is running; nothing is changed. </summary>
    public const int CraftingListBusy = -3;

    private readonly GatherBuddy _plugin;

    public GatherBuddyIpc(GatherBuddy plugin)
    {
        _plugin = plugin;
        EzIPC.Init(this, GatherBuddy.InternalName);
        Debug.Assert(AutoGatherWaiting != null);
        Debug.Assert(AutoGatherEnabledChanged != null);
    }

#pragma warning disable CA1822 // Mark members as static
    [EzIPC]
    public int Version()
        => IpcVersion;

    [EzIPC]
    public uint Identify(string text)
        => _plugin.Executor.Identificator.IdentifyGatherable(text)?.ItemId
         ?? _plugin.Executor.Identificator.IdentifyFish(text)?.ItemId ?? 0;

    [EzIPC]
    public bool IsAutoGatherEnabled()
        => GatherBuddy.AutoGather.Enabled;

    [EzIPC]
    public string GetAutoGatherStatusText()
        => GatherBuddy.AutoGather.AutoStatus;

    [EzIPC]
    public void SetAutoGatherEnabled(bool enabled)
        => GatherBuddy.AutoGather.Enabled = enabled;

    [EzIPC]
    public bool IsAutoGatherWaiting()
        => GatherBuddy.AutoGather.Waiting;

    /// <summary> Names of all vendor buy lists, in display order. </summary>
    [EzIPC]
    public string[] VendorBuyListNames()
        => GatherBuddy.VendorBuyListManager.Lists.Select(list => list.Name).ToArray();

    /// <summary>
    /// Starts the vendor buy list with the given name (case-insensitive); an empty name starts the active list.
    /// Returns a <see cref="VendorBuyListManager.StartResult"/> value, or <see cref="VendorBuyListNotFound"/>.
    /// </summary>
    [EzIPC]
    public int VendorBuyListStart(string listName)
    {
        var manager = GatherBuddy.VendorBuyListManager;
        if (string.IsNullOrWhiteSpace(listName))
            return (int)manager.Start();

        var list = FindVendorBuyList(listName);
        return list == null
            ? VendorBuyListNotFound
            : (int)manager.Start(list.Id);
    }

    /// <summary>
    /// Number of enabled entries still below their target quantity; an empty name uses the active list.
    /// Returns <see cref="VendorBuyListNotFound"/> when no list matches.
    /// </summary>
    [EzIPC]
    public int VendorBuyListPendingCount(string listName)
    {
        var manager = GatherBuddy.VendorBuyListManager;
        var list = string.IsNullOrWhiteSpace(listName)
            ? manager.ActiveList
            : FindVendorBuyList(listName);
        return list == null
            ? VendorBuyListNotFound
            : manager.GetPendingEntryCount(list);
    }

    private static VendorBuyListDefinition? FindVendorBuyList(string listName)
    {
        var name = listName.Trim();
        return GatherBuddy.VendorBuyListManager.Lists
            .FirstOrDefault(l => string.Equals(l.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }

    [EzIPC]
    public void VendorBuyListStop()
        => GatherBuddy.VendorBuyListManager.Stop();

    /// <summary> True while a list is running or still leaving / cancelling a vendor interaction. </summary>
    [EzIPC]
    public bool VendorBuyListIsBusy()
        => GatherBuddy.VendorBuyListManager.IsBusy || GatherBuddy.VendorPurchaseManager.IsRunning;

    [EzIPC]
    public string VendorBuyListStatusText()
        => GatherBuddy.VendorBuyListManager.StatusText;

    /// <summary> Outcome of the most recent run as a <see cref="VendorBuyListManager.RunOutcome"/> value, and whether it ran out of currency. </summary>
    [EzIPC]
    public (int Outcome, bool HitCurrencyLimit) VendorBuyListLastRun()
        => ((int)GatherBuddy.VendorBuyListManager.LastRunOutcome, GatherBuddy.VendorBuyListManager.LastRunHitCurrencyLimit);

    /// <summary>
    /// Creates the vendor buy list with the given name, or replaces all entries of an existing one (case-insensitive match).
    /// The active list is not changed. Items whose vendor cannot be resolved are skipped.
    /// Returns the number of entries written, or <see cref="VendorBuyListInvalidArgument"/>, <see cref="VendorBuyListBusy"/>
    /// or <see cref="VendorBuyListNotReady"/>.
    /// </summary>
    [EzIPC]
    public int VendorBuyListReplaceByName(string listName, (uint ItemId, uint TargetQuantity)[] items)
    {
        if (string.IsNullOrWhiteSpace(listName))
            return VendorBuyListInvalidArgument;

        var manager = GatherBuddy.VendorBuyListManager;
        if (manager.IsBusy)
            return VendorBuyListBusy;

        VendorBuyListManager.EnsureVendorCachesAvailable();
        if (!VendorShopResolver.IsInitialized)
            return VendorBuyListNotReady;

        var requests = (items ?? [])
            .Select(item => new VendorBuyListManager.VendorTargetRequest(item.ItemId, item.TargetQuantity))
            .ToArray();
        var count = manager.ReplaceTargets(listName, requests);
        return count < 0 ? VendorBuyListBusy : count;
    }

    /// <summary> Names of all crafting lists. </summary>
    [EzIPC]
    public string[] CraftingListNames()
        => GatherBuddy.CraftingListManager.Lists.Select(list => list.Name).ToArray();

    /// <summary>
    /// Creates the crafting list with the given name, or replaces all recipes of an existing one (case-insensitive match).
    /// <paramref name="items"/> holds the wanted item counts (repeated items are summed); they are converted to crafts using
    /// the recipe yield. Items without a recipe are skipped and returned in Uncraftable; no list is created when nothing is craftable.
    /// Written is the number of recipes in the list, or <see cref="CraftingListInvalidArgument"/> / <see cref="CraftingListBusy"/>.
    /// </summary>
    [EzIPC]
    public (int Written, uint[] Uncraftable) CraftingListReplaceByName(string listName, (uint ItemId, uint Quantity)[] items)
        => ReplaceCraftingList(listName, items, false);

    /// <summary>
    /// Same as <see cref="CraftingListReplaceByName"/>, but sets the list up for NQ crafting: all precrafts use quick synthesis
    /// with NQ materials, and final products (except expert recipes) use quick synthesis or the progress-only solver with NQ
    /// materials. Expert recipe finals keep the default quality handling.
    /// </summary>
    [EzIPC]
    public (int Written, uint[] Uncraftable) CraftingListReplaceByNameNQ(string listName, (uint ItemId, uint Quantity)[] items)
        => ReplaceCraftingList(listName, items, true);

    private static (int Written, uint[] Uncraftable) ReplaceCraftingList(string listName, (uint ItemId, uint Quantity)[] items, bool nq)
    {
        if (string.IsNullOrWhiteSpace(listName))
            return (CraftingListInvalidArgument, []);
        if (CraftingListQueueRunner.Running)
            return (CraftingListBusy, []);

        var itemCounts = new Dictionary<uint, long>();
        foreach (var (itemId, quantity) in items ?? [])
        {
            if (itemId != 0 && quantity != 0)
                itemCounts[itemId] = itemCounts.GetValueOrDefault(itemId) + quantity;
        }

        var recipes     = new List<CraftingListItem>();
        var uncraftable = new List<uint>();
        foreach (var (itemId, count) in itemCounts)
        {
            var recipe = RecipeManager.GetRecipeForItem(itemId);
            if (!recipe.HasValue)
            {
                uncraftable.Add(itemId);
                continue;
            }

            var amountResult = Math.Max(1, (long)recipe.Value.AmountResult);
            var crafts       = (int)Math.Min(int.MaxValue, (count + amountResult - 1) / amountResult);
            var listItem = new CraftingListItem(recipe.Value.RowId, crafts);
            if (nq && !recipe.Value.IsExpert)
            {
                listItem.Options.NQOnly = true;
                listItem.CraftSettings = new RecipeCraftSettings
                {
                    UseAllNQ       = true,
                    SolverOverride = SolverOverrideMode.ProgressOnlySolver,
                };
            }

            recipes.Add(listItem);
        }

        var manager = GatherBuddy.CraftingListManager;
        var name    = listName.Trim();
        var list    = manager.GetListByName(name);
        if (list == null)
        {
            if (recipes.Count == 0)
                return (0, uncraftable.ToArray());

            list = manager.CreateNewList(name);
        }

        list.Recipes = recipes;
        if (nq)
        {
            list.QuickSynthAll              = true;
            list.QuickSynthAllPreferNQ      = true;
            list.QuickSynthAllPrecraftsOnly = true;
            list.UseAllHQ                   = false;
        }

        manager.SaveList(list);
        return (list.Recipes.Count, uncraftable.ToArray());
    }

    /// <summary> Names of all auto-gather lists. </summary>
    [EzIPC]
    public string[] AutoGatherListNames()
        => _plugin.AutoGatherListsManager.Lists.Select(list => list.Name).ToArray();

    /// <summary>
    /// Creates the list with the given name, or replaces all items of an existing one (case-insensitive match).
    /// <paramref name="items"/> is a whitespace, comma or semicolon separated list of "itemIdxQuantity" (or just "itemId" for 1),
    /// e.g. "29673x40 29678x60". "Remove completed items" is switched on; a new list starts disabled.
    /// Returns the number of items in the list, or <see cref="AutoGatherListInvalidArgument"/>.
    /// </summary>
    [EzIPC]
    public int AutoGatherListSet(string listName, string items)
    {
        if (string.IsNullOrWhiteSpace(listName))
        {
            Communicator.PrintError("[GatherBuddy Reborn] AutoGatherListSet: 列表名不能为空。");
            return AutoGatherListInvalidArgument;
        }

        if (!TryParseGatherItems(items, out var parsed, out var error))
        {
            Communicator.PrintError($"[GatherBuddy Reborn] AutoGatherListSet: {error}");
            return AutoGatherListInvalidArgument;
        }

        var manager = _plugin.AutoGatherListsManager;
        var list    = manager.FindList(listName);
        if (list == null)
        {
            list = new AutoGatherList
            {
                Name                 = listName.Trim(),
                RemoveCompletedItems = true,
            };
            foreach (var (item, quantity) in parsed)
                list.Add(item, quantity);
            manager.AddList(list);
        }
        else
        {
            list.RemoveCompletedItems = true;
            manager.ReplaceItems(list, parsed);
        }

        return list.Items.Count;
    }

    /// <summary>
    /// Enables only the given list and remembers which lists were enabled before (kept from the first solo until
    /// <see cref="AutoGatherListRestore"/>). Returns 1 on success, 0 when the list could not be enabled (e.g. missing bait),
    /// or <see cref="AutoGatherListNotFound"/>.
    /// </summary>
    [EzIPC]
    public int AutoGatherListSolo(string listName)
    {
        var list = _plugin.AutoGatherListsManager.FindList(listName ?? string.Empty);
        if (list == null)
            return AutoGatherListNotFound;

        return _plugin.AutoGatherListsManager.SoloList(list) ? 1 : 0;
    }

    /// <summary> Restores the enabled lists from before <see cref="AutoGatherListSolo"/>. Returns false when no solo was active. </summary>
    [EzIPC]
    public bool AutoGatherListRestore()
        => _plugin.AutoGatherListsManager.RestoreSolo();

    [EzIPC]
    public bool AutoGatherListIsSoloActive()
        => _plugin.AutoGatherListsManager.IsSoloActive;

    /// <summary> Deletes the list with the given name. Returns false when no list matches. </summary>
    [EzIPC]
    public bool AutoGatherListRemove(string listName)
    {
        var list = _plugin.AutoGatherListsManager.FindList(listName ?? string.Empty);
        if (list == null)
            return false;

        _plugin.AutoGatherListsManager.DeleteList(list);
        return true;
    }

    private static bool TryParseGatherItems(string? text, out List<(IGatherable Item, uint Quantity)> items, out string error)
    {
        items = [];
        error = string.Empty;
        var seen   = new HashSet<uint>();
        var tokens = (text ?? string.Empty).Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            error = "物品列表为空。";
            return false;
        }

        foreach (var token in tokens)
        {
            var parts = token.Split(['x', 'X', '*'], 2);
            if (!uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var itemId))
            {
                error = $"无法解析物品 ID: '{token}'。";
                return false;
            }

            var quantity = 1u;
            if (parts.Length == 2 && !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out quantity))
            {
                error = $"无法解析数量: '{token}'。";
                return false;
            }

            IGatherable? item = GatherBuddy.GameData.Gatherables.TryGetValue(itemId, out var gatherable)
                ? gatherable
                : GatherBuddy.GameData.Fishes.GetValueOrDefault(itemId);
            if (item == null)
            {
                error = $"物品 {itemId} 不是可采集物或鱼。";
                return false;
            }

            if (!seen.Add(itemId))
            {
                error = $"物品 {itemId} 重复出现。";
                return false;
            }

            items.Add((item, quantity));
        }

        return true;
    }

    [EzIPCEvent]
    public Action AutoGatherWaiting;

    [EzIPCEvent]
    public Action<bool> AutoGatherEnabledChanged;

#pragma warning restore CA1822 // Mark members as static

    public void Dispose()
    {
        // EzIPC disposal is handled in GatherBuddy.cs Dispose method
    }
}
