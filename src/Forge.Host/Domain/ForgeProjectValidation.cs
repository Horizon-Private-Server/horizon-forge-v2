namespace Forge.Host.Domain;

internal static class ForgeProjectValidation
{
    internal const int MaxInstancedCollisionFaceTypeOverrides = 100_000;

    public static void Validate(string rootPath, ForgeProjectManifest manifest, ForgeProjectContent content)
    {
        ValidateManifest(rootPath, manifest);
        ValidateContent(content);
    }

    public static void ValidateManifest(string rootPath, ForgeProjectManifest manifest)
    {
        if (manifest.SchemaVersion != ProjectSchema.CurrentVersion)
            throw new UnsupportedProjectSchemaException(manifest.SchemaVersion);
        if (manifest.DocumentType != ProjectSchema.ManifestDocumentType)
            throw new InvalidDataException("Project manifest document type is invalid.");
        if (manifest.ProjectId.Value == Guid.Empty) throw new InvalidDataException("Project ID cannot be empty.");
        if (manifest.Target is null || manifest.BaseLevel is null)
            throw new InvalidDataException("Project target and base level are required.");
        ValidateText(manifest.Name, nameof(manifest.Name));
        ValidateText(manifest.Target.Game, nameof(manifest.Target.Game));
        ValidateText(manifest.Target.Region, nameof(manifest.Target.Region));
        ValidateText(manifest.Target.Revision, nameof(manifest.Target.Revision));
        ValidateText(manifest.Target.BakeProfile, nameof(manifest.Target.BakeProfile));
        ValidateText(manifest.BaseLevel.Game, nameof(manifest.BaseLevel.Game));
        ValidateText(manifest.BaseLevel.Region, nameof(manifest.BaseLevel.Region));
        ValidateText(manifest.BaseLevel.Revision, nameof(manifest.BaseLevel.Revision));
        if (manifest.BaseLevel.Level < 0) throw new InvalidDataException("Base level cannot be negative.");
        if (manifest.BaseLevel.MissingAssetCount < 0)
            throw new InvalidDataException("Base-level missing asset count cannot be negative.");
        if (manifest.BaseLevel.SourceFingerprint is null
            || manifest.BaseLevel.SourceFingerprint.Length != 32
            || !manifest.BaseLevel.SourceFingerprint.All(Uri.IsHexDigit))
            throw new InvalidDataException("Base-level source fingerprint must be a 32-character MD5 value.");
        _ = ForgeProjectPersistence.ResolveRelativePath(rootPath, manifest.Content);
    }

