using System.Text.RegularExpressions;

namespace RsxShaderMatch;

/// <summary>
/// Fingerprint of one RPCS3 vertex-program capture
/// (<c>~/.cache/rpcs3/shaderlog/VertexProgramN.spirv</c> - plain GLSL despite the
/// extension), reduced to the shape <see cref="RsxVp.Fingerprint"/> carries.
///
/// RPCS3's vertex decompiler exposes everything the microcode fingerprint needs without
/// any name table:
///  - <c>read_location(N)</c> in <c>vs_main()</c> - N is the input-attribute index
///    (0 pos, 1 weight, 2 normal, 3 diffuse, 7 in_7, 8..15 tc0..tc7), so the set of N is
///    the vertex input mask.
///  - <c>dst_regN</c> declarations - N is the microcode's <c>D3.dst</c> output register.
///  - <c>_fetch_constant(K)</c> - K is the microcode's constant index; <c>_fetch_constant(K + a0.x)</c>
///    is relative (<c>D3.index_const</c>) addressing, the bone-matrix access pattern.
///  - <c>exp2(</c> / <c>sqrt(</c> / <c>dot(</c> / <c>texture(</c> counts inside <c>vs_main()</c>
///    stand in for the EX2 / RSQ / DP3-DP4 / TXL microcode ops.
/// </summary>
sealed class CaptureVp
{
    public required string Name { get; init; }
    public required string Path { get; init; }

    public required IReadOnlyList<int> InputRegs { get; init; }   // distinct, sorted read_location() indices
    public required IReadOnlyList<int> OutRegs { get; init; }      // distinct, sorted dst_reg indices
    public required IReadOnlyList<int> ConstRefs { get; init; }    // distinct, sorted _fetch_constant() bases
    public required bool IndexedConst { get; init; }
    public required int Ex2Count { get; init; }
    public required int RsqCount { get; init; }
    public required int DotCount { get; init; }
    public required bool HasTexture { get; init; }
    public required int BodyStatements { get; init; }             // ;-terminated lines in vs_main(), minus declarations

    static readonly Regex VsMain = new(@"void\s+vs_main\s*\(\s*\)\s*\{(.*?)\n\}", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex ReadLoc = new(@"read_location\((\d+)\)", RegexOptions.Compiled);
    static readonly Regex DstReg = new(@"\bdst_reg(\d+)\b", RegexOptions.Compiled);
    static readonly Regex FetchConst = new(@"_fetch_constant\(\s*(\d+)\s*(\+[^)]*)?\)", RegexOptions.Compiled);

    public static CaptureVp? TryLoad(string path)
    {
        string text = File.ReadAllText(path);
        var m = VsMain.Match(text);
        if (!m.Success) return null;
        string body = m.Groups[1].Value;

        // dst_reg declarations sit just above vs_main(); scan the whole file for them.
        var outRegs = DstReg.Matches(text).Select(x => int.Parse(x.Groups[1].Value)).Distinct().OrderBy(x => x).ToList();
        var inputRegs = ReadLoc.Matches(body).Select(x => int.Parse(x.Groups[1].Value)).Distinct().OrderBy(x => x).ToList();

        var fc = FetchConst.Matches(body).ToList();
        var constRefs = fc.Select(x => int.Parse(x.Groups[1].Value)).Distinct().OrderBy(x => x).ToList();
        bool indexed = fc.Any(x => x.Groups[2].Success);

        int Count(string needle) => Regex.Matches(body, Regex.Escape(needle)).Count;

        // Statement lines that are real work, not the `vec4 in_x = read_location();` prologue
        // or the `vec4 rN = vec4(0.);` temp declarations.
        int stmts = body.Split('\n')
            .Count(l =>
            {
                l = l.Trim();
                return l.EndsWith(";") && !l.StartsWith("vec4 ") && !l.StartsWith("ivec4 ") && !l.StartsWith("uint ");
            });

        return new CaptureVp
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(path),
            Path = path,
            InputRegs = inputRegs,
            OutRegs = outRegs,
            ConstRefs = constRefs,
            IndexedConst = indexed,
            Ex2Count = Count("exp2("),
            RsqCount = Count("sqrt("),
            DotCount = Count("dot("),
            HasTexture = body.Contains("texture(") || body.Contains("textureLod("),
            BodyStatements = stmts,
        };
    }

    /// Build from decoded RSX vertex microcode (an .rrc draw) rather than shaderlog GLSL.
    /// Everything ScoreVp needs is in the microcode fingerprint directly, and InstrCount is
    /// exact here (better than the GLSL statement-count estimate).
    public static CaptureVp FromRsxVp(RsxVp.Fingerprint fp) => new()
    {
        Name = "<rrc>",
        Path = "",
        InputRegs = fp.InputRegs.ToList(),
        OutRegs = fp.OutRegs.ToList(),
        ConstRefs = fp.ConstRefs.ToList(),
        IndexedConst = fp.IndexedConst,
        Ex2Count = fp.Ex2Count,
        RsqCount = fp.RsqCount,
        DotCount = fp.DotCount,
        HasTexture = fp.HasTxl,
        BodyStatements = fp.InstrCount,
    };

    public string InputKey => string.Join(",", InputRegs);
    public string OutKey => string.Join(",", OutRegs);
    public string ConstKey => string.Join(",", ConstRefs);

    /// Session-independent identity - RPCS3 renumbers VertexProgramN every capture session.
    public string Signature =>
        $"in={InputKey};out={OutKey};c={ConstKey};idx={(IndexedConst ? 1 : 0)};ex2={Ex2Count}";
}
