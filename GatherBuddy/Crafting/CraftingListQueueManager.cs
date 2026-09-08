using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Crafting;

/// <summary>
/// The single global queue of crafting lists. Holds only the ordering/quantity/skip state;
/// running a queued list goes through <see cref="CraftingListLauncher"/> so the existing
/// single-list gather and craft pipeline is reused unchanged.
/// </summary>
public sealed class CraftingListQueueManager
{
    private readonly List<CraftingListQueueEntry> _entries;

    public CraftingListQueueManager()
    {
        GatherBuddy.Config.CraftingListQueue ??= [];
        _entries = GatherBuddy.Config.CraftingListQueue;
        Sanitize();
    }

    public IReadOnlyList<CraftingListQueueEntry> Entries
        => _entries;

    public int Count
        => _entries.Count;

    public bool Contains(int listId)
        => _entries.Any(e => e.ListId == listId);

    public void Add(int listId, int quantity = 1)
    {
        var list = GatherBuddy.CraftingListManager.GetListByID(listId);
        if (list == null)
        {
            GatherBuddy.Log.Warning($"[CraftingListQueue] Cannot queue unknown crafting list {listId}");
            return;
        }

        _entries.Add(new CraftingListQueueEntry
        {
            ListId   = listId,
            Quantity = Math.Max(1, quantity),
        });
        GatherBuddy.Log.Debug($"[CraftingListQueue] Queued crafting list '{list.Name}' ({listId}) x{Math.Max(1, quantity)}");
        Save();
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _entries.Count)
            return;

        _entries.RemoveAt(index);
        Save();
    }

    public void RemoveByListId(int listId)
    {
        if (_entries.RemoveAll(e => e.ListId == listId) > 0)
            Save();
    }

    public void SetQuantity(int index, int quantity)
    {
        if (index < 0 || index >= _entries.Count)
            return;

        var clamped = Math.Max(1, quantity);
        if (_entries[index].Quantity == clamped)
            return;

        _entries[index].Quantity = clamped;
        Save();
    }

    public void SetSkipping(int index, bool skipping)
    {
        if (index < 0 || index >= _entries.Count)
            return;

        if (_entries[index].Skipping == skipping)
            return;

        _entries[index].Skipping = skipping;
        Save();
    }

    public void Move(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _entries.Count)
            return;
        if (toIndex < 0 || toIndex >= _entries.Count || toIndex == fromIndex)
            return;

        var entry = _entries[fromIndex];
        _entries.RemoveAt(fromIndex);
        _entries.Insert(toIndex, entry);
        Save();
    }

    public void Clear()
    {
        if (_entries.Count == 0)
            return;

        _entries.Clear();
        Save();
    }

    /// <summary>Drops entries whose crafting list no longer exists. Returns true when something was removed.</summary>
    public bool PruneMissingLists()
    {
        var removed = _entries.RemoveAll(e => GatherBuddy.CraftingListManager.GetListByID(e.ListId) == null);
        if (removed == 0)
            return false;

        GatherBuddy.Log.Debug($"[CraftingListQueue] Pruned {removed} queue entr{(removed == 1 ? "y" : "ies")} referencing deleted crafting lists");
        Save();
        return true;
    }

    public void Save()
        => GatherBuddy.Config.Save();

    private void Sanitize()
    {
        foreach (var entry in _entries)
            entry.Quantity = Math.Max(1, entry.Quantity);
    }
}
