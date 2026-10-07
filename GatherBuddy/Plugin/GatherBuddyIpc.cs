using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Interfaces;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.Plugin;

public sealed class GatherBuddyIpc : IDisposable
{
    public const int IpcVersion = 6;

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
