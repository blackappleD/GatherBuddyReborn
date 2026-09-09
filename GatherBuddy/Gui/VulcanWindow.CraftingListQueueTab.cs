using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using ElliLib;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private string _queueAddSearch = string.Empty;

    private void DrawCraftingListQueueTab()
    {
        IDisposable tabItem;
        bool        tabOpen;

        if (GatherBuddy.ControllerSupport != null)
        {
            var handle = GatherBuddy.ControllerSupport.TabNavigation.TabItem("清单队列##craftingListQueueTab", 1, 11);
            tabItem = handle;
            tabOpen = handle;
        }
        else
        {
            var handle = ImRaii.TabItem("清单队列##craftingListQueueTab");
            tabItem = handle;
            tabOpen = handle.Success;
        }

        using (tabItem)
        {
            if (!tabOpen)
                return;

            DrawCraftingListQueueContent();
        }
    }

    private void DrawCraftingListQueueContent()
    {
        var queue = GatherBuddy.CraftingListQueue;
        queue.PruneMissingLists();

        DrawQueueStatusLine(queue);
        ImGui.Spacing();
        var disableSkipIfEnough = GatherBuddy.Config.CraftingListQueueDisableSkipIfEnough;
        using (ImRaii.Disabled(CraftingListQueueRunner.Running))
        {
            if (ImGui.Checkbox("队列执行时自动关闭“持有足够时跳过”##queueDisableSkipIfEnough", ref disableSkipIfEnough))
            {
                GatherBuddy.Config.CraftingListQueueDisableSkipIfEnough = disableSkipIfEnough;
                GatherBuddy.Config.Save();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("仅影响本次清单队列执行,不会修改各清单自身设置。默认开启。");
        ImGui.Spacing();
        DrawQueueAddListCombo(queue);
        ImGui.Separator();
        ImGui.Spacing();

        var style     = ImGui.GetStyle();
        var footerH   = VulcanUiScaling.Scaled(22f) * 2f + style.ItemSpacing.Y * 3f;
        var entriesH  = Math.Max(ImGui.GetContentRegionAvail().Y - footerH, VulcanUiScaling.Scaled(60f));

        using (var child = ImRaii.Child("##queueEntries", new Vector2(-1f, entriesH), false))
        {
            if (child)
                DrawQueueEntryRows(queue);
        }

        ImGui.Separator();
        ImGui.Spacing();
        DrawQueueControls(queue);
    }

    private static void DrawQueueStatusLine(CraftingListQueueManager queue)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text("待制作清单队列");
        ImGui.SameLine();

        if (!CraftingListQueueRunner.Running)
        {
            var pending = queue.Entries.Count(e => !e.Skipping);
            ImGui.TextColored(ImGuiColors.DalamudGrey3, $"({pending} 个待执行 / 共 {queue.Count} 个)");
            return;
        }

        var index = CraftingListQueueRunner.CurrentEntryIndex;
        var name  = index >= 0 && index < queue.Count
            ? GatherBuddy.CraftingListManager.GetListByID(queue.Entries[index].ListId)?.Name ?? "?"
            : "?";
        ImGui.TextColored(ImGuiColors.ParsedGold,
            $"正在执行 {index + 1}/{queue.Count}: {name} (第 {CraftingListQueueRunner.CurrentRepeat}/{CraftingListQueueRunner.CurrentRepeatTotal} 次)");
    }

    private void DrawQueueAddListCombo(CraftingListQueueManager queue)
    {
        ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(260f));
        if (!ImGui.BeginCombo("##queueAddList", "添加清单到队列..."))
        {
            _queueAddSearch = string.Empty;
            return;
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##queueAddSearch", "搜索清单", ref _queueAddSearch, 64);
        ImGui.Separator();

        var lists = GatherBuddy.CraftingListManager.Lists
            .Where(l => !l.Ephemeral)
            .Where(l => string.IsNullOrEmpty(_queueAddSearch)
                || l.Name.Contains(_queueAddSearch, StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Order)
            .ToList();

        if (lists.Count == 0)
            ImGui.TextColored(ImGuiColors.DalamudGrey, "没有匹配的清单");

        foreach (var list in lists)
        {
            var label = string.IsNullOrEmpty(list.FolderPath)
                ? $"{list.Name}##queueAdd_{list.ID}"
                : $"{CraftingListManager.FormatFolderPath(list.FolderPath)} / {list.Name}##queueAdd_{list.ID}";
            if (!ImGui.Selectable(label))
                continue;

            queue.Add(list.ID);
            _queueAddSearch = string.Empty;
        }

        ImGui.EndCombo();
    }

    private void DrawQueueEntryRows(CraftingListQueueManager queue)
    {
        if (queue.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "队列为空。使用上方的下拉框把清单添加到队列。");
            return;
        }

        var indicesToRemove = new List<int>();
        for (var i = 0; i < queue.Count; i++)
            DrawQueueEntryRow(queue, i, indicesToRemove);

        for (var i = indicesToRemove.Count - 1; i >= 0; i--)
            queue.RemoveAt(indicesToRemove[i]);
    }

    private void DrawQueueEntryRow(CraftingListQueueManager queue, int index, List<int> indicesToRemove)
    {
        var entry = queue.Entries[index];
        var list  = GatherBuddy.CraftingListManager.GetListByID(entry.ListId);
        if (list == null)
            return;

        var innerSpacing   = ImGui.GetStyle().ItemInnerSpacing.X;
        var frameHeight    = ImGui.GetFrameHeight();
        var rowStartY      = ImGui.GetCursorPosY();
        var qtyTotalWidth  = VulcanUiScaling.Scaled(50f) + 2f * (frameHeight + innerSpacing);
        var iconBtnSize    = new Vector2(frameHeight, frameHeight);
        var selectableWidth = Math.Max(50f,
            ImGui.GetContentRegionAvail().X - qtyTotalWidth - 2f * frameHeight - 3f * innerSpacing);

        var isRunning = CraftingListQueueRunner.CurrentEntryIndex == index;
        var textColor = entry.Skipping
            ? new Vector4(0.7f, 0.7f, 0.7f, 1f)
            : isRunning
                ? new Vector4(1f, 0.84f, 0.35f, 1f)
                : new Vector4(1f, 1f, 1f, 1f);
        var label = $"{(entry.Skipping ? "[SKIP] " : string.Empty)}{(isRunning ? "[运行中] " : string.Empty)}{list.Name} ({list.Recipes.Count} 个配方)##queueEntry_{index}";

        using (ImRaii.PushColor(ImGuiCol.Text, textColor))
        using (ImRaii.PushStyle(ImGuiStyleVar.SelectableTextAlign, new Vector2(0f, 0.5f)))
        {
            if (ImGui.Selectable(label, false, ImGuiSelectableFlags.None, new Vector2(selectableWidth, frameHeight)))
            {
                OpenCraftingList(list);
                _craftingListsRequestFocus = true;
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("点击打开此清单进行编辑");

        DrawQueueEntryContextMenu(queue, index);

        ImGui.SameLine(0, innerSpacing);
        ImGui.SetCursorPosY(rowStartY);
        var qty     = entry.Quantity;
        var qtyStep = ImGui.GetIO().KeyShift ? 100 : ImGui.GetIO().KeyCtrl ? 10 : 1;
        ImGui.SetNextItemWidth(qtyTotalWidth);
        if (ImGui.InputInt($"##queueQty_{index}", ref qty, qtyStep, qtyStep))
            queue.SetQuantity(index, qty);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("此清单连续执行的次数。\n点击 +/- 调整 1\n按住 Ctrl: ±10\n按住 Shift: ±100");

        ImGui.SameLine(0, innerSpacing);
        var skipIcon    = entry.Skipping ? FontAwesomeIcon.Check : FontAwesomeIcon.Ban;
        var skipTooltip = entry.Skipping ? "在队列中重新启用此清单" : "在队列中跳过此清单";
        if (ImGuiUtil.DrawDisabledButton(skipIcon.ToIconString() + $"##queueSkip_{index}", iconBtnSize, skipTooltip, false, true))
            queue.SetSkipping(index, !entry.Skipping);

        ImGui.SameLine(0, innerSpacing);
        if (ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.Trash.ToIconString() + $"##queueRemove_{index}", iconBtnSize, "从队列中移除此清单", false, true))
            indicesToRemove.Add(index);
    }

    private static void DrawQueueEntryContextMenu(CraftingListQueueManager queue, int index)
    {
        var isPopupOpen = GatherBuddy.ControllerSupport != null
            ? GatherBuddy.ControllerSupport.ContextMenu.BeginPopupContextItemWithGamepad($"queueContext_{index}", Dalamud.GamepadState)
            : ImGui.BeginPopupContextItem($"queueContext_{index}");

        if (!isPopupOpen)
            return;

        using (ImRaii.Disabled(index == 0))
        {
            if (ImGui.Selectable("上移"))
                queue.Move(index, index - 1);
        }

        using (ImRaii.Disabled(index >= queue.Count - 1))
        {
            if (ImGui.Selectable("下移"))
                queue.Move(index, index + 1);
        }

        ImGui.EndPopup();
    }

    private void DrawQueueControls(CraftingListQueueManager queue)
    {
        var buttonH = VulcanUiScaling.Scaled(22f);

        if (CraftingListQueueRunner.Running)
        {
            using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.45f, 0.12f, 0.12f, 1f)))
            {
                if (ImGui.Button("停止队列##queueStop", new Vector2(-1f, buttonH)))
                {
                    CraftingListQueueRunner.Stop("用户停止");
                    CraftingGatherBridge.StopQueue();
                }
            }
        }
        else if (CraftingStartGate.GetBlock() is { } startBlock)
        {
            ImGuiUtil.DrawDisabledButton($"{startBlock.ButtonLabel}##queueStart", new Vector2(-1f, buttonH),
                startBlock.Tooltip, true);
        }
        else
        {
            var canStart = queue.Entries.Any(e => !e.Skipping);
            using (ImRaii.Disabled(!canStart))
            {
                if (ImGui.Button("开始队列采集/制作##queueStart", new Vector2(-1f, buttonH)))
                {
                    if (CraftingListQueueRunner.Start())
                        MinimizeWindow();
                }
            }
            if (!canStart && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("队列中没有可执行的清单");
        }

        using (ImRaii.Disabled(queue.Count == 0 || CraftingListQueueRunner.Running))
        {
            if (ImGui.Button("清空队列##queueClear", new Vector2(-1f, buttonH)))
                queue.Clear();
        }
    }
}
