﻿using System.Text;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.Ghost.Roles.Components;
using Content.Server.Nutrition.Components;
using Content.Server.Nyanotrasen.Kitchen.Components;
using Content.Shared.Atmos.Rotting;
using Content.Shared.Body.Components;
using Content.Shared.Buckle.Components;
using Content.Shared.Chemistry.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nyanotrasen.Kitchen.Prototypes;
using Content.Shared.Paper;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server.Nyanotrasen.Kitchen.EntitySystems;

public sealed partial class DeepFryerSystem
{
    /// <summary>
    ///     Make an item look deep-fried.
    /// </summary>
    public void MakeCrispy(EntityUid item, ProtoId<CrispinessLevelSetPrototype> crispiness) // Frontier: add CrispinessLevelSetPrototype
    {
        EnsureComp<AppearanceComponent>(item);
        // Frontier: apply the fryer-appropriate shader
        var deepFried = EnsureComp<DeepFriedComponent>(item);
        var oldCrispinessProto = deepFried.CrispinessLevelSet;
        SetDeepFriedCrispinessLevelSet(item, deepFried, crispiness);

        if (_prototypeManager.TryIndex<CrispinessLevelSetPrototype>(oldCrispinessProto, out var oldCrispiness))
        {
            _appearanceSystem.SetData(item, oldCrispiness.Visual, false);
        }
        if (_prototypeManager.TryIndex<CrispinessLevelSetPrototype>(crispiness, out var newCrispiness))
        {
            _appearanceSystem.SetData(item, newCrispiness.Visual, true);
        }
        // End Frontier
    }

    /// <summary>
    ///     Turn a dead mob into food.
    /// </summary>
    /// <remarks>
    ///     This is meant to be an irreversible process, similar to gibbing.
    /// </remarks>
    public bool TryMakeMobIntoFood(EntityUid mob, MobStateComponent mobStateComponent, bool force = false)
    {
        // Don't do anything to mobs until they're dead.
        if (force || _mobStateSystem.IsDead(mob, mobStateComponent))
        {
            RemComp<ActiveNPCComponent>(mob);
            RemComp<AtmosExposedComponent>(mob);
            RemComp<BarotraumaComponent>(mob);
            RemComp<BuckleComponent>(mob);
            RemComp<GhostTakeoverAvailableComponent>(mob);
            RemComp<InternalsComponent>(mob);
            RemComp<PerishableComponent>(mob);
            RemComp<RespiratorComponent>(mob);
            RemComp<RottingComponent>(mob);

            // Ensure it's Food here, so it passes the whitelist.
            var mobFoodComponent = EnsureComp<FoodComponent>(mob);
            if (!_solutionContainerSystem.EnsureSolutionEntity(mob, mobFoodComponent.Solution, out var alreadyHadFood, out var mobFood))
                return false;

            var mobFoodSolution = mobFood.Value.Comp.Solution;

            // This line here is mainly for mice, because they have a food
            // component that mirrors how much blood they have, which is
            // used for the reagent grinder.
            if (alreadyHadFood)
                mobFoodSolution.RemoveAllSolution();

            if (TryComp<BloodstreamComponent>(mob, out var bloodstreamComponent) && bloodstreamComponent.ChemicalSolution != null)
            {
                // Fry off any blood into protein.
                var bloodSolution = bloodstreamComponent.BloodSolution;
                var solPresent = bloodSolution!.Value.Comp.Solution.Volume;
                mobFoodSolution.RemoveReagent("Blood", FixedPoint2.MaxValue);
                var bloodRemoved = solPresent - bloodSolution.Value.Comp.Solution.Volume;

                var proteinQuantity = bloodRemoved * BloodToProteinRatio;
                mobFoodSolution.MaxVolume += proteinQuantity;
                mobFoodSolution.AddReagent("Protein", proteinQuantity);

                // This is a heuristic. If you had blood, you might just taste meaty.
                if (bloodRemoved > FixedPoint2.Zero)
                    EnsureComp<FlavorProfileComponent>(mob).Flavors.Add(MobFlavorMeat);

                // Bring in whatever chemicals they had in them too.
                mobFoodSolution.MaxVolume +=
                    bloodstreamComponent.ChemicalSolution.Value.Comp.Solution.Volume;
                mobFoodSolution.AddSolution(bloodstreamComponent.ChemicalSolution.Value.Comp.Solution, _prototypeManager);
            }
            _solutionContainerSystem.UpdateChemicals(mobFood.Value);

            return true;
        }

        return false;
    }

