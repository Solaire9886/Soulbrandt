using System.IO.Compression;

namespace RsxShaderMatch;

/// <summary>
/// Reader for RPCS3 RSX frame captures (<c>captures/*.rrc.gz</c>) - one full frame of the
/// RSX FIFO command stream plus every memory block it touches. Format from RPCS3's
/// <c>serialize&lt;rsx::frame_capture_data&gt;</c> (RSXThread.cpp) and <c>rsx_replay.h</c>:
///
///   gzip -> a <c>utils::serial</c> stream:
///     u32 magic ("RRC\0" = 0x00435252), u32 version (6), u32 LE_format (1)
///     tile_map            : map&lt;tile_state (432 B bitwise), u64&gt;
///     memory_map          : map&lt;memory_block {u32 offset; u32 location; u64 data_state} (16 B), u64&gt;
///     memory_data_map     : map&lt;memory_block_data {vec&lt;u8&gt;}, u64&gt;   -- the actual bytes
///     display_buffers_map : map&lt;display_buffers_state (132 B bitwise), u64&gt;
///     replay_commands     : vec&lt;replay_command&gt;
///     reg_state           : rsx_state                                -- not parsed here
///
///   replay_command = { pair&lt;u32,u32&gt; rsx_command; set&lt;u64&gt; memory_state; u64 tile_state; u64 display_buffer_state }
///
/// Containers are VLE-length-prefixed (LEB128); PODs and <c>ENABLE_BITWISE_SERIALIZATION</c>
/// structs are raw little-endian. The map values are indices; a <c>memory_block</c>'s
/// <c>data_state</c> indexes <c>memory_data_map</c>.
///
/// FIFO encoding (RSXFIFO.cpp / rsx_replay.cpp): a "leading" entry has <c>rsx_command.first != 0</c>
/// and packs <c>reg</c> in bits [2..15] and <c>count = (first &gt;&gt; 18) &amp; 0x7ff</c> (total args
/// including this entry's own <c>.second</c>); it is followed by <c>count-1</c> "continuation"
/// entries with <c>first == 0</c> carrying one raw arg word each in <c>.second</c>. Arg words are
/// host-native (RPCS3 swaps on capture).
/// </summary>
static class RrcCapture
{
    // RSX method register indices (offset >> 2), from gcm_enums.h.
    public const int NV4097_SET_SHADER_PROGRAM = 0x08e4 >> 2;
    public const int NV4097_SET_TRANSFORM_PROGRAM = 0x0b80 >> 2;
    public const int NV4097_SET_TRANSFORM_PROGRAM_LOAD = 0x1e9c >> 2;
    public const int NV4097_SET_TRANSFORM_PROGRAM_START = 0x1ea0 >> 2;
    public const int NV4097_SET_TRANSFORM_CONSTANT_LOAD = 0x1efc >> 2;
    public const int NV4097_SET_TRANSFORM_CONSTANT = 0x1f00 >> 2;   // 32-dword window
    public const int NV4097_SET_FOG_MODE = 0x08cc >> 2;            // gcm CELL_GCM_FOG_MODE_* (default reg value 0x800 = EXP)
    public const int NV4097_SET_FOG_PARAMS = 0x08d0 >> 2;          // 2 floats: bias (param0), scale (param1); +1/+2 follow
    // Render-target / pipeline state - enough to tell a depth-only shadow-cast pass from the colour pass.
    public const int NV4097_SET_SURFACE_CLIP_HORIZONTAL = 0x0200 >> 2;  // x | (width  << 16)
    public const int NV4097_SET_SURFACE_CLIP_VERTICAL = 0x0204 >> 2;    // y | (height << 16)
    public const int NV4097_SET_SURFACE_FORMAT = 0x0208 >> 2;           // [0:5] colour fmt, [5:8] depth fmt (1=Z16,2=Z24S8), [16:24] log2w, [24:32] log2h
    public const int NV4097_SET_SURFACE_COLOR_TARGET = 0x0220 >> 2;     // 0 none, 1 A, 2 B, 3 AB, ...
    public const int NV4097_SET_COLOR_MASK = 0x0324 >> 2;               // per-channel write mask; 0 = depth-only
    public const int NV4097_SET_DEPTH_MASK = 0x0a70 >> 2;               // 0/1 depth write
    public const int NV4097_SET_VIEWPORT_HORIZONTAL = 0x0a00 >> 2;      // x | (width  << 16)
    public const int NV4097_SET_VIEWPORT_VERTICAL = 0x0a04 >> 2;        // y | (height << 16)
    public const int NV4097_SET_BEGIN_END = 0x1808 >> 2;
    public const int NV4097_DRAW_ARRAYS = 0x1814 >> 2;
    public const int NV4097_DRAW_INDEX_ARRAY = 0x1824 >> 2;

