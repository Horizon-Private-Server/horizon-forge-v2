using Forge.Host.Domain;
using Forge.Host.Games.UYA;
using RatchetPs2.Games.UYA.Gameplay;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaOcclusionVisibilityTests
{
    internal static void Run()
    {
        ProjectEntity Entity(string layer, byte[]? raw = null, ProjectTfragPiece? tfrag = null) =>
            new(EntityId.New(),layer,layer,ProjectTransform.Identity,null,Source:raw is null?null:new(1,raw),Tfrag:tfrag);
        var piece = new ProjectTfragPiece("primary:0","primary",ProjectTfragSourceKind.Primary,0,4,0,0,[]);
        var terrain = Entity("tfrags",tfrag:piece);
        var tie = Entity("ties",UyaGameplayInstanceTemplates.CreateTie(0x180E,99));
        var moby = Entity("mobys",UyaGameplayInstanceTemplates.CreateMoby(1,123));
        var unknown = Entity("ties",UyaGameplayInstanceTemplates.CreateTie(0x180E,100));
        var stash = Entity("tfrags",tfrag:piece with { OcclusionIndexStash = 1 });
        var mappings = new UyaOcclusionMappings([new(5,12)],[new(7,99),new(8,99)],[new(9,123)],0,[]);
        var result = UyaOcclusionVisibilityService.LinkEntities([terrain,tie,moby,unknown,stash],mappings);
        if (result.Count!=3 || !result.Single(e=>e.Id==tie.EntityId.ToString()).Groups.SequenceEqual([7,8])
            || !result.Single(e=>e.Id==terrain.EntityId.ToString()).Groups.SequenceEqual([5])
            || !result.Single(e=>e.Id==moby.EntityId.ToString()).Groups.SequenceEqual([9]))
            throw new Exception("Viewport occlusion must link native IDs and tfrag mapping indices, retaining all aliases and leaving unknown sources visible.");
        var reserved = UyaOcclusionVisibilityService.LinkEntities([terrain],mappings with { Tfrags=[new(1023,12)] });
        if (reserved.Count!=0) throw new Exception("The reserved visibility bit must not cull editor objects.");
    }
}
