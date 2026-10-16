using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public sealed class CraftingListPlan
{
    public List<CraftingListItem> OriginalRecipes { get; } = new();
    public List<CraftingListItem> Recipes { get; } = new();
    public Dictionary<uint, int> Materials { get; } = new();
    public Dictionary<uint, int> Precrafts { get; } = new();
    public Dictionary<uint, IngredientQualityDemand> IngredientDemands { get; } = new();
    public Dictionary<uint, int> RetainerConsumedCraftables { get; } = new();

    /// <summary>Final products covered by pre-existing stock (inventory or retainers) instead of being crafted.</summary>
    public Dictionary<uint, int> FinalStockConsumed { get; } = new();
}

public readonly record struct CraftingListPlannerOptions(
    bool UseRetainerCraftableAvailability = false,
    bool ConsumeIntermediateAvailability = true,
    bool ConsumeFinalAvailability = true);

public static class CraftingListPlanner
{
    public static CraftingListPlan Build(CraftingListDefinition list, CraftingListPlannerOptions options = default)
        => new Planner(list, options).Build();

    private sealed class Planner
    {
        private readonly CraftingListDefinition _list;
        private readonly CraftingListPlan _plan = new();
        private readonly AvailabilityLedger _availability;
        private readonly bool _useRetainers;
        private readonly bool _consumeIntermediateAvailability;
        private readonly bool _consumeFinalAvailability;
        private readonly Dictionary<uint, CraftingListItem> _originalRecipeLookup;

        public Planner(CraftingListDefinition list, CraftingListPlannerOptions options)
        {
            _list = list;
            _useRetainers = options.UseRetainerCraftableAvailability;
            _consumeIntermediateAvailability = options.ConsumeIntermediateAvailability;
            _consumeFinalAvailability = options.ConsumeFinalAvailability;
            _availability = new AvailabilityLedger(_useRetainers, list.QueueReservedInventory);
            _originalRecipeLookup = list.Recipes
                .GroupBy(item => item.RecipeId)
                .ToDictionary(group => group.Key, group => group.First());
        }

        public CraftingListPlan Build()
        {
            foreach (var item in GetOriginalRecipesInDependencyOrder())
            {
                if (item.Options.Skipping || item.Quantity <= 0)
                    continue;

                var recipe = RecipeManager.GetRecipe(item.RecipeId);
                if (!recipe.HasValue)
                    continue;

                PlanOriginalRecipe(item, recipe.Value);
            }

            return _plan;
        }

        private List<CraftingListItem> GetOriginalRecipesInDependencyOrder()
        {
            var orderedRecipes = new List<CraftingListItem>();
            var processedRecipeIds = new HashSet<uint>();
            var visitingRecipeIds = new HashSet<uint>();

            foreach (var item in _list.Recipes)
                VisitOriginalRecipe(item, processedRecipeIds, visitingRecipeIds, orderedRecipes);

            return orderedRecipes;
        }

        private void VisitOriginalRecipe(
            CraftingListItem item,
            HashSet<uint> processedRecipeIds,
            HashSet<uint> visitingRecipeIds,
            List<CraftingListItem> orderedRecipes)
        {
            if (processedRecipeIds.Contains(item.RecipeId))
                return;

            if (!visitingRecipeIds.Add(item.RecipeId))
                return;

            var recipe = RecipeManager.GetRecipe(item.RecipeId);
            if (recipe.HasValue)
            {
                foreach (var (itemId, _) in RecipeManager.GetIngredients(recipe.Value))
                {
                    var dependencyRecipe = RecipeManager.GetRecipeForItem(itemId);
                    if (!dependencyRecipe.HasValue)
                        continue;

                    var dependencyItem = _list.Recipes.FirstOrDefault(candidate => candidate.RecipeId == dependencyRecipe.Value.RowId);
                    if (dependencyItem != null)
                        VisitOriginalRecipe(dependencyItem, processedRecipeIds, visitingRecipeIds, orderedRecipes);
                }
            }

            visitingRecipeIds.Remove(item.RecipeId);
            processedRecipeIds.Add(item.RecipeId);
            orderedRecipes.Add(item);
        }