    private static void ValidateContent(ForgeProjectContent content)
    {
        if (content.SchemaVersion != ProjectSchema.CurrentVersion)
            throw new UnsupportedProjectSchemaException(content.SchemaVersion);
        if (content.DocumentType != ProjectSchema.ContentDocumentType)
            throw new InvalidDataException("Project content document type is invalid.");
        if (content.Entities is null || content.Assets is null || content.InstancedCollisionBindings is null)
            throw new InvalidDataException("Project content lists are required.");
        if (content.LevelSettings is not null) ValidateLevelSettings(content.LevelSettings);
        if (content.Entities.Any(entity => entity is null))
            throw new InvalidDataException("Project entities cannot contain null entries.");
        if (content.Entities.Select(entity => entity.EntityId).Distinct().Count() != content.Entities.Count)
            throw new InvalidDataException("Project contains duplicate Entity IDs.");
        foreach (var entity in content.Entities)
        {
            if (entity is null || entity.EntityId.Value == Guid.Empty)
                throw new InvalidDataException("Project entity ID cannot be empty.");
            ValidateText(entity.Name, nameof(entity.Name));
            ValidateText(entity.Layer, nameof(entity.Layer));
            if (entity.Transform is null)
                throw new InvalidDataException($"Entity {entity.EntityId} transform is required.");
            ValidateTransform(entity.Transform);
            if (entity.Asset is not null)
            {
                if (entity.Asset.Id.ToString().Length != AssetId.TextLength)
                    throw new InvalidDataException($"Entity {entity.EntityId} has an empty Asset ID.");
                if (!Enum.IsDefined(entity.Asset.Kind))
                    throw new InvalidDataException($"Entity {entity.EntityId} has an unknown asset kind.");
            }
            if (entity.InstancedCollisionEnabled is not null && !entity.Asset.IsInstancedCollisionSource())
                throw new InvalidDataException($"Entity {entity.EntityId} cannot override instanced collision.");
            if (entity.Provenance is not null)
            {
                ValidateText(entity.Provenance.Game, nameof(entity.Provenance.Game));
                ValidateText(entity.Provenance.Section, nameof(entity.Provenance.Section));
                if (entity.Provenance.Level < 0 || entity.Provenance.SourceIndex < 0)
                    throw new InvalidDataException($"Entity {entity.EntityId} provenance indexes cannot be negative.");
            }
            if (entity.Source is not null)
            {
                if (entity.Source.ClassId < 0 || entity.Source.RawRecord is null
                    || entity.Source.RawRecord.Length is 0 or > 4_096)
                    throw new InvalidDataException($"Entity {entity.EntityId} source record is invalid.");
                if (entity.Source.SourceIndex < 0)
                    throw new InvalidDataException($"Entity {entity.EntityId} source index cannot be negative.");
            }
            ValidateGeometry(entity, content.Entities);
            ValidateLighting(entity);
            ValidateEnvironmentInstance(entity);
            ValidateSkyShell(entity);
            ValidateCollision(entity, content.Entities);
        }
        var skyOrders = content.Entities.Where(entity => entity.SkyShell is not null)
            .Select(entity => entity.SkyShell!.Order).Order().ToArray();
        if (!skyOrders.SequenceEqual(Enumerable.Range(0, skyOrders.Length)))
            throw new InvalidDataException("Project sky shell orders must be unique and contiguous.");
        if (content.Assets.Any(asset => asset is null))
            throw new InvalidDataException("Project assets cannot contain null entries.");
        if (content.Assets.Select(asset => asset.Id).Distinct().Count() != content.Assets.Count)
            throw new InvalidDataException("Project contains duplicate attached Asset IDs.");
        foreach (var asset in content.Assets)
        {
            if (asset is null || asset.Id.ToString().Length != AssetId.TextLength
                || asset.ParentId.ToString().Length != AssetId.TextLength)
                throw new InvalidDataException("Project asset IDs cannot be empty.");
            if (!Enum.IsDefined(asset.Kind))
                throw new InvalidDataException($"Project asset {asset.Id} has an unknown kind.");
            if (asset.Size < 1) throw new InvalidDataException($"Project asset {asset.Id} has an invalid size.");
            if (asset.Id == asset.ParentId)
                throw new InvalidDataException($"Project asset {asset.Id} cannot derive from itself.");
        }
        if (content.InstancedCollisionBindings.Any(binding => binding is null))
            throw new InvalidDataException("Project instanced collision bindings cannot contain null entries.");
        if (content.InstancedCollisionBindings.Select(binding => (binding.SourceAssetId, binding.InstanceEntityId)).Distinct().Count()
            != content.InstancedCollisionBindings.Count)
            throw new InvalidDataException("Project contains duplicate instanced collision bindings.");
        foreach (var binding in content.InstancedCollisionBindings)
        {
            if (binding.SourceAssetId.ToString().Length != AssetId.TextLength
                || binding.ProxyAssetId.ToString().Length != AssetId.TextLength
                || binding.SourceAssetId == binding.ProxyAssetId)
                throw new InvalidDataException("Project instanced collision binding IDs are invalid.");
            ValidateInstancedCollisionRecipe(binding.Recipe);
            if (binding.FaceTypeOverrides is null
                || binding.FaceTypeOverrides.Count > MaxInstancedCollisionFaceTypeOverrides
                || binding.FaceTypeOverrides.Any(value => value is null || value.FaceIndex < 0)
                || !binding.FaceTypeOverrides.Select(value => value.FaceIndex)
                    .SequenceEqual(binding.FaceTypeOverrides.Select(value => value.FaceIndex).Order()))
                throw new InvalidDataException($"Instanced collision proxy {binding.ProxyAssetId} has invalid face-type overrides.");
            if (binding.FaceTypeOverrides.Select(value => value.FaceIndex).Distinct().Count()
                != binding.FaceTypeOverrides.Count)
                throw new InvalidDataException($"Instanced collision proxy {binding.ProxyAssetId} has duplicate face-type overrides.");
            if (binding.FaceTypeOverrides.Any(value => value.RawType == binding.Recipe.RawType))
                throw new InvalidDataException($"Instanced collision proxy {binding.ProxyAssetId} stores a redundant face-type override.");
            var proxy = content.Assets.SingleOrDefault(asset => asset.Id == binding.ProxyAssetId);
            if (proxy is null || proxy.Kind != AssetKind.Collision || proxy.ParentId != binding.SourceAssetId)
                throw new InvalidDataException($"Instanced collision proxy {binding.ProxyAssetId} has invalid attached metadata.");
            if (binding.InstanceEntityId is { } instanceEntityId
                && !content.Entities.Any(entity => entity.EntityId == instanceEntityId
                    && entity.Asset is { } asset && asset.IsInstancedCollisionSource()
                    && asset.Id == binding.SourceAssetId))
                throw new InvalidDataException($"Instanced collision proxy {binding.ProxyAssetId} has an invalid instance binding.");
        }
    }

