using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

internal static partial class LauncherTools
{
    static JsonObject VerifyCinematics(string root)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Nanaimo.DungeonCinematics.json");
        Require(stream is not null, "Cinematic release contract is missing");
        using var reader = new StreamReader(stream!, Encoding.UTF8);
        var recipe = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        var marker = ReadJson(SafePath(root, ".openNanaimo-dungeon-cinematics.json"));
        Require(S(marker, "schema") == "openNanaimo.dungeon-cinematics.v1"
                && S(marker, "id") == S(recipe, "id"), "Cinematic release identity differs");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Objects(recipe, "files"))
        {
            var path = S(row, "path");
            Require((path.StartsWith("effs/game/shootinggamebasic/Ending_Event/", StringComparison.Ordinal)
                     || path == "flying/hd0_ep11_dg02_st00.sstg" || path == "flying/hd0_ep13_dg02_st00.sstg")
                    && seen.Add(path), "Invalid cinematic release path");
            CheckFile(root, path, N(row, "size"), S(row, "sha256"));
        }
        Require(seen.Count == N(recipe["summary"]!, "resources"), "Cinematic coverage differs");
        return recipe;
    }

    static bool CinematicParentMatches(JsonNode candidate, JsonNode parent)
        => Objects(candidate, "accepted_before").Any(old =>
            S(old, "sha256").Equals(S(parent, "sha256"), StringComparison.OrdinalIgnoreCase)
            && (parent["size"] is null || N(old, "size") == N(parent, "size")));
}