        private void PlanOriginalRecipe(CraftingListItem item, Recipe recipe)
        {
            var resultItemId = recipe.ItemResult.RowId;
            var requestedItemCount = item.Quantity * (int)recipe.AmountResult;
            var remainingItemCount = requestedItemCount;

            remainingItemCount -= _availability.ConsumePlanned(resultItemId, remainingItemCount);

            if (_list.SkipIfEnough && _list.SkipFinalIfEnough && _consumeFinalAvailability)
            {
                var hqOnly = _list.QueueFinalHQOnly;
                var consumedInventory = _availability.ConsumeInventory(resultItemId, remainingItemCount, hqOnly);
                remainingItemCount -= consumedInventory;
                var consumedRetainers = 0;
                var countedRetainers = 0;
                if (_useRetainers)
                {
                    consumedRetainers = _availability.ConsumeRetainers(resultItemId, remainingItemCount, hqOnly);
                    remainingItemCount -= consumedRetainers;
                }
                else if (_list.QueueCountRetainerFinals)
                {
                    // Count only: retainer stock lets the craft be skipped but is never queued for withdrawal.
                    // Re-plans query retainers live, so the count stays correct across the run.
                    countedRetainers = _availability.CountRetainers(resultItemId, remainingItemCount, hqOnly);
                    remainingItemCount -= countedRetainers;
                }

                if (consumedRetainers > 0)
                    AddCount(_plan.RetainerConsumedCraftables, resultItemId, consumedRetainers);
                AddCount(_plan.FinalStockConsumed, resultItemId, consumedInventory + consumedRetainers + countedRetainers);
            }

            if (remainingItemCount <= 0)
                return;

            var craftCount = DivideRoundUp(remainingItemCount, (int)recipe.AmountResult);
            AddRecipe(_plan.OriginalRecipes, item.RecipeId, craftCount, true, PlannedOutputQuality.Unknown);
            AddRecipe(_plan.Recipes, item.RecipeId, craftCount, true, PlannedOutputQuality.Unknown);

            var surplus = craftCount * (int)recipe.AmountResult - remainingItemCount;
            _availability.AddPlanned(resultItemId, surplus);

            PlanIngredients(recipe, craftCount, true);
        }

        private void PlanIngredients(Recipe recipe, int craftCount, bool isOriginalRecipe)
        {
            var qualityPolicy = ResolveQualityPolicy(recipe, isOriginalRecipe);
            foreach (var (itemId, _) in RecipeManager.GetIngredients(recipe))
            {
                var itemDemand = qualityPolicy.GetDemand(itemId).Scale(craftCount);
                AddDemand(_plan.IngredientDemands, itemId, itemDemand);
                var subRecipe = ResolveSubRecipe(itemId);
                if (!subRecipe.HasValue)
                {
                    AddCount(_plan.Materials, itemId, itemDemand.Total);
                    continue;
                }

                AddCount(_plan.Precrafts, itemId, itemDemand.Total);
                PlanPrecraftDemand(subRecipe.Value, itemDemand);
            }
        }

