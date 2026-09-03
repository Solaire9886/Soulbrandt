namespace RsxShaderMatch;

/// <summary>
/// Decoder for RSX <b>vertex</b>-program microcode - the separate ISA the fragment
/// decoder in <see cref="RsxFp"/> does not handle. <see cref="Decode"/> produces a
/// fingerprint for matching; <see cref="Disassemble"/> a readable listing. Ported from
/// RPCS3's <c>CgBinaryDisasm::TaskVP</c> (<c>CgBinaryVertexProgram.cpp</c>) and the
/// <c>D0..D3</c> / <c>SRC</c> unions in <c>RSXVertexProgram.h</c>.
///
/// Encoding differs from the fragment side in three ways that matter here:
///  - each instruction is four u32 words, big-endian in the file, and <b>no</b> 16-bit
///    halfword swap (fragment needs one; RPCS3's <c>ConvertToLE</c> byte-swaps VP ucode
///    straight to native).
///  - one instruction co-issues a vector op (word1 bits 22-26) and a scalar op
///    (word1 bits 27-31); either may be NOP.
///  - constants are not inlined. A const reference is <c>c[d1.const_src]</c>, optionally
///    relative (<c>c[A + n]</c>) when <c>d3.index_const</c> is set - the real values live
///    in the CgBinaryProgram parameter table (names stripped, see <see cref="CgProgram"/>).
///
/// word0=D0, word1=D1, word2=D2, word3=D3. The walk ends at the instruction whose
/// <c>D3.end</c> bit is set. Branches (BRA/BRI) are not handled - no ds_flver vertex
/// program uses them; <see cref="Decode"/> flags one in <c>Fingerprint.HasBranch</c> if
/// it ever appears rather than silently mis-walking.
/// </summary>
static class RsxVp
{
    // rsx_vp_sca_op_names / rsx_vp_vec_op_names, RSXVertexProgram.h.
    static readonly string[] ScaOps =
    {
        "NOP", "MOV", "RCP", "RCC", "RSQ", "EXP", "LOG", "LIT", "BRA", "BRI", "CAL",
        "CLI", "RET", "LG2", "EX2", "SIN", "COS", "BRB", "CLB", "PSH", "POP",
    };

    static readonly string[] VecOps =
    {
        "NOP", "MOV", "MUL", "ADD", "MAD", "DP3", "DPH", "DP4", "DST", "MIN", "MAX",
        "SLT", "SGE", "ARL", "FRC", "FLR", "SEQ", "SFL", "SGT", "SLE", "SNE", "STR",
        "SSG", "?23", "?24", "TXL",
    };

    // reg_type 2 sources index this by D1.input_src (one input read per instruction).
    static readonly string[] InputRegs =
    {
        "in_pos", "in_weight", "in_normal", "in_diff_color", "in_spec_color", "in_fog",
        "in_point_size", "in_7", "in_tc0", "in_tc1", "in_tc2", "in_tc3", "in_tc4",
        "in_tc5", "in_tc6", "in_tc7",
    };

    // Which SRC slots each op reads, from CgBinaryDisasm::TaskVP's format strings
    // ("$0"=src0, "$1"=src1, "$2"/"$s"=src2). VEC ops keyed by name; the scalar op always
    // reads src2.
    static readonly Dictionary<string, int[]> VecSrcSlots = new()
    {
        ["MOV"] = new[] { 0 }, ["FRC"] = new[] { 0 }, ["FLR"] = new[] { 0 }, ["SFL"] = new[] { 0 },
        ["STR"] = new[] { 0 }, ["SSG"] = new[] { 0 }, ["ARL"] = new[] { 0 },
        ["MUL"] = new[] { 0, 1 }, ["DP3"] = new[] { 0, 1 }, ["DP4"] = new[] { 0, 1 },
        ["DPH"] = new[] { 0, 1 }, ["DST"] = new[] { 0, 1 }, ["MIN"] = new[] { 0, 1 },
        ["MAX"] = new[] { 0, 1 }, ["SLT"] = new[] { 0, 1 }, ["SGE"] = new[] { 0, 1 },
        ["SEQ"] = new[] { 0, 1 }, ["SGT"] = new[] { 0, 1 }, ["SLE"] = new[] { 0, 1 },
        ["SNE"] = new[] { 0, 1 }, ["TXL"] = new[] { 0 },
        ["ADD"] = new[] { 0, 2 },
        ["MAD"] = new[] { 0, 1, 2 },
    };

