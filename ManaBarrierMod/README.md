# Mana Barrier Mod

This mod gives a player a chance to mitigate melee and missile attacks entirely
through mana. It activates when the player has specialized Mana Conversion,
specialized War Magic or Void Magic, and untrained Melee Defense. Magic attacks,
spell damage-over-time, environmental damage, and other non-attack damage use
ACE's normal damage path. Training Melee Defense or advancing it further
disables Mana Barrier.

For each qualifying physical attack, the mod combines the player's effective Mana
Conversion and Assess Creature values. Effective Mana Conversion includes the
equipped weapon's Mana Conversion modifier. The combined total is mapped
linearly to a mitigation chance from 5% to 75%, capped at the configured skill
total. The player must have at least as much current mana as the full rounded
incoming damage before the roll is attempted. A successful roll sets the damage
to zero and leaves mana unchanged. If the player does not have enough mana, or
if the roll fails, the incoming damage is left unchanged for ACE to apply.

## Damage math

```text
incomingDamage = round(incoming damage)
effectiveManaConversion = round(currentManaConversion * weaponModifier)
skillTotal = effectiveManaConversion + currentAssessCreature
skillProgress = clamp(skillTotal / 993, 0, 1)
mitigationChance = 5% + (75% - 5%) * skillProgress

if currentMana < incomingDamage:
    healthDamage = incomingDamage
    manaDamage = 0
else if randomRoll >= mitigationChance:
    healthDamage = incomingDamage
    manaDamage = 0
else:
    healthDamage = 0
    manaDamage = 0
```

The default settings are stored in `Meta.json`:

```json
{
  "MaximumMitigationChance": 0.75,
  "MinimumMitigationChance": 0.05,
  "OverchargeMitigationBonus": 0.25,
  "SkillTotalForMaximumMitigationChance": 993.0
}
```

The chance fields are decimal fractions. A skill total of 0 gives a 5% chance,
and a total of 993 or higher gives a 75% chance. When Assess Creature is
untrained, its contribution is treated as 0.

## Overcharge

Overcharge is disabled by default for each player. `/mboc` and
`/manabarrierovercharge` toggle it for the current player. While enabled, the
player receives the configured `OverchargeMitigationBonus` added to their
mitigation chance, capped at 100%. With the default settings, maximum skills
and overcharge produce a 100% mitigation chance. Every calculated spell mana cost is
doubled. This includes costs after the normal Mana Conversion calculation.
Toggling it off restores the normal mitigation chance and spell cost.

## Status command

While in the world, `/mb` and `/manabarrier` display the current mitigation
chance and the Mana Conversion, weapon modifier, Assess Creature, and combined
skill values used to calculate it. If the required skills are not specialized,
the command reports the missing requirement.

## Build and deployment

Building `ManaBarrierMod.csproj` deploys the mod after a successful build. The
default deployment directory is `C:\\ACE\\Mods\\ManaBarrierMod`.

```powershell
dotnet build ManaBarrierMod\\ManaBarrierMod.csproj
```
