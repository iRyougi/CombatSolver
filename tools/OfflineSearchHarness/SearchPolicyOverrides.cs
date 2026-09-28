using System.Text.Json;
using System.Text.Json.Serialization;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;

namespace OfflineSearchHarness;

/// <summary>Task F input uses the persisted SolverSettings field names, plus the runtime-only policy fields.</summary>
internal sealed class SearchPolicyOverrides
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly JsonDocument _document;
    private JsonElement Root => _document.RootElement;

    private SearchPolicyOverrides(JsonDocument document) => _document = document;

    public static SearchPolicyOverrides? Load(string? path)
    {
        if (path == null) return null;
        JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("--search-policy must contain a JSON object.");
        HashSet<string> fields = new(StringComparer.OrdinalIgnoreCase)
        {
            "potionPolicy", "potionStrategy", "predictPotionReward", "relicCounterRules",
            "relicStrategyEnabled", "growthBudgets", "brightestFlameMaxHpLossLimit",
            "ignoreLongTermRewards", "actTransitionBossHpStrategy", "finalBossHpStrategy",
            "act3BossStrategy", "acceptableBattleHpLoss", "stopAtAcceptableBattleHpLoss",
            "theftPolicy", "searchBeamWidth", "searchPotionFreeBeamWidth",
            "searchPotionBeamWidth", "searchMaxExpandedNodes", "searchMaxCardBranchesPerNode",
            "searchMaxPileChoiceBranchesPerAction", "searchMaxHandChoiceBranchesPerAction",
            "searchTimeLimitSeconds",
        };
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
            if (!fields.Contains(property.Name))
                throw new InvalidDataException($"Unsupported search policy field: {property.Name}.");
        return new SearchPolicyOverrides(document);
    }

    private T Value<T>(string name, T fallback)
        => Root.TryGetProperty(name, out JsonElement value)
            ? value.Deserialize<T>(Json)!
            : fallback;

    public SolverSettingsData Apply(SolverSettingsData settings)
    {
        SolverSettingsData updated = settings with
        {
            PotionPolicy = Value("potionPolicy", settings.PotionPolicy),
            PredictPotionReward = Value("predictPotionReward", settings.PredictPotionReward),
            RelicStrategyEnabled = Value("relicStrategyEnabled", settings.RelicStrategyEnabled),
            RelicCounterRules = Value("relicCounterRules", settings.RelicCounterRules),
            GrowthBudgets = Value("growthBudgets", settings.GrowthBudgets),
            BrightestFlameMaxHpLossLimit = Value("brightestFlameMaxHpLossLimit", settings.BrightestFlameMaxHpLossLimit),
            IgnoreLongTermRewards = Value("ignoreLongTermRewards", settings.IgnoreLongTermRewards),
            ActTransitionBossHpStrategy = Value("actTransitionBossHpStrategy", settings.ActTransitionBossHpStrategy),
            FinalBossHpStrategy = Value("finalBossHpStrategy", settings.FinalBossHpStrategy),
            AcceptableBattleHpLoss = Value("acceptableBattleHpLoss", settings.AcceptableBattleHpLoss),
            StopAtAcceptableBattleHpLoss = Value("stopAtAcceptableBattleHpLoss", settings.StopAtAcceptableBattleHpLoss),
            // Legacy split widths only take effect when the unified field is absent.
            SearchBeamWidth = Root.TryGetProperty("searchBeamWidth", out _)
                ? Value("searchBeamWidth", settings.SearchBeamWidth)
                : Root.TryGetProperty("searchPotionFreeBeamWidth", out _)
                    || Root.TryGetProperty("searchPotionBeamWidth", out _)
                    ? null : settings.SearchBeamWidth,
            SearchPotionFreeBeamWidth = Value("searchPotionFreeBeamWidth", settings.SearchPotionFreeBeamWidth),
            SearchPotionBeamWidth = Value("searchPotionBeamWidth", settings.SearchPotionBeamWidth),
            SearchMaxExpandedNodes = Value("searchMaxExpandedNodes", settings.SearchMaxExpandedNodes),
            SearchMaxCardBranchesPerNode = Value("searchMaxCardBranchesPerNode", settings.SearchMaxCardBranchesPerNode),
            SearchMaxPileChoiceBranchesPerAction = Value("searchMaxPileChoiceBranchesPerAction", settings.SearchMaxPileChoiceBranchesPerAction),
            SearchMaxHandChoiceBranchesPerAction = Value("searchMaxHandChoiceBranchesPerAction", settings.SearchMaxHandChoiceBranchesPerAction),
            SearchTimeLimitSeconds = Value("searchTimeLimitSeconds", settings.SearchTimeLimitSeconds),
        };
        if (Root.TryGetProperty("potionStrategy", out JsonElement strategy))
        {
            if (strategy.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("potionStrategy must be an object.");
            foreach (JsonProperty field in strategy.EnumerateObject())
                if (field.Name is not ("preset" or "directives"))
                    throw new InvalidDataException($"Unsupported potionStrategy field: {field.Name}.");
            if (strategy.TryGetProperty("directives", out JsonElement directives))
                updated = updated with { PotionDirectives = directives.Deserialize<PersistedPotionDirective[]>(Json)
                    ?? throw new InvalidDataException("potionStrategy.directives must be an array.") };
        }
        return updated;
    }

    public SearchPolicySnapshot ApplyToRoot(SearchPolicySnapshot policy, CombatState state)
    {
        if (Root.TryGetProperty("potionStrategy", out JsonElement strategy)
            && strategy.TryGetProperty("preset", out JsonElement presetValue))
        {
            PotionStrategyPreset preset = presetValue.Deserialize<PotionStrategyPreset>(Json);
            if (!Enum.IsDefined(preset)) throw new InvalidDataException("Invalid potionStrategy.preset.");
            List<PotionSlotDirective> slots = [];
            var player = MegaCrit.Sts2.Core.Context.LocalContext.GetMe(state)
                ?? throw new InvalidOperationException("No local player for potion strategy.");
            for (int slot = 0; slot < player.PotionSlots.Count; slot++)
            {
                var potion = player.PotionSlots[slot];
                if (potion == null) continue;
                slots.Add(new PotionSlotDirective(slot, potion.Id.Entry,
                    policy.PotionStrategy.Resolve(slot, potion.Id.Entry)));
            }
            SolverSettingsData settings = SolverSettings.ApplyPotionPreset(
                SolverSettings.Current, slots, preset);
            policy = policy with { PotionStrategy = new PotionStrategySnapshot(
                policy.PotionPolicy, settings.PotionDirectives.Select(d =>
                    new PotionSlotDirective(d.Slot, d.PotionId, d.Directive))) };
        }
        if (Root.TryGetProperty("theftPolicy", out JsonElement theft))
            policy = policy with { TheftPolicy = theft.Deserialize<SolverTheftPolicy>(Json) };
        if (Root.TryGetProperty("act3BossStrategy", out JsonElement act3))
            policy = policy with { Act3BossStrategy = act3.GetBoolean() && policy.Act3BossStrategy };
        return policy;
    }

    public int? TimeLimitMilliseconds => Root.TryGetProperty("searchTimeLimitSeconds", out JsonElement value)
        ? checked((int)Math.Round(value.GetDouble() * 1000d, MidpointRounding.AwayFromZero)) : null;
}
