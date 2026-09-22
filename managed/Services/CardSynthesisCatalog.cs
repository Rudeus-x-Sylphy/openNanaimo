using System.Globalization;
using System.Reflection;

namespace OpenNanaimo.Adapter.Services;

internal readonly record struct CardSynthesisRecipe(
    uint Token,
    uint Input0,
    uint Input1,
    uint Input2,
    uint Output)
{
    public IEnumerable<uint> Inputs
    {
        get
        {
            if (Input0 != 0) yield return Input0;
            if (Input1 != 0) yield return Input1;
            if (Input2 != 0) yield return Input2;
        }
    }
}

internal static class CardSynthesisCatalog
{
    private const string ResourceName = "OpenNanaimo.Adapter.CardSynthesisRecipes.inc";
    private const int ExpectedRecipeCount = 41_002;
    private static readonly IReadOnlyDictionary<uint, CardSynthesisRecipe> Recipes = Load();

    public static int Count => Recipes.Count;

    public static bool TryGet(uint token, out CardSynthesisRecipe recipe)
        => Recipes.TryGetValue(token, out recipe);

    private static IReadOnlyDictionary<uint, CardSynthesisRecipe> Load()
    {
        var assembly = typeof(CardSynthesisCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException(
                $"The embedded card-synthesis catalog '{ResourceName}' is missing from {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        var recipes = new Dictionary<uint, CardSynthesisRecipe>(ExpectedRecipeCount);
        uint previousToken = 0;
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            var trimmed = line.AsSpan().Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
                continue;

            if (!TryParseRecipe(trimmed, out var recipe))
                throw new InvalidDataException($"The embedded card-synthesis catalog has an invalid row at line {lineNumber}.");
            if (recipe.Token <= previousToken || !recipes.TryAdd(recipe.Token, recipe))
                throw new InvalidDataException($"The embedded card-synthesis catalog is not strictly ordered at token {recipe.Token}.");
            if (recipe.Input0 / 1_000_000u != 13u
                || recipe.Input1 != 0 && recipe.Input1 / 1_000_000u != 13u
                || recipe.Input2 != 0 && recipe.Input2 / 1_000_000u != 13u
                || recipe.Output / 1_000_000u is not (14u or 15u or 17u or 19u or 21u or 41u))
                throw new InvalidDataException($"The embedded card-synthesis catalog has invalid domains at token {recipe.Token}.");
            previousToken = recipe.Token;
        }

        if (recipes.Count != ExpectedRecipeCount)
            throw new InvalidDataException(
                $"The embedded card-synthesis catalog contains {recipes.Count} recipes; expected {ExpectedRecipeCount}.");
        return recipes;
    }

    private static bool TryParseRecipe(ReadOnlySpan<char> row, out CardSynthesisRecipe recipe)
    {
        recipe = default;
        Span<uint> values = stackalloc uint[5];
        var valueIndex = 0;
        var cursor = 0;
        while (cursor < row.Length && valueIndex < values.Length)
        {
            while (cursor < row.Length && row[cursor] is not (>= '0' and <= '9')) cursor++;
            var start = cursor;
            while (cursor < row.Length && row[cursor] is (>= '0' and <= '9')) cursor++;
            if (start == cursor
                || !uint.TryParse(row[start..cursor], NumberStyles.None, CultureInfo.InvariantCulture, out values[valueIndex]))
                return false;
            valueIndex++;
        }
        if (valueIndex != values.Length)
            return false;
        recipe = new CardSynthesisRecipe(values[0], values[1], values[2], values[3], values[4]);
        return true;
    }
}