        private void PlanPrecraftDemand(Recipe recipe, IngredientQualityDemand itemDemand)
        {
            var resultItemId = recipe.ItemResult.RowId;
            var remainingDemand = _availability.ConsumePlanned(resultItemId, itemDemand);

            if (_list.SkipIfEnough && _consumeIntermediateAvailability)
            {
                remainingDemand = _availability.ConsumeInventory(resultItemId, remainingDemand, out var nqRequiredFilledFromHQ);

                // Inventory had no NQ to give, so the HQ pile covered a hard NQ requirement. Put that
                // much NQ demand back so the NQ intermediates are still queued and the HQ ones survive
                // for the crafts that actually need HQ.
                remainingDemand = remainingDemand.WithNQFilledFromHQ(nqRequiredFilledFromHQ);

                if (_useRetainers)
                {
                    var remainingAfterRetainers = _availability.ConsumeRetainers(resultItemId, remainingDemand);
                    var fromRetainers = remainingDemand.Total - remainingAfterRetainers.Total;
                    remainingDemand = remainingAfterRetainers;
                    AddCount(_plan.RetainerConsumedCraftables, resultItemId, fromRetainers);
                }
            }

            if (remainingDemand.Total <= 0)
                return;

            var amountResult = Math.Max(1, (int)recipe.AmountResult);
            var craftCount = 0;

            craftCount += AddPrecraftBatch(recipe.RowId, resultItemId, amountResult, ref remainingDemand,
                remainingDemand.RequiredHQ, PlannedOutputQuality.HQ);
            craftCount += AddPrecraftBatch(recipe.RowId, resultItemId, amountResult, ref remainingDemand,
                remainingDemand.RequiredNQ, PlannedOutputQuality.NQ);
            craftCount += AddPrecraftBatch(recipe.RowId, resultItemId, amountResult, ref remainingDemand,
                remainingDemand.PreferNQ, PlannedOutputQuality.NQ);
            craftCount += AddPrecraftBatch(recipe.RowId, resultItemId, amountResult, ref remainingDemand,
                remainingDemand.PreferHQ, PlannedOutputQuality.Unknown);

            PlanIngredients(recipe, craftCount, false);
        }

        private int AddPrecraftBatch(
            uint recipeId,
            uint resultItemId,
            int amountResult,
            ref IngredientQualityDemand remainingDemand,
            int requestedOutput,
            PlannedOutputQuality outputQuality)
        {
            if (requestedOutput <= 0)
                return 0;

            var craftCount = DivideRoundUp(requestedOutput, amountResult);
            var produced = craftCount * amountResult;
            remainingDemand = ConsumePlannedOutput(remainingDemand, produced, outputQuality, out var consumed);
            AddRecipe(_plan.Recipes, recipeId, craftCount, false, outputQuality);
            _availability.AddPlanned(resultItemId, produced - consumed, outputQuality);
            return craftCount;
        }

        private static IngredientQualityDemand ConsumePlannedOutput(
            IngredientQualityDemand demand,
            int available,
            PlannedOutputQuality outputQuality,
            out int consumed)
        {
            var requiredHQ = demand.RequiredHQ;
            var requiredNQ = demand.RequiredNQ;
            var preferHQ = demand.PreferHQ;
            var preferNQ = demand.PreferNQ;
            consumed = 0;

            static void Consume(ref int demandPart, ref int availableAmount, ref int consumedAmount)
            {
                var take = Math.Min(demandPart, availableAmount);
                demandPart -= take;
                availableAmount -= take;
                consumedAmount += take;
            }

            if (outputQuality == PlannedOutputQuality.HQ)
            {
                Consume(ref requiredHQ, ref available, ref consumed);
                Consume(ref preferHQ, ref available, ref consumed);
                Consume(ref preferNQ, ref available, ref consumed);
            }
            else if (outputQuality == PlannedOutputQuality.NQ)
            {
                Consume(ref requiredNQ, ref available, ref consumed);
                Consume(ref preferNQ, ref available, ref consumed);
                Consume(ref preferHQ, ref available, ref consumed);
            }
            else
            {
                Consume(ref preferHQ, ref available, ref consumed);
                Consume(ref preferNQ, ref available, ref consumed);
            }

            return new IngredientQualityDemand(requiredHQ, requiredNQ, preferHQ, preferNQ);
        }

