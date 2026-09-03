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
    }

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
                        shaderProgReg, vpWords, vpStart, consts, constWritten,
                        fogMode, fogParam0, fogParam1,
                        new RenderState(surfW, surfH, depthFmt, (byte)colorTarget, colorMask, depthWrite, vpX, vpY, vpW, vpH));
                    draws.Add(ds);
                    pending.Add(ds);
                    break;
                }
            }
        }

        ResolvePendingFp();
        return draws;
    }

    static DrawState Snapshot(int index, bool indexed, uint shaderProgReg,
        uint[] vpWords, int vpStart, float[][] consts, bool[] constWritten,
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
        };
    }
}
