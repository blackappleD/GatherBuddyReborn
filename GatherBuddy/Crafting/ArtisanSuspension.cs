using System;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.Plugin;
using GatherBuddy.Utility;

namespace GatherBuddy.Crafting;

/// <summary>
/// Artisan watches the game's crafting state and, with its auto mode on, executes its own solver's
/// actions on any craft — including ones Vulcan started. So Vulcan unloads Artisan before it starts
/// a craft and loads it again once Vulcan has gone idle. Only an Artisan that Vulcan unloaded itself
/// is loaded again; the flag is kept in the config so a game close mid-run still restores it.
/// </summary>
public static class ArtisanSuspension
{
    private const string ArtisanPluginName = "Artisan";

    /// <summary>
    /// Debounce only: whether Vulcan is done is decided by <see cref="IsVulcanIdle"/>. This just rides out the
    /// game's crafting condition flags flickering for a moment when a craft ends.
    /// </summary>
    private static readonly TimeSpan RestoreIdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UnloadTimeout = TimeSpan.FromSeconds(15);

    private static Task? _unloadTask = null;
    private static DateTime _unloadRequestedAt = DateTime.MinValue;
    private static DateTime _idleSince = DateTime.MinValue;

    /// <summary>
    /// A single craft from the recipe tab runs job switch, consumables and the Raphael wait as task-manager steps
    /// before <see cref="CraftingGameInterop.StartCraft"/>, outside any queue, so it is tracked here.
    /// </summary>
    private static bool _singleCraftInFlight = false;

    private static bool SuspendedByVulcan
    {
        get => GatherBuddy.Config.ArtisanSuspendedByVulcan;
        set
        {
            if (GatherBuddy.Config.ArtisanSuspendedByVulcan == value)
                return;

            GatherBuddy.Config.ArtisanSuspendedByVulcan = value;
            GatherBuddy.Config.Save();
        }
    }

    /// <summary>
    /// Task-manager step run before a craft starts: unloads Artisan if it is loaded and returns true once it is gone.
    /// Also returns true when Artisan cannot be unloaded, so the craft still goes ahead (with a chat warning).
    /// </summary>
    public static bool EnsureSuspended()
    {
        var state = DalamudPluginToggleHelper.GetPluginToggleState(ArtisanPluginName);
        if (!state.IsInstalled || !state.IsLoaded)
            return FinishUnload();

        if (_unloadTask != null)
        {
            if (_unloadTask.IsFaulted || _unloadTask.IsCanceled)
                return GiveUp(_unloadTask.Exception?.GetBaseException().Message ?? "卸载被取消");
            if (DateTime.UtcNow - _unloadRequestedAt > UnloadTimeout)
                return GiveUp("等待卸载超时");
            return false;
        }

        if (!state.CanToggle)
            return GiveUp(state.BlockedReason);

        if (!DalamudPluginToggleHelper.TrySetPluginEnabled(ArtisanPluginName, false, out var toggleTask, out var failureReason))
            return GiveUp(failureReason);

        _unloadTask = toggleTask ?? Task.CompletedTask;
        _unloadRequestedAt = DateTime.UtcNow;
        SuspendedByVulcan = true;
        GatherBuddy.Log.Information("[ArtisanSuspension] Unloading Artisan for the Vulcan craft.");
        Communicator.Print("已临时禁用 Artisan, Vulcan 制作结束后会自动重新启用");
        return false;
    }

    /// <summary>
    /// Called at the very start of a recipe-tab single craft, before the job switch: marks the flow as running
    /// and starts unloading Artisan right away. The SuspendArtisan step in StartCraft still waits for the unload.
    /// </summary>
    public static void BeginSingleCraft()
    {
        _singleCraftInFlight = true;
        _idleSince = DateTime.MinValue;
        EnsureSuspended();
    }

    /// <summary>The user toggled Artisan by hand, so it is theirs again and is not loaded back automatically.</summary>
    public static void Forget()
    {
        _unloadTask = null;
        SuspendedByVulcan = false;
    }

    public static void Update()
    {
        // The single-craft flow is over once nothing of it is queued any more and no craft is in progress:
        // that covers a finished craft, a cancel, a preparation failure (state is forced back to IdleNormal)
        // and a stop (the task manager is cleared).
        if (_singleCraftInFlight
         && GatherBuddy.AutoGather?.TaskManager.IsBusy != true
         && CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleNormal)
            _singleCraftInFlight = false;

        if (!SuspendedByVulcan)
            return;

        // Normally the SuspendArtisan step clears this, but a stop can drop that step before it runs again.
        if (_unloadTask != null)
        {
            if (!_unloadTask.IsCompleted && DateTime.UtcNow - _unloadRequestedAt <= UnloadTimeout)
                return;
            _unloadTask = null;
        }

        if (!IsVulcanIdle())
        {
            _idleSince = DateTime.MinValue;
            return;
        }

        var now = DateTime.UtcNow;
        if (_idleSince == DateTime.MinValue)
            _idleSince = now;
        if (now - _idleSince < RestoreIdleDelay)
            return;

        SuspendedByVulcan = false;
        var state = DalamudPluginToggleHelper.GetPluginToggleState(ArtisanPluginName);
        if (!state.IsInstalled || state.IsLoaded)
            return;

        string? failureReason = state.BlockedReason;
        if (state.CanToggle && DalamudPluginToggleHelper.TrySetPluginEnabled(ArtisanPluginName, true, out _, out failureReason))
        {
            GatherBuddy.Log.Information("[ArtisanSuspension] Vulcan is idle, loading Artisan again.");
            Communicator.Print("Vulcan 制作已结束, 已重新启用 Artisan");
            return;
        }

        GatherBuddy.Log.Warning($"[ArtisanSuspension] Failed to load Artisan again: {failureReason}");
        Communicator.PrintError("自动重新启用 Artisan 失败, 请在插件安装器中手动启用");
    }

    private static bool FinishUnload()
    {
        if (_unloadTask != null)
            GatherBuddy.Log.Debug("[ArtisanSuspension] Artisan unloaded, starting the craft.");
        _unloadTask = null;
        return true;
    }

    private static bool GiveUp(string? reason)
    {
        _unloadTask = null;
        GatherBuddy.Log.Warning($"[ArtisanSuspension] Could not unload Artisan before crafting: {reason}");
        Communicator.PrintError($"无法自动禁用 Artisan, Artisan 可能会接管本次制作: {reason ?? "未知原因"}");
        return true;
    }

    private static bool IsVulcanIdle()
        => !_singleCraftInFlight
         && !CraftingGatherBridge.IsQueueRunning
         && !CraftingListQueueRunner.Running
         && CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleNormal
         && !Dalamud.Conditions[ConditionFlag.Crafting]
         && !Dalamud.Conditions[ConditionFlag.PreparingToCraft]
         && !Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction];
}