        private Recipe? ResolveSubRecipe(uint itemId)
        {
            if (_list.PrecraftRecipeOverrides.TryGetValue(itemId, out var overrideRecipeId))
            {
                var overrideRecipe = RecipeManager.GetRecipe(overrideRecipeId);
                if (overrideRecipe.HasValue)
                    return overrideRecipe;
            }
            return RecipeManager.GetRecipeForItem(itemId);
        }

        private CraftingQualityPolicy ResolveQualityPolicy(Recipe recipe, bool isOriginalRecipe)
        {
            var settings = isOriginalRecipe
                ? _originalRecipeLookup.GetValueOrDefault(recipe.RowId)?.CraftSettings
                : _list.PrecraftCraftSettings.GetValueOrDefault(recipe.RowId);
            var overrideMode = _list.GetQualityOverrideMode(recipe, isOriginalRecipe);
            var effectiveSettings = CraftingQualityPolicyResolver.BuildEffectiveSettings(recipe, settings, _list.UseAllHQ);
            return CraftingQualityPolicyResolver.Resolve(recipe, effectiveSettings, overrideMode);
        }

        private static void AddRecipe(
            List<CraftingListItem> target,
            uint recipeId,
            int craftCount,
            bool isOriginalRecipe,
            PlannedOutputQuality outputQuality)
        {
            if (craftCount <= 0)
                return;

            var existing = target.FirstOrDefault(item => item.RecipeId == recipeId
                && item.IsOriginalRecipe == isOriginalRecipe
                && item.OutputQuality == outputQuality);
            if (existing != null)
            {
                existing.Quantity += craftCount;
                return;
            }

            target.Add(new CraftingListItem(recipeId, craftCount)
            {
                IsOriginalRecipe = isOriginalRecipe,
                OutputQuality = outputQuality,
            });
        }

        private static void AddCount(Dictionary<uint, int> target, uint itemId, int amount)
        {
            if (amount <= 0)
                return;

            target[itemId] = target.GetValueOrDefault(itemId) + amount;
        }

        private static void AddDemand(Dictionary<uint, IngredientQualityDemand> target, uint itemId, IngredientQualityDemand demand)
        {
            if (demand.Total <= 0)
                return;

            target[itemId] = target.TryGetValue(itemId, out var existing)
                ? existing.Add(demand)
                : demand;
        }

        private static int DivideRoundUp(int value, int divisor)
            => (int)Math.Ceiling((double)value / divisor);
    }

    private sealed class AvailabilityLedger
    {
        private readonly bool _useRetainers;
        private readonly IReadOnlyDictionary<uint, int> _reservedInventory;
        private readonly Dictionary<uint, PlannedAvailability> _plannedAvailable = new();
        private readonly Dictionary<uint, (int NQ, int HQ)> _inventoryAvailable = new();
        private readonly Dictionary<uint, (int NQ, int HQ)> _retainerAvailable = new();

        public AvailabilityLedger(bool useRetainers, IReadOnlyDictionary<uint, int>? reservedInventory = null)
        {
            _useRetainers      = useRetainers;
            _reservedInventory = reservedInventory ?? new Dictionary<uint, int>();
        }

        public int ConsumePlanned(uint itemId, int requested)
        {
            if (requested <= 0)
                return 0;

            var available = _plannedAvailable.GetValueOrDefault(itemId);
            var consumed = Math.Min(requested, available.Total);
            if (consumed <= 0)
                return 0;

            var remainingToConsume = consumed;
            var consumeUnknown = Math.Min(remainingToConsume, available.Unknown);
            remainingToConsume -= consumeUnknown;
            var consumeNQ = Math.Min(remainingToConsume, available.NQ);
            remainingToConsume -= consumeNQ;
            var consumeHQ = Math.Min(remainingToConsume, available.HQ);
            _plannedAvailable[itemId] = new PlannedAvailability(
                available.Unknown - consumeUnknown,
                available.NQ - consumeNQ,
                available.HQ - consumeHQ);
            return consumed;
        }

