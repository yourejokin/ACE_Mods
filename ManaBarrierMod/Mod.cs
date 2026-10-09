using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;
using log4net;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Entity;
using ACE.Server.Mods;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ManaBarrierMod
{
    public sealed class Mod : IHarmonyMod
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(Mod));
        private static readonly ConditionalWeakTable<Player, OverchargeState> overchargeStates =
            new ConditionalWeakTable<Player, OverchargeState>();

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

            var calculateManaUsageMethod = AccessTools.Method(
                typeof(Creature),
                nameof(Creature.CalculateManaUsage),
                new[] { typeof(Creature), typeof(Spell), typeof(WorldObject) });

            if (calculateManaUsageMethod == null)
                throw new MissingMethodException("Could not find the spell mana usage method.");

            harmony.Patch(
                calculateManaUsageMethod,
                postfix: new HarmonyMethod(typeof(Mod), nameof(CalculateManaUsagePostfix)));

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

            if (loadedSettings.MaximumMitigationChance < loadedSettings.MinimumMitigationChance ||
                loadedSettings.MinimumMitigationChance < 0 ||
                loadedSettings.MaximumMitigationChance > 1 ||
                loadedSettings.OverchargeMitigationBonus < 0 ||
                loadedSettings.OverchargeMitigationBonus > 1 ||
                loadedSettings.SkillTotalForMaximumMitigationChance <= 0)
            {
                throw new InvalidDataException(
                    "Mana Barrier settings must have mitigation chances and overcharge bonus between 0 and 1, " +
                    "a maximum chance greater than or equal to the minimum, and a positive " +
                    "skill total for the maximum chance.");
            }

            return loadedSettings;
        }

        private static void PlayerTakeDamagePrefix(
            Player __instance,
            WorldObject source,
            DamageType damageType,
            ref float _amount)
        {
            if (!IsPhysicalAttack(source))
                return;

            ApplyManaBarrier(__instance, damageType, ref _amount);
        }

        private static void CalculateManaUsagePostfix(Creature caster, ref uint __result)
        {
            if (!(caster is Player player) || !IsOverchargeEnabled(player))
                return;

            __result = __result > uint.MaxValue / 2
                ? uint.MaxValue
                : __result * 2;
        }

        private static bool IsPhysicalAttack(WorldObject source)
        {
            if (!(source is Creature attacker))
                return false;

            if (attacker is Player player && player.CombatMode == CombatMode.Magic)
                return false;

            return attacker.GetCombatType() == CombatType.Melee ||
                attacker.GetCombatType() == CombatType.Missile;
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

            // Mana is only an eligibility check. If the player cannot cover the
            // entire hit, leave the amount unchanged and let ACE apply normal damage.
            if (player.Mana.Current < incomingDamage)
                return;

            var effectiveManaConversion = GetEffectiveManaConversion(player);
            var assessCreature = GetAssessCreatureValue(player);
            var combinedSkillTotal = effectiveManaConversion + assessCreature;
            var skillProgress = Math.Min(
                1.0,
                combinedSkillTotal / settings.SkillTotalForMaximumMitigationChance);
            var mitigationChance = settings.MinimumMitigationChance +
                (settings.MaximumMitigationChance - settings.MinimumMitigationChance) * skillProgress;
            var roll = ThreadSafeRandom.Next(0.0f, 1.0f);

            if (roll >= mitigationChance)
            {
                log.Debug(
                    $"[ManaBarrier] Player={player.Name} Guid={player.Guid} " +
                    $"Incoming={incomingDamage} ManaConversion={effectiveManaConversion} " +
                    $"AssessCreature={assessCreature} CombinedSkill={combinedSkillTotal} " +
                    $"SkillProgress={skillProgress:0.####} MitigationChance={mitigationChance:P2} " +
                    $"Roll={roll:0.####} Mitigated=False.");
                return;
            }

            log.Debug(
                $"[ManaBarrier] Player={player.Name} Guid={player.Guid} " +
                $"Incoming={incomingDamage} ManaConversion={effectiveManaConversion} " +
                $"AssessCreature={assessCreature} CombinedSkill={combinedSkillTotal} " +
                $"SkillProgress={skillProgress:0.####} MitigationChance={mitigationChance:P2} " +
                $"Roll={roll:0.####} Mitigated=True " +
                $"CurrentMana={player.Mana.Current} ManaSpent=0 HealthDamage=0.");

            amount = 0;

            player.SendMessage(
                $"Mana Barrier mitigated {incomingDamage} damage.",
                ChatMessageType.Combat);
        }

        [CommandHandler("mb", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Displays your current Mana Barrier mitigation chance.")]
        [CommandHandler("manabarrier", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Displays your current Mana Barrier mitigation chance.")]
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
            var mitigationChance = GetMitigationChance(player) * 100.0;
            var overchargeStatus = IsOverchargeEnabled(player) ? "Enabled" : "Disabled";

            player.SendMessage(
                $"Mana Barrier mitigation chance: {mitigationChance:0.##}% | " +
                $"Mana Conversion: {baseManaConversion} (skill) x {wandManaConversionModifier:0.###} (wand modifier) = {effectiveManaConversion} | " +
                $"Assess Creature: {assessCreature} | Combined: {combinedSkillTotal} | " +
                $"Overcharge: {overchargeStatus}.",
                ChatMessageType.Broadcast);
        }

        [CommandHandler("mboc", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Toggles Mana Barrier overcharge.")]
        [CommandHandler("manabarrierovercharge", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Toggles Mana Barrier overcharge.")]
        public static void HandleManaBarrierOvercharge(Session session, params string[] parameters)
        {
            var player = session.Player;
            var state = overchargeStates.GetOrCreateValue(player);
            state.Enabled = !state.Enabled;

            var status = state.Enabled ? "enabled" : "disabled";
            player.SendMessage(
                $"Mana Barrier overcharge {status}. " +
                (state.Enabled
                    ? $"Mitigation chance increased by {settings.OverchargeMitigationBonus:P0}; spell mana costs are doubled."
                    : "Mitigation chance and spell mana costs returned to normal."),
                ChatMessageType.Broadcast);
        }

        private static double GetMitigationChance(Player player)
        {
            var combinedSkillTotal = GetCombinedSkillTotal(player);

            var skillProgress = Math.Min(
                1.0,
                combinedSkillTotal / settings.SkillTotalForMaximumMitigationChance);

            var mitigationChance = settings.MinimumMitigationChance +
                (settings.MaximumMitigationChance - settings.MinimumMitigationChance) * skillProgress +
                (IsOverchargeEnabled(player) ? settings.OverchargeMitigationBonus : 0);

            return Math.Min(1.0, mitigationChance);
        }

        private static bool IsOverchargeEnabled(Player player)
        {
            return overchargeStates.TryGetValue(player, out var state) && state.Enabled;
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

            if (!IsUntrained(player, Skill.MeleeDefense))
            {
                if (missingRequirements.Length > 0)
                    missingRequirements += " and ";

                missingRequirements += "untrained Melee Defense";
            }

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
                IsUntrained(player, Skill.MeleeDefense) &&
                (IsSpecialized(player, Skill.WarMagic) || IsSpecialized(player, Skill.VoidMagic));
        }

        private static bool IsUntrained(Player player, Skill skill)
        {
            return player.Skills.TryGetValue(skill, out var creatureSkill) &&
                creatureSkill.AdvancementClass == SkillAdvancementClass.Untrained;
        }

        private static bool IsSpecialized(Player player, Skill skill)
        {
            return player.Skills.TryGetValue(skill, out var creatureSkill) &&
                creatureSkill.AdvancementClass == SkillAdvancementClass.Specialized;
        }

        private sealed class OverchargeState
        {
            public bool Enabled { get; set; }
        }

        private sealed class ManaBarrierSettings
        {
            public double MaximumMitigationChance { get; set; }
            public double MinimumMitigationChance { get; set; }
            public double OverchargeMitigationBonus { get; set; }
            public double SkillTotalForMaximumMitigationChance { get; set; }
        }
    }
}
