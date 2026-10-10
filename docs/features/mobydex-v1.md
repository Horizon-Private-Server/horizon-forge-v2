# MobyDex entry schema v1

MobyDex is declarative data. An entry describes one exact `(game, OClass)` pair and
one fixed-length PVar layout; it cannot contain scripts, expressions, callbacks, or
client-selected native offsets. The canonical machine-readable schema is
[`mobydex-v1.schema.json`](../schemas/mobydex-v1.schema.json).

## Minimal entry

```json
{
  "schemaVersion": 1,
  "game": "UYA",
  "oClass": 6300,
  "name": "Ammo - Gravity Bomb",
  "tags": [],
  "pvar": {
    "length": 48,
    "relocations": [],
    "fields": [
      {
        "key": "respawnTimeSeconds",
        "label": "Respawn time",
        "offset": 20,
        "length": 4,
        "minimum": 0,
        "maximum": 60,
        "definition": { "kind": "int32" }
      }
    ]
  }
}
```

Integers and floats are little-endian. Scalar kinds are signed/unsigned 8-, 16-,
32-, or 64-bit integers, `float32`, and `float64`. Other fixed kinds are `bool8`,
`bool32`, `rgb8`, `rgba8`, and two- through four-component float32 vectors. `bytes`
marks an understood fixed raw span without assigning numeric meaning.

Enums and flags declare integer `storage` plus inline keyed options. Each flag option
is one positive bit. A reference declares integer storage, `entity` or `asset` domain,
a compatible `targetKind`, and an explicit `nullValue`. Arrays have a fixed positive
`count` and `stride`; their total span is exactly `count * stride`. Struct fields cannot
overlap. Union alternatives may overlap and are the only way to declare shared bytes.

`defaultHex`, when present, is the complete PVar blob and must decode to exactly
`pvar.length` bytes. Per-field defaults are deliberately unsupported: Forge never
combines partial values with guessed padding or unknown source bytes. Relocations are
unique, four-byte-aligned offsets whose four-byte slots fit inside the PVar.

## Forge v1 overlay translation

| Forge v1 concept | MobyDex v1 representation |
| --- | --- |
| `Float` | `float32`, length 4 |
| `Byte` | `uint8`, length 1 |
| `Enum` + `DataSize` | `enum` with explicit integer storage and inline options |
| `ColorRGB` / `ColorRGBA` | `rgb8` / `rgba8` |
| `Count` | `array` with explicit count, stride, and element definition |
| `MobyRef` | entity `reference` targeting `moby`, normally with `-1` null |
| `CuboidRef` | entity `reference` targeting `cuboid`, normally with `-1` null |
| untyped understood span | `bytes` |

The checked-in synthetic fixture at `tests/fixtures/mobydex-v1.json` demonstrates all
of these conversions. Old lookup keys are expanded into local options; they are not
runtime dependencies. Old defaults are accepted only after being promoted to a full,
exact-length `defaultHex` blob. Ambiguous field-level defaults are validation errors.

## Validation and canonical form

Forge rejects unknown properties and kinds, duplicate JSON properties, duplicate
field/option keys or option values, invalid spans and overlaps, invalid bounds,
bad reference sentinels, bad arrays and relocations, and newer schema versions.
Entry JSON is limited to 4 MiB, PVars to 1 MiB, arrays to 65,536 elements, entries to
4,096 recursive fields/options, and semantic nesting to 16 levels.

Canonical formatting uses fixed property order, two-space indentation, sorted tags
and relocations, lowercase hexadecimal defaults, UTF-8, LF line endings, and one final
newline. Field and option order is retained because it controls author-facing display.

## Built-in data and project overrides

Forge ships a versioned [UYA JSON dataset](../../src/Forge.Host/Games/UYA/MobyDex/dataset.json).
The data is embedded as a read-only resource at build time and parsed through the same
bounded MobyDex entry parser as project imports; C# only loads it and enforces its game
boundary. Dataset version 2 contains all 75 unique UYA (`RCVersion: 3`) entries from the
Forge v1 overlay. The exact duplicate Ammo Pad (`5000`) row is included once, while the
DL (`RCVersion: 4`) rows—including `8306`—are excluded. The translation retains PVar
lengths, field offsets, scalar types, bounds, inline enum options, arrays, and typed
Moby/Cuboid/Spline references. Level-dependent FX texture lookups remain numeric indexes
because their choices come from the active level. Source PVar defaults and unverified
pointer metadata are deliberately not copied. Project entries are stored whole in
project content; an exact `(game, OClass)` match replaces the whole built-in entry.
Fields are never merged between sources.

Use **Forge → Reload MobyDex Dataset…** to select a complete dataset JSON while Forge
is running. Forge validates the whole replacement and its target-game boundary before
atomically activating it; an invalid file leaves the previous catalog active. A reload
affects only the current host session, refreshes the open editor, and does not modify the
selected file, the bundled fallback, or project-local overrides. Restarting Forge restores
the bundled dataset.

Import parses and validates the complete entry before changing project state. Import
and removal use normal editor history, recovery, and save semantics. Export runs the
canonical formatter over the stored entry, so it has no source filename or machine
path and survives moving or ZIP-transferring the project. A missing lookup returns no
entry; Forge retains the original moby/PVar bytes for raw access.

Each project PVar also retains the bytes it had when imported or initialized. The raw
view compares the current payload with that project-owned baseline and sends only a
bounded bit mask to the renderer, so modified-byte markers remain portable and do not
make the hex view a second byte authority. Legacy draft PVars adopt their bytes at first
migration as the baseline because older project state did not retain one.

## Contributing an entry

Start with the minimal entry above. A minimal reference field looks like this:

```json
{
  "key": "spawnMoby",
  "label": "Spawn moby",
  "offset": 464,
  "length": 4,
  "definition": {
    "kind": "reference",
    "storage": "int32",
    "domain": "entity",
    "targetKind": "moby",
    "nullValue": -1
  }
}
```

Built-in dataset files wrap entries with a stable `id`, positive `version`, and `entries`
array. Contributions must identify the game and OClass, cite the source used to establish
every length/offset/type, and include a focused fixture or test. Reviewers check schema
validation, overlaps, reference domains/sentinels, and deterministic formatting before
raising the built-in dataset version. Prefer the smallest verified field set; unknown
bytes do not need placeholder fields.

Do not commit PVar dumps, extracted game files, source-level blobs, or copied proprietary
defaults. Layout facts and hand-authored labels are sufficient. Run
`dotnet run --project tests/Forge.Host.ProtocolTests/Forge.Host.ProtocolTests.csproj --configuration Release`
to validate the schema corpus, built-in catalog, precedence, rollback, history, recovery,
and ZIP portability checks.

The initial UYA facts were reviewed against the Forge v1
[`pvar_overlay.json`](https://github.com/Horizon-Private-Server/horizon-forge/blob/develop/pvar_overlay.json).