        public int ConsumeInventory(uint itemId, int requested, bool hqOnly = false)
            => ConsumeTotal(_inventoryAvailable, itemId, requested, GetReservedAdjustedInventoryCounts, hqOnly);

        public IngredientQualityDemand ConsumePlanned(uint itemId, IngredientQualityDemand demand)
        {
            if (demand.Total <= 0)
                return demand;

            var available = _plannedAvailable.GetValueOrDefault(itemId);
            if (available.Total <= 0)
                return demand;

            var remaining = demand.ConsumeSplit(available.NQ, available.HQ, out var consumedNQ, out var consumedHQ);
            remaining = remaining.ConsumeUnknownQuality(available.Unknown, out var consumedUnknown);
            _plannedAvailable[itemId] = new PlannedAvailability(
                available.Unknown - consumedUnknown,
                available.NQ - consumedNQ,
                available.HQ - consumedHQ);
            return remaining;
        }

        public IngredientQualityDemand ConsumeInventory(uint itemId, IngredientQualityDemand demand)
            => ConsumeSplit(_inventoryAvailable, itemId, demand, GetReservedAdjustedInventoryCounts);

        /// <summary>
        /// Consumes inventory for a precraft demand, reporting how many items with a hard NQ requirement
        /// had to come from the HQ pool (<paramref name="nqRequiredFilledFromHQ"/>). PlanPrecraftDemand
        /// restores that much NQ demand so the NQ intermediates still get crafted instead of the HQ pile
        /// being spent in their place.
        /// </summary>
        public IngredientQualityDemand ConsumeInventory(uint itemId, IngredientQualityDemand demand, out int nqRequiredFilledFromHQ)
            => ConsumeSplit(_inventoryAvailable, itemId, demand, GetReservedAdjustedInventoryCounts, out nqRequiredFilledFromHQ);

        /// <summary>
        /// Live inventory counts minus the items reserved by earlier queue entries (their crafted
        /// output plus the stock they counted as "enough"). Reserved amounts come off the HQ count
        /// first, since queue-run lists target HQ results.
        /// </summary>
        private (int NQ, int HQ) GetReservedAdjustedInventoryCounts(uint itemId)
            => SubtractReserved(GetInventorySplitCounts(itemId), _reservedInventory.GetValueOrDefault(itemId));

        /// <summary>
        /// Live retainer counts minus whatever reservation the inventory could not absorb, so stock an
        /// earlier queue entry counted in a retainer is not counted again by a later entry.
        /// </summary>
        private (int NQ, int HQ) GetReservedAdjustedRetainerCounts(uint itemId)
        {
            var reserved = _reservedInventory.GetValueOrDefault(itemId);
            if (reserved > 0)
            {
                var (nq, hq) = GetInventorySplitCounts(itemId);
                reserved = Math.Max(0, reserved - nq - hq);
            }

            return SubtractReserved(GetRetainerSplitCounts(itemId), reserved);
        }

        private static (int NQ, int HQ) SubtractReserved((int NQ, int HQ) counts, int reserved)
        {
            if (reserved <= 0)
                return counts;

            var fromHQ = Math.Min(counts.HQ, reserved);
            var fromNQ = Math.Min(counts.NQ, reserved - fromHQ);
            return (counts.NQ - fromNQ, counts.HQ - fromHQ);
        }

        public IngredientQualityDemand ConsumeRetainers(uint itemId, IngredientQualityDemand demand)
            => _useRetainers
                ? ConsumeSplit(_retainerAvailable, itemId, demand, GetReservedAdjustedRetainerCounts)
                : demand;

        public int ConsumeRetainers(uint itemId, int requested, bool hqOnly = false)
            => _useRetainers
                ? ConsumeTotal(_retainerAvailable, itemId, requested, GetReservedAdjustedRetainerCounts, hqOnly)
                : 0;

