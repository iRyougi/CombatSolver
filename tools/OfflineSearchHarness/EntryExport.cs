using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace OfflineSearchHarness;

/// <summary>Read-only native run snapshot immediately before EnterRoomDebug.</summary>
internal static class EntryExport
{
    public static void Write(RunState run, EncounterModel encounter, RoomType roomType, string path)
    {
        if (run.Players.Count != 1 || run.CurrentActIndex < 0 || run.CurrentActIndex >= run.Acts.Count)
            throw new InvalidOperationException("Entry export requires one player and a current act.");
        if (encounter.RoomType != roomType)
            throw new InvalidOperationException("Entry encounter room type differs from the request.");
        // The native save serializer captures model SavedProperties, RNG words, odds and relic bags.
        // Select only stable state fields; save timestamps and profile metadata are not entry state.
        string nativeJson = JsonSerializationUtility.ToJson(RunManager.Instance.ToSave(null));
        JsonObject saved = JsonNode.Parse(nativeJson)?.AsObject()
            ?? throw new InvalidDataException("Native run save is empty.");
        JsonArray players = saved["players"]?.AsArray()
            ?? throw new InvalidDataException("Native run save has no players.");
        JsonArray acts = saved["acts"]?.AsArray()
            ?? throw new InvalidDataException("Native run save has no acts.");
        if (players.Count != 1 || acts.Count != run.Acts.Count)
            throw new InvalidDataException("Native run save has a different player or act count.");
        JsonObject roomSequence = acts[run.CurrentActIndex]?["rooms"]?.AsObject()
            ?? throw new InvalidDataException("Native act has no room sequence.");
        int completedActFloors = run.MapPointHistory.Take(run.CurrentActIndex).Sum(points => points.Count);
        var entry = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["upstreamCommit"] = "d231e9e5",
            ["seed"] = run.Rng.StringSeed,
            ["characterId"] = run.Players[0].Character.Id.ToString(),
            ["ascension"] = run.AscensionLevel,
            ["actIndex"] = run.CurrentActIndex,
            ["actIds"] = JsonSerializer.SerializeToNode(run.Acts.Select(act => act.Id.ToString()).ToArray()),
            ["visitedMapCoords"] = saved["visited_map_coords"]?.DeepClone() ?? new JsonArray(),
            ["completedActFloors"] = completedActFloors,
            ["targetRoomType"] = roomType.ToString(),
            ["encounterId"] = encounter.Id.Entry,
            ["isInjected"] = true,
            ["player"] = players[0]!.DeepClone(),
            ["runRng"] = saved["rng"]?.DeepClone()
                ?? throw new InvalidDataException("Native run RNG is absent."),
            ["runOdds"] = saved["odds"]?.DeepClone()
                ?? throw new InvalidDataException("Native run odds are absent."),
            ["sharedRelicBag"] = saved["shared_relic_grab_bag"]?.DeepClone()
                ?? throw new InvalidDataException("Native shared relic bag is absent."),
            ["roomSequence"] = roomSequence.DeepClone(),
            ["eventsSeen"] = saved["events_seen"]?.DeepClone() ?? new JsonArray(),
            ["nextRoomId"] = run.NextRoomId,
            ["cardRemovalsUsed"] = run.Players[0].ExtraFields.CardShopRemovalsUsed,
            ["mapPointHistory"] = saved["map_point_history"]?.DeepClone() ?? new JsonArray(),
            ["extraFields"] = saved["extra_fields"]?.DeepClone() ?? new JsonObject(),
        };
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, entry.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