    internal static void ValidateInstancedCollisionRecipe(ProjectInstancedCollisionRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (!Enum.IsDefined(recipe.Kind) || recipe.GeneratorVersion < 1 || recipe.RecipeVersion < 1
            || recipe.LodIndex < 0 || !float.IsFinite(recipe.DetailSize)
            || !float.IsFinite(recipe.SealOpeningSize) || !float.IsFinite(recipe.SurfaceOffset))
            throw new InvalidDataException("Instanced collision recipe is invalid.");
        if (recipe.ProfileSections is < 0 or > 16
            || recipe.Kind == ProjectInstancedCollisionRecipeKind.Surface
            && (recipe.DetailSize != 0 || recipe.SealOpeningSize != 0
                || recipe.SurfaceOffset != 0 || recipe.OpenBase || recipe.ProfileSections != 0)
            || recipe.Kind == ProjectInstancedCollisionRecipeKind.Hull
            && (recipe.DetailSize != 0 || recipe.SealOpeningSize != 0
                || recipe.SurfaceOffset != 0 || recipe.OpenBase)
            || recipe.Kind == ProjectInstancedCollisionRecipeKind.Wrap
            && (recipe.DetailSize <= 0 || recipe.SealOpeningSize < 0 || recipe.ProfileSections != 0))
            throw new InvalidDataException("Instanced collision recipe parameters do not match its generator.");
    }

