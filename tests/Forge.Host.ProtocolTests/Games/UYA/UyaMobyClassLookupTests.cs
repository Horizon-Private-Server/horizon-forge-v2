using System.Buffers.Binary;
using Forge.Host.Games.UYA;
using RatchetPs2.Core.Games;
using RatchetPs2.Games.UYA.Level;
using RatchetPs2.Sdk;

namespace Forge.Host.ProtocolTests.Games.UYA;

internal static class UyaMobyClassLookupTests
{
    public static void Run()
    {
        var source = Fixture();
        var header = Header(0x4f9, 0x2079, 0x24f9);
        var output = LevelAssetComposer.ComposeMobyClassLookup(GameId.UYA, source, header);
        Verify(source, output, [0x4f9, 0x2079, 0x24f9]);
        Verify(source, LevelAssetComposer.ComposeMobyClassLookup(GameId.UYA, source, Header(0x7fff)), [0x7fff]);
        Check(LevelAssetComposer.ComposeMobyClassLookup(GameId.UYA, source, Header(100)).SequenceEqual(source),
            "native classes retain the source overlay byte-for-byte");
        try
        {
            LevelAssetComposer.ComposeMobyClassLookup(GameId.UYA, source, Header(0x8000));
            throw new Exception("Expected signed class-list rejection");
        }
        catch (InvalidDataException) { }
        var unsupported = source.ToArray();
        Write(unsupported, 16 + 0x2000 + 16 + 16, 0);
        try
        {
            LevelAssetComposer.ComposeMobyClassLookup(GameId.UYA, unsupported, header);
            throw new Exception("Expected unsupported runtime rejection");
        }
        catch (InvalidDataException) { }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try
        {
            LevelAssetComposer.ComposeMobyClassLookup(GameId.UYA, source, header, cancel.Token);
            throw new Exception("Expected cancellation");
        }
        catch (OperationCanceledException) { }
    }

