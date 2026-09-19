namespace RsxShaderMatch;

/// <summary>
/// Minimal RSX FIFO replay over a parsed <see cref="RrcCapture.Frame"/>. Tracks only what a
/// draw needs for shader identification and constant recovery:
///  - <c>SET_SHADER_PROGRAM</c>       -> fragment-program address (bytes come from the frame's
///                                       memory blocks, attached to the draw by capture_draw_memory)
///  - <c>SET_TRANSFORM_PROGRAM[_LOAD]</c> -> vertex-program microcode, assembled into a 544-slot
///                                       buffer (RPCS3 note: "vertex shader is passed in registers")
///  - <c>SET_TRANSFORM_CONSTANT[_LOAD]</c> -> the 468 vec4 vertex constant registers c[] - where
///                                       the CPU-computed per-draw values (c104..c110 etc.) live
///
/// Arg words in the capture are host-native. FP ucode blocks are raw big-endian PS3 memory
/// (fed straight to <see cref="RsxFp.Decode"/>, which does its own BE + halfword handling);
/// VP ucode dwords are native and are re-emitted big-endian so <see cref="RsxVp.Decode"/>'s
/// BE reader recovers them.
/// </summary>
static class RrcInterp
{
    const int MaxVpInstr = 544;

    public sealed class DrawState
    {
        public required int Index { get; init; }              // 0-based draw ordinal within the frame
        public required bool Indexed { get; init; }
        public byte[]? FpUcode { get; set; }                  // filled once the FP block resolves; null if never found
        public required uint FpAddr { get; init; }
        public required byte[] VpUcode { get; init; }         // big-endian dwords from slot `VpStart`
        public required int VpStart { get; init; }
        public required float[][] Constants { get; init; }    // [468][4]; NaN-free, zero where never written
        public required bool[] ConstWritten { get; init; }    // [468] - which c[] the frame actually set

        // RSX fixed-function fog state in force at this draw (what the decompiled shaders'
        // never-in-our-corpus fetch_fog_value() reads). FogMode 0 = SET_FOG_MODE never issued
        // (the RSX default reg value is 0x800 = EXP, but "never issued" is the useful signal).
        public required uint FogMode { get; init; }           // gcm CELL_GCM_FOG_MODE_* (LINEAR 0x2601, EXP 0x0800, ...)
        public required float FogParam0 { get; init; }        // SET_FOG_PARAMS arg 0 (bias)
        public required float FogParam1 { get; init; }        // SET_FOG_PARAMS arg 1 (scale)

        // Render-target / write state - lets a depth-only shadow-cast pass be told apart from
        // the colour pass without touching pixels.
        public required RenderState Rs { get; init; }

        // Per fragment-texture-unit state at this draw (units 0-15). Raw RSX method register
        // values; decode with the helpers below. NV4097_SET_TEXTURE_* live at method
        // 0x680 + unit*8 (+1 FORMAT, +2 ADDRESS, +3 CONTROL0).
        public required uint[] TexFormat { get; init; }    // [16] - format() = (v>>8)&0xff, cubemap = (v>>2)&1, mips = (v>>16)&0xffff
        public required uint[] TexAddress { get; init; }   // [16] - gamma (sRGB-on-fetch, RGBA) = (v>>20)&0xf; unsigned_remap = (v>>12)&0xf
        public required uint[] TexControl0 { get; init; }  // [16] - enabled = (v>>31)&1
        public required uint[] TexControl1 { get; init; }  // [16] - the channel remap/swizzle register
    }