    private static void ValidateCollision(ProjectEntity entity, IReadOnlyList<ProjectEntity> entities)
    {
        if (entity.Collision is not { } collision) return;
        if (!Enum.IsDefined(collision.Kind) || collision.SourcePayloadIndex < 0 || collision.SourcePieceIndex < 0
            || collision.FaceCount < 0 || collision.VertexCount < 0
            || collision.Types is null || collision.Types.Any(value => value is null || value.Count <= 0)
            || collision.Types.Select(value => value.RawType).Distinct().Count() != collision.Types.Count)
            throw new InvalidDataException($"Entity {entity.EntityId} collision metadata is invalid.");
        if (entity.Asset?.Kind != AssetKind.Collision || entity.Provenance is not { Game: "UYA" })
            throw new InvalidDataException($"Entity {entity.EntityId} collision source is invalid.");
        if (collision.Kind == ProjectCollisionPieceKind.Solid
            && collision.Types.Sum(value => value.Count) != collision.FaceCount
            || collision.Kind == ProjectCollisionPieceKind.PlayerBarrier && collision.Types.Count != 0)
            throw new InvalidDataException($"Entity {entity.EntityId} collision type counts are invalid.");
        if (entity.Transform.Rotation != ProjectTransform.Identity.Rotation
            || entity.Transform.Scale != ProjectTransform.Identity.Scale)
            throw new InvalidDataException($"Entity {entity.EntityId} collision supports translation only.");
        if (collision.Attachment is not { } attachment) return;
        if (collision.Kind != ProjectCollisionPieceKind.Solid)
            throw new InvalidDataException($"Entity {entity.EntityId} player barrier cannot follow an instance.");
        ValidateTransform(attachment.BindTransform);
        var parent = entities.SingleOrDefault(value => value.EntityId == attachment.ParentEntityId);
        if (parent is null || !parent.Asset.IsInstancedCollisionSource())
            throw new InvalidDataException($"Entity {entity.EntityId} collision attachment does not reference a TIE or shrub.");
    }

    public static void ValidateTransform(ProjectTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (transform.Position is null || transform.Rotation is null || transform.Scale is null)
            throw new InvalidDataException("Entity transform components are required.");
        var values = new[]
        {
            transform.Position.X, transform.Position.Y, transform.Position.Z,
            transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W,
            transform.Scale.X, transform.Scale.Y, transform.Scale.Z,
        };
        if (values.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Entity transform values must be finite.");
        if (transform.Rotation is { X: 0, Y: 0, Z: 0, W: 0 })
            throw new InvalidDataException("Entity rotation cannot be an empty quaternion.");
        if (transform.Scale.X == 0 || transform.Scale.Y == 0 || transform.Scale.Z == 0)
            throw new InvalidDataException("Entity scale cannot contain zero.");
    }

    public static void ValidateSkyShell(ProjectEntity entity)
    {
        if (entity.SkyShell is not { } shell) return;
        if (entity.Asset?.Kind != AssetKind.Sky || entity.Provenance is null
            || entity.Source is not null || entity.Geometry is not null || entity.Lighting is not null
            || entity.TieLighting is not null || entity.Camera is not null || entity.AmbientSound is not null)
            throw new InvalidDataException($"Sky shell entity {entity.EntityId} has incompatible payload data.");
        if (entity.Transform != ProjectTransform.Identity)
            throw new InvalidDataException($"Sky shell entity {entity.EntityId} must use the identity transform.");
        if (shell.SourceShellIndex < 0 || shell.Order < 0
            || !ValidVector(shell.InitialRotationRadians) || !ValidVector(shell.AngularVelocityRadiansPerSecond))
            throw new InvalidDataException($"Sky shell entity {entity.EntityId} is invalid.");
    }

    public static void ValidateLevelSettings(ProjectLevelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.BackgroundColor is null || settings.FogColor is null)
            throw new InvalidDataException("Level setting colors are required.");
        var values = new[]
        {
            settings.FogNearDistance, settings.FogFarDistance,
            settings.FogNearIntensity, settings.FogFarIntensity,
        };
        if (values.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Level setting fog values must be finite.");
        if (settings.FogNearDistance < 0 || settings.FogFarDistance < 0)
            throw new InvalidDataException("Level setting fog distances cannot be negative.");
        if (settings.FogNearIntensity is < 0 or > 255 || settings.FogFarIntensity is < 0 or > 255)
            throw new InvalidDataException("Level setting fog intensities must be between 0 and 255.");
    }

    public static bool ValidPoints(IReadOnlyList<ProjectVector4>? points) => points is not null
        && points.Count <= 100_000
        && points.All(ValidVector);

    public static void ValidateText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            throw new InvalidDataException($"{name} must contain between 1 and 256 characters.");
    }

