using Forge.Host.Domain;

namespace Forge.Host.Games.UYA;

internal static class UyaContentImportCapabilities
{
    private sealed record Contract(
        string Family,
        string Label,
        ContentImportMode Mode,
        bool DefaultSelectedWhenReady,
        bool ImplementationReady,
        string Blocker,
        string Ownership,
        string[] Dependencies,
        string ConflictNamespace,
        string NativeSemantics,
        string ProtectedSurvivorPolicy,
        string NoEditIdentity,
        string[] Limits,
        string[] Evidence);

    private static readonly Contract[] Contracts =
    [
        Ready("ties", "TIEs", ContentImportMode.AddAndReplace, true,
            "Definition owns model/material/texture assets; instances own transforms, attached collision, group membership, ambient data, and typed references.",
            ["definitions", "instances", "materials", "textures", "attached collision", "tie groups", "occlusion mappings", "typed references"],
            "UYA TIE class ID; conflicts allocate project custom IDs in 0x9000-0xFFFF.",
            "Rebuild class lists, definitions, instances, groups, occlusion mappings, palettes, and asset-WAD sections after final class allocation.",
            "Replace removes target TIE content and its exclusively attached collision; independent inbound references retain and report survivors.",
            "Unselected source definitions/instances remain semantically stable; source-native assets preserve texture indexes.",
            ["16-bit class IDs", "custom class range 0x9000-0xFFFF", "SDK packet, texture, palette, group, and occlusion limits"],
            ["UyaBaseLevelService", "UyaStaticLayerStore", "UyaStaticAssetPackService", "UyaStaticReferenceStore", "M14-006"]),
        Ready("shrubs", "Shrubs", ContentImportMode.AddAndReplace, true,
            "Definition owns model/material/texture assets; instances own transforms, attached collision, shrub-group membership, and typed references.",
            ["definitions", "instances", "materials", "textures", "attached collision", "shrub groups", "typed references"],
            "UYA shrub class ID; colliding donor definitions require a deterministic project allocation.",
            "Rebuild class lists, definitions, instances, groups, palettes, and asset-WAD sections after final class allocation.",
            "Replace removes target shrubs and their exclusively attached collision; independent inbound references retain and report survivors.",
            "Unselected source definitions/instances remain semantically stable; source-native assets preserve texture indexes.",
            ["16-bit class IDs", "SDK instance, texture, palette, and group limits"],
            ["UyaBaseLevelService", "UyaStaticLayerStore", "UyaStaticAssetPackService", "UyaStaticReferenceStore"]),
        Ready("tfrags", "Tfrags", ContentImportMode.AddAndReplace, true,
            "One native terrain owner contains complete ordered groups; each project piece retains owner/group provenance and canonical geometry.",
            ["owner payload", "groups", "three LODs", "materials", "terrain textures", "occlusion mappings"],
            "Owner-local group and texture indexes plus the ordered tfrag occlusion mapping table.",
            "Rebuild complete primary or streamed terrain owners, texture tables, aligned sections, bounds, indexes, and occlusion mappings.",
            "Replace retains any target owner/group required by protected project references and reports the exact owner.",
            "Untouched primary/chunk owners meet the M12 byte/semantic identity contract.",
            ["100000 project groups", "4096 texture dependencies", "256 custom imports", "SDK native offset/count/alignment limits"],
            ["UyaTfragProjectService", "UyaTfragContentImportService", "UyaTfragBakeService", "UyaTfragPackService", "M12-007"]),
        Ready("mobys", "Mobys", ContentImportMode.AddAndReplace, false,
            "Definition owns model/material/texture assets; instance owns raw native record and PVar; typed links retain entity and sound dependencies.",
            ["definitions", "instances", "materials", "textures", "raw PVars", "entity references", "sound entries", "samples", "remaps", "map groups"],
            "Moby class ID, native instance/PVar indexes, entity references, sound slots, and remap indexes.",
            "Order final entities first, then rebuild instances, PVar tables/relocations, definitions, sounds, remaps, and references.",
            "Replace retains mobys, definitions, sounds, and referenced entities required by surviving content.",
            "Unknown PVar bytes and untouched native records remain exact under the M9 contract.",
            ["16-bit class IDs", "native instance/PVar/reference limits", "M15 bank/remap limits"],
            ["UyaBaseLevelService", "UyaMobyContentImportService", "UyaStaticLayerStore", "UyaMobyPVarService", "UyaMobySoundImportService", "M9-009"]),
        Ready("collision", "Collision", ContentImportMode.AddAndReplace, false,
            "A native collision component owns its faces, vertices, octant placement, raw types, and optional explicit entity association.",
            ["vertices", "faces", "octants", "raw collision types", "collision sounds", "explicit links"],
            "Collision component/face identity, octant placement, and target material/sound type values.",
            "Compose complete validated collision, rebuilding octants and indexes without inferring tfrag ownership.",
            "Referenced components survive replace; coarse retail collision is never deleted through inferred proximity.",
            "Unedited retail collision remains byte-identical through the base-layer contract.",
            ["SDK vertex, face, octant, coordinate, and raw-type limits"],
            ["UyaBaseLevelService", "UyaCollisionContentImportService", "UyaBaseLayerStore.ComposeCollisionAsync", "CollisionWork", "M2 collision qualification"]),
        ReadyGeometry("cuboids", "Cuboids", "gameplay/core/cuboids", "matrix record and optional camera-collision grid entry"),
        ReadyGeometry("spheres", "Spheres", "gameplay/core/spheres", "matrix record and optional camera-collision grid entry"),
        ReadyGeometry("splines", "Splines", "gameplay/core/splines", "variable point record and area links"),
        ReadyGeometry("paths", "Paths", "gameplay/core/grind_splines", "variable grind-path record"),
        ReadyGeometry("areas", "Areas", "gameplay/core/areas", "bounds plus remapped spline/shape reference lists"),
        Ready("mapGroups", "Map groups", ContentImportMode.Replace, false,
            "Each native map group owns an ordered member list for one entity family; project groups retain stable Entity IDs.",
            ["member entities", "family-specific source indexes", "stable group membership"],
            "Family-local native group index and ordered member indexes.",
            "Rebuild family group tables only after final entity ordering and reference remapping.",
            "Replacement requires the corresponding member families to be replaced in the same import.",
            "Unselected group tables preserve their source bytes/order under the owning family contract.",
            ["SDK group/member count and index-width limits"],
            ["UyaMapGroupService", "ProjectMapGroupTable", "UyaStaticReferenceStore", "M10-005"]),
        Ready("skybox", "Skybox", ContentImportMode.Replace, false,
            "The project owns one ordered sky composition; each shell references one verified source asset and shell index.",
            ["shell geometry", "textures", "rotation state"],
            "Replace-only singleton composition with ordered shell indexes.",
            "Atomically compose the selected donor sky as the sole authoritative project sky.",
            "The prior sky remains protected until the replacement validates and commits.",
            "No-edit sky bytes meet the M2 skybox identity contract.",
            ["SDK shell count, texture, material, geometry, and alignment limits"],
            ["UyaSkyShellEditorService", "UyaBaseLayerStore", "M2 skybox qualification"]),
        Ready("levelSettings", "Level settings", ContentImportMode.Replace, false,
            "One project-level settings record owns environment/world/ship values and typed entity references.",
            ["settings record", "ship path", "ship camera cuboids", "world/environment values"],
            "Replace-only singleton; entity references resolve to imported or retained stable IDs.",
            "Patch the validated target-native settings record after final reference ordering.",
            "The prior settings remain until every required reference resolves and the replacement validates.",
            "Unchanged fields and trailing bytes remain source-exact under the M11 writer contract.",
            ["native field widths", "bounded chunk-plane list", "typed reference-kind constraints"],
            ["ProjectLevelSettings", "UyaBaseLayerStore.WriteLevelSettings", "M11-004"]),
        Ready("directionalLights", "Directional lights", ContentImportMode.Replace, false,
            "The lighting layer owns the ordered directional-light array and tie selector dependencies.",
            ["light colors", "directions", "tie directional selectors"],
            "Replace-only ordered light array; tie selectors must remain valid after final ordering.",
            "Rebuild the complete directional-light section and validate every dependent selector.",
            "Lights referenced by protected ties block incompatible replacement.",
            "Unchanged records preserve source bytes under the M11 lighting contract.",
            ["native light-count and selector-width limits"],
            ["UyaGameplayLightingReader", "UyaDirectionalLightsWriter", "UyaBaseLayerStore"]),
        Ready("hud", "HUD", ContentImportMode.Replace, false,
            "The project owns one complete HUD header and five-bank source payload plus its normalized preview textures.",
            ["HUD header", "palette and texture banks", "icon/frame mappings", "preview textures"],
            "Replace-only whole HUD bank set; source texture identities remain content-addressed.",
            "Attach the verified donor HUD payload and textures, then rebuild all physical banks from that portable source.",
            "The prior HUD remains intact until the donor header, banks, mappings, and textures validate atomically.",
            "An unedited imported HUD composes to the exact donor header and bank payloads.",
            ["five physical banks", "1,024 icons", "attached-asset byte limit"],
            ["UyaHudProjectService", "UyaHudImportPayload", "UyaHudBakeService", "HudBankReader", "HudComposer"]),
        Ready("fx", "FX", ContentImportMode.Replace, false,
            "The project owns one complete ordered FX texture bank; every source index retains its normalized indexed texture.",
            ["ordered texture definitions", "palettes", "indexed pixels", "stable FX indexes"],
            "Replace-only whole FX bank with contiguous source indexes.",
            "Rebuild the donor bank at its exact count while relocating unrelated target asset-WAD payloads unchanged.",
            "The prior FX bank remains intact until every donor texture is attached and validates atomically.",
            "An unedited imported FX bank semantically preserves every donor index, dimension, palette, and pixel payload.",
            ["4,096 textures", "power-of-two dimensions up to 4,096", "attached-asset byte limit"],
            ["UyaFxProjectService", "UyaFxContentImportService", "UyaFxBakeService", "FxTextureCatalog.ReplaceAll"]),
        Ready("occlusion", "Occlusion", ContentImportMode.Replace, false,
            "The project owns one donor visibility set containing gameplay mappings, the asset-WAD spatial grid, and the level-WAD occlusion payload.",
            ["TIE instances", "Moby instances", "primary and streamed tfrags", "gameplay mapping table", "asset visibility grid", "level occlusion payload"],
            "Replace-only singleton; mapping slots and visibility bits must remain aligned with the donor instance and terrain order.",
            "Replace all three native occlusion payloads and rebind imported terrain groups to their final gameplay mapping slots.",
            "Replacement requires complete TIE, Moby, and tfrag replacement so retained content cannot inherit unrelated donor visibility bits.",
            "Unselected occlusion payloads remain byte-identical to the verified target level.",
            ["1,024 visibility bits", "signed 16-bit tfrag mapping indexes", "complete donor static-family replacement"],
            ["UyaOcclusionMappingsReader", "UyaOcclusionGridReader", "UyaOcclusionContentImportService", "UyaTfragPackService"]),
        Unsupported("opaqueContent", "Opaque content", "Opaque or unclassified payloads cannot be dependency-checked or safely remapped."),
    ];

