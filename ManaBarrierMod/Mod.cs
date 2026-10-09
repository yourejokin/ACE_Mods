using System;
using System.IO;
using System.Text.Json;
using HarmonyLib;
using log4net;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Mods;
using ACE.Server.Network;
using ACE.Server.Network.Enum;
using ACE.Server.WorldObjects;

namespace ManaBarrierMod
{
    public sealed class Mod : IHarmonyMod
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(Mod));

        private static ManaBarrierSettings settings;

        private readonly Harmony harmony = new Harmony("ace.mods.mana-barrier");

        public void Initialize()
        {
            settings = LoadSettings();

            var takeDamageMethod = AccessTools.Method(
                typeof(Player),
                nameof(Player.TakeDamage),
                new[]
                {
                    typeof(WorldObject),
                    typeof(DamageType),
                    typeof(float),
                    typeof(BodyPart),
                    typeof(bool),
                    typeof(AttackConditions)
                });

            if (takeDamageMethod == null)
                throw new MissingMethodException("Could not find the player damage method.");

            harmony.Patch(
                takeDamageMethod,
                prefix: new HarmonyMethod(typeof(Mod), nameof(PlayerTakeDamagePrefix)));

            var takeDamageOverTimeMethod = AccessTools.Method(
                typeof(Player),
                nameof(Player.TakeDamageOverTime),
                new[] { typeof(float), typeof(DamageType) });

            if (takeDamageOverTimeMethod == null)
                throw new MissingMethodException("Could not find the player periodic damage method.");

            harmony.Patch(
                takeDamageOverTimeMethod,
                prefix: new HarmonyMethod(typeof(Mod), nameof(PlayerTakeDamageOverTimePrefix)));
        }

        public void Dispose()
        {
            harmony.UnpatchAll(harmony.Id);
        }

        private ManaBarrierSettings LoadSettings()
        {
            var modContainer = this.GetModContainer();
            if (modContainer == null)
                throw new InvalidOperationException("Could not find the Mana Barrier mod container.");

            var metadataPath = modContainer.MetadataPath;
            var loadedSettings = JsonSerializer.Deserialize<ManaBarrierSettings>(
                File.ReadAllText(metadataPath),
                ConfigManager.SerializerOptions);

            if (loadedSettings == null)
                throw new InvalidDataException($"Could not load Mana Barrier settings from {metadataPath}.");

            if (loadedSettings.MaximumManaSurcharge < loadedSettings.MinimumManaSurcharge ||
                loadedSettings.MinimumManaSurcharge < 0 ||
                loadedSettings.SkillTotalForMinimumSurcharge <= 0)
            {
                throw new InvalidDataException(
                    "Mana Barrier settings must have a non-negative minimum surcharge, " +
                    "a maximum surcharge greater than or equal to the minimum, and a positive " +
                    "skill total for the minimum surcharge.");
            }

            return loadedSettings;
        }

        private static void PlayerTakeDamagePrefix(
            Player __instance,
            DamageType damageType,
            ref float _amount)
        {
            ApplyManaBarrier(__instance, damageType, ref _amount);
        }

        private static void PlayerTakeDamageOverTimePrefix(
            Player __instance,
            DamageType damageType,
            ref float _amount)
        {
            ApplyManaBarrier(__instance, damageType, ref _amount);
        }

        private static void ApplyManaBarrier(Player player, DamageType damageType, ref float amount)
        {
            if (amount <= 0 || player.IsDead || !HasManaBarrierSkills(player))
                return;

            // Mana and stamina drains already target their respective vitals and should
            // not be converted into additional mana-barrier damage.
            if (damageType == DamageType.Mana || damageType == DamageType.Stamina)
                return;

            var incomingDamage = (uint)Math.Round(amount);
            if (incomingDamage == 0)
                return;

            // Split damage evenly, assigning the odd point to mana. The mana share
            // then receives a surcharge that is reduced by the combined Mana
            // Conversion and Assess Creature skill totals.
            var calculatedHealthDamage = incomingDamage / 2;
            var calculatedManaDamage = incomingDamage - calculatedHealthDamage;
            var effectiveManaConversion = GetEffectiveManaConversion(player);
            var assessCreature = GetAssessCreatureValue(player);
            var combinedSkillTotal = effectiveManaConversion + assessCreature;
            var skillProgress = Math.Min(1.0, combinedSkillTotal / settings.SkillTotalForMinimumSurcharge);
            var manaSurcharge = settings.MaximumManaSurcharge -
                (settings.MaximumManaSurcharge - settings.MinimumManaSurcharge) * skillProgress;
            var requestedManaDamage = (uint)Math.Min(
                uint.MaxValue,
                Math.Ceiling(calculatedManaDamage * (1.0 + manaSurcharge)));
            var manaDamage = Math.Min(requestedManaDamage, player.Mana.Current);
            var unabsorbedManaDamage = requestedManaDamage > manaDamage
                ? requestedManaDamage - manaDamage
                : 0;
            var healthDamage = calculatedHealthDamage + unabsorbedManaDamage;

            log.Debug(
                $"[ManaBarrier] Player={player.Name} Guid={player.Guid} " +
                $"Incoming={incomingDamage} ManaConversion={effectiveManaConversion} " +
                $"AssessCreature={assessCreature} CombinedSkill={combinedSkillTotal} " +
                $"SkillProgress={skillProgress:0.####} Surcharge={manaSurcharge:P2} " +
                $"CalculatedHealth={calculatedHealthDamage} CalculatedMana={calculatedManaDamage} " +
                $"RequestedMana={requestedManaDamage} CurrentMana={player.Mana.Current} " +
                $"ActualMana={manaDamage} UnabsorbedMana={unabsorbedManaDamage} " +
                $"HealthDamage={healthDamage}.");

            if (manaDamage > 0)
                player.UpdateVitalDelta(player.Mana, -(int)manaDamage);

            amount = healthDamage;

            player.SendMessage(
                $"Mana Barrier absorbed {manaDamage} damage.",
                ChatMessageType.Combat);
        }

        [CommandHandler("mb", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Displays your current Mana Barrier surcharge.")]
        [CommandHandler("manabarrier", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Displays your current Mana Barrier surcharge.")]
        public static void HandleManaBarrierStatus(Session session, params string[] parameters)
        {
            var player = session.Player;

            if (!HasManaBarrierSkills(player))
            {
                var missingRequirements = GetMissingSkillRequirements(player);

                player.SendMessage(
                    $"Mana Barrier is inactive. Requires {missingRequirements}.",
                    ChatMessageType.Broadcast);
                return;
            }

            var baseManaConversion = player.GetCreatureSkill(Skill.ManaConversion).Current;
            var wandManaConversionModifier = WorldObject.GetWeaponManaConversionModifier(player);
            var effectiveManaConversion = GetEffectiveManaConversion(player);
            var assessCreature = GetAssessCreatureValue(player);
            var combinedSkillTotal = effectiveManaConversion + assessCreature;
            var surcharge = GetManaSurcharge(player) * 100.0;

            player.SendMessage(
                $"Mana Barrier surcharge: {surcharge:0.##}% | " +
                $"Mana Conversion: {baseManaConversion} (skill) x {wandManaConversionModifier:0.###} (wand modifier) = {effectiveManaConversion} | " +
                $"Assess Creature: {assessCreature} | Combined: {combinedSkillTotal}.",
                ChatMessageType.Broadcast);
        }

        private static double GetManaSurcharge(Player player)
        {
            var combinedSkillTotal = GetCombinedSkillTotal(player);

            var skillProgress = Math.Min(
                1.0,
                combinedSkillTotal / settings.SkillTotalForMinimumSurcharge);

            return settings.MaximumManaSurcharge -
                (settings.MaximumManaSurcharge - settings.MinimumManaSurcharge) * skillProgress;
        }

        private static uint GetEffectiveManaConversion(Player player)
        {
            var manaConversion = player.GetCreatureSkill(Skill.ManaConversion).Current;
            var wandManaConversionModifier = WorldObject.GetWeaponManaConversionModifier(player);

            return (uint)Math.Round(manaConversion * wandManaConversionModifier);
        }

        private static uint GetCombinedSkillTotal(Player player)
        {
            return GetEffectiveManaConversion(player) +
                GetAssessCreatureValue(player);
        }

        private static uint GetAssessCreatureValue(Player player)
        {
            var assessCreature = player.GetCreatureSkill(Skill.AssessCreature);

            return assessCreature.AdvancementClass == SkillAdvancementClass.Untrained
                ? 0
                : assessCreature.Current;
        }

        private static string GetMissingSkillRequirements(Player player)
        {
            var missingRequirements = string.Empty;

            if (!IsSpecialized(player, Skill.ManaConversion))
                missingRequirements = "specialized Mana Conversion";

            if (!IsSpecialized(player, Skill.WarMagic) &&
                !IsSpecialized(player, Skill.VoidMagic))
            {
                if (missingRequirements.Length > 0)
                    missingRequirements += " and ";

                missingRequirements += "either specialized War Magic or specialized Void Magic";
            }

            return missingRequirements;
        }

        private static bool HasManaBarrierSkills(Player player)
        {
            return IsSpecialized(player, Skill.ManaConversion) &&
                (IsSpecialized(player, Skill.WarMagic) || IsSpecialized(player, Skill.VoidMagic));
        }

        private static bool IsSpecialized(Player player, Skill skill)
        {
            return player.Skills.TryGetValue(skill, out var creatureSkill) &&
                creatureSkill.AdvancementClass == SkillAdvancementClass.Specialized;
        }

        private sealed class ManaBarrierSettings
        {
            public double MaximumManaSurcharge { get; set; }
            public double MinimumManaSurcharge { get; set; }
            public double SkillTotalForMinimumSurcharge { get; set; }
        }
    }
}
