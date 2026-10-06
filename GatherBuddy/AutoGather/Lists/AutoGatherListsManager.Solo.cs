using GatherBuddy.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.AutoGather.Lists;

public partial class AutoGatherListsManager
{
    /// <summary> True while <see cref="SoloList"/> has saved enabled states that <see cref="RestoreSolo"/> has not restored yet. </summary>
    public bool IsSoloActive
        => GatherBuddy.Config.AutoGatherListSoloBackup != null;

    public AutoGatherList? FindList(string name)
    {
        var trimmed = name.Trim();
        return Lists.FirstOrDefault(l => string.Equals(l.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary> Replaces every item of the list, all enabled, without touching its name, folder or enabled state. </summary>
    public void ReplaceItems(AutoGatherList list, IReadOnlyList<(IGatherable Item, uint Quantity)> items)
    {
        for (var i = list.Items.Count - 1; i >= 0; --i)
            list.RemoveAt(i);
        foreach (var (item, quantity) in items)
            list.Add(item, quantity);

        Save();
        if (list.Enabled)
            SetActiveItems();
    }

    /// <summary>
    /// Enables only the given list. The enabled state of every list is saved first, unless an earlier solo is still
    /// unrestored, so that <see cref="RestoreSolo"/> always returns to the state from before the first solo.
    /// Returns false when the list could not be enabled (e.g. missing bait); nothing is changed then.
    /// </summary>
    public bool SoloList(AutoGatherList list)
    {
        var previouslyEnabled = Lists.Where(l => l.Enabled).Select(l => l.Name).ToList();
        if (!list.Enabled)
        {
            ToggleList(list);
            if (!list.Enabled)
                return false;
        }

        if (!IsSoloActive)
        {
            GatherBuddy.Config.AutoGatherListSoloBackup = previouslyEnabled;
            GatherBuddy.Config.Save();
        }

        foreach (var other in Lists.Where(l => l != list))
            other.Enabled = false;

        Save();
        SetActiveItems();
        return true;
    }

    /// <summary> Restores the enabled states saved by <see cref="SoloList"/>. Returns false when no solo was active. </summary>
    public bool RestoreSolo()
    {
        var backup = GatherBuddy.Config.AutoGatherListSoloBackup;
        if (backup == null)
            return false;

        var enabled = new HashSet<string>(backup, StringComparer.Ordinal);
        foreach (var list in Lists)
            list.Enabled = enabled.Contains(list.Name);

        GatherBuddy.Config.AutoGatherListSoloBackup = null;
        GatherBuddy.Config.Save();
        Save();
        SetActiveItems();
        return true;
    }
}