    public static ContentImportCapabilityMatrix Create(
        string region,
        string revision,
        IReadOnlyList<ContentImportFamilyInventory>? inventory = null)
    {
        if (region != "NTSC-U" || revision != "1.00")
            throw new ArgumentException($"UYA {region} {revision} is not a writable import target.");
        var states = (inventory ?? []).ToDictionary(value => value.Family, StringComparer.Ordinal);
        if (states.Keys.Any(family => Contracts.All(contract => contract.Family != family)))
            throw new ArgumentException($"Unknown UYA import family: {states.Keys.First(family => Contracts.All(contract => contract.Family != family))}.");
        foreach (var value in states.Values)
        {
            if (!Enum.IsDefined(value.State)) throw new ArgumentException($"{value.Family} has an invalid source state.");
            if (value.State == ContentImportSourceState.Malformed && string.IsNullOrWhiteSpace(value.Diagnostic))
                throw new ArgumentException($"Malformed {value.Family} inventory requires a diagnostic.");
            if (value.Diagnostic?.Length > 512) throw new ArgumentException($"{value.Family} diagnostic exceeds 512 characters.");
        }

        return new(
            ContentImportCapabilitySchema.CurrentVersion,
            "UYA",
            region,
            revision,
            Contracts.Select(contract => Resolve(contract, states.GetValueOrDefault(contract.Family))).ToArray());
    }