    public readonly record struct Cmd(uint First, uint Second, ulong[] MemState);
    public readonly record struct MemBlock(uint Offset, uint Location, ulong DataIndex);

    public sealed class Frame
    {
        public required List<Cmd> Commands { get; init; }
        public required Dictionary<ulong, MemBlock> Blocks { get; init; }     // memory_map index -> block
        public required Dictionary<ulong, byte[]> Data { get; init; }         // memory_data_map index -> bytes
        public required long DecompressedBytes { get; init; }
        public required long BytesConsumed { get; init; }
    }

    sealed class Cur
    {
        public byte[] B = Array.Empty<byte>();
        public long P;

        public ulong Vle()
        {
            ulong v = 0;
            for (int i = 0; ; i += 7)
            {
                byte b = B[P++];
                v |= (ulong)(b & 0x7F) << i;
                if ((b & 0x80) == 0) return v;
            }
        }

        public uint U32() { uint v = BitConverter.ToUInt32(B, (int)P); P += 4; return v; }
        public ulong U64() { ulong v = BitConverter.ToUInt64(B, (int)P); P += 8; return v; }
        public byte[] Bytes(long n) { var s = new byte[n]; Array.Copy(B, P, s, 0, n); P += n; return s; }
        public void Skip(long n) => P += n;
    }

    public static Frame Parse(string path, bool keepData)
    {
        byte[] raw = Inflate(path);
        var c = new Cur { B = raw };

        uint magic = c.U32(); c.U32(); c.U32();                 // magic, version, LE_format
        if (magic != 0x00435252u) throw new InvalidDataException($"not an RRC stream (magic 0x{magic:X8})");

        ulong nTile = c.Vle();
        c.Skip((long)nTile * (432 + 8));

        ulong nMem = c.Vle();
        var blocks = new Dictionary<ulong, MemBlock>((int)nMem);
        for (ulong i = 0; i < nMem; i++)
        {
            uint off = c.U32(), loc = c.U32();
            ulong dataState = c.U64();
            ulong idx = c.U64();
            blocks[idx] = new MemBlock(off, loc, dataState);
        }

        ulong nData = c.Vle();
        var data = new Dictionary<ulong, byte[]>((int)nData);
        for (ulong i = 0; i < nData; i++)
        {
            ulong len = c.Vle();
            if (keepData)
            {
                byte[] bytes = c.Bytes((long)len);
                data[c.U64()] = bytes;
            }
            else
            {
                c.Skip((long)len);
                c.U64();
            }
        }

        ulong nDisp = c.Vle();
        c.Skip((long)nDisp * (132 + 8));

        ulong nCmd = c.Vle();
        var cmds = new List<Cmd>((int)nCmd);
        for (ulong i = 0; i < nCmd; i++)
        {
            uint first = c.U32(), second = c.U32();
            ulong m = c.Vle();
            ulong[] ms = m == 0 ? System.Array.Empty<ulong>() : new ulong[m];
            for (ulong k = 0; k < m; k++) ms[k] = c.U64();
            c.U64();                                            // tile_state index
            c.U64();                                            // display_buffer_state index
            cmds.Add(new Cmd(first, second, ms));
        }

        return new Frame
        {
            Commands = cmds,
            Blocks = blocks,
            Data = data,
            DecompressedBytes = raw.Length,
            BytesConsumed = c.P,
        };
    }

