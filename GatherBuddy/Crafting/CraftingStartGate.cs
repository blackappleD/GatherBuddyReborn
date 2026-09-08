using GatherBuddy.Plugin;

namespace GatherBuddy.Crafting;

/// <summary>
/// Single place that decides whether a new craft run may be started right now, so every
/// start button (recipe browser, crafting list, list editor, list queue) shows the same reason.
/// </summary>
public static class CraftingStartGate
{
    /// <summary>Why a start button is disabled: the label to show on it, plus the hover explanation.</summary>
    public readonly record struct Block(string ButtonLabel, string Tooltip);

    /// <summary>Null when a craft may start now, otherwise the reason it is blocked.</summary>
    public static Block? GetBlock()
    {
        if (IPCSubscriber.IsReady("Artisan"))
            return new Block("检测到 Artisan", "Artisan 插件已加载, 请卸载 Artisan 后使用 Vulcan 制作系统");

        // A paused run still owns the queue processor, so it also blocks: starting a new run would
        // replace that processor and there would be nothing left to resume.
        if (CraftingGatherBridge.IsQueueRunning)
            return new Block("制作进行中", "制作队列进行中, 请先在制作状态窗口点击\"停止\"后再开始新的制作");

        return null;
    }
}
