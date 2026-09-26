using System;
using System.Diagnostics;
using System.Linq;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.Plugin;

public sealed class GatherBuddyIpc : IDisposable
{
    public const int IpcVersion = 3;

    /// <summary> Returned by <see cref="VendorBuyListStart"/> when no list matches the requested name. </summary>
    public const int VendorBuyListNotFound = -1;

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
