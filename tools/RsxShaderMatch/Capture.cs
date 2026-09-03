using System.Text.RegularExpressions;

namespace RsxShaderMatch;

/// <summary>
/// Fingerprint of one RPCS3 shader-log capture (<c>~/.cache/rpcs3/shaderlog/FragmentProgramN.spirv</c>
/// - plain GLSL despite the extension), reduced to the same shape
/// <see cref="RsxFp.Fingerprint"/> carries so the two can be compared.
///
/// RPCS3's fragment decompiler emits an almost line-per-instruction transliteration
/// inside <c>fs_main()</c>: RSX opcode names are kept (<c>TEX2D</c>, <c>fma</c> = MAD,
/// <c>_fetch_constant(N)</c> = constant register N, <c>_kill()</c> = KIL, <c>// FENCT</c>).
/// Two wrinkles this parser has to absorb:
///  - a single RSX <c>TEX</c> on a 1D sampler, or one with a partial write mask, can
///    expand to several GLSL <c>TEXxD(unit, ...)</c> lines; consecutive calls on the
///    same unit are collapsed for the unit-sequence key.
///  - <c>_fetch_constant</c> indices are assigned sequentially by the decompiler, so
///    the count of distinct indices tracks the microcode's inline-constant count.
/// </summary>
sealed class Capture
{
    public required string Name { get; init; }
    public required string Path { get; init; }

    public required IReadOnlyList<(string Type, int Unit)> Samplers { get; init; }
    public required IReadOnlyList<(string Call, int Unit)> TextureSequence { get; init; }
    public required IReadOnlyList<int> TextureUnits { get; init; }   // distinct, sorted
    public required IReadOnlyList<int> UnitRun { get; init; }        // ordered, consecutive dups collapsed
    public required int ConstCount { get; init; }
    public required bool HasKil { get; init; }
    public required bool HasFenc { get; init; }
    public required int BodyLines { get; init; }

    static readonly Regex FsMain = new(@"void\s+fs_main\s*\(\s*\)\s*\{(.*?)\n\}", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex SamplerDecl = new(@"uniform\s+(sampler\w+)\s+tex(\d+)\s*;", RegexOptions.Compiled);
    static readonly Regex TexCall = new(@"\b(TEX\w*)\((\d+)\s*,", RegexOptions.Compiled);
    static readonly Regex FetchConst = new(@"_fetch_constant\((\d+)\)", RegexOptions.Compiled);

    public static Capture? TryLoad(string path)
    {
        string text = File.ReadAllText(path);
        var m = FsMain.Match(text);
        if (!m.Success) return null;                 // interpreter-fallback / non-standard dump
        string body = m.Groups[1].Value;

        var samplers = SamplerDecl.Matches(text)
            .Select(x => (Type: x.Groups[1].Value, Unit: int.Parse(x.Groups[2].Value)))
            .OrderBy(s => s.Unit)
            .ToList();

        var texSeq = TexCall.Matches(body)
            .Select(x => (Call: x.Groups[1].Value, Unit: int.Parse(x.Groups[2].Value)))
            .ToList();

        var unitRun = new List<int>();
        foreach (var (_, unit) in texSeq)
            if (unitRun.Count == 0 || unitRun[^1] != unit)
                unitRun.Add(unit);

        var fetch = FetchConst.Matches(body).Select(x => int.Parse(x.Groups[1].Value)).ToList();
        int constCount = fetch.Count == 0 ? 0 : fetch.Distinct().Count();

        return new Capture
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(path),
            Path = path,
            Samplers = samplers,
            TextureSequence = texSeq,
            TextureUnits = texSeq.Select(t => t.Unit).Distinct().OrderBy(x => x).ToList(),
            UnitRun = unitRun,
            ConstCount = constCount,
            HasKil = body.Contains("_kill()"),
            HasFenc = body.Contains("// FENC"),
            BodyLines = body.Split('\n').Count(l => l.Trim().Length > 0),
        };
    }

    /// Build from decoded RSX fragment microcode (an .rrc draw) rather than shaderlog GLSL.
    /// The microcode carries the texture-unit run, inline-constant count and KIL/FENC, but
    /// not sampler types - those come from RSX texture-control state, not decoded here. The
    /// DeS unit conventions the fingerprint work established stand in: unit 11/13 = env
    /// cubemap, unit 7 = shadow map. Cruder than the GLSL path, but the unit-run and
    /// constant-count still carry the match, and for an .rrc draw the value is the constants.
    public static Capture FromRsxFp(RsxFp.Fingerprint fp)
    {
        var samplers = fp.TextureUnits
            .Select(u => (Type: u is 11 or 13 ? "samplerCube" : u == 7 ? "sampler2DShadow" : "sampler2D", Unit: u))
            .ToList();
        return new Capture
        {
            Name = "<rrc>",
            Path = "",
            Samplers = samplers,
            TextureSequence = fp.TextureSequence.Select(t => (Call: t.Op, t.Unit)).ToList(),
            TextureUnits = fp.TextureUnits.ToList(),
            UnitRun = fp.UnitRun.ToList(),
            ConstCount = fp.ConstCount,
            HasKil = fp.HasKil,
            HasFenc = fp.HasFenc,
            BodyLines = fp.InstrCount,
        };
    }

    public string UnitRunKey => string.Join(",", UnitRun);
    public string UnitSetKey => string.Join(",", TextureUnits);

    /// A session-independent identity for the shader this capture represents. RPCS3
    /// renumbers <c>FragmentProgramN</c> every session, so the file name is not a stable
    /// key - this is. Two captures with the same Signature are the same shader.
    public string Signature =>
        $"u={UnitRunKey};c={ConstCount};k={(HasKil ? 1 : 0)};f={(HasFenc ? 1 : 0)};" +
        $"s={string.Join("+", Samplers.Select(x => $"{x.Unit}{Abbrev(x.Type)}"))}";

    static string Abbrev(string t) => t.Replace("sampler", "").Replace("Shadow", "Shd");

    public bool HasShadowSampler => Samplers.Any(s => s.Type.Contains("Shadow"));
    public bool HasCubeSampler => Samplers.Any(s => s.Type.Contains("Cube"));
    public bool Has1dSampler => Samplers.Any(s => s.Type is "sampler1D");
    public bool Has3dSampler => Samplers.Any(s => s.Type is "sampler3D");

    /// Samplers that carry ordinary 2D material textures (diffuse/spec/bump/2nd-layer/lightmap) -
    /// i.e. every sampler that is not the shadow map or an environment cubemap.
    public int MaterialSamplerCount => Samplers.Count(s => !s.Type.Contains("Shadow") && !s.Type.Contains("Cube"));
}