    // D3.dst (when != 0x1f) selects the output register - the GLSL decompiler names these
    // dst_reg<d3.dst>, so the number is directly comparable to a capture's dst_regN tokens.
    // 0 pos, 1 diffuse, 2 specular, 3 back-diffuse, 4 back-specular, 5 fog, 6 point-size/tc9,
    // 7..14 tc0..tc7, 15 tc8. Kept as raw indices; naming is the caller's problem.
    const int DstNone = 0x1f;

    public readonly record struct Instr(
        int Index, int ScaOp, int VecOp, string Sca, string Vec,
        int Dst, int DstTmp, int ScaDstTmp,
        int ConstRef, bool IndexConst, int InputRef,
        bool ReadsConst, bool ReadsInput, bool End,
        uint Src0, uint Src1, uint Src2,
        uint W0, uint W1, uint W2, uint W3);

    public readonly record struct Fingerprint(
        int InstrCount,
        IReadOnlyList<int> ConstRefs,
        bool IndexedConst,
        IReadOnlyList<int> InputRegs,
        IReadOnlyList<int> OutRegs,
        int Ex2Count,
        int RsqCount,
        int RcpCount,
        int DotCount,
        bool HasTxl,
        bool HasBranch,
        IReadOnlyDictionary<string, int> VecHistogram,
        IReadOnlyDictionary<string, int> ScaHistogram)
    {
        public string ConstKey => string.Join(",", ConstRefs);
        public string InputKey => string.Join(",", InputRegs);
        public string OutKey => string.Join(",", OutRegs);
    }

    public static (List<Instr> Instrs, Fingerprint Fp) Decode(byte[] ucode)
    {
        var instrs = new List<Instr>();
        bool branch = false;

        for (int off = 0; off + 16 <= ucode.Length; off += 16)
        {
            uint w0 = ReadU32Be(ucode, off + 0);
            uint w1 = ReadU32Be(ucode, off + 4);
            uint w2 = ReadU32Be(ucode, off + 8);
            uint w3 = ReadU32Be(ucode, off + 12);

            int scaOp = (int)((w1 >> 27) & 0x1F);
            int vecOp = (int)((w1 >> 22) & 0x1F);
            int inputSrc = (int)((w1 >> 8) & 0xF);
            int constSrc = (int)((w1 >> 12) & 0x3FF);

            bool end = (w3 & 0x1) != 0;
            bool indexConst = ((w3 >> 1) & 0x1) != 0;
            int dst = (int)((w3 >> 2) & 0x1F);
            int scaDstTmp = (int)((w3 >> 7) & 0x3F);
            int dstTmp = (int)((w0 >> 15) & 0x3F);

            // 17-bit SRC fields, reassembled per TaskVP: src0 = d2.src0l(9) | d1.src0h(8)<<9;
            // src1 = d2.src1(17); src2 = d3.src2l(11) | d2.src2h(6)<<11.
            uint src0 = ((w2 >> 23) & 0x1FF) | ((w1 & 0xFFu) << 9);
            uint src1 = (w2 >> 6) & 0x1FFFF;
            uint src2 = ((w3 >> 21) & 0x7FF) | ((w2 & 0x3Fu) << 11);

            bool readsConst = SrcType(src0) == 3 || SrcType(src1) == 3 || SrcType(src2) == 3;
            bool readsInput = SrcType(src0) == 2 || SrcType(src1) == 2 || SrcType(src2) == 2;

            if (scaOp is 0x08 or 0x09) branch = true;   // BRA / BRI - not walked correctly past here

            instrs.Add(new Instr(
                instrs.Count, scaOp, vecOp,
                scaOp < ScaOps.Length ? ScaOps[scaOp] : $"SCA{scaOp}",
                vecOp < VecOps.Length ? VecOps[vecOp] : $"VEC{vecOp}",
                dst, dstTmp, scaDstTmp,
                readsConst ? constSrc : -1, indexConst, readsInput ? inputSrc : -1,
                readsConst, readsInput, end,
                src0, src1, src2,
                w0, w1, w2, w3));

            if (end || branch) break;
        }

        var constRefs = new SortedSet<int>();
        var inputRegs = new SortedSet<int>();
        var outRegs = new SortedSet<int>();
        var vecHist = new Dictionary<string, int>();
        var scaHist = new Dictionary<string, int>();
        int ex2 = 0, rsq = 0, rcp = 0, dot = 0;
        bool indexed = false, txl = false;

        foreach (var i in instrs)
        {
            if (i.VecOp != 0) Bump(vecHist, i.Vec);
            if (i.ScaOp != 0) Bump(scaHist, i.Sca);
            if (i.ReadsConst) { constRefs.Add(i.ConstRef); if (i.IndexConst) indexed = true; }
            if (i.ReadsInput) inputRegs.Add(i.InputRef);
            if (i.Dst != DstNone && (i.VecOp != 0 || i.ScaOp != 0)) outRegs.Add(i.Dst);
            if (i.Sca == "EX2" || i.Sca == "EXP") ex2++;
            if (i.Sca == "RSQ") rsq++;
            if (i.Sca == "RCP" || i.Sca == "RCC") rcp++;
            if (i.Vec is "DP3" or "DP4" or "DPH") dot++;
            if (i.Vec == "TXL") txl = true;
        }

        var fp = new Fingerprint(
            InstrCount: instrs.Count,
            ConstRefs: constRefs.ToList(),
            IndexedConst: indexed,
            InputRegs: inputRegs.ToList(),
            OutRegs: outRegs.ToList(),
            Ex2Count: ex2, RsqCount: rsq, RcpCount: rcp, DotCount: dot,
            HasTxl: txl, HasBranch: branch,
            VecHistogram: vecHist, ScaHistogram: scaHist);

        return (instrs, fp);
    }