    internal static void Verify(byte[] source, byte[] output, int[] classes)
    {
        var sourceSections = Sections(source);
        var outputSections = Sections(output);
        var codeSection = sourceSections.Single(value => value.Address <= value.Entry
            && value.Entry < value.Address + value.Length && value.Type == 1);
        var code = source.AsSpan(codeSection.Offset, codeSection.Length);
        var initializer = -1;
        var heapInitializer = -1;
        var heapLow = -1;
        for (var offset = 0; offset <= code.Length - 40; offset += 4)
        {
            if ((Read(code, offset) & 0xffff0000) == 0x3c040000
                && Read(code, offset + 4) == 0x2405ffff && Read(code, offset + 16) == 0x24062000
                && Read(code, offset + 36) == 0x24060f00) initializer = offset;
            if (Read(code, offset + 4) == 0x3c02ffff && Read(code, offset + 28) == 0x00822024)
            { heapInitializer = offset; heapLow = offset + 20; }
            if (offset <= code.Length - 52 && Read(code, offset + 8) == 0x3c02ffff
                && Read(code, offset + 32) == 0x3442f000 && Read(code, offset + 48) == 0x00822024)
            { heapInitializer = offset; heapLow = offset + 36; }
        }
        Check(initializer >= 0 && heapInitializer >= 0, "retail overlay has verified initializers");
        var oldTable = (int)((Read(code, initializer) & 0xffff) << 16) + (short)Read(code, initializer + 8);
        var beforeHeap = (int)((Read(code, heapInitializer) & 0xffff) << 16) + (short)Read(code, heapLow);
        var memory = new byte[0x2000000];
        foreach (var section in outputSections)
        {
            Check(section.Address > 0 && section.Address <= memory.Length - section.Length, "overlay section fits EE memory");
            if (section.Type == 8) memory.AsSpan(section.Address, section.Length).Clear();
            else output.AsSpan(section.Offset, section.Length).CopyTo(memory.AsSpan(section.Address));
        }
        // The crash dump had a zeroed BSS pointer despite the expanded table being loaded.
        foreach (var section in outputSections.Where(value => value.Type == 8))
            memory.AsSpan(section.Address, section.Length).Clear();
        var patchedCode = memory.AsSpan(codeSection.Address, codeSection.Length);
        var clearAddress = (int)((Read(code, initializer + 12) & 0x03ffffff) << 2);
        Check(Read(memory, oldTable) == 0, "lookup initialization begins with a zero BSS pointer");
        ExecuteInitializer(memory, codeSection.Address + initializer, clearAddress);
        var table = (int)Read(memory, oldTable);
        var capacity = classes.Max() < 0x4000 ? 0x4000 : 0x8000;
        Check(table == (beforeHeap & ~0xfff) && table >= sourceSections.Max(value => value.Address + value.Length),
            "expanded lookup occupies newly reserved static memory");
        var afterHeap = (int)((Read(patchedCode, heapInitializer) & 0xffff) << 16) + (short)Read(patchedCode, heapLow);
        Check((afterHeap & ~0xfff) > table + capacity + 32, "level arena starts after the expanded lookup and its initializer");
        Check(memory.AsSpan(table, capacity).IndexOfAnyExcept((byte)0xff) < 0,
            "native initialization fills the complete lookup, including high class entries");
        var neighbours = memory.AsSpan(oldTable + 0x2000, 0x1000).ToArray();
        for (var index = 0; index < classes.Length; index++) memory[table + classes[index]] = (byte)(index + 1);
        Check(classes.Select(value => memory[table + value]).SequenceEqual(Enumerable.Range(1, classes.Length).Select(value => (byte)value)),
            "native and DL classes keep separate lookup entries even when their lower 13 bits collide");
        Check(neighbours.SequenceEqual(memory.AsSpan(oldTable + 0x2000, 0x1000).ToArray()),
            "DL class registration preserves neighbouring rendering tables");
        var references = 0;
        for (var offset = 0; offset < code.Length; offset += 4)
        {
            var original = Read(code, offset);
            var actual = Read(patchedCode, offset);
            if (original == actual) continue;
            if (offset == heapInitializer || offset == heapLow) continue;
            if (original == Read(code, initializer + 12)
                && offset >= 4 && Read(code, offset - 4) >> 26 == 9
                && (Read(code, offset - 4) & 0xffff) == ((uint)oldTable & 0xffff)
                && Read(code, offset + 4) == 0x24062000)
            {
                Check(actual >> 26 == 3 && (actual & 0x03ffffff) << 2 == table + capacity,
                    "every native lookup reset calls the initializer outside BSS");
                continue;
            }
            Check(original >> 26 == 9 && actual == ((original & 0x03ffffff) | 0x8c000000),
                "runtime patch only replaces verified address calculations with pointer loads");
            var registers = new uint[32];
            var sourceRegister = (int)(original >> 21) & 31;
            registers[sourceRegister] = (uint)(oldTable + 0x8000) & 0xffff0000;
            // Execute the patched LW exactly, including its signed immediate.
            var address = checked((int)registers[sourceRegister] + unchecked((short)actual));
            registers[(int)(actual >> 16) & 31] = Read(memory, address);
            Check(registers[(int)(actual >> 16) & 31] == table, "every patched lookup reference loads the expanded table");
            references++;
        }
        Check(references >= 7, "all model-loading and instance lookup routes are patched");
        for (var index = 1; index < outputSections.Count; index++)
            Check(outputSections[index].Address >= outputSections[index - 1].Address + outputSections[index - 1].Length,
                "overlay sections stay ordered and do not overlap BSS");
    }

    private static void ExecuteInitializer(byte[] memory, int start, int clearAddress)
    {
        var registers = Enumerable.Range(0, 32).Select(value => (uint)(0xabcdef00 + value)).ToArray();
        registers[0] = 0;
        var original = registers.ToArray();
        var resume = start + 20;
        var pc = start;
        int? pendingJump = null;
        for (var step = 0; step < 64 && pc != resume; step++)
        {
            if (pc == clearAddress)
            {
                var address = checked((int)registers[4]);
                var length = checked((int)registers[6]);
                Check(address >= 0x100000 && address <= memory.Length - length,
                    "native fill must not write through the null pointer from the reported crash");
                Check(registers[5] == uint.MaxValue && length is 0x4000 or 0x8000,
                    "native fill receives the full unsigned lookup length");
                memory.AsSpan(address, length).Fill(0xff);
                registers[4] += registers[6]; registers[6] = 0;
                pc = checked((int)registers[31]);
                continue;
            }
            var word = Read(memory, pc);
            var rs = (int)(word >> 21) & 31;
            var rt = (int)(word >> 16) & 31;
            var next = pendingJump ?? pc + 4;
            pendingJump = null;
            switch (word >> 26)
            {
                case 0 when word == 0: break;
                case 0 when (word & 0x3f) == 0x25:
                    registers[(int)(word >> 11) & 31] = registers[rs] | registers[rt]; break;
                case 15: registers[rt] = word << 16; break;
                case 9: registers[rt] = unchecked(registers[rs] + (uint)(short)word); break;
                case 13: registers[rt] = registers[rs] | (word & 0xffff); break;
                case 35: registers[rt] = Read(memory, checked((int)registers[rs] + unchecked((short)word))); break;
                case 43: Write(memory, checked((int)registers[rs] + unchecked((short)word)), registers[rt]); break;
                case 3:
                    registers[31] = (uint)(pc + 8);
                    goto case 2;
                case 2: pendingJump = checked((int)((word & 0x03ffffff) << 2)); break;
                default: throw new Exception($"Unexpected initializer instruction {word:X8} at {pc:X8}");
            }
            registers[0] = 0;
            pc = next;
        }
        Check(pc == resume && registers[31] == resume, "lookup initializer returns through the original native call site");
        Check(Enumerable.Range(0, 32).Where(index => index is not (4 or 5 or 6 or 31))
            .All(index => registers[index] == original[index]), "initializer preserves every register untouched by the native fill");
    }

