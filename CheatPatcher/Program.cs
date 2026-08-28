using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DiscUtils.Iso9660;

[assembly: InternalsVisibleTo("CheatPatcher.Gui")]
[assembly: InternalsVisibleTo("CheatPatcherGui")]

namespace CheatPatcher;

internal static class GameConstants
{
    public const uint KernelRamCeiling = 0x100000;
    public const uint BlockClusterGap = 0x10;
    public const uint PageSize = 0x1000;
}

internal sealed class HookConfig
{
    public uint? Address;
    public string? MastercodeLine;
}

internal sealed class ProgramHeader
{
    public uint Type, Offset, VAddr, PAddr, FileSize, MemSize, Flags, Align;
    public const int Size = 32;

    public static ProgramHeader Read(byte[] d, int o) => new()
    {
        Type = BitConverter.ToUInt32(d, o), Offset = BitConverter.ToUInt32(d, o + 4),
        VAddr = BitConverter.ToUInt32(d, o + 8), PAddr = BitConverter.ToUInt32(d, o + 12),
        FileSize = BitConverter.ToUInt32(d, o + 16), MemSize = BitConverter.ToUInt32(d, o + 20),
        Flags = BitConverter.ToUInt32(d, o + 24), Align = BitConverter.ToUInt32(d, o + 28),
    };

    public void WriteInto(byte[] d, int o)
    {
        BitConverter.GetBytes(Type).CopyTo(d, o); BitConverter.GetBytes(Offset).CopyTo(d, o + 4);
        BitConverter.GetBytes(VAddr).CopyTo(d, o + 8); BitConverter.GetBytes(PAddr).CopyTo(d, o + 12);
        BitConverter.GetBytes(FileSize).CopyTo(d, o + 16); BitConverter.GetBytes(MemSize).CopyTo(d, o + 20);
        BitConverter.GetBytes(Flags).CopyTo(d, o + 24); BitConverter.GetBytes(Align).CopyTo(d, o + 28);
    }
}

internal static class Program
{
    private const uint PT_LOAD = 1;
    private static readonly Regex PatchRe = new(@"^patch=(\d+),EE,([0-9A-Fa-f]+),(\w+),([0-9A-Fa-f]+)", RegexOptions.Compiled);

    private static byte[] _data = Array.Empty<byte>();
    private static readonly List<ProgramHeader> Segs = new();

    private static int Main(string[] args)
    {
        try
        {
            Console.WriteLine("=== Cheat Patcher ===");
            Console.WriteLine();

            string input = StripQuotes(PromptNonEmpty("1) Enter the ELF or ISO path to patch: "));
            bool isIso = Path.GetExtension(input).Equals(".iso", StringComparison.OrdinalIgnoreCase);

            if (isIso)
            {
                string outFolder = StripQuotes(PromptNonEmpty("2) Enter the output folder (patched disc contents will be extracted here): "));
                string elfName = StripQuotes(PromptNonEmpty("   Enter the ELF filename inside the ISO (e.g. SLXX_XXX.XX): "));

                HookConfig hook = PromptHookConfig();
                string[] pnachArgs = PromptPnachPaths();

                RunIso(input, outFolder, elfName, pnachArgs, hook);
            }
            else
            {
                string outElf = StripQuotes(PromptNonEmpty("2) Enter the output ELF path: "));

                HookConfig hook = PromptHookConfig();
                string[] pnachArgs = PromptPnachPaths();

                RunElf(input, outElf, pnachArgs, hook);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static string PromptNonEmpty(string prompt)
    {
        while (true)
        {
            Console.Write(prompt);
            string? line = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(line)) return line.Trim();
            Console.WriteLine("   (can't be empty, try again)");
        }
    }

    private static string StripQuotes(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s.StartsWith("\"") && s.EndsWith("\""))
            s = s[1..^1];
        return s;
    }

    private static HookConfig PromptHookConfig()
    {
        Console.WriteLine();
        Console.WriteLine("3) CodeBreaker mastercode (optional) -- hooks sceSifSendCmd so cheats run");
        Console.WriteLine("   every frame. Format: AAAAAAAA VVVVVVVV.");
        Console.Write("   Enter mastercode (or press Enter to skip): ");

        string? line = Console.ReadLine()?.Trim();
        return ParseHookConfig(line);
    }

    internal static HookConfig ParseHookConfig(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            Console.WriteLine("   -> No mastercode. Hook-integrity validation will be skipped.");
            return new HookConfig();
        }

        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || parts[0].Length <= 2)
        {
            Console.WriteLine("   -> Unrecognized format, mastercode ignored.");
            return new HookConfig();
        }