    private static ContentImportFamilyCapability Resolve(Contract contract, ContentImportFamilyInventory? inventory)
    {
        var state = inventory?.State ?? ContentImportSourceState.Unknown;
        var selectable = contract.ImplementationReady && state == ContentImportSourceState.Present;
        var reason = state switch
        {
            ContentImportSourceState.Absent => $"{contract.Label} are absent from the donor map.",
            ContentImportSourceState.Empty => $"{contract.Label} are empty in the donor map.",
            ContentImportSourceState.Malformed => inventory!.Diagnostic,
            _ when contract.Mode == ContentImportMode.Unsupported => contract.Blocker,
            _ when !contract.ImplementationReady => contract.Blocker,
            ContentImportSourceState.Unknown => "Donor inventory has not been evaluated.",
            _ => null,
        };
        return new(
            contract.Family,
            contract.Label,
            contract.Mode,
            state,
            contract.ImplementationReady,
            selectable,
            contract.DefaultSelectedWhenReady,
            selectable && contract.DefaultSelectedWhenReady,
            reason,
            contract.Ownership,
            contract.Dependencies,
            contract.ConflictNamespace,
            contract.NativeSemantics,
            contract.ProtectedSurvivorPolicy,
            contract.NoEditIdentity,
            contract.Limits,
            contract.Evidence);
    }

