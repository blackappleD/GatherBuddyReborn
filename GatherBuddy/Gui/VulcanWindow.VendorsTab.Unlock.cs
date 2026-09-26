using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    // Dalamud's ItemActionAction.SoulShards (internal enum); phantom job unlocks are only readable inside Occult Crescent.
    private const uint SoulShardItemActionId = 43142;

    private enum VendorUnlockStatus { Unlocked, Locked, NeedsOccultCrescent, Unavailable }

    private static readonly HashSet<uint> VendorUnlockErrorsLogged = new();

    private static bool IsSoulShard(Item item)
        => item.ItemAction.ValueNullable?.Action.RowId == SoulShardItemActionId;

    private static Item? GetVendorUnlockableItem(uint itemId)
    {
        if (!Dalamud.GameData.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
            return null;

        try
        {
            return Dalamud.UnlockState.IsItemUnlockable(item) ? item : null;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[VulcanWindow] Failed to check unlockable state for item {itemId}: {ex.Message}");
            return null;
        }
    }

    private static unsafe VendorUnlockStatus GetVendorUnlockStatus(Item item)
    {
        if (!Dalamud.PlayerState.IsLoaded)
            return VendorUnlockStatus.Unavailable;
        if (IsSoulShard(item) && PublicContentOccultCrescent.GetState() == null)
            return VendorUnlockStatus.NeedsOccultCrescent;

        try
        {
            return Dalamud.UnlockState.IsItemUnlocked(item) ? VendorUnlockStatus.Unlocked : VendorUnlockStatus.Locked;
        }
        catch (Exception ex)
        {
            // Drawn every frame, so only log the first failure per item.
            if (VendorUnlockErrorsLogged.Add(item.RowId))
                GatherBuddy.Log.Debug($"[VulcanWindow] Failed to check unlock state for item {item.RowId}: {ex.Message}");
            return VendorUnlockStatus.Unavailable;
        }
    }

    private static unsafe int GetVendorOwnedCount(uint itemId)
    {
        var inventory = InventoryManager.Instance();
        return inventory == null ? 0 : inventory->GetInventoryItemCount(itemId);
    }

    private static void DrawVendorUnlockStatus(Item item)
    {
        var status = GetVendorUnlockStatus(item);
        var owned  = GetVendorOwnedCount(item.RowId);

        var (color, icon, tooltip) = status switch
        {
            VendorUnlockStatus.Unlocked => (ImGuiColors.HealerGreen, FontAwesomeIcon.Check, "已学习，无需再购买"),
            VendorUnlockStatus.Locked   => (ImGuiColors.DalamudGrey, FontAwesomeIcon.Times, "尚未学习"),
            VendorUnlockStatus.NeedsOccultCrescent => (ImGuiColors.DalamudGrey3, FontAwesomeIcon.Question, "辅助职业的学习状态只能在新月岛内读取"),
            _ => (ImGuiColors.DalamudGrey3, FontAwesomeIcon.Question, "暂时无法读取学习状态（角色未登录？）"),
        };

        ImGui.SameLine(0, VulcanUiScaling.Scaled(6f));
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(color, icon.ToIconString());
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);

        if (owned <= 0)
            return;

        ImGui.SameLine(0, VulcanUiScaling.Scaled(4f));
        ImGui.TextColored(ImGuiColors.DalamudYellow, $"[持有 {owned}]");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(status == VendorUnlockStatus.Unlocked
                ? "背包中还有未使用的，已学习，可出售或丢弃"
                : "背包中已有，使用即可学习，无需再购买");
    }
}