    /// <summary>
    ///     Make an item actually edible.
    /// </summary>
    private void MakeEdible(EntityUid uid, DeepFryerComponent component, EntityUid item, FixedPoint2 solutionQuantity)
    {
        if (!TryComp<DeepFriedComponent>(item, out var deepFriedComponent))
        {
            _sawmill.Error($"{ToPrettyString(item)} is missing the DeepFriedComponent before being made Edible.");
            return;
        }

        // Remove any components that wouldn't make sense anymore.
        RemComp<ButcherableComponent>(item);

        if (TryComp<PaperComponent>(item, out var paperComponent))
        {
            var stringBuilder = new StringBuilder();

            for (var i = 0; i < paperComponent.Content.Length; ++i)
            {
                var uchar = paperComponent.Content.Substring(i, 1);

                if (uchar == "\n" || _random.Prob(0.4f))
                    stringBuilder.Append(uchar);
                else
                    stringBuilder.Append("x");
            }

            paperComponent.Content = stringBuilder.ToString();
        }

        var foodComponent = EnsureComp<FoodComponent>(item);
        var extraSolution = new Solution();
        if (TryComp(item, out FlavorProfileComponent? flavorProfileComponent))
        {
            HashSet<string> goodFlavors = new(flavorProfileComponent.Flavors);
            goodFlavors.IntersectWith(component.GoodFlavors);

            HashSet<string> badFlavors = new(flavorProfileComponent.Flavors);
            badFlavors.IntersectWith(component.BadFlavors);

            deepFriedComponent.PriceCoefficient = Math.Max(0.01f,
                1.0f
                + goodFlavors.Count * component.GoodFlavorPriceBonus
                - badFlavors.Count * component.BadFlavorPriceMalus);

            if (goodFlavors.Count > 0)
            {
                foreach (var reagent in component.GoodReagents)
                {
                    extraSolution.AddReagent(reagent.Reagent.ToString(), reagent.Quantity * goodFlavors.Count);

                    // Mask the taste of "medicine."
                    flavorProfileComponent.IgnoreReagents.Add(reagent.Reagent.ToString());
                }
            }

            if (badFlavors.Count > 0)
            {
                foreach (var reagent in component.BadReagents)
                {
                    extraSolution.AddReagent(reagent.Reagent.ToString(), reagent.Quantity * badFlavors.Count);
                }
            }
        }
        else
        {
            flavorProfileComponent = EnsureComp<FlavorProfileComponent>(item);
            // TODO: Default flavor?
        }

        // Make sure there's enough room for the fryer solution.
        if (!_solutionContainerSystem.EnsureSolutionEntity(item, foodComponent.Solution, out var foodEnt))
            return;

        var foodSolution = foodEnt.Value.Comp.Solution;

        // The solution quantity is used to give the fried food an extra
        // buffer too, to support injectables or condiments.
        foodSolution.MaxVolume = 2 * solutionQuantity + foodSolution.Volume + extraSolution.Volume;
        foodSolution.AddSolution(component.Solution.SplitSolution(solutionQuantity), _prototypeManager);
        foodSolution.AddSolution(extraSolution, _prototypeManager);
        _solutionContainerSystem.UpdateChemicals(foodEnt.Value);
    }
}