    private static void ValidateGeometry(ProjectEntity entity, IReadOnlyList<ProjectEntity> entities)
    {
        if (entity.Geometry is null) return;
        if (entity.Asset is not null || entity.Source is not null || entity.Lighting is not null)
            throw new InvalidDataException($"Geometry entity {entity.EntityId} cannot reference a model asset or class record.");
        var geometry = entity.Geometry;
        if ((geometry.Cuboid is not null ? 1 : 0) + (geometry.Sphere is not null ? 1 : 0)
            + (geometry.Cylinder is not null ? 1 : 0) + (geometry.Pill is not null ? 1 : 0)
            + (geometry.Spline is not null ? 1 : 0) + (geometry.GrindPath is not null ? 1 : 0)
            + (geometry.Area is not null ? 1 : 0) != 1)
            throw new InvalidDataException($"Entity {entity.EntityId} must contain exactly one geometry kind.");
        foreach (var (name, shape) in new[]
        {
            ("cuboid", geometry.Cuboid),
            ("sphere", geometry.Sphere),
            ("cylinder", geometry.Cylinder),
            ("pill", geometry.Pill),
        })
        {
            if (shape is null) continue;
            if (shape.Matrix is null || shape.Matrix.Count != 16
                || shape.InverseRotationMatrix is null || shape.InverseRotationMatrix.Count != 12
                || shape.EulerRotation is null
                || shape.Matrix.Concat(shape.InverseRotationMatrix)
                    .Append(shape.EulerRotation.X).Append(shape.EulerRotation.Y)
                    .Append(shape.EulerRotation.Z).Any(value => !float.IsFinite(value)))
                throw new InvalidDataException($"Entity {entity.EntityId} {name} geometry is invalid.");
            if (shape.CameraCollision is { } cameraCollision
                && (!float.IsFinite(cameraCollision.FloatValue) || !ValidVector(cameraCollision.BoundingSphere)))
                throw new InvalidDataException($"Entity {entity.EntityId} camera collision metadata is invalid.");
        }
        if (geometry.Spline is not null && !ValidPoints(geometry.Spline.Points))
            throw new InvalidDataException($"Entity {entity.EntityId} spline geometry is invalid.");
        if (geometry.GrindPath is not null
            && (!ValidVector(geometry.GrindPath.BoundingSphere) || !ValidPoints(geometry.GrindPath.Points)))
            throw new InvalidDataException($"Entity {entity.EntityId} grind path geometry is invalid.");
        if (geometry.Area is not null)
        {
            var area = geometry.Area;
            if (area.BoundingSphere is null
                || area.Splines is null || area.Cuboids is null || area.Spheres is null
                || area.Cylinders is null || area.NegativeCuboids is null
                || new[] { area.BoundingSphere.X, area.BoundingSphere.Y, area.BoundingSphere.Z, area.BoundingSphere.W }
                    .Any(value => !float.IsFinite(value)))
                throw new InvalidDataException($"Entity {entity.EntityId} area bounds are invalid.");
            var known = entities.Select(value => value.EntityId).ToHashSet();
            foreach (var link in area.Splines.Concat(area.Cuboids).Concat(area.Spheres)
                .Concat(area.Cylinders).Concat(area.NegativeCuboids))
            {
                if (link is null || link.EntityId is not null && !known.Contains(link.EntityId.Value))
                    throw new InvalidDataException($"Entity {entity.EntityId} area link is invalid.");
            }
        }
    }

    private static bool ValidVector(ProjectVector4? value) => value is not null
        && new[] { value.X, value.Y, value.Z, value.W }.All(float.IsFinite);

    private static bool ValidVector(ProjectVector3? value) => value is not null
        && new[] { value.X, value.Y, value.Z }.All(float.IsFinite);

