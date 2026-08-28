// PS2 ELF loading notes:
//
// e_phoff/e_phnum must not move after patching. The real PS2 loader
// ("loadelf 3.30", unlike lenient tools such as uLaunchELF) fails to
// boot an ELF whose program header table relocated, even though some
// emulators tolerate it fine. To avoid this, new patch data is appended
// by growing an existing trailing PT_LOAD segment in place instead of
// minting a new one, whenever possible.
//
// Known limitation: when relocating a kernel-RAM code block, only
// j/jal instructions targeting it are rewritten automatically. Absolute
// address loads (lui+ori / lui+addiu pairs, i.e. `li $reg, addr`) are
// only detected and reported for manual review -- the same bit pattern
// could just as easily be an ordinary integer constant, so it's never
// auto-rewritten.
//
// This tool is game-agnostic: the sceSifSendCmd-caller hook address and
// CodeBreaker mastercode are supplied interactively at runtime (see
// HookConfig / PromptHookConfig) rather than hardcoded, so it works
// against any 32-bit PS2 ELF/ISO.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DiscUtils.Iso9660;

// Lets CheatPatcher.Gui call the non-interactive entry points (RunIso/RunElf)
// and reuse HookConfig/ParseHookConfig directly, without making the console
// app's internals part of its public API.
[assembly: InternalsVisibleTo("CheatPatcher.Gui")]
[assembly: InternalsVisibleTo("CheatPatcherGui")]

namespace CheatPatcher;

internal static class GameConstants
{
    public const uint KernelRamCeiling = 0x100000;
    public const uint BlockClusterGap = 0x10;
    // PS2 EE MMU page size. New PT_LOAD regions must start (and their sizes
    // should round up to) this boundary so the kernel can install a clean
    // TLB entry for them -- misaligned regions are one of the real causes
    // behind reported TLB MISS crashes, not a cosmetic detail.
    public const uint PageSize = 0x1000;
}

