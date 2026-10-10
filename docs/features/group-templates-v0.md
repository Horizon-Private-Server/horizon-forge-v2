# UYA group templates v0

Forge's first built-in group templates are deterministic UYA adapter rules loaded from
[`dataset.json`](../../src/Forge.Host/Games/UYA/GroupTemplates/dataset.json). They only
create Custom-group membership and provenance; they do not add map groups or alter any
gameplay payload.

The versioned dataset owns template labels, seed OClasses, PVar field traversal,
reviewed nearby classes, distance parameters, naming rules, and linked-collision
expansion. OClasses use readable hexadecimal strings. Reference rules run in file order,
so a rule may inspect members included by an earlier rule. Forge validates the embedded
dataset strictly at startup; changing rule semantics requires incrementing that
template's `templateVersion` before release.

The `Apply group templates to new projects` setting is enabled by default. During project
creation, Forge evaluates every template against every matching seed with the template's
default parameters and saves one generated Custom group per seed. Existing template/seed
pairs are skipped, making the automatic pass safe to repeat.

## Siege node v1

The seed must be a Siege Node moby (`0x1A47`). The template includes:

- the seed;
- valid `attachedMoby1` through `attachedMoby8`, `attachedDefenses`, and `boltCrank`
  MobyDex references;
- mobys whose `parentNode` reference targets the seed;
- valid `spawnMoby1` through `spawnMoby8` references from the attached Mobile Defenses
  Spawner; and
- cuboids referenced by any included node moby; and
- nearby reviewed node TIE classes `0x1F19` and `0x1F1F`.

The default TIE radius is 20 level units. The default ambiguity margin is 1 unit.

## Siege base v1

The seed must be a Base Light moby (`0x0CBE`). The template includes:

- the seed and its `baseComputer` target;
- mobys whose `baseLight`, `parentNode`, or `siegeBase` reference targets the seed;
- valid `attachedMoby1` through `attachedMoby3` references from linked Gatling Turrets
  (`0x1AD6`); and
- nearby reviewed base TIE classes `0x02D5`, `0x02D6`, `0x1D42`–`0x1D48`,
  `0x1D4C`–`0x1D4E`, and `0x1F20`–`0x1F22`;
- nearby cuboids; and
- source collision components linked to an included base TIE.

`redBaseLight` and `blueBaseLight` references from Multiplayer Config name the group but
do not make the shared config moby a member. The default nearby radius is 16 level units;
the ambiguity margin is 1 unit.

## Preview and regeneration

For both templates, nearby geometry inside the radius is included only when the selected
seed is its unambiguous nearest matching anchor. Geometry closer to another anchor, or
within the ambiguity margin of one, is reported as an excluded ambiguous candidate with
both distances in its evidence.

Radius and ambiguity margin are stored versioned numeric parameters. Changing either
creates a new preview; it does not mutate the group. Acceptance stores the selected
seed, generated baseline, and ordinary membership. Later manual additions and removals
remain explicit and are preserved by confirmed regeneration.

Preview responses report complete membership and candidate counts while transporting at
most 250 evidence rows, prioritizing ambiguous matches for review.

Deleting a generated group's seed preserves its remaining membership as an ordinary
Custom group and removes its regeneration provenance.