    /// <summary>Readable one-line-per-instruction listing (vector op | scalar op),
    /// close to RPCS3's own ARB-style VP disassembly plus the resolved register roles.</summary>
    public static List<string> Disassemble(byte[] ucode)
    {
        var (instrs, _) = Decode(ucode);
        var lines = new List<string>();

        foreach (var i in instrs)
        {
            var cols = new List<string>();
            if (i.VecOp != 0)
            {
                var slots = VecSrcSlots.GetValueOrDefault(i.Vec, new[] { 0, 1, 2 });
                cols.Add($"{i.Vec,-4} {DstStr(i, sca: false)}{SrcList(i, slots)}");
            }
            if (i.ScaOp != 0)
                cols.Add($"{i.Sca,-4} {DstStr(i, sca: true)}{SrcList(i, new[] { 2 })}");
            if (cols.Count == 0) cols.Add("NOP");

            string line = $"  {i.Index,3}  {string.Join("   |   ", cols)}";
            if (i.End) line += "   ; END";
            lines.Add(line);
        }
        return lines;
    }

    static string DstStr(Instr i, bool sca)
    {
        int tmp = sca ? i.ScaDstTmp : i.DstTmp;
        if (i.Dst != DstNone) return $"o[{i.Dst}]";       // output register (dst_reg<n> in GLSL)
        if (tmp != 0x3F) return $"R{tmp}";
        return "RC";
    }

    static string SrcList(Instr i, int[] slots)
    {
        uint[] src = { i.Src0, i.Src1, i.Src2 };
        var s = slots.Select(slot => FormatSrc(src[slot], i));
        return ", " + string.Join(", ", s);
    }

    // A 17-bit SRC field: reg_type(2) tmp_src(6) swz_w/z/y/x(2 each) neg(1). Input number and
    // const index are per-instruction (D1.input_src / D1.const_src), not in the SRC field.
    static string FormatSrc(uint s, Instr i)
    {
        int type = (int)(s & 0x3);
        int tmp = (int)((s >> 2) & 0x3F);
        int sx = (int)((s >> 14) & 3), sy = (int)((s >> 12) & 3), sz = (int)((s >> 10) & 3), sw = (int)((s >> 8) & 3);
        bool neg = ((s >> 16) & 1) != 0;

        string reg = type switch
        {
            1 => $"R{tmp}",
            2 => i.InputRef >= 0 && i.InputRef < InputRegs.Length ? InputRegs[i.InputRef] : $"v[{i.InputRef}]",
            3 => i.IndexConst ? $"c[A + {i.ConstRef}]" : $"c[{i.ConstRef}]",
            _ => "?",
        };

        const string f = "xyzw";
        string sw4 = $"{f[sx]}{f[sy]}{f[sz]}{f[sw]}";
        if (sw4 != "xyzw") reg += sw4[0] == sw4[1] && sw4[1] == sw4[2] && sw4[2] == sw4[3] ? $".{sw4[0]}" : $".{sw4}";
        return neg ? "-" + reg : reg;
    }

    static int SrcType(uint src17) => (int)(src17 & 0x3);   // 1 temp, 2 input, 3 const

    static void Bump(Dictionary<string, int> h, string k) { h.TryGetValue(k, out int n); h[k] = n + 1; }

    static uint ReadU32Be(byte[] b, int o) =>
        (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
}
