using System;
using System.Linq;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.Plugin;

namespace GatherBuddy.Crafting;

/// <summary>
/// Chains the queued crafting lists together: it starts one list at a time through
/// <see cref="CraftingListLauncher"/> and waits for <see cref="CraftingGatherBridge"/> to finish
/// that run before starting the next entry. All gathering and crafting behaviour comes from the
/// existing single-list pipeline; this type only decides what runs next.
/// </summary>
public static class CraftingListQueueRunner
{
    private static bool _running;
    private static int  _entryIndex     = -1;
    private static int  _repeatIndex;
    private static int  _repeatTotal;
    private static bool _waitingForRunStart;
    private static bool _disableSkipIfEnough;

    public static bool Running
        => _running;

    /// <summary>Index into <see cref="CraftingListQueueManager.Entries"/> of the list currently running, or -1.</summary>
    public static int CurrentEntryIndex
        => _running ? _entryIndex : -1;

    /// <summary>1-based repetition of the current entry, e.g. 2 of 3.</summary>
    public static int CurrentRepeat
        => _running ? _repeatIndex : 0;

    public static int CurrentRepeatTotal
        => _running ? _repeatTotal : 0;

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
        _disableSkipIfEnough = GatherBuddy.Config.CraftingListQueueDisableSkipIfEnough;
        GatherBuddy.Log.Information($"[CraftingListQueueRunner] Starting crafting list queue with {queue.Entries.Count(e => !e.Skipping)} enabled entries");
        return AdvanceToNextRun();
    }

    /// <summary>Stops chaining. Does not touch a run that is already in flight.</summary>
    public static void Stop(string? reason = null)
    {
        if (!_running)
            return;

        _running            = false;
        _entryIndex         = -1;
        _repeatIndex        = 0;
        _repeatTotal        = 0;
        _waitingForRunStart = false;
        _disableSkipIfEnough = false;
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

        AdvanceToNextRun();
    }

    private static bool AdvanceToNextRun()
    {
        var queue = GatherBuddy.CraftingListQueue;

        while (true)
        {
            if (_entryIndex >= 0 && _repeatIndex < _repeatTotal)
            {
                _repeatIndex++;
                if (TryStartCurrentEntry(queue))
                    return true;

                // Starting this repetition failed; drop the rest of it and move on.
                _repeatIndex = _repeatTotal;
                continue;
            }

            _entryIndex++;
            if (_entryIndex >= queue.Entries.Count)
            {
                GatherBuddy.Log.Information("[CraftingListQueueRunner] Crafting list queue finished");
                Communicator.Print("清单队列已全部完成。");
                if (GatherBuddy.Config.CraftingCompletionSound)
                    SoundHelper.StartCompletionSoundTask(3, GatherBuddy.Config.CraftingSoundPlaybackVolume);
                Stop();
                return false;
            }

            var entry = queue.Entries[_entryIndex];
            if (entry.Skipping)
            {
                _repeatIndex = 0;
                _repeatTotal = 0;
                continue;
            }

            _repeatIndex = 0;
            _repeatTotal = Math.Max(1, entry.Quantity);
        }
    }

    private static bool TryStartCurrentEntry(CraftingListQueueManager queue)
    {
        if (_entryIndex < 0 || _entryIndex >= queue.Entries.Count)
            return false;

        var entry = queue.Entries[_entryIndex];
        var list  = GatherBuddy.CraftingListManager.GetListByID(entry.ListId);
        if (list == null)
        {
            GatherBuddy.Log.Warning($"[CraftingListQueueRunner] Queue entry {_entryIndex} references missing crafting list {entry.ListId}, skipping");
            return false;
        }

        GatherBuddy.Log.Information($"[CraftingListQueueRunner] Starting queue entry {_entryIndex + 1}/{queue.Entries.Count}: '{list.Name}' (run {_repeatIndex}/{_repeatTotal})");
        if (!CraftingListLauncher.TryStart(list, _disableSkipIfEnough))
        {
            GatherBuddy.Log.Warning($"[CraftingListQueueRunner] Failed to start '{list.Name}', skipping to the next queue entry");
            Communicator.PrintError($"清单 \"{list.Name}\" 无法开始,已跳过。");
            return false;
        }

        _waitingForRunStart = true;
        return true;
    }
}