    private static Contract PendingGeometry(string family, string label, string section, string ownership) => Pending(
        family, label, ContentImportMode.AddAndReplace, false,
        $"M16-006 left {label.ToLowerInvariant()} disabled because structural composition and complete reference remapping are not proven.",
        $"Each {label.ToLowerInvariant()} entity owns its {ownership} in {section}.",
        ["native record", "transform/points", "typed entity links", "camera-collision entry when present"],
        $"{section} source index and every typed cross-family reference.",
        "Rebuild the complete section after final ordering, then rewrite known entity and camera-collision indexes.",
        "Referenced entities survive replace unless every inbound owner is included and remapped.",
        "Unchanged source records and unknown fields remain exact under the M11 gameplay contract.",
        ["SDK record-count, point-count, index-width, coordinate, and camera-grid limits"],
        ["UyaBaseLevelService", "UyaGameplayLayerStore", "M11-004"]);

    private static Contract ReadyGeometry(string family, string label, string section, string ownership) => Ready(
        family, label, ContentImportMode.AddAndReplace, false,
        $"Each {label.ToLowerInvariant()} entity owns its {ownership} in {section}.",
        ["native record", "transform", "camera-collision entry when present", "typed inbound references"],
        $"{section} target index and every typed inbound entity reference.",
        "Order retained and imported shapes by stable project identity, rebuild the complete section, then rebuild camera-collision indexes.",
        "Locked/grouped/referenced shapes survive replace and report every retaining owner.",
        "Unselected sections remain source-exact; selected sections semantically preserve every retained shape.",
        ["native 32-bit count", "finite invertible transforms", "camera-collision type/index limits"],
        ["UyaBaseLevelService", "UyaGeometryContentImportService", "UyaGameplayLayerStore", "M11-004"]);

    private static Contract Pending(
        string family,
        string label,
        ContentImportMode mode,
        bool defaultSelectedWhenReady,
        string blocker,
        string ownership,
        string[] dependencies,
        string conflictNamespace,
        string nativeSemantics,
        string protectedSurvivorPolicy,
        string noEditIdentity,
        string[] limits,
        string[] evidence) => new(
            family, label, mode, defaultSelectedWhenReady, false, blocker, ownership, dependencies,
            conflictNamespace, nativeSemantics, protectedSurvivorPolicy, noEditIdentity, limits, evidence);

    private static Contract Ready(
        string family,
        string label,
        ContentImportMode mode,
        bool defaultSelectedWhenReady,
        string ownership,
        string[] dependencies,
        string conflictNamespace,
        string nativeSemantics,
        string protectedSurvivorPolicy,
        string noEditIdentity,
        string[] limits,
        string[] evidence) => new(
            family, label, mode, defaultSelectedWhenReady, true, string.Empty, ownership, dependencies,
            conflictNamespace, nativeSemantics, protectedSurvivorPolicy, noEditIdentity, limits, evidence);

    private static Contract Unsupported(string family, string label, string reason) => new(
        family,
        label,
        ContentImportMode.Unsupported,
        false,
        false,
        reason,
        string.Empty,
        [],
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        [],
        ["P3 scope decision"]);
}
