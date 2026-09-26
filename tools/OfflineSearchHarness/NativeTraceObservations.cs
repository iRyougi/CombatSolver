using System.Security.Cryptography;
using System.Text.Json;
using CombatSolver;

namespace OfflineSearchHarness;

/// <summary>
/// Streams the retention evidence exposed by SearchPathObserver without holding search nodes.
/// This is a partial trace: the observer does not report B2/B3/B4 trigger entry points.
/// </summary>
internal sealed class NativeTraceObservations : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(UnattendedTestFiles.JsonOptions)
    {
        WriteIndented = false,
    };

    private readonly string _partialPath;
    private readonly string _rawPath;
    private readonly StreamWriter _writer;
    private readonly object _writeLock = new();
    private long _written;

    public SearchPathObserver Observer { get; }

    public NativeTraceObservations(string requestedPath)
    {
        _partialPath = requestedPath + ".partial.json";
        _rawPath = requestedPath + ".partial-observations.jsonl";
        Directory.CreateDirectory(Path.GetDirectoryName(requestedPath)!);
        _writer = new StreamWriter(new FileStream(_rawPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        // Rich evaluation and ranking fields exist only on GlobalRetention observations.
        Observer = new SearchPathObserver(_ => false, Observe, _ => true);
    }

    private void Observe(SearchPathObservation observation)
    {
        if (observation.Stage != SearchPathObservationStage.GlobalRetention)
            return;

        lock (_writeLock)
        {
            using IncrementalHash prefix = NewPrefix(observation.RootTurnSetupChoices);
            foreach (PlanAction action in observation.Actions)
                Append(prefix, action);
            _writer.WriteLine(JsonSerializer.Serialize(new
            {
                prefix = Convert.ToHexString(prefix.GetCurrentHash()),
                observation.SolverId,
                observation.BeamWidth,
                observation.BoundaryId,
                observation.StateKey,
                observation.ParentStateKey,
                observation.Turn,
                observation.ActionCount,
                observation.PolicyLabel,
                observation.ParentPolicyLabel,
                observation.Traits,
                observation.BoundaryReason,
                observation.IsTerminal,
                observation.HasPredictionRisk,
                observation.PlayerHp,
                observation.PlayerMaxHp,
                observation.EnemyHp,
                observation.ShufflesCrossed,
                observation.CumulativeEnemyHpLost,
                observation.Retention,
                action = observation.Actions.Count == 0 ? null : ActionIdentity(observation.Actions[^1]),
            }, Json));
            _written++;
        }
    }

    public void WritePartial(SolverResult result)
    {
        lock (_writeLock)
            _writer.Dispose();

        using IncrementalHash prefix = NewPrefix(result.TurnSetupChoices);
        Dictionary<string, int> selectedPrefixes = new(StringComparer.Ordinal)
        {
            [Convert.ToHexString(prefix.GetCurrentHash())] = 0,
        };
        for (int index = 0; index < result.BestNode.Actions.Count; index++)
        {
            Append(prefix, result.BestNode.Actions[index]);
            selectedPrefixes.Add(Convert.ToHexString(prefix.GetCurrentHash()), index + 1);
        }

        Dictionary<int, HashSet<int>> selectedBoundaries = [];
        foreach (string line in File.ReadLines(_rawPath))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement item = document.RootElement;
            string key = item.GetProperty("prefix").GetString()!;
            if (!selectedPrefixes.TryGetValue(key, out int step))
                continue;
            int boundaryId = item.GetProperty("boundaryId").GetInt32();
            if (!selectedBoundaries.TryGetValue(boundaryId, out HashSet<int>? steps))
                selectedBoundaries[boundaryId] = steps = [];
            steps.Add(step);
        }

        Dictionary<int, List<JsonElement>> candidatesByBoundary = [];
        foreach (string line in File.ReadLines(_rawPath))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement item = document.RootElement;
            int boundaryId = item.GetProperty("boundaryId").GetInt32();
            if (!selectedBoundaries.ContainsKey(boundaryId))
                continue;
            if (!candidatesByBoundary.TryGetValue(boundaryId, out List<JsonElement>? candidates))
                candidatesByBoundary[boundaryId] = candidates = [];
            candidates.Add(item.Clone());
        }

        var nodes = selectedBoundaries.OrderBy(item => item.Key)
            .SelectMany(item => item.Value.OrderBy(step => step).Select(step =>
            {
                JsonElement[] candidates = candidatesByBoundary[item.Key].ToArray();
                JsonElement[] selected = candidates.Where(candidate =>
                    selectedPrefixes.TryGetValue(candidate.GetProperty("prefix").GetString()!, out int candidateStep)
                    && candidateStep == step).ToArray();
                return new
                {
                    step,
                    boundaryId = item.Key,
                    fingerprintFields = selected.Select(candidate => new
                    {
                        stateKey = candidate.GetProperty("stateKey"),
                        parentStateKey = candidate.GetProperty("parentStateKey"),
                    }).ToArray(),
                    evaluation = selected.Select(candidate => candidate.GetProperty("retention")
                        .GetProperty("evaluation")).ToArray(),
                    selected,
                    candidates,
                };
            })).ToArray();
        int[] observedSteps = selectedBoundaries.Values.SelectMany(steps => steps).Distinct().ToArray();
        int[] unobservedSteps = Enumerable.Range(0, result.BestNode.Actions.Count + 1)
            .Except(observedSteps).ToArray();

        using FileStream stream = new(_partialPath, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, new
        {
            schemaVersion = 1,
            complete = false,
            pending = "B1-PENDING-025",
            reason = "SearchPathObserver exposes evaluations only at GlobalRetention and no B2/B3/B4 trigger events.",
            triggered = (string[]?)null,
            candidateStage = SearchPathObservationStage.GlobalRetention,
            route = result.BestNode.Actions.Select(ActionIdentity).ToArray(),
            turnSetupChoices = result.TurnSetupChoices.Select(ChoiceIdentity).ToArray(),
            nodes,
            unobservedSteps,
            rawObservationCount = _written,
            rawObservationPath = _rawPath,
        }, Json);
    }

    private static object ActionIdentity(PlanAction action) => new
    {
        action.Kind,
        action.Turn,
        action.CardId,
        action.CardOccurrence,
        action.TargetIndex,
        action.TargetCombatId,
        action.PotionId,
        action.PotionSlot,
        action.CardStateKey,
        action.CardStateOccurrence,
        action.ReplayCount,
        action.EndsPlayerTurn,
        action.CardUpgradeLevel,
        action.CardEnchantmentId,
        action.NestedChoicesBeforePrimary,
        choice = action.Choice == null ? null : ChoiceIdentity(action.Choice),
        nestedChoices = action.NestedChoices?.Select(ChoiceIdentity).ToArray(),
        turnStartChoices = action.TurnStartChoices?.Select(ChoiceIdentity).ToArray(),
        relicIds = action.RelicEffects?.Select(effect => effect.RelicId).ToArray(),
    };

    private static object ChoiceIdentity(PlanCardChoice choice) => new
    {
        choice.Effect,
        choice.SourcePile,
        choice.SourceId,
        choice.ContextId,
        choice.Timing,
        cards = choice.Cards.Select(card => new
        {
            card.CardId,
            card.UpgradeLevel,
            card.StateKey,
            card.SourceOccurrence,
            card.OptionOccurrence,
        }).ToArray(),
    };

    private static IncrementalHash NewPrefix(IReadOnlyList<PlanCardChoice> choices)
    {
        IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(choices, Json));
        return hash;
    }

    private static void Append(IncrementalHash hash, PlanAction action)
        => hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(action with
        { RelicEffects = null, CardTitle = "", TargetName = "", PotionTitle = "" }, Json));

    public void Dispose() => _writer.Dispose();
}
