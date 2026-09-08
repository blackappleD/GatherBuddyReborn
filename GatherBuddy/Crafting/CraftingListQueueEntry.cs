namespace GatherBuddy.Crafting;

/// <summary>
/// One pending crafting list inside the global crafting list queue.
/// <see cref="Quantity"/> is how many times the referenced list is run back to back.
/// </summary>
public sealed class CraftingListQueueEntry
{
    public int  ListId   { get; set; }
    public int  Quantity { get; set; } = 1;
    public bool Skipping { get; set; }
}
