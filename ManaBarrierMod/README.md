# Mana Barrier Mod

This mod applies a 50/50 mana barrier to incoming player damage when the player has specialized Mana Conversion and specialized War Magic or Void Magic.

The barrier assigns half of incoming damage to mana and half to health. When the
damage is odd, the extra point goes to mana. The mana share has an additional
surcharge, so the barrier consumes more mana than the calculated share while the
health share remains in place. The surcharge is reduced by the character's
combined Mana Conversion and Assess Creature skill totals.

## Build and deployment

Building `ManaBarrierMod.csproj` automatically deploys the mod after a
successful build. The project creates the deployment directory if necessary
and copies these files:

```text
bin\<Configuration>\net10.0\ManaBarrierMod.dll -> C:\ACE\Mods\ManaBarrierMod\ManaBarrierMod.dll
Meta.json                                -> C:\ACE\Mods\ManaBarrierMod\Meta.json
```

The default deployment directory is `C:\ACE\Mods\ManaBarrierMod`, configured
by the `ModDeploymentDirectory` property in the project file. For example, from
the repository root:

```powershell
dotnet build ManaBarrierMod\ManaBarrierMod.csproj
```

The copy step uses `SkipUnchangedFiles`, so files that have not changed are not
copied again. The build output also contains a `Meta.json` copy, but the
deployment step copies the project-root `Meta.json` directly to the ACE mods
directory.

## Damage math

Let:

```text
calculatedHealth = floor(incomingDamage / 2)
calculatedMana = incomingDamage - calculatedHealth
wandManaConversionModifier = equippedWandManaConversionModifier
effectiveManaConversion = round(currentManaConversion * wandManaConversionModifier)
skillTotal = effectiveManaConversion + currentAssessCreature
skillProgress = clamp(skillTotal / 993, 0, 1)
manaSurcharge = 100% - (95% * skillProgress)
requestedMana = ceil(calculatedMana * (1 + manaSurcharge))
actualMana = min(requestedMana, currentMana)
unabsorbedMana = max(requestedMana - actualMana, 0)
healthDamage = calculatedHealth + unabsorbedMana
```

The surcharge ranges from 100% at a combined skill total of 0 to 5% at a
combined skill total of 993 or higher. A combined total of 993 represents the
assumed maximum of effective Mana Conversion 677 plus Assess Creature 316. The
equipped wand's Mana Conversion percentage is applied to Mana Conversion before
the two skills are added. Skill bonuses included in the server's `Current` skill
value are therefore included automatically.

The surcharge values are configured in `Meta.json`:

```json
{
  "MaximumManaSurcharge": 1.00,
  "MinimumManaSurcharge": 0.05,
  "SkillTotalForMinimumSurcharge": 993.0
}
```

`MaximumManaSurcharge` and `MinimumManaSurcharge` are expressed as decimal
fractions, so `1.00` is 100% and `0.05` is 5%. The skill total must be positive,
and the maximum surcharge must be greater than or equal to the minimum.

When the surcharge is below 50%, the mod sends the client `HealthDownBlue`
particle effect (`PlayScript` `0x22`, backed by emitter info `0x32000A22`) on the
player while qualifying damage is processed. This is the blue particle effect
shown in the mod's effect reference.

If no applicable wand modifier is active, the wand multiplier is `1.0`. For
example, a wand with a 31% Mana Conversion modifier changes a Mana Conversion
skill of 461 to `round(461 * 1.31) = 604`. With Assess Creature at 316, the
combined total becomes 920, which reaches the 5% surcharge floor.

Examples with a low combined skill total, assuming the character has enough mana:

| Incoming damage | Combined skill total | Surcharge | Calculated mana | Mana consumed | Health damage |
|---:|---:|---:|---:|---:|---:|
| 1 | 0 | 100% | 1 | 2 | 0 |
| 2 | 0 | 100% | 1 | 2 | 1 |
| 5 | 0 | 100% | 3 | 6 | 2 |
| 100 | 0 | 100% | 50 | 100 | 50 |
| 1,000 | 0 | 100% | 500 | 1,000 | 500 |
| 10,000 | 0 | 100% | 5,000 | 10,000 | 5,000 |

For damage values from 1 through 400, the midpoint and maximum skill totals
produce the following results. The requested mana values include the surcharge
and integer rounding:

| Incoming damage | Calculated mana | Mana at skill total 500 | Health at skill total 500 | Mana at skill total 993 | Health at skill total 993 |
|---:|---:|---:|---:|---:|---:|
| 1 | 1 | 2 | 0 | 2 | 0 |
| 10 | 5 | 8 | 5 | 6 | 5 |
| 25 | 13 | 20 | 12 | 14 | 12 |
| 50 | 25 | 39 | 25 | 27 | 25 |
| 100 | 50 | 77 | 50 | 53 | 50 |
| 150 | 75 | 115 | 75 | 79 | 75 |
| 200 | 100 | 153 | 100 | 105 | 100 |
| 250 | 125 | 191 | 125 | 132 | 125 |
| 300 | 150 | 229 | 150 | 158 | 150 |
| 350 | 175 | 267 | 175 | 184 | 175 |
| 400 | 200 | 305 | 200 | 210 | 200 |

At a combined skill total of 500, the surcharge is approximately 52.2%. At
993, it reaches the minimum 5% surcharge. At a combined skill total of 0, the
100% surcharge causes the barrier to request approximately twice the calculated
mana share. Health still receives its half, so 100 incoming damage becomes 100
mana damage and 50 health damage when enough mana is available.

If the character does not have enough mana, the available mana is consumed and
the entire unpaid requested mana amount, including the surcharge, goes to health.
This means that insufficient mana can cause total damage to exceed the original
incoming damage. For example, with a combined skill total of 993, 100 incoming
damage calculates a 50-point mana share and requests 53 mana after the 5%
surcharge:

| Current mana | Mana damage | Health damage |
|---:|---:|---:|
| 6 | 6 | 97 |
| 25 | 25 | 78 |
| 53 or more | 53 | 50 |

## Status command

While in the world, the following commands display the character's current
barrier surcharge and the values used to calculate it:

```text
/mb
/manabarrier
```

If the character does not have specialized Mana Conversion and either
specialized War Magic or specialized Void Magic, the response reports the
missing requirement instead of showing a surcharge breakdown. When Assess
Creature is untrained, its value is treated as 0 for the combined skill total.
Otherwise, the response includes the current Mana Conversion skill, the
equipped wand modifier, effective Mana Conversion, Assess Creature, combined
skill total, and the resulting surcharge.