    private static void ValidateLighting(ProjectEntity entity)
    {
        if (entity.Lighting is not null)
        {
            if (entity.Asset is not null || entity.Source is not null || entity.Geometry is not null)
                throw new InvalidDataException($"Lighting entity {entity.EntityId} cannot reference another entity payload.");
            var lighting = entity.Lighting;
            if ((lighting.DirectionalLight is not null ? 1 : 0) + (lighting.PointLight is not null ? 1 : 0)
                + (lighting.EnvironmentSamplePoint is not null ? 1 : 0)
                + (lighting.EnvironmentTransition is not null ? 1 : 0) != 1)
                throw new InvalidDataException($"Entity {entity.EntityId} must contain exactly one lighting kind.");
            if (lighting.DirectionalLight is { } directional
                && !new[] { directional.TopColor, directional.TopDirection,
                    directional.InverseColor, directional.InverseDirection }.All(ValidVector))
                throw new InvalidDataException($"Entity {entity.EntityId} directional light is invalid.");
            if (lighting.EnvironmentSamplePoint is { HeroColor: null } or { FogColor: null })
                throw new InvalidDataException($"Entity {entity.EntityId} environment sample point is invalid.");
            if (lighting.EnvironmentTransition is { } transition
                && (!ValidVector(transition.BoundingSphere)
                    || transition.InverseMatrix is null || transition.InverseMatrix.Count != 16
                    || transition.InverseMatrix.Any(value => !float.IsFinite(value))
                    || transition.HeroColor1 is null || transition.HeroColor2 is null
                    || transition.FogColor1 is null || transition.FogColor2 is null
                    || new[] { transition.FogNearDistance1, transition.FogNearIntensity1,
                        transition.FogFarDistance1, transition.FogFarIntensity1,
                        transition.FogNearDistance2, transition.FogNearIntensity2,
                        transition.FogFarDistance2, transition.FogFarIntensity2 }
                        .Any(value => !float.IsFinite(value))))
                throw new InvalidDataException($"Entity {entity.EntityId} environment transition is invalid.");
        }
        if (entity.TieLighting is not null
            && (entity.Layer != "ties" || entity.Source is null
                || entity.Provenance is not null && entity.Provenance.Section != "gameplay/core/tie_instances"
                || entity.TieLighting.AmbientRgbas is null || entity.TieLighting.AmbientRgbas.Length % 2 != 0))
            throw new InvalidDataException($"Entity {entity.EntityId} tie lighting is invalid.");
    }

    private static void ValidateEnvironmentInstance(ProjectEntity entity)
    {
        if (entity.Camera is not null && entity.AmbientSound is not null)
            throw new InvalidDataException($"Entity {entity.EntityId} cannot be both a camera and ambient sound.");
        if (entity.Camera is { } camera
            && (entity.Asset is not null || entity.Geometry is not null || entity.Lighting is not null
                || entity.Provenance?.Section != "gameplay/core/cameras" || entity.Source is null
                || !ValidVector(camera.EulerRotation)))
            throw new InvalidDataException($"Entity {entity.EntityId} camera data is invalid.");
        if (entity.AmbientSound is { } sound
            && (entity.Asset is not null || entity.Geometry is not null || entity.Lighting is not null
                || entity.Provenance?.Section != "gameplay/core/sound_instances" || entity.Source is null
                || !float.IsFinite(sound.Range) || sound.Range < 0
                || sound.Matrix is null || sound.Matrix.Count != 16
                || sound.InverseRotationMatrix is null || sound.InverseRotationMatrix.Count != 12
                || sound.Matrix.Concat(sound.InverseRotationMatrix).Any(value => !float.IsFinite(value))
                || !ValidVector(sound.EulerRotation) || !float.IsFinite(sound.Padding)))
            throw new InvalidDataException($"Entity {entity.EntityId} ambient sound data is invalid.");
    }
}