    public static async Task QualifyAsync(string sourceIso)
    {
        using var iso = File.OpenRead(sourceIso);
        var levels = LevelCatalogReader.FindAvailable(GameId.UYA, iso).Where(value => value >= 3).ToArray();
        foreach (var level in levels)
        {
            var wad = LevelArchiveReader.ExtractPrimary(GameId.UYA, iso, level);
            var source = UyaLevelWadUnpacker.Unpack(wad).Files.Single(value => value.Path == "code/code.bin").Bytes;
            try
            {
                var output = LevelAssetComposer.ComposeMobyClassLookup(GameId.UYA, source, Header(0x4f9, 0x2079, 0x24f9));
                Verify(source, output, [0x4f9, 0x2079, 0x24f9]);
                Console.WriteLine($"Level {level}: expanded lookup, signed class identities, neighbouring tables and level arena verified.");
            }
            catch (Exception error)
            {
                throw new Exception($"Level {level} lookup qualification failed: {error.Message}", error);
            }
        }
        Console.WriteLine($"Qualified high Moby class lookup for {levels.Length} retail UYA levels.");
        await Task.CompletedTask;
    }

    private static byte[] Header(params int[] classes)
    {
        var bytes = new byte[0xc0 + classes.Length * 0x20];
        Write(bytes, 0x18, (uint)classes.Length); Write(bytes, 0x1c, 0xc0);
        for (var index = 0; index < classes.Length; index++) Write(bytes, 0xc4 + index * 0x20, (uint)classes[index]);
        return bytes;
    }

    private static byte[] Fixture()
    {
        using var output = new MemoryStream();
        const int codeAddress = 0x400000;
        void Section(int address, byte[] bytes, int type)
        {
            var header = new byte[16];
            Write(header, 0, (uint)address); Write(header, 4, (uint)bytes.Length);
            Write(header, 8, (uint)type); Write(header, 12, codeAddress);
            output.Write(header); output.Write(bytes);
        }
        Section(0x260000, new byte[0x2000], 8);
        var code = new byte[0x200];
        uint[] initialization = [0x3c040026, 0x2405ffff, 0x24840000, 0x0c100100, 0x24062000,
            0x3c040026, 0x2405ffff, 0x24843000, 0x0c100100, 0x24060f00];
        for (var index = 0; index < initialization.Length; index++) Write(code, index * 4, initialization[index]);
        uint[] heap = [0x3c040041, 0x3c02ffff, 0x3c060025, 0x8cc60000, 0x3442f000, 0x248405ff,
            0x3c0801f4, 0x00822024];
        for (var index = 0; index < heap.Length; index++) Write(code, 0x40 + index * 4, heap[index]);
        for (var index = 0; index < 8; index++)
        {
            Write(code, 0x80 + index * 16, 0x3c020026);
            Write(code, 0x84 + index * 16, 0x24430000);
        }
        Section(codeAddress, code, 1);
        return output.ToArray();
    }

    private sealed record Section(int Offset, int Address, int Length, int Type, int Entry);
    private static List<Section> Sections(byte[] bytes)
    {
        var result = new List<Section>();
        for (var offset = 0; offset < bytes.Length;)
        {
            var length = (int)Read(bytes, offset + 4);
            result.Add(new(offset + 16, (int)Read(bytes, offset), length, (int)Read(bytes, offset + 8), (int)Read(bytes, offset + 12)));
            offset += 16 + length;
        }
        return result;
    }
    private static uint Read(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static void Write(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
