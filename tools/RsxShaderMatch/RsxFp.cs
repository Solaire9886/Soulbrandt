namespace RsxShaderMatch;

/// <summary>
/// Decoder for RSX fragment-program microcode. <see cref="Decode"/> produces a
/// fingerprint for matching; <see cref="Disassemble"/> produces a readable
/// instruction listing. Ported from RPCS3's <c>CgBinaryDisasm::TaskFP</c>
/// (<c>CgBinaryFragmentProgram.cpp</c>) and the bitfield unions in
/// <c>ShaderParam.h</c> / <c>RSXFragmentProgram.h</c>.
///
/// Encoding: each instruction is four u32 words, stored big-endian in the file, and
/// each word then needs a 16-bit halfword swap (<c>GetData(d) = d&lt;&lt;16 | d&gt;&gt;16</c>).
/// word0 = OPDEST, word1 = SRC0, word2 = SRC1, word3 = SRC2.
/// opcode = <c>dst.opcode | (src1.opcode_hi &lt;&lt; 6)</c> (7 bits).
/// An instruction whose any source is a constant register (reg_type == 2) is
/// followed by a 128-bit inline literal, so it occupies 32 bytes, not 16. That
/// literal is the slot the game patches with a real value at draw time - in the
/// shipped file it is often all-zero.
/// The walk ends at the instruction whose OPDEST.end bit is set.
/// </summary>
static class RsxFp
{
    // rsx_fp_op_names, index = 7-bit opcode. NULL = unused slot.
    static readonly string[] OpNames =
    {
        "NOP", "MOV", "MUL", "ADD", "MAD", "DP3", "DP4",
        "DST", "MIN", "MAX", "SLT", "SGE", "SLE", "SGT",
        "SNE", "SEQ", "FRC", "FLR", "KIL", "PK4", "UP4",
        "DDX", "DDY", "TEX", "TXP", "TXD", "RCP", "RSQ",
        "EX2", "LG2", "LIT", "LRP", "STR", "SFL", "COS",
        "SIN", "PK2", "UP2", "POW", "PKB", "UPB", "PK16",
        "UP16", "BEM", "PKG", "UPG", "DP2A", "TXL", "NULL",
        "TXB", "NULL", "TEXBEM", "TXPBEM", "BEMLUM", "REFL", "TIMESWTEX",
        "DP2", "NRM", "DIV", "DIVSQ", "LIF", "FENCT", "FENCB",
        "NULL", "BRK", "CAL", "IFE", "LOOP", "REP", "RET",
    };

    // rsx_fp_input_attr_regs - reg_type 1 sources index this by OPDEST.src_attr_reg_num.
    static readonly string[] InputRegs =
    {
        "WPOS", "COL0", "COL1", "FOGC", "TEX0", "TEX1", "TEX2", "TEX3",
        "TEX4", "TEX5", "TEX6", "TEX7", "TEX8", "TEX9", "SSA",
    };

    // Operand count per opcode (0 = flow/none). Default 3 for anything unlisted.
    static readonly Dictionary<string, int> Arity = new()
    {
        ["NOP"] = 0, ["FENCT"] = 0, ["FENCB"] = 0, ["KIL"] = 0, ["BRK"] = 0, ["RET"] = 0,
        ["CAL"] = 0, ["IFE"] = 0, ["LOOP"] = 0, ["REP"] = 0,
        ["MOV"] = 1, ["RCP"] = 1, ["RSQ"] = 1, ["EX2"] = 1, ["LG2"] = 1, ["FRC"] = 1,
        ["FLR"] = 1, ["LIT"] = 1, ["LIF"] = 1, ["COS"] = 1, ["SIN"] = 1, ["NRM"] = 1,
        ["DDX"] = 1, ["DDY"] = 1, ["TXP"] = 1,
        ["PK2"] = 1, ["PK4"] = 1, ["PK16"] = 1, ["PKB"] = 1, ["PKG"] = 1,
        ["UP2"] = 1, ["UP4"] = 1, ["UP16"] = 1, ["UPB"] = 1, ["UPG"] = 1,
        ["TEX"] = 1, ["TXB"] = 1, ["TXL"] = 1,
        ["ADD"] = 2, ["MUL"] = 2, ["MIN"] = 2, ["MAX"] = 2, ["DP2"] = 2, ["DP3"] = 2,
        ["DP4"] = 2, ["DIV"] = 2, ["DIVSQ"] = 2, ["DST"] = 2, ["REFL"] = 2, ["POW"] = 2,
        ["SEQ"] = 2, ["SGE"] = 2, ["SGT"] = 2, ["SLE"] = 2, ["SLT"] = 2, ["SNE"] = 2,
        ["SFL"] = 2, ["STR"] = 2, ["TXD"] = 2,
        ["MAD"] = 3, ["DP2A"] = 3, ["LRP"] = 3,
    };