// Holds the (optional) CodeBreaker hook info this run should use, gathered
// interactively at startup instead of being hardcoded per-game.
//
// - Address: the sceSifSendCmd-caller word address whose value must stay
//   byte-for-byte unchanged across the whole patch pipeline. If this is
// Optional hook info gathered at startup instead of hardcoded per-game.
// Address: the sceSifSendCmd-caller word that must stay unchanged; null
// skips that check. MastercodeLine: the raw CodeBreaker line written
// into exported *_CodeBreaker.txt files for skipped conditional codes.
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

    // ============================================================
    // Interactive prompts
    // ============================================================
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
        Console.WriteLine("   every frame. Format: AAAAAAAA VVVVVVVV. The hook address is taken from");
        Console.WriteLine("   the last 6 hex digits of AAAAAAAA automatically.");
        Console.Write("   Enter mastercode (or press Enter to skip): ");

        string? line = Console.ReadLine()?.Trim();
        return ParseHookConfig(line);
    }

    // Pure parsing logic pulled out of PromptHookConfig so the GUI can reuse
    // the exact same mastercode-parsing rules without duplicating them.
    // Still logs via Console.WriteLine -- callers that redirect Console.Out
    // (e.g. the GUI's log box) get the same feedback the console prompt gives.
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
            Console.WriteLine("   -> Unrecognized format, mastercode ignored (no hook validation).");
            return new HookConfig();
        }

        string addrPart = parts[0];
        string addrHex = addrPart.Length > 2 ? addrPart[2..] : addrPart;
        if (!uint.TryParse(addrHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint addr))
        {
            Console.WriteLine("   -> Could not parse an address from the mastercode, ignored (no hook validation).");
            return new HookConfig();
        }

        Console.WriteLine($"   -> Hook address detected: {addr:X}");
        return new HookConfig { Address = addr, MastercodeLine = line };
    }

    private static string[] PromptPnachPaths()
    {
        Console.WriteLine();
        Console.WriteLine("4) Enter .pnach file or folder paths, one per line. Press Enter on an");
        Console.WriteLine("   empty line when done.");

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

    // ============================================================
    // ISO mode
    // ============================================================
    internal static void RunIso(string inIso, string outFolder, string elfName, string[] pnachArgs, HookConfig hook)
    {
        Console.WriteLine($"Opening ISO (read-only): {inIso}");
        byte[] elfBytes;
        using (var isoStream = File.OpenRead(inIso))
        using (var reader = new CDReader(isoStream, joliet: false))
        {
            string? foundPath = FindFile(reader, reader.Root, elfName);
            if (foundPath is null)
                throw new FileNotFoundException($"Could not find '{elfName}' anywhere in the ISO. " +
                                                 "Check the exact filename (case-insensitive match attempted).");

            Console.WriteLine($"Found executable at: {foundPath}");
            using var s = reader.OpenFile(foundPath, FileMode.Open);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            elfBytes = ms.ToArray();
        }

        Console.WriteLine($"Extracted {elfBytes.Length} bytes. Running patch pipeline...");
        byte[] patchedElf = PatchElfBytes(elfBytes, pnachArgs, hook);
        Console.WriteLine($"\nPatched executable: {patchedElf.Length} bytes " +
                           $"(was {elfBytes.Length}, +{patchedElf.Length - elfBytes.Length})");

        Directory.CreateDirectory(outFolder);
        Console.WriteLine($"\nDumping full disc contents to: {outFolder}");

        using (var isoStream = File.OpenRead(inIso))
        using (var reader = new CDReader(isoStream, joliet: false))
        {
            ExtractAllFiles(reader, reader.Root, outFolder, elfName, patchedElf);
        }

        Console.WriteLine("\nDone. Next steps:");
        Console.WriteLine($"  1. Open CD-DVD GenTool, import the contents of: {outFolder}");
        Console.WriteLine("  2. Export as .IML");
        Console.WriteLine("  3. Convert the .IML to a final .ISO with iml2iso");
        Console.WriteLine("  4. Test the resulting ISO in PCSX2 before using it on real hardware.");
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

    private static void ExtractAllFiles(CDReader reader, DiscUtils.DiscDirectoryInfo dir, string outDir,
        string elfName, byte[] patchedElf)
    {
        Directory.CreateDirectory(outDir);
        foreach (var f in dir.GetFiles())
        {
            string baseName = f.Name.Split(';')[0];
            string destFile = Path.Combine(outDir, baseName);

            if (baseName.Equals(elfName, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllBytes(destFile, patchedElf);
                Console.WriteLine($"  [PATCHED] {destFile}");
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

    // ============================================================
    // Raw ELF mode
    // ============================================================
    internal static void RunElf(string inElf, string outElf, string[] pnachArgs, HookConfig hook)
    {
        byte[] inputBytes = File.ReadAllBytes(inElf);
        byte[] result = PatchElfBytes(inputBytes, pnachArgs, hook);
        File.WriteAllBytes(outElf, result);
        Console.WriteLine($"\nWrote {outElf}: {result.Length} bytes (was {inputBytes.Length}, +{result.Length - inputBytes.Length})");
    }

    // ============================================================
    // Shared patch pipeline
    // ============================================================
    private static byte[] PatchElfBytes(byte[] inputBytes, string[] pnachArgs, HookConfig hook)
    {
        _data = (byte[])inputBytes.Clone();
        int origLen = _data.Length;

        if (_data.Length < 52 || _data[0] != 0x7F || _data[1] != (byte)'E' || _data[2] != (byte)'L' || _data[3] != (byte)'F')
            throw new InvalidDataException("Not a valid ELF file.");
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
        else
        {
            Console.WriteLine("No hook/mastercode address provided -- skipping hook-integrity validation.");
        }

        // Handle the case where there are no PT_LOAD segments at all
        var loadSegs = Segs.Where(s => s.Type == PT_LOAD).ToList();
        if (loadSegs.Count == 0)
            throw new InvalidDataException("ELF has no PT_LOAD segments.");
        // Use MemSize, not FileSize: a segment's BSS tail (MemSize >
        // FileSize) is still part of its live VAddr range even though
        // nothing is backed on disk there. Starting a new region at
        // FileSize would overlap that range.
        uint nextFreeVAddr = loadSegs.Max(s => s.VAddr + Math.Max(s.FileSize, s.MemSize));
        Console.WriteLine($"Free RAM starts at: {nextFreeVAddr:X}");

        foreach (var pnachPath in ExpandPnachPaths(pnachArgs))
        {
            Console.WriteLine($"\n=== Processing: {Path.GetFileName(pnachPath)} ===");
            var (normal, forcedBlocks, conditionals) = ParsePnach(pnachPath);

            if (conditionals.Count > 0)
            {
                Console.WriteLine($"  {conditionals.Count} conditional (E-type/D-type) line(s) found -- these need live " +
                                   "per-frame evaluation and CANNOT be baked statically. Skipped:");
                foreach (var c in conditionals)
                {
                    string tag = Regex.IsMatch(c, @"patch=\d+,EE,[Dd]", RegexOptions.None) ? "D-type" : "E-type";
                    Console.WriteLine($"    [{tag}] {c}");
                }

                // Export the skipped conditional lines to an external
                // PS2rd/OPL .cht file, so they aren't just discarded -- the
                // player can still use them via OPL's PS2RD cheat engine
                // instead of losing the effect entirely. This does NOT change
                // what gets baked into the ELF; conditionals are still never
                // written to _data, exactly as before.
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
                        cur = new Dictionary<uint, uint>();
                        cur[a] = normal[a];
                    }
                    lastAddr = a;
                }
                autoBlocks.Add(cur);
            }
            foreach (var addr in lowAddrs) normal.Remove(addr);

            var allBlocks = forcedBlocks.Concat(autoBlocks).ToList();
            Console.WriteLine($"  {normal.Count} normal (in-segment / high-RAM) patches, {allBlocks.Count} " +
                               $"kernel-RAM custom-code block(s) ({forcedBlocks.Count} marked, {autoBlocks.Count} auto-detected)");

            var pendingSegments = new List<ProgramHeader>();
            var pendingBytes = new List<byte>();

            // Grow the existing trailing PT_LOAD in place for each block
            // instead of minting a new one, so e_phnum/e_phoff never move.
            var blockGrowSeg = Segs.Where(s => s.Type == PT_LOAD &&
                    s.VAddr + Math.Max(s.FileSize, s.MemSize) == nextFreeVAddr)
                .OrderByDescending(s => s.VAddr)
                .FirstOrDefault();

            foreach (var block in allBlocks)
            {
                // Page-align the region start (EE MMU works in fixed page granularity).
                nextFreeVAddr = AlignUp(nextFreeVAddr, GameConstants.PageSize);

                uint regionVAddr = nextFreeVAddr;
                var (words, entries, regionSize) = RelocateBlock(block, normal, regionVAddr);

                // Pad to a full page so the next region also starts page-aligned.
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
                    blockGrowSeg.Flags = 7; // RWE -- this region now holds injected machine code, not just data

                    Console.WriteLine($"    block -> grown into existing PT_LOAD (VAddr {blockGrowSeg.VAddr:X}) at " +
                                       $"{regionVAddr:X} (size {regionSize}, page-aligned to {paddedSize}), " +
                                       $"{entries.Count} entry point(s) -- adds ZERO new program header entries.");
                }
                else
                {
                    // Fallback: no existing trailing PT_LOAD to grow -- mint
                    // a new PT_LOAD like the old code did, but warn loudly
                    // since this WILL force e_phoff to relocate at write-back
                    // time.
                    uint regionOffset = (uint)(_data.Length + pendingBytes.Count);
                    pendingBytes.AddRange(regionBytes);
                    pendingSegments.Add(new ProgramHeader
                    {
                        Type = PT_LOAD, Offset = regionOffset, VAddr = regionVAddr, PAddr = regionVAddr,
                        FileSize = regionSize, MemSize = regionSize, Flags = 7, Align = GameConstants.PageSize,
                    });
                    Console.WriteLine($"    block -> relocated to {regionVAddr:X} (size {regionSize}, page-aligned " +
                                       $"to {paddedSize}), {entries.Count} entry point(s) -- WARNING: no existing " +
                                       "segment available to grow, this mints a new PT_LOAD and WILL move e_phoff.");
                }

                foreach (var (addr, instr, origTarget) in entries)
                {
                    Console.WriteLine($"      entry @ {addr:X} (was -> {origTarget:X}) now -> " +
                                       $"{regionVAddr + (origTarget - block.Keys.Min()):X}");
                    normal[addr] = instr;
                }

                nextFreeVAddr = regionVAddr + paddedSize;
            }

            var allCurrentSegs = Segs.Concat(pendingSegments).ToList();
            // Include BSS tail (MemSize > FileSize) when checking coverage.
            bool InAnySegment(uint a) => allCurrentSegs.Any(s => s.Type == PT_LOAD &&
                a >= s.VAddr && a < s.VAddr + Math.Max(s.FileSize, s.MemSize));

            var outOfRange = normal.Keys.Where(a => !InAnySegment(a)).OrderBy(a => a).ToList();
            if (outOfRange.Count > 0)
            {
                // Split out-of-range addresses into "growable" (past nextFreeVAddr,
                // can extend the trailing PT_LOAD) and "remaining" (a genuine gap
                // between existing segments, needs the per-cluster fallback below).
                var growable = outOfRange.Where(a => a >= nextFreeVAddr).OrderBy(a => a).ToList();
                var remaining = outOfRange.Where(a => a < nextFreeVAddr).OrderBy(a => a).ToList();

                if (growable.Count > 0 && pendingSegments.Count == 0)
                {
                    // Pick the segment with the highest VAddr to avoid
                    // its growth overlapping a trailing placeholder segment.
                    var growSeg = Segs.Where(s => s.Type == PT_LOAD &&
                            s.VAddr + Math.Max(s.FileSize, s.MemSize) == nextFreeVAddr)
                        .OrderByDescending(s => s.VAddr)
                        .FirstOrDefault();

                    if (growSeg != null)
                    {
                        uint maxNeeded = growable.Max() + 4;
                        uint newEndVAddr = AlignUp(maxNeeded, GameConstants.PageSize);
                        uint growBy = newEndVAddr - nextFreeVAddr;

                        Console.WriteLine($"  Growing existing PT_LOAD (VAddr {growSeg.VAddr:X}) in place by " +
                                           $"0x{growBy:X} bytes to cover {growable.Count} out-of-range patch " +
                                           $"target(s) between {growable.Min():X} and {growable.Max():X} -- adds " +
                                           "ZERO new program header entries, so e_phoff/e_phnum stay completely " +
                                           "untouched.");

                        int insertOffset = (int)(growSeg.Offset + growSeg.FileSize);
                        InsertBytes(insertOffset, new byte[growBy], growSeg);

                        growSeg.FileSize += growBy;
                        growSeg.MemSize = growSeg.FileSize;
                        growSeg.Flags = 7; // RWE -- this region now holds injected machine code, not just data
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

                // Per-cluster fallback: mint a new PT_LOAD for addresses in
                // genuine gaps that can't be satisfied by growing an existing segment.
                if (remaining.Count > 0)
                {
                var sorted = remaining;
                var clusters = new List<(uint min, uint max)>();
                uint curMin = sorted[0], curMax = sorted[0] + 4;
                for (int oi = 1; oi < sorted.Count; oi++)
                {
                    uint a = sorted[oi];
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
                        Console.WriteLine($"  WARNING: patch(es) targeting {cMin:X}-{cMax:X} fall in a gap that " +
                                           "can't be safely extended without overlapping an existing segment -- " +
                                           "these addresses were NOT written. Investigate manually (this pnach " +
                                           "may target an address inside the original ELF that this tool doesn't " +
                                           "recognize as covered).");
                        foreach (var a in sorted.Where(a => a >= cMin && a < cMax)) normal.Remove(a);
                        continue;
                    }

                    Console.WriteLine($"  extending image by 0x{extendSize:X} bytes at {extendFrom:X} to cover " +
                                       $"{cMax - cMin} byte(s) of out-of-range patch target(s) (no relocation, " +
                                       "addresses unchanged)");

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

        // Only relocate the program header table when segment count grew.
        // Writing it back at its original offset keeps e_phoff/e_phnum
        // unchanged, which the real PS2 loader requires.
        if (Segs.Count == ePhnum)
        {
            for (int i = 0; i < Segs.Count; i++)
                Segs[i].WriteInto(_data, (int)ePhoff + i * ePhentsize);
        }
        else
        {
            Console.WriteLine($"  NOTE: segment count grew ({ePhnum} -> {Segs.Count}); program header table " +
                               "must be relocated to fit the new entries. This changes e_phoff away from the " +
                               "original ELF's layout -- verify this build still boots via normal disc/ELF " +
                               "load (not just a lenient loader) before relying on it.");

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
            Console.WriteLine($"\nHook address @ {hook.Address.Value:X} AFTER: {after:08X}");
            if (after != before)
                throw new InvalidOperationException(
                    "Hook address word was modified! Refusing to produce output -- " +
                    "something in these pnach files touches the mastercode hook address.");
        }

        // Validate ELF structure before returning
        ValidateElfStructure(_data, Segs);

        // Check for known TLB-risk patterns before returning
        CheckTlbRisk(_data, Segs);

        Console.WriteLine($"Patch pipeline complete: {_data.Length} bytes (was {origLen}, +{_data.Length - origLen})");
        return _data;
    }

    private static void ValidateElfStructure(byte[] data, List<ProgramHeader> segs)
    {
        Console.WriteLine("\n[VALIDATION] Checking ELF structure integrity...");

        var ptLoads = segs.Where(s => s.Type == PT_LOAD).ToList();

        // Every PT_LOAD must stay within the file's bounds
        foreach (var seg in ptLoads)
        {
            if (seg.Offset + seg.FileSize > (uint)data.Length)
                throw new InvalidDataException(
                    $"PT_LOAD segment out of bounds: offset={seg.Offset:X}, filesize={seg.FileSize:X}, data.len={data.Length:X}");

            if (seg.MemSize < seg.FileSize)
                throw new InvalidDataException(
                    $"PT_LOAD MemSize < FileSize: mem={seg.MemSize:X}, file={seg.FileSize:X}");
        }

        // The entry point must be covered by some PT_LOAD
        uint eEntry = BitConverter.ToUInt32(data, 24);
        bool entryMapped = ptLoads.Any(s => eEntry >= s.VAddr && eEntry < s.VAddr + s.FileSize);
        if (!entryMapped)
            throw new InvalidDataException(
                $"Entry point {eEntry:X} not covered by any PT_LOAD segment");

        // Program header table must sit within the file
        uint ePhoff = BitConverter.ToUInt32(data, 28);
        if (ePhoff >= (uint)data.Length)
            throw new InvalidDataException($"e_phoff {ePhoff:X} beyond file size");

        // No two PT_LOAD segments may overlap
        var loads = ptLoads.OrderBy(s => s.VAddr).ToList();
        for (int i = 0; i < loads.Count - 1; i++)
        {
            uint thisEnd = loads[i].VAddr + loads[i].MemSize;
            uint nextStart = loads[i + 1].VAddr;
            if (thisEnd > nextStart)
                throw new InvalidDataException(
                    $"PT_LOAD segment overlap: seg[{i}] end={thisEnd:X}, seg[{i + 1}] start={nextStart:X}");
        }

        Console.WriteLine("  All ELF header validations passed");
    }

    private static void CheckTlbRisk(byte[] data, List<ProgramHeader> segs)
    {
        var ptLoads = segs.Where(s => s.Type == PT_LOAD).ToList();
        uint eEntry = BitConverter.ToUInt32(data, 24);

        // Entry point should be mapped
        bool entryMapped = ptLoads.Any(s => eEntry >= s.VAddr && eEntry < s.VAddr + s.FileSize);

        if (!entryMapped)
        {
            Console.WriteLine("\nTLB RISK WARNING:");
            Console.WriteLine($"   Entry point {eEntry:X} is not covered by any PT_LOAD segment.");
            Console.WriteLine("   This patched ELF cannot be loaded directly as the disc's boot executable.");
            Console.WriteLine("\n   SOLUTION:");
            Console.WriteLine("   1. Use uLaunchELF as bootloader (replace the boot executable with uLaunchELF)");
            Console.WriteLine("   2. uLaunchELF loads this patched ELF from external location");
            Console.WriteLine("   3. uLaunchELF handles proper memory mapping -> jump to entry point");
            Console.WriteLine("\n   DO NOT attempt to load this directly. Will cause TLB MISS crash.\n");
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

    // Insert bytes into _data at insertOffset, adjusting all offset fields
    // (e_shoff, e_phoff, other segments' p_offset) past that point.
    // growingSeg's p_offset is intentionally excluded -- the caller
    // updates its FileSize/MemSize directly.
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

        // Fix sh_offset fields too so the ELF stays valid for
        // readelf/objdump/IDA. Re-read e_shoff post-adjustment.
        uint shoffNow = BitConverter.ToUInt32(_data, 32);
        if (shoffNow != 0)
        {
            ushort eShentsize = BitConverter.ToUInt16(_data, 46);
            ushort eShnum = BitConverter.ToUInt16(_data, 48);
            for (int i = 0; i < eShnum; i++)
            {
                int shOff = (int)shoffNow + i * eShentsize + 16; // sh_offset is the 5th uint32 field
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
        if (off < 0) throw new InvalidDataException($"Address {addr:X} not in any segment -- cannot apply patch.");
        BitConverter.GetBytes(val).CopyTo(_data, off);
    }

    private static (Dictionary<uint, uint> normal, List<Dictionary<uint, uint>> forcedBlocks, List<string> conditionals)
        ParsePnach(string path)
    {
        var normal = new Dictionary<uint, uint>();
        var forcedBlocks = new List<Dictionary<uint, uint>>();
        var conditionals = new List<string>();
        Dictionary<uint, uint>? currentForced = null;

        // Read all lines up front so conditional headers can look ahead
        // at their payload lines (E/D-type header format: TNNVVVVV,
        // NN = number of following payload lines).
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

                // NN payload count lives at addrStr[2..4].
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
                        // Blank/comment lines don't count against the payload budget.
                        if (string.IsNullOrWhiteSpace(payloadLine) || payloadLine.StartsWith("//"))
                        {
                            i++;
                            continue;
                        }
                        // Unexpected non-patch line -- stop consuming.
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
                    if (payloadIsAnotherHeader)
                    {
                        // Next conditional header appeared before count was
                        // satisfied -- let the outer loop handle it.
                        Console.WriteLine($"  WARNING: conditional header '{s}' declared {expectedCount} " +
                                           $"payload line(s) but only {consumed} were found before the next " +
                                           "header -- pnach count mismatch, stopping this group here.");
                        break;
                    }

                    conditionals.Add(payloadLine);
                    consumed++;
                    i++;
                }

                if (consumed < expectedCount)
                {
                    Console.WriteLine($"  WARNING: conditional header '{s}' declared {expectedCount} " +
                                       $"payload line(s) but only {consumed} were found before end of file -- " +
                                       "pnach may be truncated or malformed.");
                }

                continue;
            }

            if (!typeStr.Equals("word", StringComparison.OrdinalIgnoreCase) &&
                !typeStr.Equals("extended", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  WARNING: patch type '{typeStr}' is not word-sized; skipping line: {s}");
                continue;
            }

            // Use TryParse instead of Parse so a malformed line is skipped with a warning, not a crash
            if (!uint.TryParse(addrStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint addrRaw))
            {
                Console.WriteLine($"  WARNING: invalid hex address '{addrStr}', skipping line: {s}");
                continue;
            }

            if (!uint.TryParse(valStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint val))
            {
                Console.WriteLine($"  WARNING: invalid hex value '{valStr}', skipping line: {s}");
                continue;
            }

            uint addr = addrRaw & 0x1FFFFFFFu;

            if (currentForced is not null) currentForced[addr] = val;
            else normal[addr] = val;
        }
        return (normal, forcedBlocks, conditionals);
    }

    // Exports skipped E-type/D-type lines as PS2rd/OPL .cht and raw
    // CodeBreaker .txt. Groups by contiguous run rather than trusting
    // the NN count field, which can be stale in hand-edited pnach files.
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

            if (isHeader)
            {
                groupNum++;
                inGroup = true;
                codeLines.Add($"Conditional block {groupNum}");
            }
            else if (!inGroup)
            {
                // Orphan payload line -- open a new group for it.
                groupNum++;
                codeLines.Add($"Conditional block {groupNum}");
            }

            codeLines.Add($"{addrStr.ToUpperInvariant()} {valStr.ToUpperInvariant()}");
        }

        if (codeLines.Count == 0) return;

        Directory.CreateDirectory(outDir);

        // Output 1: PS2rd/OPL .cht (no mastercode needed -- PS2rd has its
        // own hook and doesn't use the CodeBreaker device path).
        string chtPath = Path.Combine(outDir, $"{sourceName}.cht");
        var chtOutput = new List<string>
        {
            "\"Conditional cheats (exported)\"",
            "// Auto-exported from skipped E-type/D-type pnach lines.",
            "// These require PS2RD's live per-frame evaluation and cannot",
            "// be baked into the ELF -- see the patcher's README.",
            ""
        };
        chtOutput.AddRange(codeLines);
        chtOutput.Add("");
        File.WriteAllLines(chtPath, chtOutput);

        // Output 2: raw CodeBreaker .txt. The mastercode must be first --
        // it hooks the frame loop so codes actually execute.
        string cbPath = Path.Combine(outDir, $"{sourceName}_CodeBreaker.txt");
        var cbOutput = new List<string> { "\"Conditional cheats (exported)\"" };
        if (hook.MastercodeLine is not null)
        {
            cbOutput.Add("Mastercode (required -- do not remove or reorder)");
            cbOutput.Add(hook.MastercodeLine);
        }
        else
        {
            cbOutput.Add("// No mastercode was provided for this run.");
            cbOutput.Add("// Without a working sceSifSendCmd-caller mastercode for this game, these");
            cbOutput.Add("// codes may show as enabled in CodeBreaker but never actually execute.");
        }
        cbOutput.Add("");
        cbOutput.AddRange(codeLines);
        cbOutput.Add("");
        File.WriteAllLines(cbPath, cbOutput);

        Console.WriteLine($"  Exported {groupNum} conditional group(s) ({codeLines.Count - groupNum} code line(s)):");
        Console.WriteLine($"    PS2rd/OPL format: {chtPath}");
        Console.WriteLine("    Raw CodeBreaker format" +
                           (hook.MastercodeLine is not null
                               ? $" (includes provided mastercode {hook.MastercodeLine})"
                               : " (NO mastercode included -- provide one next run for this to actually work in CodeBreaker)") +
                           $": {cbPath}");
    }

    // Re-extracts address/value hex strings from a pnach line for export.
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
        uint cmaxIncl = block.Keys.Max();
        uint cmax = cmaxIncl + 4;
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
                uint field = val & 0x03FFFFFFu;
                uint target = field << 2;
                if (target >= cmin && target < cmax)
                {
                    uint newTarget = (uint)(target + delta);
                    uint newField = (newTarget >> 2) & 0x03FFFFFFu;
                    words[i] = (op << 26) | newField;
                    Console.WriteLine($"      fixed internal {(op == 3 ? "jal" : "j")} at block+0x{i * 4:X}: {target:X} -> {newTarget:X}");
                }
            }
        }

        // Detect (but don't fix) lui/ori address loads into this block --
        // the bit pattern is ambiguous, so only reported for manual review.
        DetectLikelyAddressLoads(words, cmin, cmax, "inside relocated block");

        var entries = new List<(uint, uint, uint)>();
        foreach (var (addr, val) in normalPatches)
        {
            uint op = (val >> 26) & 0x3F;
            if (op != 2 && op != 3) continue;
            uint field = val & 0x03FFFFFFu;
            uint target = field << 2;
            if (target >= cmin && target < cmax)
            {
                uint newTarget = newBase + (target - cmin);
                uint newField = (newTarget >> 2) & 0x03FFFFFFu;
                entries.Add((addr, (op << 26) | newField, target));
            }
        }

        if (entries.Count == 0)
            Console.WriteLine("      WARNING: no entry jump found for this block -- relocated but nothing calls it. Verify manually.");

        // Same scan for patches outside the block targeting the relocated range.
        var outsideWords = normalPatches
            .Where(kv => (kv.Value >> 26 & 0x3F) == 0x0F) // lui only, ori has no fixed high bits to pre-filter on
            .OrderBy(kv => kv.Key)
            .ToList();
        if (outsideWords.Count > 0)
        {
            var orderedOutside = normalPatches.OrderBy(kv => kv.Key).ToList();
            DetectLikelyAddressLoadsInSparsePatchSet(orderedOutside, cmin, cmax, "outside block, targets relocated range");
        }

        return (words, entries, (uint)(cmax - cmin));
    }

    // Scans a contiguous word array for lui+ori/addiu pairs whose resolved
    // 32-bit constant falls inside [cmin, cmax). Report-only; never auto-fixed.
    private static void DetectLikelyAddressLoads(uint[] words, uint cmin, uint cmax, string context)
    {
        for (int i = 0; i + 1 < words.Length; i++)
        {
            uint hi = words[i];
            uint hiOp = (hi >> 26) & 0x3F;
            if (hiOp != 0x0F) continue; // lui
            uint hiReg = (hi >> 16) & 0x1F; // rt field holds the destination for lui

            uint lo = words[i + 1];
            uint loOp = (lo >> 26) & 0x3F;
            bool isOri = loOp == 0x0D;   // ori
            bool isAddiu = loOp == 0x09; // addiu
            if (!isOri && !isAddiu) continue;

            uint loRs = (lo >> 21) & 0x1F; // source reg for ori/addiu
            uint loRt = (lo >> 16) & 0x1F; // dest reg for ori/addiu
            if (loRs != hiReg || loRt != hiReg) continue; // must chain into the same register

            uint hiImm = hi & 0xFFFF;
            uint loImm = lo & 0xFFFF;
            uint candidate = (hiImm << 16) + loImm; // '+' matches addiu's sign-extend behavior; ori would use OR, close enough for candidate detection

            if (candidate >= cmin && candidate < cmax)
            {
                Console.WriteLine($"      CANDIDATE li ({context}) at word+0x{i * 4:X}: " +
                                   $"lui/{(isAddiu ? "addiu" : "ori")} $r{hiReg} -> {candidate:X} " +
                                   "-- NOT auto-fixed (could be a real address OR a coincidental integer). Review manually.");
            }
        }
    }

    // Same detection over a sparse patch set. Only checks address-adjacent
    // pairs (4 bytes apart) since lui+ori must be consecutive instructions.
    private static void DetectLikelyAddressLoadsInSparsePatchSet(
        List<KeyValuePair<uint, uint>> ordered, uint cmin, uint cmax, string context)
    {
        for (int i = 0; i + 1 < ordered.Count; i++)
        {
            var (addrHi, hi) = (ordered[i].Key, ordered[i].Value);
            var (addrLo, lo) = (ordered[i + 1].Key, ordered[i + 1].Value);
            if (addrLo != addrHi + 4) continue; // must be the very next instruction

            uint hiOp = (hi >> 26) & 0x3F;
            if (hiOp != 0x0F) continue;
            uint hiReg = (hi >> 16) & 0x1F;

            uint loOp = (lo >> 26) & 0x3F;
            bool isOri = loOp == 0x0D;
            bool isAddiu = loOp == 0x09;
            if (!isOri && !isAddiu) continue;

            uint loRs = (lo >> 21) & 0x1F;
            uint loRt = (lo >> 16) & 0x1F;
            if (loRs != hiReg || loRt != hiReg) continue;

            uint hiImm = hi & 0xFFFF;
            uint loImm = lo & 0xFFFF;
            uint candidate = (hiImm << 16) + loImm;

            if (candidate >= cmin && candidate < cmax)
            {
                Console.WriteLine($"      CANDIDATE li ({context}) at {addrHi:X}: " +
                                   $"lui/{(isAddiu ? "addiu" : "ori")} $r{hiReg} -> {candidate:X} " +
                                   "-- NOT auto-fixed (could be a real address OR a coincidental integer). Review manually.");
            }
        }
    }
}
