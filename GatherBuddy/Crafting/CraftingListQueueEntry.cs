namespace GatherBuddy.Crafting;

/// <summary>
/// One pending crafting list inside the global crafting list queue.
/// <see cref="Quantity"/> multiplies every recipe quantity of the referenced list for its run
/// (a snapshot is planned once, so "skip if enough" counts pre-existing stock exactly once).
/// </summary>
public sealed class CraftingListQueueEntry
{
    public int  ListId   { get; set; }
    public int  Quantity { get; set; } = 1;
    public bool Skipping { get; set; }
}