    // Base pixel format (NV4097_SET_TEXTURE_FORMAT bits 8-15, with the LN/UN linear/unnorm flags
    // stripped). CELL_GCM_TEXTURE_* per RPCS3's gcm_enums.h.
    public static byte TexBaseFormat(uint texFormatReg) => (byte)(((texFormatReg >> 8) & 0xff) & ~0x60);
    public static bool TexIsCube(uint texFormatReg) => ((texFormatReg >> 2) & 1) != 0;
    public static int TexMipCount(uint texFormatReg) => (int)((texFormatReg >> 16) & 0xffff);
    // Per-channel sRGB->linear-on-fetch mask, RGBA order (bit0=R,1=G,2=B,3=A). Applied by RSX only
    // for the gamma-capable colour formats (A8R8G8B8, DXT1/23/45, B8, G8B8, R5G6B5, ... - not
    // depth/float/X16). See RPCS3 RSXTexture.cpp fragment_texture::gamma() + get_format_features().
    public static int TexGammaMask(uint texAddressReg) => (int)((texAddressReg >> 20) & 0xf);
    public static int TexUnsignedRemap(uint texAddressReg) => (int)((texAddressReg >> 12) & 0xf); // 1 = BIASED/BX2 range decompress
    public static int TexSignedRemap(uint texAddressReg) => (int)((texAddressReg >> 24) & 0xf);
    public static bool TexEnabled(uint texControl0Reg) => ((texControl0Reg >> 31) & 1) != 0;

    public static string TexFormatName(byte baseFormat) => baseFormat switch
    {
        0x81 => "B8", 0x82 => "A1R5G5B5", 0x83 => "A4R4G4B4", 0x84 => "R5G6B5", 0x85 => "A8R8G8B8",
        0x86 => "DXT1", 0x87 => "DXT23", 0x88 => "DXT45", 0x8B => "G8B8", 0x8F => "R6G5B5",
        0x90 => "DEPTH24_D8", 0x91 => "DEPTH24_D8_F", 0x92 => "DEPTH16", 0x94 => "X16",
        0x97 => "R5G5B5A1", 0x9C => "X32_FLOAT", 0x9E => "D8R8G8B8", 0x9F => "Y16_X16_F",
        _ => $"0x{baseFormat:X2}",
    };
    // The colour formats RSX will actually gamma-correct on fetch (RPCS3 get_format_features).
    public static bool TexFormatGammaCapable(byte baseFormat) => baseFormat is
        0x81 or 0x82 or 0x83 or 0x84 or 0x85 or 0x86 or 0x87 or 0x88 or 0x8B or 0x8D or 0x8E or 0x8F or 0x97 or 0x9D or 0x9E;

    public readonly record struct RenderState(
        uint SurfaceW, uint SurfaceH,     // SET_SURFACE_CLIP_* extents (the actual render area)
        byte DepthFmt,                    // SET_SURFACE_FORMAT [5:8] - 1 = Z16, 2 = Z24S8
        byte ColorTarget,                 // SET_SURFACE_COLOR_TARGET - 0 = none bound
        uint ColorMask,                   // SET_COLOR_MASK - 0 = no colour written (depth-only)
        bool DepthWrite,                  // SET_DEPTH_MASK
        uint ViewportX, uint ViewportY,   // SET_VIEWPORT_* origin - which atlas tile
        uint ViewportW, uint ViewportH)   // SET_VIEWPORT_* extents (< surface => atlas tiling)
    {
        public bool IsDepthOnly => ColorMask == 0 || ColorTarget == 0;
        public bool IsSquare => SurfaceW != 0 && SurfaceW == SurfaceH;
        public string Kind =>
            IsDepthOnly && IsSquare ? "SHADOW"
            : IsDepthOnly ? "DEPTH"
            : "COLOR";
    }

