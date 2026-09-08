using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public enum ImportedItemQuality
{
    Unspecified,
    NQ,
    HQ,
}

public sealed class CraftingListTextImportLine
{
    public string ItemName { get; init; } = string.Empty;
    public string RawLine  { get; init; } = string.Empty;
    public int    Quantity { get; init; } = 1;
    public ImportedItemQuality Quality { get; init; } = ImportedItemQuality.Unspecified;
}

public static class CraftingListTextImporter
{
    // In-game HQ icon character, present when copying item links from chat.
    private const char HqSymbol = '';
    private const int  MaxQuantity = 99999;

    private static readonly Regex TrailingQuantity = new(@"^(?<name>.+?)\s*[x×*＊]\s*(?<qty>\d{1,5})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LeadingQuantity = new(@"^(?<qty>\d{1,5})\s*[x×*＊]\s*(?<name>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BareTrailingQuantity = new(@"^(?<name>.+?)\s+(?<qty>\d{1,5})$",
        RegexOptions.Compiled);
    private static readonly Regex QualityPrefix = new(@"^(?:[\[(（【]\s*(?<q>HQ|NQ)\s*[\])）】]|(?<q>HQ|NQ)(?=\s))\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QualitySuffix = new(@"\s*(?:[\[(（【]\s*(?<q>HQ|NQ)\s*[\])）】]|(?<=[\s　])(?<q>HQ|NQ))$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Lazy<Dictionary<string, uint>> CraftableItemIdByName =
        new(BuildNameIndex, LazyThreadSafetyMode.ExecutionAndPublication);

    public static List<CraftingListTextImportLine> ParseLines(string? text)
    {
        var result = new List<CraftingListTextImportLine>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Replace('　', ' ').Trim();
            if (line.Length == 0)
                continue;

            if (TryParseLine(line, out var parsed))
                result.Add(parsed);
        }

        return result;
    }

    public static bool TryParseLine(string line, out CraftingListTextImportLine parsed)
    {
        parsed = new CraftingListTextImportLine();
        var remainder = line;
        var quality = StripQualityMarkers(ref remainder);
        var (name, quantity) = ExtractQuantity(remainder);
        var nameQuality = StripQualityMarkers(ref name);
        if (quality == ImportedItemQuality.Unspecified)
            quality = nameQuality;

        name = name.Trim();
        if (name.Length == 0)
            return false;

        parsed = new CraftingListTextImportLine
        {
            ItemName = name,
            RawLine  = line,
            Quantity = Math.Clamp(quantity, 1, MaxQuantity),
            Quality  = quality,
        };
        return true;
    }

    public static bool TryResolveRecipeId(string itemName, out uint recipeId)
    {
        recipeId = 0;
        var name = itemName.Trim();
        if (name.Length == 0)
            return false;

        if (!CraftableItemIdByName.Value.TryGetValue(name, out var itemId))
            return false;

        var recipe = RecipeManager.GetRecipeForItem(itemId);
        if (!recipe.HasValue)
            return false;

        recipeId = recipe.Value.RowId;
        return true;
    }

    private static (string Name, int Quantity) ExtractQuantity(string line)
    {
        var match = TrailingQuantity.Match(line);
        if (!match.Success)
            match = LeadingQuantity.Match(line);
        if (!match.Success)
            match = BareTrailingQuantity.Match(line);
        if (!match.Success || !int.TryParse(match.Groups["qty"].Value, out var quantity))
            return (line, 1);

        return (match.Groups["name"].Value, quantity);
    }

    private static ImportedItemQuality StripQualityMarkers(ref string value)
    {
        var quality = ImportedItemQuality.Unspecified;
        var changed = true;
        while (changed)
        {
            changed = false;
            var trimmed = value.Trim();

            if (trimmed.Length > 0 && (trimmed[0] == HqSymbol || trimmed[^1] == HqSymbol))
            {
                if (quality == ImportedItemQuality.Unspecified)
                    quality = ImportedItemQuality.HQ;
                value = trimmed.Trim(HqSymbol);
                changed = true;
                continue;
            }

            var match = QualityPrefix.Match(trimmed);
            if (!match.Success)
                match = QualitySuffix.Match(trimmed);
            if (!match.Success)
            {
                value = trimmed;
                continue;
            }

            if (quality == ImportedItemQuality.Unspecified)
                quality = match.Groups["q"].Value.Equals("HQ", StringComparison.OrdinalIgnoreCase)
                    ? ImportedItemQuality.HQ
                    : ImportedItemQuality.NQ;
            value = trimmed.Remove(match.Index, match.Length);
            changed = true;
        }

        return quality;
    }

    private static Dictionary<string, uint> BuildNameIndex()
    {
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var sheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (sheet == null)
        {
            GatherBuddy.Log.Debug("[CraftingListTextImporter] Recipe sheet unavailable while building item name index");
            return result;
        }

        foreach (var recipe in sheet)
        {
            try
            {
                if (recipe.ItemResult.RowId == 0 || recipe.Number == 0)
                    continue;

                var name = recipe.ItemResult.Value.Name.ExtractText().Trim();
                if (name.Length > 0)
                    result.TryAdd(name, recipe.ItemResult.RowId);
            }
            catch
            {
            }
        }

        GatherBuddy.Log.Debug($"[CraftingListTextImporter] Built craftable item name index: {result.Count} entries");
        return result;
    }
}