    static readonly HashSet<string> TextureOps = new()
    {
        "TEX", "TXP", "TXD", "TXL", "TXB", "TEXBEM", "TXPBEM", "BEMLUM", "TIMESWTEX",
    };

    public readonly record struct Instr(
        int Opcode, string Op, int DestReg, bool Fp16, int WriteMask, int TexNum, bool HasConst,
        uint W0, uint W1, uint W2, uint W3, int ConstIndex, float[]? Const, bool NoDest, bool Saturate, bool Bx2);

    public readonly record struct Fingerprint(
        int SlotCount,
        int InstrCount,
        int ConstCount,
        bool HasKil,
        bool HasFenc,
        IReadOnlyList<(string Op, int Unit)> TextureSequence,
        IReadOnlyList<int> TextureUnits,
        IReadOnlyList<int> UnitRun,
        IReadOnlyDictionary<string, int> OpHistogram)
    {
        public string TexKey => string.Join(",", TextureSequence.Select(t => $"{t.Op}{t.Unit}"));
        public string UnitKey => string.Join(",", TextureUnits);
        public string UnitRunKey => string.Join(",", UnitRun);
    }

    public static (List<Instr> Instrs, Fingerprint Fp) Decode(byte[] ucode)
    {
        var instrs = new List<Instr>();
        int slots = 0;
        int off = 0;
        int constIndex = 0;

        while (off + 16 <= ucode.Length)
        {
            uint w0 = Swap16(ReadU32Be(ucode, off + 0));
            uint w1 = Swap16(ReadU32Be(ucode, off + 4));
            uint w2 = Swap16(ReadU32Be(ucode, off + 8));
            uint w3 = Swap16(ReadU32Be(ucode, off + 12));

            int opcode = (int)(((w0 >> 24) & 0x3F) | (((w2 >> 31) & 0x1) << 6));
            string op = opcode < OpNames.Length ? OpNames[opcode] : $"OP{opcode}";

            int destReg = (int)((w0 >> 1) & 0x3F);
            bool fp16 = ((w0 >> 7) & 0x1) != 0;
            int writeMask = (int)((w0 >> 9) & 0xF);
            int texNum = (int)((w0 >> 17) & 0xF);
            bool bx2 = ((w0 >> 21) & 0x1) != 0;
            bool noDest = ((w0 >> 30) & 0x1) != 0;
            bool sat = ((w0 >> 31) & 0x1) != 0;
            bool end = (w0 & 0x1) != 0;

            bool hasConst = (w1 & 0x3) == 2 || (w2 & 0x3) == 2 || (w3 & 0x3) == 2;

            float[]? konst = null;
            int thisConstIdx = -1;
            if (hasConst && off + 32 <= ucode.Length)
            {
                konst = new float[4];
                for (int k = 0; k < 4; k++)
                {
                    uint cw = Swap16(ReadU32Be(ucode, off + 16 + k * 4));
                    konst[k] = BitConverter.Int32BitsToSingle((int)cw);
                }
                thisConstIdx = constIndex++;
            }

            instrs.Add(new Instr(opcode, op, destReg, fp16, writeMask, texNum, hasConst,
                w0, w1, w2, w3, thisConstIdx, konst, noDest, sat, bx2));

            int step = hasConst ? 32 : 16;
            slots += step / 16;
            off += step;

            if (end) break;
        }

        var texSeq = new List<(string, int)>();
        var histogram = new Dictionary<string, int>();
        int constCount = 0;
        bool hasKil = false, hasFenc = false;

        foreach (var i in instrs)
        {
            histogram.TryGetValue(i.Op, out int n);
            histogram[i.Op] = n + 1;
            if (i.HasConst) constCount++;
            if (i.Op == "KIL") hasKil = true;
            if (i.Op is "FENCT" or "FENCB") hasFenc = true;
            if (TextureOps.Contains(i.Op)) texSeq.Add((i.Op, i.TexNum));
        }

        var unitRun = new List<int>();
        foreach (var (_, unit) in texSeq)
            if (unitRun.Count == 0 || unitRun[^1] != unit)
                unitRun.Add(unit);

        var fp = new Fingerprint(
            SlotCount: slots,
            InstrCount: instrs.Count,
            ConstCount: constCount,
            HasKil: hasKil,
            HasFenc: hasFenc,
            TextureSequence: texSeq,
            TextureUnits: texSeq.Select(t => t.Item2).Distinct().OrderBy(x => x).ToList(),
            UnitRun: unitRun,
            OpHistogram: histogram);

        return (instrs, fp);
    }

