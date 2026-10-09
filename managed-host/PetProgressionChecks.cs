using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class PetProgressionChecks
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidDataException("PET_PROGRESSION_CHECK_FAILED " + name);
            checks++;
        }

        foreach (var pet in ShopCatalog.All.Where(x => x.Category == 15))
        {
            var initial = PetProgression.NormalizeState(new PetState(pet.ItemCode, 0, 0, 0, 0, 0, 0, 0, 48));
            Check(initial.CurrentStage == pet.PetModelStage && initial.MaximumStage == pet.PetUpgradeStage,
                $"catalog initialization {pet.ItemCode}");
            var hasDust = pet.PetGoldDustItemCode != 0 && ShopCatalog.TryGet(19, pet.PetGoldDustItemCode, out _);
            if (!hasDust) continue;
            var earned = initial with { MaximumStage = 3 };
            Check(PetProgression.NormalizeState(earned) == earned, $"earned dust allowance {pet.ItemCode}");
            for (byte stage = pet.PetMinimumModelStage; stage < 3; stage++)
            {
                Check(ShopCatalog.TryGetPetGrowthStage(pet.PetGrowthClass, stage, out var growth), "growth row");
                var state = earned with { CurrentStage = stage, Level = growth.MaximumLevel - 1,
                    Experience = growth.ExperiencePerLevel - 1 };
                Check(PetProgression.AddExperience(state, 0).State == state, "zero reward is inert");
                var result = PetProgression.AddExperience(state, 1);
                Check(result.LevelOrStageChanged && result.State.CurrentStage == stage + 1
                    && result.State.MaximumStage == 3 && result.State.Level == 0 && result.State.Experience == 0
                    && result.State.Durability == 48, $"exact threshold advances immediately {pet.ItemCode}/{stage}");
                var capped = PetProgression.AddExperience(state with { MaximumStage = stage }, 1).State;
                Check(capped.CurrentStage == stage && capped.MaximumStage == stage
                    && capped.Level == growth.MaximumLevel && capped.Experience == 0, "no unearned allowance");
                var remainder = PetProgression.AddExperience(state, 2).State;
                Check(remainder.CurrentStage == stage + 1 && remainder.Experience == 1, "carry next-stage remainder");
            }
        }

        Check(ShopCatalog.TryGet(15, ShopCatalog.HeroDragonCode, out var hero) && hero.PetMaxDurability == 50
            && hero.DurationDays == 0, "hero lifespan fifty is not a fifty-day expiry");
        var character = new CharacterRecord { Items = [new() { ItemCode = ShopCatalog.HeroDragonCode, Quantity = 1 }] };
        var stateHero = PetProgression.GetState(character, ShopCatalog.HeroDragonCode);
        Check(stateHero.Durability == 50 && stateHero.CurrentStage == 3 && stateHero.MaximumStage == 3,
            "unset hero durability uses corrected catalog");
        character.Items[0].PetDurability = 17;
        Check(PetProgression.GetState(character, ShopCatalog.HeroDragonCode).Durability == 17,
            "explicit durability is not refilled by resource correction");
        var payload = NetworkAdapterService.BuildPetInventoryPayload(character);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8)) == 2100123100u,
            "hero inventory remains permanent");
        Console.WriteLine($"PET_PROGRESSION_CHECKS_PASS checks={checks}");
    }
}