        /// <summary>Consumes retainer stock even without retainer restock planning; the caller never withdraws it.</summary>
        public int CountRetainers(uint itemId, int requested, bool hqOnly)
            => ConsumeTotal(_retainerAvailable, itemId, requested, GetReservedAdjustedRetainerCounts, hqOnly);

        public void AddPlanned(uint itemId, int amount, PlannedOutputQuality outputQuality = PlannedOutputQuality.Unknown)
        {
            if (amount <= 0)
                return;

            var available = _plannedAvailable.GetValueOrDefault(itemId);
            _plannedAvailable[itemId] = outputQuality switch
            {
                PlannedOutputQuality.NQ => available with { NQ = available.NQ + amount },
                PlannedOutputQuality.HQ => available with { HQ = available.HQ + amount },
                _ => available with { Unknown = available.Unknown + amount },
            };
        }

        private readonly record struct PlannedAvailability(int Unknown, int NQ, int HQ)
        {
            public int Total => Unknown + NQ + HQ;
        }

        private static int ConsumeTotal(
            Dictionary<uint, (int NQ, int HQ)> ledger,
            uint itemId,
            int requested,
            Func<uint, (int NQ, int HQ)> valueFactory,
            bool hqOnly = false)
        {
            if (requested <= 0)
                return 0;

            if (!ledger.TryGetValue(itemId, out var available))
            {
                available = valueFactory(itemId);
                ledger[itemId] = available;
            }

            var usableNQ = hqOnly ? 0 : available.NQ;
            var totalAvailable = usableNQ + available.HQ;
            if (totalAvailable <= 0)
                return 0;

            var consumed = Math.Min(requested, totalAvailable);
            var consumeNQ = Math.Min(consumed, usableNQ);
            var consumeHQ = consumed - consumeNQ;
            ledger[itemId] = (Math.Max(0, available.NQ - consumeNQ), Math.Max(0, available.HQ - consumeHQ));
            return consumed;
        }

        private static IngredientQualityDemand ConsumeSplit(
            Dictionary<uint, (int NQ, int HQ)> ledger,
            uint itemId,
            IngredientQualityDemand demand,
            Func<uint, (int NQ, int HQ)> valueFactory)
            => ConsumeSplit(ledger, itemId, demand, valueFactory, out _);

        private static IngredientQualityDemand ConsumeSplit(
            Dictionary<uint, (int NQ, int HQ)> ledger,
            uint itemId,
            IngredientQualityDemand demand,
            Func<uint, (int NQ, int HQ)> valueFactory,
            out int nqRequiredFilledFromHQ)
        {
            nqRequiredFilledFromHQ = 0;
            if (demand.Total <= 0)
                return demand;

            if (!ledger.TryGetValue(itemId, out var available))
            {
                available = valueFactory(itemId);
                ledger[itemId] = available;
            }

            if (available.NQ <= 0 && available.HQ <= 0)
                return demand;

            var remaining = demand.ConsumeSplit(available.NQ, available.HQ, out var consumedNQ, out var consumedHQ, out nqRequiredFilledFromHQ);
            ledger[itemId] = (Math.Max(0, available.NQ - consumedNQ), Math.Max(0, available.HQ - consumedHQ));
            return remaining;
        }

        private static unsafe (int NQ, int HQ) GetInventorySplitCounts(uint itemId)
        {
            try
            {
                var inventory = InventoryManager.Instance();
                if (inventory == null)
                    return (0, 0);

                return (
                    (int)inventory->GetInventoryItemCount(itemId, false, false, false),
                    (int)inventory->GetInventoryItemCount(itemId, true, false, false));
            }
            catch
            {
                return (0, 0);
            }
        }

        private static (int NQ, int HQ) GetRetainerSplitCounts(uint itemId)
        {
            try
            {
                var snapshot = RetainerItemQuery.CreateSnapshot(new[] { itemId });
                return (snapshot.GetCountNQ(itemId), snapshot.GetCountHQ(itemId));
            }
            catch
            {
                return (0, 0);
            }
        }
    }
}