    /// <summary>Readable one-line-per-instruction listing. Not GLSL - RSX assembly, close
    /// to the form RPCS3's own <c>CgBinaryDisasm</c> emits, plus the inline constant values.</summary>
    public static List<string> Disassemble(byte[] ucode)
    {
        var (instrs, _) = Decode(ucode);
        var lines = new List<string>();
        int idx = 0;

        // NV_fragment_program output scale, SRC1 bits 28-30: 0 none, 1 x2, 2 x4, 3 x8, 5 /2, 6 /4, 7 /8.
        string[] scaleTag = { "", "_x2", "_x4", "_x8", "", "_d2", "_d4", "_d8" };

        foreach (var i in instrs)
        {
            string mnem = i.Op + (i.Fp16 ? "H" : "R");
            if (i.Saturate) mnem += "_sat";
            if (i.Bx2) mnem += "_bx2";
            mnem += scaleTag[(i.W2 >> 28) & 0x7];

            string dst = i.NoDest ? "RC" : $"{(i.Fp16 ? "H" : "R")}{i.DestReg}";
            dst += MaskSuffix(i.WriteMask);

            int arity = Arity.GetValueOrDefault(i.Op, 3);
            var ops = new List<string> { dst };
            uint[] srcW = { i.W1, i.W2, i.W3 };
            for (int s = 0; s < arity && s < 3; s++)
                ops.Add(FormatSrc(srcW[s], s, i.W0, i.ConstIndex));

            if (TextureOps.Contains(i.Op))
                ops.Add($"tex{i.TexNum}");

            string line = $"  {idx,3}  {mnem,-10} {string.Join(", ", ops)}";

            if (i.Op is "KIL" or "IFE" or "REP" or "LOOP" or "BRK" or "RET" or "CAL")
                line = $"  {idx,3}  {mnem,-10} {CondStr(i.W1)}";

            if (i.Const != null)
                line += $"   ; c[{i.ConstIndex}] = {{{string.Join(", ", i.Const.Select(f => f.ToString("0.######")))}}}";

            if ((i.W0 & 1) != 0) line += "   ; END";

            lines.Add(line);
            idx++;
        }
        return lines;
    }

    static string FormatSrc(uint w, int slot, uint opdest, int constIndex)
    {
        int regType = (int)(w & 0x3);
        bool fp16 = ((w >> 8) & 1) != 0;
        int sx = (int)((w >> 9) & 3), sy = (int)((w >> 11) & 3), sz = (int)((w >> 13) & 3), sw = (int)((w >> 15) & 3);
        bool neg = ((w >> 17) & 1) != 0;
        // SRC0.abs is bit 29; SRC1/SRC2.abs is bit 18.
        bool abs = slot == 0 ? ((w >> 29) & 1) != 0 : ((w >> 18) & 1) != 0;

        string reg = regType switch
        {
            0 => $"{(fp16 ? "H" : "R")}{(int)((w >> 2) & 0x3F)}",
            1 => InputAttr(opdest),
            2 => $"c[{constIndex}]",
            _ => "?",
        };

        reg += SwizzleSuffix(sx, sy, sz, sw);
        if (abs) reg = $"|{reg}|";
        if (neg) reg = "-" + reg;
        return reg;
    }

    static string InputAttr(uint opdest)
    {
        int n = (int)((opdest >> 13) & 0xF);
        return n < InputRegs.Length ? $"f[{InputRegs[n]}]" : $"f[?{n}]";
    }

    static string MaskSuffix(int mask)
    {
        if (mask == 0xF || mask == 0) return "";
        var s = ".";
        if ((mask & 1) != 0) s += "x";
        if ((mask & 2) != 0) s += "y";
        if ((mask & 4) != 0) s += "z";
        if ((mask & 8) != 0) s += "w";
        return s;
    }

    static string SwizzleSuffix(int x, int y, int z, int w)
    {
        const string c = "xyzw";
        string s = $"{c[x]}{c[y]}{c[z]}{c[w]}";
        if (s == "xyzw") return "";
        if (s[0] == s[1] && s[1] == s[2] && s[2] == s[3]) return "." + s[0];
        return "." + s;
    }

    static string CondStr(uint src0)
    {
        bool lt = ((src0 >> 18) & 1) != 0, eq = ((src0 >> 19) & 1) != 0, gr = ((src0 >> 20) & 1) != 0;
        string c =
            gr && eq ? "GE" : lt && eq ? "LE" : gr && lt ? "NE" :
            gr ? "GT" : lt ? "LT" : eq ? "FL" : "TR";
        const string sw = "xyzw";
        string s = $"{sw[(int)((src0 >> 21) & 3)]}{sw[(int)((src0 >> 23) & 3)]}{sw[(int)((src0 >> 25) & 3)]}{sw[(int)((src0 >> 27) & 3)]}";
        if (s[0] == s[1] && s[1] == s[2] && s[2] == s[3]) s = s[0].ToString();
        return s == "xyzw" ? c : $"{c}.{s}";
    }

    static uint ReadU32Be(byte[] b, int o) =>
        (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

    static uint Swap16(uint d) => (d << 16) | (d >> 16);
}