    public static List<DrawState> Replay(RrcCapture.Frame f)
    {
        // Rolling "latest bytes at (offset,location)" as memory_state blocks are applied in order.
        var mem = new Dictionary<(uint, uint), byte[]>();

        uint shaderProgReg = 0;
        uint fogMode = 0;
        float fogParam0 = 0f, fogParam1 = 0f;
        uint surfW = 0, surfH = 0, colorMask = 0xFFFFFFFF, colorTarget = 1, vpX = 0, vpY = 0, vpW = 0, vpH = 0;
        byte depthFmt = 0;
        bool depthWrite = true;
        int constLoad = 0, vpLoad = 0, vpStart = 0;
        var texRegs = new uint[128];                      // NV4097_SET_TEXTURE_* methods 0x680..0x6FF
        var vpWords = new uint[MaxVpInstr * 4];
        var consts = new float[468][];
        for (int i = 0; i < 468; i++) consts[i] = new float[4];
        var constWritten = new bool[468];

        var draws = new List<DrawState>();
        var cmds = f.Commands;

        // capture_draw_memory attaches the fragment-program bytes to the SET_BEGIN_END(0) that
        // *follows* the draw (thread::end()), so a draw's FP block isn't in `mem` yet at the DRAW
        // itself - and a begin/end may contain several draws sharing one FP. Hold all draws since
        // the last resolution and fill them in when their FP address appears in `mem`.
        var pending = new List<DrawState>();
        void ResolvePendingFp()
        {
            if (pending.Count == 0) return;
            uint addr = pending[0].FpAddr;
            uint off = addr & ~3u, loc = (addr & 3u) - 1u;
            if (mem.TryGetValue((off, loc), out var b) || mem.TryGetValue((off, loc ^ 1u), out b))
            {
                foreach (var d in pending) if (d.FpAddr == addr) d.FpUcode = b;
                pending.RemoveAll(d => d.FpAddr == addr);
            }
        }

        for (int i = 0; i < cmds.Count; i++)
        {
            var cmd = cmds[i];

            foreach (var idx in cmd.MemState)
                if (f.Blocks.TryGetValue(idx, out var blk) && f.Data.TryGetValue(blk.DataIndex, out var bytes))
                    mem[(blk.Offset, blk.Location)] = bytes;
            ResolvePendingFp();

            if (cmd.First == 0) continue;                     // stray continuation (shouldn't happen at top level)

            int reg = (int)((cmd.First >> 2) & 0x3FFF);
            int count = (int)((cmd.First >> 18) & 0x7FF);     // total args, including cmd.Second
            if (count == 0) continue;                         // e.g. the frame's leading NOP (first = NV4097_NO_OPERATION, unshifted)

            // Gather this method's args: cmd.Second, then (count-1) following continuation entries.
            var args = new uint[count];
            args[0] = cmd.Second;
            for (int k = 1; k < count; k++)
                args[k] = (i + k < cmds.Count) ? cmds[i + k].Second : 0u;
            i += count - 1;

            switch (reg)
            {
                case RrcCapture.NV4097_SET_SHADER_PROGRAM:
                    shaderProgReg = args[0];
                    break;

                case RrcCapture.NV4097_SET_FOG_MODE:
                    fogMode = args[0];
                    break;

                case RrcCapture.NV4097_SET_FOG_PARAMS:
                    fogParam0 = BitConverter.Int32BitsToSingle((int)args[0]);
                    if (count > 1) fogParam1 = BitConverter.Int32BitsToSingle((int)args[1]);
                    break;

                case RrcCapture.NV4097_SET_SURFACE_CLIP_HORIZONTAL:
                    surfW = args[0] >> 16;                    // low 16 = x origin, high 16 = width
                    break;

                case RrcCapture.NV4097_SET_SURFACE_CLIP_VERTICAL:
                    surfH = args[0] >> 16;
                    break;

                case RrcCapture.NV4097_SET_SURFACE_FORMAT:
                    depthFmt = (byte)((args[0] >> 5) & 0x7);
                    break;

                case RrcCapture.NV4097_SET_SURFACE_COLOR_TARGET:
                    colorTarget = args[0];
                    break;

                case RrcCapture.NV4097_SET_COLOR_MASK:
                    colorMask = args[0];
                    break;

                case RrcCapture.NV4097_SET_DEPTH_MASK:
                    depthWrite = args[0] != 0;
                    break;

                case RrcCapture.NV4097_SET_VIEWPORT_HORIZONTAL:
                    vpX = args[0] & 0xFFFF;
                    vpW = args[0] >> 16;
                    break;

                case RrcCapture.NV4097_SET_VIEWPORT_VERTICAL:
                    vpY = args[0] & 0xFFFF;
                    vpH = args[0] >> 16;
                    break;

                case RrcCapture.NV4097_SET_TRANSFORM_PROGRAM_LOAD:
                    vpLoad = (int)args[0];
                    break;

                case RrcCapture.NV4097_SET_TRANSFORM_PROGRAM_START:
                    vpStart = (int)args[0];
                    break;

                case RrcCapture.NV4097_SET_TRANSFORM_PROGRAM:
                {
                    int baseW = vpLoad * 4;
                    for (int k = 0; k < count && baseW + k < vpWords.Length; k++)
                        vpWords[baseW + k] = args[k];
                    vpLoad += (count + 3) / 4;                // handler advances load by whole instructions
                    break;
                }

                case RrcCapture.NV4097_SET_TRANSFORM_CONSTANT_LOAD:
                    constLoad = (int)args[0];
                    break;

                case RrcCapture.NV4097_SET_TRANSFORM_CONSTANT:
                {
                    // reg is the window base (0x7C0); the capture always starts at sub-index 0.
                    for (int k = 0; k < count; k++)
                    {
                        int cid = constLoad + k / 4, sub = k % 4;
                        if ((uint)cid < 468) { consts[cid][sub] = BitConverter.Int32BitsToSingle((int)args[k]); constWritten[cid] = true; }
                    }
                    break;
                }

                case RrcCapture.NV4097_DRAW_ARRAYS:
                case RrcCapture.NV4097_DRAW_INDEX_ARRAY:
                {
                    var ds = Snapshot(draws.Count, reg == RrcCapture.NV4097_DRAW_INDEX_ARRAY,
                        shaderProgReg, vpWords, vpStart, consts, constWritten, texRegs,
                        fogMode, fogParam0, fogParam1,
                        new RenderState(surfW, surfH, depthFmt, (byte)colorTarget, colorMask, depthWrite, vpX, vpY, vpW, vpH));
                    draws.Add(ds);
                    pending.Add(ds);
                    break;
                }

                default:
                    // Per-unit fragment-texture state (units 0-15) lives at method 0x680 + unit*8
                    // and up; a FIFO command writes `count` consecutive method registers.
                    for (int k = 0; k < count; k++)
                    {
                        int m = reg + k;
                        if (m >= 0x680 && m < 0x700) texRegs[m - 0x680] = args[k];
                    }
                    break;
            }
        }

        ResolvePendingFp();
        return draws;
    }

