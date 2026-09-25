using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace OfflineSearchHarness;

internal static class RootExport
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Write(CombatState combat, RunState run, string path)
    {
        Player player = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("Cannot export a combat without the local player.");
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("Cannot export a combat without player combat state.");
        CardModel[] deck = player.Deck.Cards.ToArray();

        var root = new
        {
            SchemaVersion = 1,
            UpstreamCommit = "d231e9e5",
            Seed = run.Rng.StringSeed,
            Ascension = run.AscensionLevel,
            ActIndex = run.CurrentActIndex,
            ActId = run.Acts[run.CurrentActIndex].Id.Entry,
            EncounterId = combat.Encounter?.Id.Entry,
            RoomType = combat.Encounter?.RoomType.ToString(),
            Player = new
            {
                CharacterId = player.Character.Id.Entry,
                Hp = player.Creature.CurrentHp,
                MaxHp = player.Creature.MaxHp,
                Block = player.Creature.Block,
                Energy = state.Energy,
                MaxEnergy = state.MaxEnergy,
                Stars = state.Stars,
                Powers = player.Creature.Powers.Select(power => new { Id = power.Id.Entry, power.Amount }).ToArray(),
                Relics = player.Relics.Select(relic => new
                {
                    Id = relic.Id.Entry,
                    Counter = relic.ShowCounter ? (int?)relic.DisplayAmount : null,
                }).ToArray(),
                Potions = Enumerable.Range(0, player.PotionSlots.Count)
                    .Select(slot => new { Slot = slot, Id = player.GetPotionAtSlotIndex(slot)?.Id.Entry })
                    .Where(potion => potion.Id != null).ToArray(),
                PotionSlotCount = player.PotionSlots.Count,
                Deck = deck.Select(card => Card(card)).ToArray(),
                Piles = new
                {
                    Hand = state.Hand.Cards.Select(card => PileCard(card, deck)).ToArray(),
                    Draw = state.DrawPile.Cards.Select(card => PileCard(card, deck)).ToArray(),
                    Discard = state.DiscardPile.Cards.Select(card => PileCard(card, deck)).ToArray(),
                    Exhaust = state.ExhaustPile.Cards.Select(card => PileCard(card, deck)).ToArray(),
                },
            },
            Enemies = combat.Enemies.Select(enemy => new
            {
                Slot = enemy.SlotName,
                Id = enemy.Monster?.Id.Entry,
                Hp = enemy.CurrentHp,
                MaxHp = enemy.MaxHp,
                Block = enemy.Block,
                Powers = enemy.Powers.Select(power => new { Id = power.Id.Entry, power.Amount }).ToArray(),
                NextMoveId = enemy.Monster?.NextMove?.Id,
            }).ToArray(),
            Rng = new
            {
                Run = Enum.GetValues<RunRngType>().Select(type => Stream(type.ToString(), run.Rng.Seed,
                    run.Rng.GetRng(type).ToSerializable())).ToArray(),
                Player = Enum.GetValues<PlayerRngType>().Select(type => Stream(type.ToString(), player.PlayerRng.Seed,
                    player.PlayerRng.GetRng(type).ToSerializable())).ToArray(),
            },
            Round = combat.RoundNumber,
            Turn = state.TurnNumber,
        };

        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(root, Json));
    }

    private static object Card(CardModel card) => new
    {
        Id = card.Id.Entry,
        Upgrade = card.CurrentUpgradeLevel,
        Enchantment = card.Enchantment?.Id.Entry,
    };

    private static object PileCard(CardModel card, CardModel[] deck)
    {
        CardModel source = card.DeckVersion ?? card;
        int deckIndex = Array.FindIndex(deck, candidate => ReferenceEquals(candidate, source));
        return new
        {
            Id = card.Id.Entry,
            Upgrade = card.CurrentUpgradeLevel,
            Enchantment = card.Enchantment?.Id.Entry,
            DeckIndex = deckIndex,
        };
    }

    private static object Stream(string typeName, ulong baseSeed, SerializableRng state)
    {
        string name = StringHelper.SnakeCase(typeName);
        return new
        {
            Name = name,
            Seed = unchecked(baseSeed + StringHelper.GetDeterministicHashCode(name)),
            Counter = state.counter,
            S0 = state.state0,
            S1 = state.state1,
            S2 = state.state2,
            S3 = state.state3,
        };
    }
}
