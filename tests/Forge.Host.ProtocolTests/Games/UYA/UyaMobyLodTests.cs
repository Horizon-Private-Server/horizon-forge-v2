using System.Buffers.Binary;
using RatchetPs2.Core.Games;
using RatchetPs2.Core.LevelAssets;
using RatchetPs2.Core.Moby;
using RatchetPs2.Games.UYA.Moby;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaMobyLodTests
{
    public static void Run()
    {
        foreach (var oldJointCount in new byte[] { 0, 1 })
        {
            var model = new MobyModel { Scale = 1, JointCount = 4, Padding = oldJointCount,
                SkeletonFormat = MobyAnimationFormat.Compact, AnimationFormat = MobyAnimationFormat.Compact,
                CommonTransforms = new byte[64], Skeleton = new(), MeshTable = new(),
                HighLodMeshCount = 1, LowLodMeshCount = 1, MetalOffsets = 2 };
            for (var joint = 0; joint < 4; joint++)
                model.Skeleton.Bones.Add(new() { Row1 = new() { X = 1 }, Row2 = new() { Y = 1 }, Row3 = new() { Z = 1 } });
            model.CommonTransforms[12] = 0x7f;
            foreach (var kind in new[] { MobyMeshType.HighLod, MobyMeshType.LowLod })
            {
                var vertices = new byte[64];
                BinaryPrimitives.WriteUInt16LittleEndian(vertices, 4);
                for (var joint = 0; joint < 4; joint++) { vertices[16 + joint * 2] = (byte)joint; vertices[17 + joint * 2] = (byte)(joint * 4); }
                model.MeshTable.Entries.Add(new() { MeshType = kind, VifData = new byte[16], VifListSize = 1,
                    VertexData = vertices, VertexDataSize = 4 });
            }
            var source = MobyModelPacker.Build(model);
            var output = LevelAssetModelConverter.Convert(GameId.DL, GameId.UYA, FrontendAssetKind.Moby, source, 0x2079);
            Verify(output);
            Check(source[9] == oldJointCount, "LOD conversion preserves the source model");
            var previous = output.ToArray(); previous[9] = oldJointCount;
            Check(LevelAssetModelConverter.RepairPreviousMobyConversion(GameId.DL, GameId.UYA, source, previous)!.SequenceEqual(output),
                "existing skeleton-repaired conversions regain the UYA low-LOD joint count");
            previous[0x2d] ^= 1;
            Check(LevelAssetModelConverter.RepairPreviousMobyConversion(GameId.DL, GameId.UYA, source, previous) is null,
                "LOD header repair preserves independently edited models");
            foreach (var runtimeMetadata in new[] { false, true })
            {
                using var legacyInput = new MemoryStream(source);
                var legacyModel = MobyModelReader.Read(legacyInput, new() { AnimationFormat = MobyAnimationFormat.Compact });
                var legacy = UyaMobyConverter.ConvertFromCompact(legacyModel, [],
                    normalizeRuntimeMetadata: runtimeMetadata, rebuildSkeletonFallback: false);
                legacy[9] = oldJointCount;
                Check(LevelAssetModelConverter.RepairPreviousMobyConversion(GameId.DL, GameId.UYA, source, legacy)!.SequenceEqual(output),
                    "earlier skeleton and runtime conversions also regain their low-LOD joints");
            }
            var editedHeader = output.ToArray(); editedHeader[9] = 2;
            Check(LevelAssetModelConverter.RepairPreviousMobyConversion(GameId.DL, GameId.UYA, source, editedHeader) is null,
                "repair only recognizes the original DL header byte");
            model.LowLodMeshCount = 0; model.MetalOffsets = 1; model.MeshTable.Entries.RemoveAt(1);
            Verify(LevelAssetModelConverter.Convert(GameId.DL, GameId.UYA, FrontendAssetKind.Moby,
                MobyModelPacker.Build(model), 0x2079));
        }
    }

    internal static void Verify(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        var model = MobyModelReader.Read(input);
        var meshes = model.MeshTable?.Entries ?? [];
        foreach (var far in new[] { false, true })
        {
            var (first, count, joints) = Select(bytes, far);
            Check(first == (far && model.LowLodMeshCount > 0 ? model.HighLodMeshCount : 0)
                && count == (far && model.LowLodMeshCount > 0 ? model.LowLodMeshCount : model.HighLodMeshCount),
                "native selector retains both mesh LODs and falls back to high LOD when low LOD is absent");
            Check(count > 0 && first + count <= meshes.Count, "native renderer selects the expected mesh range");
            foreach (var mesh in meshes.Skip(first).Take(count))
            {
                if (model.JointCount == 0 || mesh.VertexData.Length < 16) continue;
                var transfers = BinaryPrimitives.ReadUInt16LittleEndian(mesh.VertexData);
                for (var transfer = 0; transfer < transfers; transfer++)
                    Check(mesh.VertexData[16 + transfer * 2] < joints,
                        $"{(far ? "low" : "high")} LOD references a joint omitted by the native renderer");
            }
        }
    }

    private static (int First, int Count, int Joints) Select(byte[] header, bool far)
    {
        // Hoven's native LOD selection at 0x47F2B0, including branch delay slots.
        uint[] code =
        [
            0x1c200009, 0x9fcb0040, 0x91280000, 0x7014a389, 0x20010000, 0x92220004,
            0x21080001, 0x92260008, 0x0811fcc0, 0xa1280000, 0x92220005, 0x20070001,
            0x1040fff5, 0x91280001, 0x92210004, 0x21080001, 0x92260009, 0x00000000,
            0xa1280001, 0x70f4a389, 0x000b6402, 0x87d4007c, 0x8e280018, 0x318c00ff,
            0x8e290014, 0x000b6e02, 0x8e310000, 0xaf020000, 0x00010900, 0xaf060010,
        ];
        var memory = new byte[0x5000]; header.AsSpan(0, 0x48).CopyTo(memory.AsSpan(0x1000));
        var registers = new uint[32]; registers[1] = far ? 1u : uint.MaxValue;
        registers[9] = 0x4000; registers[17] = 0x1000; registers[24] = 0x3000; registers[30] = 0x2000;
        var pc = 0x47f2b0; int? branch = null;
        for (var step = 0; step < 64 && pc != 0x47f328; step++)
        {
            var word = code[(pc - 0x47f2b0) / 4]; var op = word >> 26;
            var rs = (int)(word >> 21) & 31; var rt = (int)(word >> 16) & 31; var rd = (int)(word >> 11) & 31;
            var imm = unchecked((short)word); var address = unchecked((int)registers[rs] + imm);
            var next = branch ?? pc + 4; branch = null;
            switch (op)
            {
                case 0 when (word & 63) == 0: registers[rd] = registers[rt] << ((int)(word >> 6) & 31); break;
                case 0 when (word & 63) == 2: registers[rd] = registers[rt] >> ((int)(word >> 6) & 31); break;
                case 2: branch = (int)(word & 0x03ffffff) << 2; break;
                case 4: if (registers[rs] == registers[rt]) branch = pc + 4 + imm * 4; break;
                case 7: if ((int)registers[rs] > 0) branch = pc + 4 + imm * 4; break;
                case 8: registers[rt] = unchecked(registers[rs] + (uint)imm); break;
                case 12: registers[rt] = registers[rs] & (word & 0xffff); break;
                case 0x1c: break; // PCPYLD only changes an unused upper register half here.
                case 0x21: registers[rt] = unchecked((uint)BinaryPrimitives.ReadInt16LittleEndian(memory.AsSpan(address))); break;
                case 0x23 or 0x27: registers[rt] = BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan(address)); break;
                case 0x24: registers[rt] = memory[address]; break;
                case 0x28: memory[address] = (byte)registers[rt]; break;
                case 0x2b: BinaryPrimitives.WriteUInt32LittleEndian(memory.AsSpan(address), registers[rt]); break;
                default: throw new Exception($"Unexpected LOD selector instruction {word:X8}");
            }
            registers[0] = 0; pc = next;
        }
        Check(pc == 0x47f328, "native LOD selector terminates");
        return ((int)registers[1] / 16, BinaryPrimitives.ReadInt32LittleEndian(memory.AsSpan(0x3000)),
            BinaryPrimitives.ReadInt32LittleEndian(memory.AsSpan(0x3010)));
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
