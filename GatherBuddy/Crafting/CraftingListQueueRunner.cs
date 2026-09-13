using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Plugin;

namespace GatherBuddy.Crafting;

/// <summary>
/// Chains the queued crafting lists together: it starts one list at a time through
/// <see cref="CraftingListLauncher"/> and waits for <see cref="CraftingGatherBridge"/> to finish
/// that run before starting the next entry. All gathering and crafting behaviour comes from the
/// existing single-list pipeline; this type only decides what runs next.
///
/// An entry's quantity is applied by multiplying every recipe quantity in a snapshot of the list
/// and running that once, NOT by running the list multiple times: with "skip if enough" enabled,
/// a second run would see the first run's output in the inventory and skip everything.
/// For the same reason, the outputs each entry is planned to craft are accumulated in
/// <see cref="_reservedOutputs"/> and hidden from later entries' inventory availability, so two
/// queued lists sharing an item (e.g. the same accessories) each craft their own copy.
/// </summary>
public static class CraftingListQueueRunner
{
    private static bool _running;
    private static int  _entryIndex = -1;
    private static bool _waitingForRunStart;
    private static readonly Dictionary<uint, int> _reservedOutputs = new();

    public static bool Running
        => _running;

    /// <summary>Index into <see cref="CraftingListQueueManager.Entries"/> of the list currently running, or -1.</summary>
    public static int CurrentEntryIndex
        => _running ? _entryIndex : -1;

    public static bool Start()
    {
        if (_running)
        {
            GatherBuddy.Log.Warning("[CraftingListQueueRunner] Queue is already running");
            return false;
        }

        if (CraftingGatherBridge.IsQueueRunning)
        {
            Communicator.PrintError("当前已有制作队列在运行,请先停止后再开始清单队列。");
            return false;
        }

        var queue = GatherBuddy.CraftingListQueue;
        queue.PruneMissingLists();
        if (!queue.Entries.Any(e => !e.Skipping))
        {
            Communicator.PrintError("清单队列中没有可执行的清单。");
            return false;
        }

        _running    = true;
        _entryIndex = -1;
        _reservedOutputs.Clear();
        GatherBuddy.Log.Information($"[CraftingListQueueRunner] Starting crafting list queue with {queue.Entries.Count(e => !e.Skipping)} enabled entries");
        return AdvanceToNextEntry();
    }

    /// <summary>Stops chaining. Does not touch a run that is already in flight.</summary>
    public static void Stop(string? reason = null)
    {
        if (!_running)
            return;

        _running            = false;
        _entryIndex         = -1;
        _waitingForRunStart = false;
        _reservedOutputs.Clear();
        GatherBuddy.Log.Information($"[CraftingListQueueRunner] Queue stopped{(reason == null ? string.Empty : $": {reason}")}");
    }

    /// <summary>Called when the running list run was aborted from outside (command, status window).</summary>
    internal static void OnExternalStop()
        => Stop("外部停止");

    public static void Update()
    {
        if (!_running)
            return;

        if (CraftingGatherBridge.IsQueueRunning)
        {
            _waitingForRunStart = false;
            return;
        }

        // The launcher was just called but the bridge has not spun up yet; give it a frame.
        if (_waitingForRunStart)
            return;

        AdvanceToNextEntry();
    }

    private static bool AdvanceToNextEntry()
    {
        var queue = GatherBuddy.CraftingListQueue;

        while (true)
        {
            _entryIndex++;
            if (_entryIndex >= queue.Entries.Count)
            {
                GatherBuddy.Log.Information("[CraftingListQueueRunner] Crafting list queue finished");
                Communicator.Print("清单队列已全部完成。");
                Stop();
                return false;
            }

            var entry = queue.Entries[_entryIndex];
            if (entry.Skipping)
                continue;

            if (TryStartEntry(queue, entry))
                return true;
        }
    }

    private static bool TryStartEntry(CraftingListQueueManager queue, CraftingListQueueEntry entry)
    {
        var list = GatherBuddy.CraftingListManager.GetListByID(entry.ListId);
        if (list == null)
        {
            GatherBuddy.Log.Warning($"[CraftingListQueueRunner] Queue entry {_entryIndex} references missing crafting list {entry.ListId}, skipping");
            return false;
        }

        var runList = BuildRunList(list, entry.Quantity);
        GatherBuddy.Log.Information($"[CraftingListQueueRunner] Starting queue entry {_entryIndex + 1}/{queue.Entries.Count}: '{runList.Name}'");
        if (!CraftingListLauncher.TryStart(runList))
        {
            GatherBuddy.Log.Warning($"[CraftingListQueueRunner] Failed to start '{list.Name}', skipping to the next queue entry");
            Communicator.PrintError($"清单 \"{list.Name}\" 无法开始,已跳过。");
            return false;
        }

        ReservePlannedOutputs();
        _waitingForRunStart = true;
        return true;
    }

    /// <summary>
    /// Snapshot of the list carrying the queue-session inventory reservations, with every recipe
    /// quantity multiplied by the entry quantity. Always a snapshot so the persisted list is
    /// never mutated (the reservations must not leak into manual runs of the same list).
    /// </summary>
    private static CraftingListDefinition BuildRunList(CraftingListDefinition list, int quantity)
    {
        var snapshot = list.CreateRetainerPlanningSnapshot();
        snapshot.QueueReservedInventory = new Dictionary<uint, int>(_reservedOutputs);
        if (quantity <= 1)
            return snapshot;

        snapshot.Name = $"{list.Name} x{quantity}";
        foreach (var recipe in snapshot.Recipes)
            recipe.Quantity *= quantity;
        return snapshot;
    }

    /// <summary>
    /// Records what the run that just started is planned to craft, so later entries treat those
    /// inventory items as unavailable instead of counting them as pre-existing stock.
    /// </summary>
    private static void ReservePlannedOutputs()
    {
        var plan = CraftingGatherBridge.GetActiveExecutionPlan();
        if (plan == null)
            return;

        foreach (var item in plan.OriginalRecipesView)
        {
            var recipe = RecipeManager.GetRecipe(item.RecipeId);
            if (!recipe.HasValue)
                continue;

            var resultItemId = recipe.Value.ItemResult.RowId;
            var produced     = item.Quantity * (int)recipe.Value.AmountResult;
            if (produced <= 0)
                continue;

            _reservedOutputs[resultItemId] = _reservedOutputs.GetValueOrDefault(resultItemId) + produced;
        }
    }
}