    public static int Summarize(string path)
    {
        Frame f;
        try { f = Parse(path, keepData: false); }
        catch (Exception e) { Console.Error.WriteLine($"parse failed: {e.Message}"); return 1; }

        var hist = new Dictionary<int, int>();
        long cont = 0;
        int draws = 0, shaderProg = 0, xformProg = 0, xformConst = 0;
        foreach (var cmd in f.Commands)
        {
            if (cmd.First == 0) { cont++; continue; }
            int reg = (int)((cmd.First >> 2) & 0x3FFF);
            hist.TryGetValue(reg, out int n);
            hist[reg] = n + 1;
            if (reg is NV4097_DRAW_ARRAYS or NV4097_DRAW_INDEX_ARRAY) draws++;
            else if (reg == NV4097_SET_SHADER_PROGRAM) shaderProg++;
            else if (reg == NV4097_SET_TRANSFORM_PROGRAM) xformProg++;
            else if (reg == NV4097_SET_TRANSFORM_CONSTANT) xformConst++;
        }

        Console.WriteLine($"file            {System.IO.Path.GetFileName(path)}");
        Console.WriteLine($"decompressed    {f.DecompressedBytes:N0} bytes ({f.DecompressedBytes / (1024.0 * 1024):F1} MiB)");
        Console.WriteLine($"memory blocks   {f.Blocks.Count}");
        Console.WriteLine($"replay_commands {f.Commands.Count}  ({cont:N0} continuation args)");
        Console.WriteLine($"bytes consumed  {f.BytesConsumed:N0} / {f.DecompressedBytes:N0}  " +
                          $"({(f.BytesConsumed <= f.DecompressedBytes ? "ok, reg_state tail unparsed" : "OVERRUN")})");
        Console.WriteLine();
        Console.WriteLine($"draw calls          {draws}");
        Console.WriteLine($"SET_SHADER_PROGRAM  {shaderProg}");
        Console.WriteLine($"SET_TRANSFORM_PROGRAM leaders  {xformProg}");
        Console.WriteLine($"SET_TRANSFORM_CONSTANT leaders {xformConst}");
        Console.WriteLine();
        Console.WriteLine("top method registers by frequency (reg = offset>>2):");
        foreach (var kv in hist.OrderByDescending(k => k.Value).Take(20))
            Console.WriteLine($"  0x{kv.Key:X4}  (offset 0x{kv.Key << 2:X4})  {kv.Value,7}   {MethodName(kv.Key)}");
        return f.BytesConsumed <= f.DecompressedBytes ? 0 : 1;
    }

    public static string MethodName(int reg) => reg switch
    {
        NV4097_SET_SHADER_PROGRAM => "SET_SHADER_PROGRAM",
        NV4097_SET_TRANSFORM_PROGRAM => "SET_TRANSFORM_PROGRAM",
        NV4097_SET_TRANSFORM_PROGRAM_LOAD => "SET_TRANSFORM_PROGRAM_LOAD",
        NV4097_SET_TRANSFORM_PROGRAM_START => "SET_TRANSFORM_PROGRAM_START",
        NV4097_SET_TRANSFORM_CONSTANT_LOAD => "SET_TRANSFORM_CONSTANT_LOAD",
        NV4097_SET_TRANSFORM_CONSTANT => "SET_TRANSFORM_CONSTANT",
        NV4097_SET_FOG_MODE => "SET_FOG_MODE",
        NV4097_SET_FOG_PARAMS => "SET_FOG_PARAMS",
        NV4097_SET_SURFACE_CLIP_HORIZONTAL => "SET_SURFACE_CLIP_HORIZONTAL",
        NV4097_SET_SURFACE_CLIP_VERTICAL => "SET_SURFACE_CLIP_VERTICAL",
        NV4097_SET_SURFACE_FORMAT => "SET_SURFACE_FORMAT",
        NV4097_SET_SURFACE_COLOR_TARGET => "SET_SURFACE_COLOR_TARGET",
        NV4097_SET_COLOR_MASK => "SET_COLOR_MASK",
        NV4097_SET_DEPTH_MASK => "SET_DEPTH_MASK",
        NV4097_SET_VIEWPORT_HORIZONTAL => "SET_VIEWPORT_HORIZONTAL",
        NV4097_SET_VIEWPORT_VERTICAL => "SET_VIEWPORT_VERTICAL",
        NV4097_SET_BEGIN_END => "SET_BEGIN_END",
        NV4097_DRAW_ARRAYS => "DRAW_ARRAYS",
        NV4097_DRAW_INDEX_ARRAY => "DRAW_INDEX_ARRAY",
        _ => "",
    };

    static byte[] Inflate(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var ms = new MemoryStream(capacity: 1 << 26);
        gz.CopyTo(ms, 1 << 20);
        return ms.ToArray();
    }
}