        string addrPart = parts[0];
        string addrHex = addrPart.Length > 2 ? addrPart[2..] : addrPart;
        if (!uint.TryParse(addrHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint addr))
        {
            Console.WriteLine("   -> Could not parse mastercode address.");
            return new HookConfig();
        }

        Console.WriteLine($"   -> Hook address detected: {addr:X}");
        return new HookConfig { Address = addr, MastercodeLine = line };
    }

    private static string[] PromptPnachPaths()
    {
        Console.WriteLine();
        Console.WriteLine("4) Enter .pnach file or folder paths. Press Enter on empty line when done.");

        var list = new List<string>();
        while (true)
        {
            Console.Write($"   pnach [{list.Count + 1}] (Enter to finish): ");
            string? line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                if (list.Count == 0)
                {
                    Console.WriteLine("   (at least one pnach path is required)");
                    continue;
                }
                break;
            }
            list.Add(StripQuotes(line));
        }
        return list.ToArray();
    }

    internal static void RunIso(string inIso, string outFolder, string elfName, string[] pnachArgs, HookConfig hook)
    {
        Console.WriteLine($"Opening ISO: {inIso}");
        byte[] elfBytes;
        using (var isoStream = File.OpenRead(inIso))
        using (var reader = new CDReader(isoStream, joliet: false))
        {
            string? foundPath = FindFile(reader, reader.Root, elfName);
            if (foundPath is null)
                throw new FileNotFoundException($"Could not find '{elfName}' inside ISO.");

            Console.WriteLine($"Found executable: {foundPath}");
            using var s = reader.OpenFile(foundPath, FileMode.Open);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            elfBytes = ms.ToArray();
        }

        byte[] patchedElf = PatchElfBytes(elfBytes, pnachArgs, hook);
        Directory.CreateDirectory(outFolder);

        using (var isoStream = File.OpenRead(inIso))
        using (var reader = new CDReader(isoStream, joliet: false))
        {
            ExtractAllFiles(reader, reader.Root, outFolder, elfName, patchedElf);
        }

        Console.WriteLine("\nExtraction complete.");
    }

    private static string? FindFile(CDReader reader, DiscUtils.DiscDirectoryInfo dir, string name)
    {
        foreach (var f in dir.GetFiles())
        {
            string baseName = f.Name.Split(';')[0];
            if (baseName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return f.FullName;
        }
        foreach (var sub in dir.GetDirectories())
        {
            var found = FindFile(reader, sub, name);
            if (found is not null) return found;
        }
        return null;
    }

    private static void ExtractAllFiles(CDReader reader, DiscUtils.DiscDirectoryInfo dir, string outDir, string elfName, byte[] patchedElf)
    {
        Directory.CreateDirectory(outDir);
        foreach (var f in dir.GetFiles())
        {
            string baseName = f.Name.Split(';')[0];
            string destFile = Path.Combine(outDir, baseName);

            if (baseName.Equals(elfName, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllBytes(destFile, patchedElf);
                continue;
            }

            using var s = reader.OpenFile(f.FullName, FileMode.Open);
            using var outFs = File.Create(destFile);
            s.CopyTo(outFs);
        }
        foreach (var sub in dir.GetDirectories())
        {
            string baseName = sub.Name.Split(';')[0];
            ExtractAllFiles(reader, sub, Path.Combine(outDir, baseName), elfName, patchedElf);
        }
    }

    internal static void RunElf(string inElf, string outElf, string[] pnachArgs, HookConfig hook)
    {
        byte[] inputBytes = File.ReadAllBytes(inElf);
        byte[] result = PatchElfBytes(inputBytes, pnachArgs, hook);
        File.WriteAllBytes(outElf, result);
        Console.WriteLine($"Wrote {outElf} ({result.Length} bytes)");
    }

    private static byte[] PatchElfBytes(byte[] inputBytes, string[] pnachArgs, HookConfig hook)
    {
        _data = (byte[])inputBytes.Clone();
        int origLen = _data.Length;

        if (_data.Length < 52 || _data[0] != 0x7F || _data[1] != (byte)'E' || _data[2] != (byte)'L' || _data[3] != (byte)'F')
            throw new InvalidDataException("Invalid ELF format.");
        if (_data[4] != 1)
            throw new InvalidDataException("Only 32-bit ELF is supported.");

        Segs.Clear();
        uint ePhoff = BitConverter.ToUInt32(_data, 28);
        ushort ePhentsize = BitConverter.ToUInt16(_data, 42);
        ushort ePhnum = BitConverter.ToUInt16(_data, 44);
        for (int i = 0; i < ePhnum; i++)
            Segs.Add(ProgramHeader.Read(_data, (int)ePhoff + i * ePhentsize));

        bool hasHook = hook.Address.HasValue;
        uint before = 0;
        if (hasHook)
        {
            before = ReadWord(hook.Address!.Value);
            Console.WriteLine($"Hook address @ {hook.Address.Value:X} BEFORE: {before:08X}");
        }

        var loadSegs = Segs.Where(s => s.Type == PT_LOAD).ToList();
        if (loadSegs.Count == 0)
            throw new InvalidDataException("No PT_LOAD segments found.");

        uint nextFreeVAddr = loadSegs.Max(s => s.VAddr + Math.Max(s.FileSize, s.MemSize));

        foreach (var pnachPath in ExpandPnachPaths(pnachArgs))
        {
            Console.WriteLine($"\nProcessing: {Path.GetFileName(pnachPath)}");
            var (normal, forcedBlocks, conditionals) = ParsePnach(pnachPath);

            if (conditionals.Count > 0)
            {
                Console.WriteLine($"  Skipping {conditionals.Count} conditional cheat line(s).");
                string chtOutDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pnachPath)) ?? ".", "cht_export");
                ExportConditionalsToCht(conditionals, chtOutDir, Path.GetFileNameWithoutExtension(pnachPath), hook);
            }

            var claimed = new HashSet<uint>(forcedBlocks.SelectMany(b => b.Keys));
            var lowAddrs = normal.Keys.Where(a => a < GameConstants.KernelRamCeiling && !claimed.Contains(a))
                                       .OrderBy(a => a).ToList();

            var autoBlocks = new List<Dictionary<uint, uint>>();
            if (lowAddrs.Count > 0)
            {
                var cur = new Dictionary<uint, uint> { [lowAddrs[0]] = normal[lowAddrs[0]] };
                uint lastAddr = lowAddrs[0];
                for (int i = 1; i < lowAddrs.Count; i++)
                {
                    uint a = lowAddrs[i];
                    if (a - lastAddr <= GameConstants.BlockClusterGap)
                    {
                        cur[a] = normal[a];
                    }
                    else
                    {
                        autoBlocks.Add(cur);
                        cur = new Dictionary<uint, uint> { [a] = normal[a] };
                    }
                    lastAddr = a;
                }
                autoBlocks.Add(cur);
            }
            foreach (var addr in lowAddrs) normal.Remove(addr);

            var allBlocks = forcedBlocks.Concat(autoBlocks).ToList();
            var pendingSegments = new List<ProgramHeader>();
            var pendingBytes = new List<byte>();

            var blockGrowSeg = Segs.Where(s => s.Type == PT_LOAD &&
                    s.VAddr + Math.Max(s.FileSize, s.MemSize) == nextFreeVAddr)
                .OrderByDescending(s => s.VAddr)
                .FirstOrDefault();

            foreach (var block in allBlocks)
            {
                nextFreeVAddr = AlignUp(nextFreeVAddr, GameConstants.PageSize);
                uint regionVAddr = nextFreeVAddr;
                var (words, entries, regionSize) = RelocateBlock(block, normal, regionVAddr);

                uint paddedSize = AlignUp(regionSize, GameConstants.PageSize);
                uint pad = paddedSize - regionSize;
                var regionBytes = new List<byte>(words.Length * 4 + (int)pad);
                foreach (var w in words) regionBytes.AddRange(BitConverter.GetBytes(w));
                if (pad > 0) regionBytes.AddRange(new byte[pad]);

                if (blockGrowSeg != null)
                {
                    int insertOffset = (int)(blockGrowSeg.Offset + blockGrowSeg.FileSize);
                    InsertBytes(insertOffset, regionBytes.ToArray(), blockGrowSeg);
                    blockGrowSeg.FileSize += (uint)regionBytes.Count;
                    blockGrowSeg.MemSize = blockGrowSeg.FileSize;
                    blockGrowSeg.Flags = 7;
                }
                else
                {
                    uint regionOffset = (uint)(_data.Length + pendingBytes.Count);
                    pendingBytes.AddRange(regionBytes);
                    pendingSegments.Add(new ProgramHeader
                    {
                        Type = PT_LOAD, Offset = regionOffset, VAddr = regionVAddr, PAddr = regionVAddr,
                        FileSize = regionSize, MemSize = regionSize, Flags = 7, Align = GameConstants.PageSize,
                    });
                }

                foreach (var (addr, instr, origTarget) in entries)
                    normal[addr] = instr;

                nextFreeVAddr = regionVAddr + paddedSize;
            }

            var allCurrentSegs = Segs.Concat(pendingSegments).ToList();
            bool InAnySegment(uint a) => allCurrentSegs.Any(s => s.Type == PT_LOAD &&
                a >= s.VAddr && a < s.VAddr + Math.Max(s.FileSize, s.MemSize));

            var outOfRange = normal.Keys.Where(a => !InAnySegment(a)).OrderBy(a => a).ToList();
            if (outOfRange.Count > 0)
            {
                var growable = outOfRange.Where(a => a >= nextFreeVAddr).OrderBy(a => a).ToList();
                var remaining = outOfRange.Where(a => a < nextFreeVAddr).OrderBy(a => a).ToList();

                if (growable.Count > 0 && pendingSegments.Count == 0)
                {
                    var growSeg = Segs.Where(s => s.Type == PT_LOAD &&
                            s.VAddr + Math.Max(s.FileSize, s.MemSize) == nextFreeVAddr)
                        .OrderByDescending(s => s.VAddr)
                        .FirstOrDefault();

                    if (growSeg != null)
                    {
                        uint maxNeeded = growable.Max() + 4;
                        uint newEndVAddr = AlignUp(maxNeeded, GameConstants.PageSize);
                        uint growBy = newEndVAddr - nextFreeVAddr;

                        int insertOffset = (int)(growSeg.Offset + growSeg.FileSize);
                        InsertBytes(insertOffset, new byte[growBy], growSeg);

                        growSeg.FileSize += growBy;
                        growSeg.MemSize = growSeg.FileSize;
                        growSeg.Flags = 7;
                        nextFreeVAddr = newEndVAddr;
                    }
                    else
                    {
                        remaining = remaining.Concat(growable).OrderBy(a => a).ToList();
                    }
                }
                else if (growable.Count > 0)
                {
                    remaining = remaining.Concat(growable).OrderBy(a => a).ToList();
                }

                if (remaining.Count > 0)
                {
                    var clusters = new List<(uint min, uint max)>();
                    uint curMin = remaining[0], curMax = remaining[0] + 4;
                    for (int oi = 1; oi < remaining.Count; oi++)
                    {
                        uint a = remaining[oi];
                        if (a - curMax <= GameConstants.BlockClusterGap)
                        {
                            curMax = a + 4;
                        }
                        else
                        {
                            clusters.Add((curMin, curMax));
                            curMin = a; curMax = a + 4;
                        }
                    }
                    clusters.Add((curMin, curMax));

                    var allSegsSoFar = Segs.Concat(pendingSegments).ToList();
                    bool OverlapsAny(uint start, uint end) => allSegsSoFar.Any(s => s.Type == PT_LOAD &&
                        start < s.VAddr + Math.Max(s.FileSize, s.MemSize) && end > s.VAddr);

                    foreach (var (cMin, cMax) in clusters)
                    {
                        uint extendFrom = cMin;
                        uint rawSize = cMax - cMin;
                        uint extendSize = AlignUp(rawSize, GameConstants.PageSize);

                        if (OverlapsAny(extendFrom, extendFrom + extendSize))
                        {
                            foreach (var a in remaining.Where(a => a >= cMin && a < cMax)) normal.Remove(a);
                            continue;
                        }

                        uint regionOffset = (uint)(_data.Length + pendingBytes.Count);
                        pendingBytes.AddRange(new byte[extendSize]);

                        var newSeg = new ProgramHeader
                        {
                            Type = PT_LOAD, Offset = regionOffset, VAddr = extendFrom, PAddr = extendFrom,
                            FileSize = extendSize, MemSize = extendSize, Flags = 7, Align = GameConstants.PageSize,
                        };
                        pendingSegments.Add(newSeg);
                        allSegsSoFar.Add(newSeg);

                        if (extendFrom + extendSize > nextFreeVAddr)
                            nextFreeVAddr = extendFrom + extendSize;
                    }
                }
            }

            if (pendingBytes.Count > 0 || pendingSegments.Count > 0)
            {
                var grown = new byte[_data.Length + pendingBytes.Count];
                Buffer.BlockCopy(_data, 0, grown, 0, _data.Length);
                pendingBytes.CopyTo(grown, _data.Length);
                _data = grown;
                Segs.AddRange(pendingSegments);
            }

            foreach (var (addr, val) in normal) WriteWord(addr, val);
        }

        if (Segs.Count == ePhnum)
        {
            for (int i = 0; i < Segs.Count; i++)
                Segs[i].WriteInto(_data, (int)ePhoff + i * ePhentsize);
        }
        else
        {
            var finalData = new byte[_data.Length + Segs.Count * ProgramHeader.Size];
            Buffer.BlockCopy(_data, 0, finalData, 0, _data.Length);

            int newPhoff = _data.Length;
            for (int i = 0; i < Segs.Count; i++)
                Segs[i].WriteInto(finalData, newPhoff + i * ProgramHeader.Size);

            BitConverter.GetBytes(newPhoff).CopyTo(finalData, 28);
            BitConverter.GetBytes((ushort)Segs.Count).CopyTo(finalData, 44);

            _data = finalData;
        }

        if (hasHook)
        {
            uint after = ReadWord(hook.Address!.Value);
            Console.WriteLine($"Hook address @ {hook.Address.Value:X} AFTER: {after:08X}");
            if (after != before)
                throw new InvalidOperationException("Hook address word was modified! Patch aborted.");
        }

        ValidateElfStructure(_data, Segs);
        CheckTlbRisk(_data, Segs);

        return _data;
    }

    private static void ValidateElfStructure(byte[] data, List<ProgramHeader> segs)
    {
        var ptLoads = segs.Where(s => s.Type == PT_LOAD).ToList();

        foreach (var seg in ptLoads)
        {
            if (seg.Offset + seg.FileSize > (uint)data.Length)
                throw new InvalidDataException("PT_LOAD segment out of bounds.");

            if (seg.MemSize < seg.FileSize)
                throw new InvalidDataException("PT_LOAD MemSize is less than FileSize.");
        }

        uint eEntry = BitConverter.ToUInt32(data, 24);
        bool entryMapped = ptLoads.Any(s => eEntry >= s.VAddr && eEntry < s.VAddr + s.FileSize);
        if (!entryMapped)
            throw new InvalidDataException($"Entry point {eEntry:X} not covered by any PT_LOAD segment.");

        uint ePhoff = BitConverter.ToUInt32(data, 28);
        if (ePhoff >= (uint)data.Length)
            throw new InvalidDataException("e_phoff beyond file size.");

        var loads = ptLoads.OrderBy(s => s.VAddr).ToList();
        for (int i = 0; i < loads.Count - 1; i++)
        {
            if (loads[i].VAddr + loads[i].MemSize > loads[i + 1].VAddr)
                throw new InvalidDataException("PT_LOAD segment overlap detected.");
        }
    }

    private static void CheckTlbRisk(byte[] data, List<ProgramHeader> segs)
    {
        var ptLoads = segs.Where(s => s.Type == PT_LOAD).ToList();
        uint eEntry = BitConverter.ToUInt32(data, 24);

        if (!ptLoads.Any(s => eEntry >= s.VAddr && eEntry < s.VAddr + s.FileSize))
        {
            Console.WriteLine("Warning: Entry point is not mapped directly in PT_LOAD.");
        }
    }

    private static uint AlignUp(uint value, uint align) => (value + align - 1) & ~(align - 1);

    private static IEnumerable<string> ExpandPnachPaths(string[] args)
    {
        foreach (var a in args)
        {
            if (Directory.Exists(a))
                foreach (var f in Directory.GetFiles(a, "*.pnach").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    yield return f;
            else
                yield return a;
        }
    }

    private static void InsertBytes(int insertOffset, byte[] newBytes, ProgramHeader growingSeg)
    {
        var grown = new byte[_data.Length + newBytes.Length];
        Buffer.BlockCopy(_data, 0, grown, 0, insertOffset);
        Buffer.BlockCopy(newBytes, 0, grown, insertOffset, newBytes.Length);
        Buffer.BlockCopy(_data, insertOffset, grown, insertOffset + newBytes.Length, _data.Length - insertOffset);
        _data = grown;

        uint delta = (uint)newBytes.Length;

        uint eShoff = BitConverter.ToUInt32(_data, 32);
        if (eShoff != 0 && eShoff >= insertOffset) BitConverter.GetBytes(eShoff + delta).CopyTo(_data, 32);

        uint ePhoffCur = BitConverter.ToUInt32(_data, 28);
        if (ePhoffCur >= insertOffset) BitConverter.GetBytes(ePhoffCur + delta).CopyTo(_data, 28);

        uint shoffNow = BitConverter.ToUInt32(_data, 32);
        if (shoffNow != 0)
        {
            ushort eShentsize = BitConverter.ToUInt16(_data, 46);
            ushort eShnum = BitConverter.ToUInt16(_data, 48);
            for (int i = 0; i < eShnum; i++)
            {
                int shOff = (int)shoffNow + i * eShentsize + 16;
                if (shOff + 4 <= _data.Length)
                {
                    uint shOffset = BitConverter.ToUInt32(_data, shOff);
                    if (shOffset >= insertOffset) BitConverter.GetBytes(shOffset + delta).CopyTo(_data, shOff);
                }
            }
        }

        foreach (var s in Segs)
        {
            if (ReferenceEquals(s, growingSeg)) continue;
            if (s.Offset >= insertOffset) s.Offset += delta;
        }
    }

    private static int FileOffsetFor(uint addr)
    {
        foreach (var s in Segs)
            if (s.Type == PT_LOAD && addr >= s.VAddr && addr < s.VAddr + s.FileSize)
                return checked((int)(s.Offset + (addr - s.VAddr)));
        return -1;
    }

    private static uint ReadWord(uint addr)
    {
        int off = FileOffsetFor(addr);
        if (off < 0) throw new InvalidDataException($"Address {addr:X} not in any segment.");
        return BitConverter.ToUInt32(_data, off);
    }

    private static void WriteWord(uint addr, uint val)
    {
        int off = FileOffsetFor(addr);
        if (off < 0) throw new InvalidDataException($"Address {addr:X} not in any segment.");
        BitConverter.GetBytes(val).CopyTo(_data, off);
    }

    private static (Dictionary<uint, uint> normal, List<Dictionary<uint, uint>> forcedBlocks, List<string> conditionals)
        ParsePnach(string path)
    {
        var normal = new Dictionary<uint, uint>();
        var forcedBlocks = new List<Dictionary<uint, uint>>();
        var conditionals = new List<string>();
        Dictionary<uint, uint>? currentForced = null;

        var lines = File.ReadAllLines(path);
        int i = 0;
        while (i < lines.Length)
        {
            string s = lines[i].Trim();
            i++;

            if (s.Contains("CUSTOM CODE START", StringComparison.OrdinalIgnoreCase))
            {
                currentForced = new Dictionary<uint, uint>();
                continue;
            }
            if (s.Contains("CUSTOM CODE END", StringComparison.OrdinalIgnoreCase))
            {
                if (currentForced is { Count: > 0 }) forcedBlocks.Add(currentForced);
                currentForced = null;
                continue;
            }

            var m = PatchRe.Match(s);
            if (!m.Success) continue;

            string addrStr = m.Groups[2].Value, typeStr = m.Groups[3].Value, valStr = m.Groups[4].Value;

            bool isConditionalHeader =
                addrStr.StartsWith("e0", StringComparison.OrdinalIgnoreCase) ||
                addrStr.StartsWith("e1", StringComparison.OrdinalIgnoreCase) ||
                addrStr.StartsWith("d0", StringComparison.OrdinalIgnoreCase) ||
                addrStr.StartsWith("d1", StringComparison.OrdinalIgnoreCase) ||
                addrStr.StartsWith("d2", StringComparison.OrdinalIgnoreCase) ||
                addrStr.StartsWith("d3", StringComparison.OrdinalIgnoreCase);

            if (isConditionalHeader)
            {
                conditionals.Add(s);

                int expectedCount = 0;
                if (addrStr.Length >= 4 &&
                    int.TryParse(addrStr.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int parsedCount))
                {
                    expectedCount = parsedCount;
                }

                int consumed = 0;
                while (consumed < expectedCount && i < lines.Length)
                {
                    string payloadLine = lines[i].Trim();
                    var pm = PatchRe.Match(payloadLine);

                    if (!pm.Success)
                    {
                        if (string.IsNullOrWhiteSpace(payloadLine) || payloadLine.StartsWith("//"))
                        {
                            i++;
                            continue;
                        }
                        break;
                    }

                    string payloadAddr = pm.Groups[2].Value;
                    bool payloadIsAnotherHeader =
                        payloadAddr.StartsWith("e0", StringComparison.OrdinalIgnoreCase) ||
                        payloadAddr.StartsWith("e1", StringComparison.OrdinalIgnoreCase) ||
                        payloadAddr.StartsWith("d0", StringComparison.OrdinalIgnoreCase) ||
                        payloadAddr.StartsWith("d1", StringComparison.OrdinalIgnoreCase) ||
                        payloadAddr.StartsWith("d2", StringComparison.OrdinalIgnoreCase) ||
                        payloadAddr.StartsWith("d3", StringComparison.OrdinalIgnoreCase);
                    if (payloadIsAnotherHeader) break;

                    conditionals.Add(payloadLine);
                    consumed++;
                    i++;
                }

                continue;
            }

            if (!typeStr.Equals("word", StringComparison.OrdinalIgnoreCase) &&
                !typeStr.Equals("extended", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!uint.TryParse(addrStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint addrRaw) ||
                !uint.TryParse(valStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint val))
            {
                continue;
            }

            uint addr = addrRaw & 0x1FFFFFFFu;

            if (currentForced is not null) currentForced[addr] = val;
            else normal[addr] = val;
        }
        return (normal, forcedBlocks, conditionals);
    }

    private static void ExportConditionalsToCht(List<string> conditionalLines, string outDir, string sourceName, HookConfig hook)
    {
        var codeLines = new List<string>();
        int groupNum = 0;
        bool inGroup = false;

        foreach (var raw in conditionalLines)
        {
            var (addrStr, valStr) = ParseRawPatchLine(raw);
            if (addrStr is null || valStr is null) continue;

            bool isHeader = addrStr.StartsWith("e0", StringComparison.OrdinalIgnoreCase) ||
                            addrStr.StartsWith("e1", StringComparison.OrdinalIgnoreCase) ||
                            addrStr.StartsWith("d0", StringComparison.OrdinalIgnoreCase) ||
                            addrStr.StartsWith("d1", StringComparison.OrdinalIgnoreCase) ||
                            addrStr.StartsWith("d2", StringComparison.OrdinalIgnoreCase) ||
                            addrStr.StartsWith("d3", StringComparison.OrdinalIgnoreCase);

            if (isHeader || !inGroup)
            {
                groupNum++;
                inGroup = true;
                codeLines.Add($"Conditional block {groupNum}");
            }

            codeLines.Add($"{addrStr.ToUpperInvariant()} {valStr.ToUpperInvariant()}");
        }

        if (codeLines.Count == 0) return;

        Directory.CreateDirectory(outDir);

        string chtPath = Path.Combine(outDir, $"{sourceName}.cht");
        var chtOutput = new List<string> { "\"Conditional cheats (exported)\"", "" };
        chtOutput.AddRange(codeLines);
        chtOutput.Add("");
        File.WriteAllLines(chtPath, chtOutput);

        string cbPath = Path.Combine(outDir, $"{sourceName}_CodeBreaker.txt");
        var cbOutput = new List<string> { "\"Conditional cheats (exported)\"" };
        if (hook.MastercodeLine is not null)
        {
            cbOutput.Add(hook.MastercodeLine);
        }
        cbOutput.Add("");
        cbOutput.AddRange(codeLines);
        cbOutput.Add("");
        File.WriteAllLines(cbPath, cbOutput);
    }

    private static (string? addr, string? val) ParseRawPatchLine(string raw)
    {
        var m = PatchRe.Match(raw);
        if (!m.Success) return (null, null);
        return (m.Groups[2].Value, m.Groups[4].Value);
    }

    private static (uint[] words, List<(uint addr, uint instr, uint origTarget)> entries, uint regionSize)
        RelocateBlock(Dictionary<uint, uint> block, Dictionary<uint, uint> normalPatches, uint newBase)
    {
        uint cmin = block.Keys.Min();
        uint cmax = block.Keys.Max() + 4;
        int nWords = (int)((cmax - cmin) / 4);

        var words = new uint[nWords];
        foreach (var (addr, val) in block) words[(addr - cmin) / 4] = val;

        long delta = (long)newBase - cmin;
        for (int i = 0; i < words.Length; i++)
        {
            uint val = words[i];
            uint op = (val >> 26) & 0x3F;
            if (op == 2 || op == 3)
            {
                uint target = (val & 0x03FFFFFFu) << 2;
                if (target >= cmin && target < cmax)
                {
                    uint newTarget = (uint)(target + delta);
                    words[i] = (op << 26) | ((newTarget >> 2) & 0x03FFFFFFu);
                }
            }
        }

        var entries = new List<(uint, uint, uint)>();
        foreach (var (addr, val) in normalPatches)
        {
            uint op = (val >> 26) & 0x3F;
            if (op != 2 && op != 3) continue;
            uint target = (val & 0x03FFFFFFu) << 2;
            if (target >= cmin && target < cmax)
            {
                uint newTarget = newBase + (target - cmin);
                entries.Add((addr, (op << 26) | ((newTarget >> 2) & 0x03FFFFFFu), target));
            }
        }

        return (words, entries, (uint)(cmax - cmin));
    }
}