    static DrawState Snapshot(int index, bool indexed, uint shaderProgReg,
        uint[] vpWords, int vpStart, float[][] consts, bool[] constWritten, uint[] texRegs,
        uint fogMode, float fogParam0, float fogParam1, RenderState rs)
    {
        // VP: emit from slot vpStart to the end of what's been written, big-endian, for RsxVp.
        int firstWord = vpStart * 4;
        int lastWord = vpWords.Length;
        while (lastWord > firstWord && vpWords[lastWord - 1] == 0) lastWord--;
        int n = System.Math.Max(0, lastWord - firstWord);
        var vp = new byte[n * 4];
        for (int w = 0; w < n; w++)
        {
            uint v = vpWords[firstWord + w];
            vp[w * 4 + 0] = (byte)(v >> 24);
            vp[w * 4 + 1] = (byte)(v >> 16);
            vp[w * 4 + 2] = (byte)(v >> 8);
            vp[w * 4 + 3] = (byte)v;
        }

        var cc = new float[468][];
        for (int i = 0; i < 468; i++) cc[i] = (float[])consts[i].Clone();

        var tf = new uint[16];
        var ta = new uint[16];
        var tc0 = new uint[16];
        var tc1 = new uint[16];
        for (int u = 0; u < 16; u++)
        {
            tf[u] = texRegs[u * 8 + 1];   // FORMAT   = 0x681 + u*8
            ta[u] = texRegs[u * 8 + 2];   // ADDRESS  = 0x682 + u*8
            tc0[u] = texRegs[u * 8 + 3];  // CONTROL0 = 0x683 + u*8
            tc1[u] = texRegs[u * 8 + 4];  // CONTROL1 = 0x684 + u*8 (remap)
        }

        return new DrawState
        {
            Index = index,
            Indexed = indexed,
            FpAddr = shaderProgReg,
            VpUcode = vp,
            VpStart = vpStart,
            Constants = cc,
            ConstWritten = (bool[])constWritten.Clone(),
            FogMode = fogMode,
            FogParam0 = fogParam0,
            FogParam1 = fogParam1,
            Rs = rs,
            TexFormat = tf,
            TexAddress = ta,
            TexControl0 = tc0,
            TexControl1 = tc1,
        };
    }
}
