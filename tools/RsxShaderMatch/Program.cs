using RsxShaderMatch;

// Standalone research tool for naming the anonymous RPCS3 shader-log captures by
// matching them against Demon's Souls' own named shader library. See README.md.
// Fragment programs are the default; --vertex (on match/verify/coverage) runs the
// parallel vertex-program pass (separate ISA, see RsxVp.cs).

if (args.Length == 0)
{
    Console.Error.WriteLine("""
        usage:
          rsxshadermatch dump   <file.fpo|file.vpo>            inspect one program's container
          rsxshadermatch fp     <file.fpo>                     decode fragment microcode + print fingerprint
          rsxshadermatch vp     <file.vpo>                     decode vertex microcode + print fingerprint
          rsxshadermatch disasm <file.fpo|file.vpo>            readable RSX assembly listing (kind auto-detected)
          rsxshadermatch rrc    <file.rrc.gz>                  summarise an RPCS3 frame capture (method histogram)
          rsxshadermatch rrc-fog <file.rrc.gz>                 RSX fixed-function fog state per draw + FOG_BANK invert
          rsxshadermatch rrc-draws <file.rrc.gz> <lib-dir> [--only=N] [--grep=substr]
                                                               name/print each draw's bound FP/VP + dump vertex constants
          rsxshadermatch rrc-tex <file.rrc.gz> <lib-dir> [--grep=substr] [--only=N]
                                                               per-draw fragment-texture-unit state (format/address/gamma)
          rsxshadermatch rrc-mine <file.rrc.gz> <lib-dir> [--json=out.json]
                                                               per-pass constant + render-state census (shadow/depth data)
          rsxshadermatch rrc-shadow <file.rrc.gz> <lib-dir>    shadow atlas tiles, cast/receive matrices, cascade layout
          rsxshadermatch sweep  <dir> [dir ...]                parse every .fpo/.vpo under dir(s), report anomalies
          rsxshadermatch match  <lib-dir> <shaderlog-dir> [--vertex] [--only=ProgramN] [--json=out.json]
                                                               rank library shaders against capture(s)
          rsxshadermatch verify <lib-dir> <shaderlog-dir> [--vertex]    re-check the known-good pairs, exit 1 on regression
          rsxshadermatch coverage <lib-dir> <shaderlog-dir> [--vertex]  invert the match: what is still uncaptured
        """);
    return 1;
}

switch (args[0])
{
    case "dump":
        if (args.Length != 2) { Console.Error.WriteLine("dump takes exactly one path"); return 1; }
        return Dump(args[1]);

    case "fp":
        if (args.Length != 2) { Console.Error.WriteLine("fp takes exactly one path"); return 1; }
        return Fp(args[1]);

    case "vp":
        if (args.Length != 2) { Console.Error.WriteLine("vp takes exactly one path"); return 1; }
        return VpInspect(args[1]);

    case "rrc":
        if (args.Length != 2) { Console.Error.WriteLine("rrc takes exactly one path (a .rrc.gz frame capture)"); return 1; }
        return RrcCapture.Summarize(args[1]);

    case "rrc-fog":
        if (args.Length != 2) { Console.Error.WriteLine("rrc-fog takes exactly one path (a .rrc.gz frame capture)"); return 1; }
        return RrcFog(args[1]);

    case "rrc-draws":
    {
        if (args.Length < 3) { Console.Error.WriteLine("rrc-draws <capture.rrc.gz> <lib-dir> [--only=N] [--grep=substr]"); return 1; }
        var rest = args.Skip(3).ToArray();
        int? only = int.TryParse(rest.FirstOrDefault(a => a.StartsWith("--only=", StringComparison.Ordinal))?["--only=".Length..], out int o) ? o : null;
        string? grep = rest.FirstOrDefault(a => a.StartsWith("--grep=", StringComparison.Ordinal))?["--grep=".Length..];
        return RrcDraws(args[1], args[2], only, grep);
    }

    case "rrc-tex":
    {
        if (args.Length < 3) { Console.Error.WriteLine("rrc-tex <capture.rrc.gz> <lib-dir> [--grep=substr] [--only=N]"); return 1; }
        var rest = args.Skip(3).ToArray();
        string? grep = rest.FirstOrDefault(a => a.StartsWith("--grep=", StringComparison.Ordinal))?["--grep=".Length..];
        int? only = int.TryParse(rest.FirstOrDefault(a => a.StartsWith("--only=", StringComparison.Ordinal))?["--only=".Length..], out int to) ? to : null;
        return RrcTex(args[1], args[2], grep, only);
    }

    case "rrc-mine":
    {
        if (args.Length < 3) { Console.Error.WriteLine("rrc-mine <capture.rrc.gz> <lib-dir> [--json=out.json] [--vpc]"); return 1; }
        string? json = args.Skip(3).FirstOrDefault(a => a.StartsWith("--json=", StringComparison.Ordinal))?["--json=".Length..];
        bool vpc = args.Skip(3).Any(a => a == "--vpc");
        return RrcMine(args[1], args[2], json, vpc);
    }

    case "rrc-shadow":
        if (args.Length != 3) { Console.Error.WriteLine("rrc-shadow <capture.rrc.gz> <lib-dir>"); return 1; }
        return RrcShadow(args[1], args[2]);

    case "disasm":
        if (args.Length != 2) { Console.Error.WriteLine("disasm takes exactly one path"); return 1; }
        return Disasm(args[1]);

    case "match":
    {
        if (args.Length < 3) { Console.Error.WriteLine("match takes <lib-dir> <shaderlog-dir> [--vertex] [--only=...] [--json=...]"); return 1; }
        var rest = args.Skip(3).ToArray();
        string? only = rest.FirstOrDefault(a => a.StartsWith("--only=", StringComparison.Ordinal))?["--only=".Length..];
        string? json = rest.FirstOrDefault(a => a.StartsWith("--json=", StringComparison.Ordinal))?["--json=".Length..];
        return rest.Contains("--vertex")
            ? MatchVp(args[1], args[2], only, json)
            : Match(args[1], args[2], only, json);
    }

    case "verify":
        if (args.Length < 3) { Console.Error.WriteLine("verify takes <lib-dir> <shaderlog-dir> [--vertex]"); return 1; }
        return args.Skip(3).Contains("--vertex") ? VerifyVp(args[1], args[2]) : Verify(args[1], args[2]);

    case "coverage":
        if (args.Length < 3) { Console.Error.WriteLine("coverage takes <lib-dir> <shaderlog-dir> [--vertex]"); return 1; }
        return args.Skip(3).Contains("--vertex") ? CoverageVp(args[1], args[2]) : Coverage(args[1], args[2]);

    case "sweep":
        if (args.Length < 2) { Console.Error.WriteLine("sweep takes at least one directory"); return 1; }
        return Sweep(args[1..]);

    default:
        Console.Error.WriteLine($"unknown command '{args[0]}'");
        return 1;
}

static int Dump(string path)
{
    CgProgram p;
    try { p = CgProgram.Load(path); }
    catch (Exception e) { Console.Error.WriteLine($"parse failed: {e.Message}"); return 1; }

    Console.WriteLine($"name              {p.Name}");
    Console.WriteLine($"kind              {p.Kind}  (profile {p.Profile})");
    Console.WriteLine($"file length       {p.FileLength} (0x{p.FileLength:X})");
    Console.WriteLine($"magic             0x{p.Magic:X8}");
    Console.WriteLine($"cg offset         0x{p.CgOffset:X}");
    Console.WriteLine($"binary revision   {p.BinaryFormatRevision}");
    Console.WriteLine($"total size        {p.TotalSize} (0x{p.TotalSize:X})");
    Console.WriteLine($"parameter count   {p.ParameterCount}");
    Console.WriteLine($"instruction count {p.InstructionCount}");
    Console.WriteLine($"ucode             {p.UcodeSize} bytes @ file 0x{p.UcodeFileOffset:X}");

    if (p.Kind == CgProgram.ShaderKind.Fragment)
    {
        Console.WriteLine($"fp texcoord in    0x{p.FpTexCoordsInputMask:X4}   {MaskBits(p.FpTexCoordsInputMask)}");
        Console.WriteLine($"fp texcoord 2d    0x{p.FpTexCoords2D:X4}   {MaskBits(p.FpTexCoords2D)}");
        Console.WriteLine($"fp texcoord cent  0x{p.FpTexCoordsCentroid:X4}");
        Console.WriteLine($"fp register count {p.FpRegisterCount}");
        Console.WriteLine($"fp output from H0 {p.FpOutputFromH0}");
        Console.WriteLine($"fp depth replace  {p.FpDepthReplace}");
        Console.WriteLine($"fp pixel kill     {p.FpPixelKill}");
    }
    else
    {
        Console.WriteLine($"vp instr slot     {p.VpInstructionSlot}");
        Console.WriteLine($"vp register count {p.VpRegisterCount}");
        Console.WriteLine($"vp attr in  mask  0x{p.VpAttributeInputMask:X8}");
        Console.WriteLine($"vp attr out mask  0x{p.VpAttributeOutputMask:X8}");
        Console.WriteLine($"vp user clip mask 0x{p.VpUserClipMask:X8}");
    }

    Console.WriteLine($"ucode head        {BitConverter.ToString(p.Ucode, 0, Math.Min(32, p.Ucode.Length))}");

    if (p.Warnings.Count > 0)
    {
        Console.WriteLine("warnings:");
        foreach (var w in p.Warnings) Console.WriteLine($"  - {w}");
    }
    return 0;
}

static int Fp(string path)
{
    CgProgram p;
    try { p = CgProgram.Load(path); }
    catch (Exception e) { Console.Error.WriteLine($"parse failed: {e.Message}"); return 1; }

    if (p.Kind != CgProgram.ShaderKind.Fragment)
    {
        Console.Error.WriteLine($"{p.Name} is a {p.Kind} program; fp decodes fragment programs only");
        return 1;
    }

    var (instrs, fp) = RsxFp.Decode(p.Ucode);

    Console.WriteLine($"name              {p.Name}");
    Console.WriteLine($"header instr count {p.InstructionCount}   decoded slots {fp.SlotCount}   real instrs {fp.InstrCount}");
    if (fp.SlotCount != p.InstructionCount)
        Console.WriteLine($"  !! decoded slot count != header instructionCount");
    Console.WriteLine($"inline constants   {fp.ConstCount}");
    Console.WriteLine($"has KIL            {fp.HasKil}");
    Console.WriteLine($"has FENC           {fp.HasFenc}");
    Console.WriteLine($"texture sequence   {(fp.TextureSequence.Count == 0 ? "(none)" : fp.TexKey)}");
    Console.WriteLine($"texture units      {(fp.TextureUnits.Count == 0 ? "(none)" : fp.UnitKey)}");
    Console.WriteLine($"input attrs        {InputMaskStr(fp.InputMask)}");
    Console.WriteLine($"op histogram       {string.Join("  ", fp.OpHistogram.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value}"))}");
    Console.WriteLine();
    Console.WriteLine("instructions:");
    int idx = 0;
    foreach (var i in instrs)
    {
        string reg = $"{(i.Fp16 ? "H" : "R")}{i.DestReg}";
        string tex = RsxFpIsTex(i.Op) ? $" tex{i.TexNum}" : "";
        string cst = i.HasConst ? " +const" : "";
        Console.WriteLine($"  {idx,3}  {i.Op,-6} {reg}{MaskStr(i.WriteMask)}{tex}{cst}");
        idx++;
    }
    return 0;
}

static int Disasm(string path)
{
    CgProgram p;
    try { p = CgProgram.Load(path); }
    catch (Exception e) { Console.Error.WriteLine($"parse failed: {e.Message}"); return 1; }

    if (p.Kind == CgProgram.ShaderKind.Vertex)
    {
        var (_, vfp) = RsxVp.Decode(p.Ucode);
        Console.WriteLine($"; {p.Name}");
        Console.WriteLine($"; {vfp.InstrCount} instructions, inputs [{vfp.InputKey}], outputs [{vfp.OutKey}]");
        Console.WriteLine($"; consts [{vfp.ConstKey}]  indexed={vfp.IndexedConst}  ex2={vfp.Ex2Count} rsq={vfp.RsqCount} dot={vfp.DotCount} txl={vfp.HasTxl}");
        Console.WriteLine($"; o[n] = output register n (GLSL dst_reg<n>), c[n] = constant slot n, c[A + n] = relative");
        Console.WriteLine();
        foreach (var line in RsxVp.Disassemble(p.Ucode))
            Console.WriteLine(line);
        return 0;
    }

    var (_, fp) = RsxFp.Decode(p.Ucode);
    Console.WriteLine($"; {p.Name}");
    Console.WriteLine($"; {fp.InstrCount} instructions, {fp.ConstCount} inline constants, " +
                      $"samplers [{fp.UnitRunKey}], kil={fp.HasKil}, fenc={fp.HasFenc}");
    Console.WriteLine($"; f[...] = input attribute, c[n] = constant slot n (patched per-draw; value shown is the shipped default)");
    Console.WriteLine();
    foreach (var line in RsxFp.Disassemble(p.Ucode))
        Console.WriteLine(line);
    return 0;
}

static int VpInspect(string path)
{
    CgProgram p;
    try { p = CgProgram.Load(path); }
    catch (Exception e) { Console.Error.WriteLine($"parse failed: {e.Message}"); return 1; }

    if (p.Kind != CgProgram.ShaderKind.Vertex)
    {
        Console.Error.WriteLine($"{p.Name} is a {p.Kind} program; vp decodes vertex programs only");
        return 1;
    }

    var (instrs, fp) = RsxVp.Decode(p.Ucode);
    var nm = LibVpName.Parse(p.Name);

    Console.WriteLine($"name               {p.Name}");
    Console.WriteLine($"header instr count  {p.InstructionCount}   decoded {fp.InstrCount}");
    if (fp.InstrCount != p.InstructionCount)
        Console.WriteLine($"  !! decoded instr count != header instructionCount");
    if (fp.HasBranch) Console.WriteLine($"  !! branch instruction present - decode past it is unreliable");
    Console.WriteLine($"name features       family={nm.Family} skinned={nm.Skinned} tangentSets={nm.TangentSets} " +
                      $"diffUv={nm.DiffuseUvSets} lightmapUv={nm.HasLightmapUv} pass={nm.Pass}");
    Console.WriteLine($"attr input mask     0x{p.VpAttributeInputMask:X8}   [{MaskBits((ushort)p.VpAttributeInputMask)}]");
    Console.WriteLine($"input regs (ucode)  [{fp.InputKey}]");
    Console.WriteLine($"output regs         [{fp.OutKey}]");
    Console.WriteLine($"const refs          [{fp.ConstKey}]   indexed={fp.IndexedConst}");
    Console.WriteLine($"ex2={fp.Ex2Count}  rsq={fp.RsqCount}  rcp={fp.RcpCount}  dot={fp.DotCount}  txl={fp.HasTxl}");
    Console.WriteLine($"vec histogram       {string.Join("  ", fp.VecHistogram.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value}"))}");
    Console.WriteLine($"sca histogram       {string.Join("  ", fp.ScaHistogram.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value}"))}");
    Console.WriteLine();
    foreach (var line in RsxVp.Disassemble(p.Ucode))
        Console.WriteLine(line);
    return 0;
}

static bool RsxFpIsTex(string op) =>
    op is "TEX" or "TXP" or "TXD" or "TXL" or "TXB" or "TEXBEM" or "TXPBEM" or "BEMLUM" or "TIMESWTEX";

static string MaskStr(int mask)
{
    if (mask == 0xF || mask == 0) return "";
    var s = "";
    if ((mask & 1) != 0) s += "x";
    if ((mask & 2) != 0) s += "y";
    if ((mask & 4) != 0) s += "z";
    if ((mask & 8) != 0) s += "w";
    return "." + s;
}

static List<LibFp> LoadLibrary(string libDir)
{
    var lib = new List<LibFp>();
    foreach (var f in Directory.EnumerateFiles(libDir, "*.fpo", SearchOption.AllDirectories))
    {
        CgProgram p;
        try { p = CgProgram.Load(f); } catch { continue; }
        if (p.Kind != CgProgram.ShaderKind.Fragment) continue;
        var (_, fp) = RsxFp.Decode(p.Ucode);
        lib.Add(new LibFp(p.Name, LibName.Parse(p.Name), fp, (int)p.InstructionCount));
    }
    // Directory enumeration order is not guaranteed; sort so ties in scoring resolve
    // the same way every run.
    lib.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
    return lib;
}

static List<Capture> LoadCaptures(string logDir, string? only)
{
    var caps = new List<Capture>();
    foreach (var f in Directory.EnumerateFiles(logDir, "*.spirv", SearchOption.TopDirectoryOnly))
    {
        if (!System.IO.Path.GetFileName(f).StartsWith("FragmentProgram", StringComparison.Ordinal)) continue;
        if (only != null && System.IO.Path.GetFileNameWithoutExtension(f) != only) continue;
        var c = Capture.TryLoad(f);
        if (c != null) caps.Add(c);
    }
    caps.Sort((a, b) => NumericSuffix(a.Name).CompareTo(NumericSuffix(b.Name)));
    return caps;
}

static int Verify(string libDir, string logDir)
{
    if (!Directory.Exists(libDir) || !Directory.Exists(logDir)) { Console.Error.WriteLine("bad dir"); return 1; }
    var lib = LoadLibrary(libDir);
    var caps = LoadCaptures(logDir, null);

    // Structurally hand-verified pairs (see README), anchored on the capture's
    // session-independent fingerprint - NOT its FragmentProgramN number, which RPCS3
    // reassigns every session. Each anchor is (unit-run, const, fenc) -> expected name.
    var anchors = new (string Run, int Const, bool Fenc, string Want, string Note)[]
    {
        ("11,3,7,0,6", 41, true,  "DS_Phn_Dif______MulLitCsd_HemEnv",       "part-18 HemEnv reference"),
        ("2,7,1,0",    62, true,  "DS_Phn_DifSpcBmp______Csd_HemDir3",      "part-18 HemDir3"),
        ("7,0,1",      46, true,  "DS_Phn_DifSpc_________Sdw_HemDir3PntS",  "1 point light, const 46 vs 39"),
    };

    int bad = 0, missing = 0;
    foreach (var (run, cst, fenc, want, note) in anchors)
    {
        var cap = caps.FirstOrDefault(c => c.UnitRunKey == run && c.ConstCount == cst && c.HasFenc == fenc);
        if (cap == null) { missing++; Console.WriteLine($"MISS  [{run}] c={cst} - no capture with this fingerprint ({note})"); continue; }
        var best = lib.Select(l => (l, s: Score(l, cap)))
            .OrderByDescending(x => x.s.Total).ThenBy(x => x.l.Name, StringComparer.Ordinal).First();
        bool ok = best.l.Name == want;
        if (!ok) bad++;
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {cap.Name,-20} [{run}] -> {best.l.Name}  (want {want}, score {best.s.Total}; {note})");
    }
    if (missing > 0) Console.WriteLine($"({missing} anchor fingerprint(s) not present in this shader-log)");
    return bad == 0 ? 0 : 1;
}

static string JsonEsc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

// Invert the match: for every library fragment program, has any capture landed on it?
// Tells us the shape of what is still uncaptured - the user's "process of elimination".
static int Coverage(string libDir, string logDir)
{
    if (!Directory.Exists(libDir) || !Directory.Exists(logDir)) { Console.Error.WriteLine("bad dir"); return 1; }
    var lib = LoadLibrary(libDir);
    var caps = LoadCaptures(logDir, null);

    // Best score any capture gives each library shader, and whether it is that capture's own top pick.
    var bestScore = new Dictionary<string, int>();
    var isTopFor = new HashSet<string>();
    foreach (var cap in caps)
    {
        var ranked = lib.Select(l => (l, s: Score(l, cap).Total))
            .OrderByDescending(x => x.s).ThenBy(x => x.l.Name, StringComparer.Ordinal).ToList();
        if (ranked[0].s >= 155) isTopFor.Add(ranked[0].l.Name);
        foreach (var (l, s) in ranked)
            if (!bestScore.TryGetValue(l.Name, out int cur) || s > cur) bestScore[l.Name] = s;
    }

    string Bucket(LibFp l) =>
        isTopFor.Contains(l.Name) ? "captured" :
        bestScore.GetValueOrDefault(l.Name) >= 120 ? "near" :
        "unseen";

    var buckets = new Dictionary<string, List<LibFp>> { ["captured"] = new(), ["near"] = new(), ["unseen"] = new() };
    foreach (var l in lib) buckets[Bucket(l)].Add(l);

    Console.WriteLine($"library {lib.Count} fragment programs, {caps.Count} captures");
    foreach (var k in new[] { "captured", "near", "unseen" })
        Console.WriteLine($"  {k,-9} {buckets[k].Count}");
    Console.WriteLine();

    // What does the unseen set look like, by lighting model + shadow + point lights?
    Console.WriteLine("unseen, grouped:");
    var groups = buckets["unseen"]
        .GroupBy(l => $"{l.Features.Family}/{l.Features.Lighting}" +
                      (l.Features.PointLights > 0 ? $"+Pnt{new string('S', l.Features.PointLights)}" : "") +
                      (l.Features.Shadow != LibName.ShadowKind.None ? $"/{l.Features.Shadow}" : ""))
        .OrderByDescending(g => g.Count());
    foreach (var g in groups)
        Console.WriteLine($"  {g.Count(),3}  {g.Key}");

    return 0;
}

static int Match(string libDir, string logDir, string? only, string? jsonOut)
{
    if (!Directory.Exists(libDir)) { Console.Error.WriteLine($"not a directory: {libDir}"); return 1; }
    if (!Directory.Exists(logDir)) { Console.Error.WriteLine($"not a directory: {logDir}"); return 1; }

    var lib = LoadLibrary(libDir);
    Console.Error.WriteLine($"library: {lib.Count} fragment programs decoded");
    var caps = LoadCaptures(logDir, only);
    Console.Error.WriteLine($"captures: {caps.Count} parsed");
    Console.WriteLine();

    var jsonRows = new List<string>();
    int nHigh = 0, nMed = 0, nLow = 0, nNone = 0;

    foreach (var cap in caps)
    {
        var ranked = lib
            .Select(l => (l, s: Score(l, cap)))
            .OrderByDescending(x => x.s.Total)
            .ThenBy(x => x.l.Name, StringComparer.Ordinal)
            .Take(5)
            .ToList();

        int top = ranked[0].s.Total;
        int gap = ranked.Count > 1 ? top - ranked[1].s.Total : top;
        string tier =
            top < 60 ? "NONE" :
            top >= 155 && gap >= 18 ? "HIGH" :
            top >= 120 && gap >= 8 ? "MED" :
            "LOW";
        switch (tier) { case "HIGH": nHigh++; break; case "MED": nMed++; break; case "LOW": nLow++; break; default: nNone++; break; }

        string samp = string.Join(",", cap.Samplers.Select(s => $"{s.Unit}:{Abbrev(s.Type)}"));
        Console.WriteLine($"{cap.Name,-20} {tier,-4} units[{cap.UnitRunKey}] samp[{samp}] const={cap.ConstCount} kil={cap.HasKil} fenc={cap.HasFenc}");
        foreach (var (l, s) in ranked)
            Console.WriteLine($"    {s.Total,4}  {l.Name,-42} units[{l.Fp.UnitRunKey}] const={l.Fp.ConstCount} kil={l.Fp.HasKil} fenc={l.Fp.HasFenc}  ({s.Detail})");
        Console.WriteLine();

        string cands = string.Join(", ", ranked.Where(r => r.s.Total >= top - 4)
            .Select(r => $"{{\"name\":\"{r.l.Name}\",\"score\":{r.s.Total}}}"));
        jsonRows.Add(
            $"  {{\"capture\":\"{cap.Name}\",\"fingerprint\":\"{JsonEsc(cap.Signature)}\",\"tier\":\"{tier}\"," +
            $"\"best\":\"{ranked[0].l.Name}\",\"score\":{top},\"gap\":{gap},\"candidates\":[{cands}]}}");
    }

    Console.WriteLine($"tiers: HIGH {nHigh}  MED {nMed}  LOW {nLow}  NONE {nNone}   (of {caps.Count})");

    if (jsonOut != null)
    {
        File.WriteAllText(jsonOut, "[\n" + string.Join(",\n", jsonRows) + "\n]\n");
        Console.Error.WriteLine($"wrote {jsonOut}");
    }
    return 0;
}

static int NumericSuffix(string name)
{
    int i = name.Length;
    while (i > 0 && char.IsDigit(name[i - 1])) i--;
    return i < name.Length ? int.Parse(name[i..]) : 0;
}

static (int Total, string Detail) Score(LibFp lib, Capture cap)
{
    var fp = lib.Fp;
    var nm = lib.Features;
    int score = 0;
    var parts = new List<string>();

    // --- texture-unit sequence ---
    if (fp.UnitRunKey == cap.UnitRunKey && cap.UnitRun.Count > 0)
    {
        score += 90; parts.Add("run=");
    }
    else
    {
        var ls = fp.TextureUnits.ToHashSet();
        var cs = cap.TextureUnits.ToHashSet();
        int inter = ls.Intersect(cs).Count();
        int diff = ls.Except(cs).Count() + cs.Except(ls).Count();
        if (ls.SetEquals(cs) && cs.Count > 0) { score += 48; parts.Add("set="); }
        else { int u = inter * 8 - diff * 7; score += u; parts.Add($"u{(u >= 0 ? "+" : "")}{u}"); }
    }

    // --- sampler-type agreement (family discriminators) ---
    if (cap.HasCubeSampler == nm.WantsCubeSampler) { score += 22; }
    else { score -= 32; parts.Add(cap.HasCubeSampler ? "!cube" : "!nocube"); }

    if (cap.HasShadowSampler == nm.WantsShadowSampler) { score += 22; }
    else { score -= 32; parts.Add(cap.HasShadowSampler ? "!shadow" : "!noshadow"); }

    // --- inline-constant count: the point-light / shadow-variant discriminator ---
    int cd = Math.Abs(fp.ConstCount - cap.ConstCount);
    if (cd == 0) { score += 45; parts.Add("const=="); }
    else { int c = Math.Max(0, 35 - 4 * cd); score += c; parts.Add($"constd{cd}"); }

    // --- flags ---
    if (fp.HasFenc == cap.HasFenc) score += 12; else { score -= 8; parts.Add("!fenc"); }
    if (fp.HasKil == cap.HasKil) score += 12; else { score -= 8; parts.Add("!kil"); }

    // --- material-texture count implied by the filename vs samplers seen ---
    if (cap.MaterialSamplerCount > 0)
    {
        int md = Math.Abs(nm.MaterialTextureCount - cap.MaterialSamplerCount);
        if (md == 0) { score += 12; }
        else if (md == 1) { score += 3; }
        else { score -= 4 * (md - 1); parts.Add($"nmat{md}"); }
    }

    // Debug/menu/sfx families are not expected in a gameplay capture.
    if (nm.Family is "Dbg" or "Menu" or "Sfx") { score -= 15; parts.Add("dbg"); }

    return (score, string.Join(" ", parts));
}

static string Abbrev(string samplerType) => samplerType
    .Replace("sampler", "")
    .Replace("Shadow", "Shd");

static int Sweep(string[] dirs)
{
    var files = new List<string>();
    foreach (var dir in dirs)
    {
        if (!Directory.Exists(dir)) { Console.Error.WriteLine($"not a directory: {dir}"); return 1; }
        files.AddRange(Directory.EnumerateFiles(dir, "*.fpo", SearchOption.AllDirectories));
        files.AddRange(Directory.EnumerateFiles(dir, "*.vpo", SearchOption.AllDirectories));
    }
    files.Sort(StringComparer.Ordinal);

    int fp = 0, vp = 0, failed = 0, warned = 0;
    var failures = new List<string>();
    var anomalies = new List<string>();
    var instrHistogram = new SortedDictionary<uint, int>();

    foreach (var f in files)
    {
        CgProgram p;
        try { p = CgProgram.Load(f); }
        catch (Exception e) { failed++; failures.Add($"{Rel(f)}: {e.Message}"); continue; }

        if (p.Kind == CgProgram.ShaderKind.Fragment) fp++; else vp++;

        instrHistogram.TryGetValue(p.InstructionCount, out int n);
        instrHistogram[p.InstructionCount] = n + 1;

        if (p.Warnings.Count > 0)
        {
            warned++;
            foreach (var w in p.Warnings) anomalies.Add($"{Rel(f)}: {w}");
        }
    }

    Console.WriteLine($"scanned {files.Count} files: {fp} fragment, {vp} vertex, {failed} unparseable, {warned} with warnings");
    Console.WriteLine();

    if (failures.Count > 0)
    {
        Console.WriteLine($"UNPARSEABLE ({failures.Count}):");
        foreach (var s in failures) Console.WriteLine($"  {s}");
        Console.WriteLine();
    }
    if (anomalies.Count > 0)
    {
        Console.WriteLine($"INVARIANT WARNINGS ({anomalies.Count}):");
        foreach (var s in anomalies) Console.WriteLine($"  {s}");
        Console.WriteLine();
    }

    Console.WriteLine("instruction-count distribution:");
    foreach (var (count, num) in instrHistogram)
        Console.WriteLine($"  {count,4} instr : {num} program(s)");

    return failed > 0 ? 1 : 0;
}

// ===== vertex-program pass (--vertex) ==========================================
// Parallel to Match/Verify/Coverage above. The vertex ISA has fewer hard structural
// discriminators than the fragment side (no sampler types), so the weight sits on the
// three sets that survive name stripping intact: input attributes, output registers,
// and referenced constant indices.

static List<LibVp> LoadVpLibrary(string libDir)
{
    var lib = new List<LibVp>();
    foreach (var f in Directory.EnumerateFiles(libDir, "*.vpo", SearchOption.AllDirectories))
    {
        CgProgram p;
        try { p = CgProgram.Load(f); } catch { continue; }
        if (p.Kind != CgProgram.ShaderKind.Vertex) continue;
        var (_, fp) = RsxVp.Decode(p.Ucode);
        lib.Add(new LibVp(p.Name, LibVpName.Parse(p.Name), fp, (int)p.InstructionCount, p.VpAttributeInputMask));
    }
    lib.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
    return lib;
}

static List<CaptureVp> LoadVpCaptures(string logDir, string? only)
{
    var caps = new List<CaptureVp>();
    foreach (var f in Directory.EnumerateFiles(logDir, "*.spirv", SearchOption.TopDirectoryOnly))
    {
        if (!System.IO.Path.GetFileName(f).StartsWith("VertexProgram", StringComparison.Ordinal)) continue;
        if (only != null && System.IO.Path.GetFileNameWithoutExtension(f) != only) continue;
        var c = CaptureVp.TryLoad(f);
        if (c != null) caps.Add(c);
    }
    caps.Sort((a, b) => NumericSuffix(a.Name).CompareTo(NumericSuffix(b.Name)));
    return caps;
}

static int MaskToList(uint mask, List<int> into)
{
    into.Clear();
    for (int i = 0; i < 32; i++) if ((mask & (1u << i)) != 0) into.Add(i);
    return into.Count;
}

static double Jaccard(IReadOnlyList<int> a, IReadOnlyList<int> b)
{
    if (a.Count == 0 && b.Count == 0) return 1.0;
    var sa = a.ToHashSet();
    var sb = b.ToHashSet();
    int inter = sa.Intersect(sb).Count();
    int union = sa.Count + sb.Count - inter;
    return union == 0 ? 1.0 : (double)inter / union;
}

// Phn and Gst ship as separate filenames for byte-identical vertex programs (the ghost
// effect is entirely fragment-side, confirmed by an exact disasm diff). Collapsing the
// family token gives the shader's real identity - two candidates that reduce to the same
// canonical name are not a genuine ambiguity.
static string VpCanonical(string name) =>
    name.Replace("DS_Phn_", "DS_*_").Replace("DS_Gst_", "DS_*_").Replace("DS_Ghost_", "DS_*_");

static (int Total, string Detail) ScoreVp(LibVp lib, CaptureVp cap)
{
    var fp = lib.Fp;
    var nm = lib.Features;
    int score = 0;
    var parts = new List<string>();

    // --- input attributes: header mask (lib ground truth) vs read_location() set (capture) ---
    var libIn = new List<int>();
    MaskToList(lib.AttrInMask, libIn);
    if (libIn.SequenceEqual(cap.InputRegs)) { score += 110; parts.Add("in="); }
    else
    {
        double j = Jaccard(libIn, cap.InputRegs);
        score += (int)(80 * j) - 25;
        parts.Add($"inJ{j:0.00}");
        // skinning (in_weight = bit 1) is a hard split - a skinned lib vs unskinned capture is wrong.
        if (libIn.Contains(1) != cap.InputRegs.Contains(1)) { score -= 45; parts.Add("!skin"); }
    }

    // --- referenced constant indices: the D / DD / DL texgen discriminator lives here
    // (c120 alone / c120+c121 / c120+c122), so an exact set match is weighted heavily and a
    // near miss falls off fast. ---
    if (fp.ConstRefs.SequenceEqual(cap.ConstRefs) && cap.ConstRefs.Count > 0) { score += 110; parts.Add("c="); }
    else
    {
        double j = Jaccard(fp.ConstRefs, cap.ConstRefs);
        score += (int)(120 * j * j) - 20;
        parts.Add($"cJ{j:0.00}");
    }

    // --- output registers (dst_reg indices) ---
    if (fp.OutRegs.SequenceEqual(cap.OutRegs) && cap.OutRegs.Count > 0) { score += 45; parts.Add("out="); }
    else { double j = Jaccard(fp.OutRegs, cap.OutRegs); score += (int)(45 * j) - 8; parts.Add($"outJ{j:0.00}"); }

    // --- instruction count: lib exact, capture approximate (GLSL statements) ---
    int d = Math.Abs(lib.HeaderInstrCount - cap.BodyStatements);
    if (d <= 3) score += 22;
    else if (d <= 8) score += 10;
    else if (d <= 18) score += 0;
    else { score -= 12; parts.Add($"instrd{d}"); }

    // --- flags ---
    if (fp.IndexedConst == cap.IndexedConst) score += 14; else { score -= 14; parts.Add("!idx"); }
    if (fp.Ex2Count == cap.Ex2Count) score += 12; else { score -= 5; parts.Add($"ex2 {fp.Ex2Count}v{cap.Ex2Count}"); }
    if (fp.HasTxl == cap.HasTexture) score += 8; else { score -= 10; parts.Add("!txl"); }

    // --- name-feature sanity ---
    if (nm.Family == "Dbg") { score -= 20; parts.Add("dbg"); }
    // A depth-prepass program does no shading, so it never touches the fog/scatter constant
    // block (103-111) or an exp2. If the capture does, a depth-pass candidate is wrong -
    // but don't penalise a candidate just for a small output set (a real DepAlp capture is
    // position + a UV or two for the alpha test).
    if (nm.DepthOnly && (cap.Ex2Count > 0 || cap.ConstRefs.Any(c => c is >= 103 and <= 111)))
    { score -= 80; parts.Add("!depth"); }

    return (score, string.Join(" ", parts));
}

static int MatchVp(string libDir, string logDir, string? only, string? jsonOut)
{
    if (!Directory.Exists(libDir)) { Console.Error.WriteLine($"not a directory: {libDir}"); return 1; }
    if (!Directory.Exists(logDir)) { Console.Error.WriteLine($"not a directory: {logDir}"); return 1; }

    var lib = LoadVpLibrary(libDir);
    Console.Error.WriteLine($"library: {lib.Count} vertex programs decoded");
    var caps = LoadVpCaptures(logDir, only);
    Console.Error.WriteLine($"captures: {caps.Count} parsed");
    Console.WriteLine();

    var jsonRows = new List<string>();
    int nHigh = 0, nMed = 0, nLow = 0, nNone = 0;

    foreach (var cap in caps)
    {
        var ranked = lib
            .Select(l => (l, s: ScoreVp(l, cap)))
            .OrderByDescending(x => x.s.Total)
            .ThenBy(x => x.l.Name, StringComparer.Ordinal)
            .Take(8)
            .ToList();

        int top = ranked[0].s.Total;
        // Gap to the next candidate that is a genuinely different shader (not just Phn<->Gst).
        string topCanon = VpCanonical(ranked[0].l.Name);
        var nextDistinct = ranked.FirstOrDefault(r => VpCanonical(r.l.Name) != topCanon);
        int gap = nextDistinct.l != null ? top - nextDistinct.s.Total : top;
        string tier =
            top < 90 ? "NONE" :
            top >= 210 && gap >= 20 ? "HIGH" :
            top >= 160 && gap >= 10 ? "MED" :
            "LOW";
        switch (tier) { case "HIGH": nHigh++; break; case "MED": nMed++; break; case "LOW": nLow++; break; default: nNone++; break; }

        Console.WriteLine($"{cap.Name,-20} {tier,-4} in[{cap.InputKey}] out[{cap.OutKey}] c[{cap.ConstKey}] idx={cap.IndexedConst} ex2={cap.Ex2Count} stmts={cap.BodyStatements}");
        foreach (var (l, s) in ranked.Take(5))
            Console.WriteLine($"    {s.Total,4}  {l.Name,-28} instr={l.HeaderInstrCount} in[{string.Join(",", Bits(l.AttrInMask))}] out[{l.Fp.OutKey}]  ({s.Detail})");
        Console.WriteLine();

        // Candidates: everything within 6 of the top, deduped to canonical identity.
        string cands = string.Join(", ", ranked.Where(r => r.s.Total >= top - 6)
            .GroupBy(r => VpCanonical(r.l.Name)).Select(g => g.First())
            .Select(r => $"{{\"name\":\"{r.l.Name}\",\"canonical\":\"{VpCanonical(r.l.Name)}\",\"score\":{r.s.Total}}}"));
        jsonRows.Add(
            $"  {{\"capture\":\"{cap.Name}\",\"fingerprint\":\"{JsonEsc(cap.Signature)}\",\"tier\":\"{tier}\"," +
            $"\"best\":\"{ranked[0].l.Name}\",\"score\":{top},\"gap\":{gap},\"candidates\":[{cands}]}}");
    }

    Console.WriteLine($"tiers: HIGH {nHigh}  MED {nMed}  LOW {nLow}  NONE {nNone}   (of {caps.Count})");

    if (jsonOut != null)
    {
        File.WriteAllText(jsonOut, "[\n" + string.Join(",\n", jsonRows) + "\n]\n");
        Console.Error.WriteLine($"wrote {jsonOut}");
    }
    return 0;
}

static IEnumerable<int> Bits(uint mask)
{
    for (int i = 0; i < 32; i++) if ((mask & (1u << i)) != 0) yield return i;
}

static int VerifyVp(string libDir, string logDir)
{
    if (!Directory.Exists(libDir) || !Directory.Exists(logDir)) { Console.Error.WriteLine("bad dir"); return 1; }
    var lib = LoadVpLibrary(libDir);
    var caps = LoadVpCaptures(logDir, null);

    // Anchored on the capture's session-independent (input-set, const-set) fingerprint, not
    // its VertexProgramN number. `Want` is a canonical name (Phn/Gst collapsed) - the vertex
    // side cannot tell those apart, by design. Established from the first match --vertex run.
    var anchors = new (string In, string Const, string Want, string Note)[]
    {
        ("0,2,3,7,8,14", "0,1,2,3,7,8,9,10,103,104,105,106,107,108,109,110,111,120,466,467",
            "DS_*_PINT_D_Non", "lit map piece + fog, one tangent set, no shadow; single UV set (c120 only)"),
        ("0,1,2,3,7,8", "0,1,2,3,7,8,9,10,103,104,105,106,107,108,109,110,111,120,466,467",
            "DS_*_PIWN_D_Non", "same, skinned (in_weight) - c[A+n] bone matrix"),
    };

    int bad = 0, missing = 0;
    foreach (var (inp, cst, want, note) in anchors)
    {
        var cap = caps.FirstOrDefault(c => c.InputKey == inp && c.ConstKey == cst);
        if (cap == null) { missing++; Console.WriteLine($"MISS  in[{inp}] - no capture with this fingerprint ({note})"); continue; }
        var best = lib.Select(l => (l, s: ScoreVp(l, cap)))
            .OrderByDescending(x => x.s.Total).ThenBy(x => x.l.Name, StringComparer.Ordinal).First();
        bool ok = VpCanonical(best.l.Name) == want;
        if (!ok) bad++;
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {cap.Name,-18} in[{inp}] -> {VpCanonical(best.l.Name)}  (want {want}, score {best.s.Total}; {note})");
    }
    if (missing > 0) Console.WriteLine($"({missing} anchor fingerprint(s) not present in this shader-log)");
    return bad == 0 ? 0 : 1;
}

static int CoverageVp(string libDir, string logDir)
{
    if (!Directory.Exists(libDir) || !Directory.Exists(logDir)) { Console.Error.WriteLine("bad dir"); return 1; }
    var lib = LoadVpLibrary(libDir);
    var caps = LoadVpCaptures(logDir, null);

    var isTopFor = new HashSet<string>();
    var bestScore = new Dictionary<string, int>();
    foreach (var cap in caps)
    {
        var ranked = lib.Select(l => (l, s: ScoreVp(l, cap).Total))
            .OrderByDescending(x => x.s).ThenBy(x => x.l.Name, StringComparer.Ordinal).ToList();
        if (ranked[0].s >= 200) isTopFor.Add(ranked[0].l.Name);
        foreach (var (l, s) in ranked)
            if (!bestScore.TryGetValue(l.Name, out int cur) || s > cur) bestScore[l.Name] = s;
    }

    string Bucket(LibVp l) =>
        isTopFor.Contains(l.Name) ? "captured" :
        bestScore.GetValueOrDefault(l.Name) >= 150 ? "near" : "unseen";

    var buckets = new Dictionary<string, List<LibVp>> { ["captured"] = new(), ["near"] = new(), ["unseen"] = new() };
    foreach (var l in lib) buckets[Bucket(l)].Add(l);

    Console.WriteLine($"library {lib.Count} vertex programs, {caps.Count} captures");
    foreach (var k in new[] { "captured", "near", "unseen" })
        Console.WriteLine($"  {k,-9} {buckets[k].Count}");
    Console.WriteLine();
    Console.WriteLine("unseen, grouped:");
    foreach (var g in buckets["unseen"]
        .GroupBy(l => $"{l.Features.Family}/{(l.Features.Skinned ? "skinned/" : "")}{l.Features.Pass}")
        .OrderByDescending(g => g.Count()))
        Console.WriteLine($"  {g.Count(),3}  {g.Key}");
    return 0;
}

// ===== rrc-fog: what RSX fixed-function fog state each draw runs under, and how many =====
// draws use an FP that actually consumes it (reads f[FOGC]). This is the fragment-side half
// of DeS's FOG_BANK - the never-in-our-library fetch_fog_value() path (docs/context.md 27/28/33).

static string InputMaskStr(int mask)
{
    string[] names = { "WPOS", "COL0", "COL1", "FOGC", "TEX0", "TEX1", "TEX2", "TEX3", "TEX4", "TEX5", "TEX6", "TEX7", "TEX8", "TEX9" };
    var got = names.Where((_, i) => (mask & (1 << i)) != 0).ToList();
    return got.Count == 0 ? "(none)" : string.Join(",", got);
}

static string FogModeName(uint m) => m switch
{
    0x0000 => "(unset)",
    0x2601 => "LINEAR",
    0x0804 => "LINEAR_ABS",
    0x0800 => "EXP",
    0x0802 => "EXP_ABS",
    0x0801 => "EXP2",
    0x0803 => "EXP2_ABS",
    _ => $"0x{m:X4}",
};

static int RrcFog(string capturePath)
{
    if (!File.Exists(capturePath)) { Console.Error.WriteLine($"no such file: {capturePath}"); return 1; }

    RrcCapture.Frame frame;
    try { frame = RrcCapture.Parse(capturePath, keepData: true); }
    catch (Exception e) { Console.Error.WriteLine($"parse failed: {e.Message}"); return 1; }

    var draws = RrcInterp.Replay(frame);
    Console.WriteLine($"{System.IO.Path.GetFileName(capturePath)}: {draws.Count} draws\n");

    // FP ucode -> does it read f[FOGC]. Cache by reference; ucode blocks are shared across draws.
    var fogReaderCache = new Dictionary<byte[], bool>(ReferenceEqualityComparer.Instance);
    bool ReadsFog(byte[]? u)
    {
        if (u == null) return false;
        if (fogReaderCache.TryGetValue(u, out bool v)) return v;
        try { v = RsxFp.Decode(u).Fp.ReadsFog; } catch { v = false; }
        fogReaderCache[u] = v;
        return v;
    }

    // Group draws by the (mode, param0, param1) tuple in force. RSX has no fog-colour method in
    // the programmable path - a fog-consuming FP carries its own colour as an inline constant.
    var groups = new Dictionary<(uint, float, float), (int draws, int fogFp, int noUcode)>();
    foreach (var d in draws)
    {
        var key = (d.FogMode, d.FogParam0, d.FogParam1);
        var g = groups.GetValueOrDefault(key);
        g.draws++;
        if (d.FpUcode == null) g.noUcode++;
        else if (ReadsFog(d.FpUcode)) g.fogFp++;
        groups[key] = g;
    }

    Console.WriteLine("RSX fixed-function fog state by draw count:");
    Console.WriteLine("  mode         param0        param1      draws   FP reads f[FOGC]");
    foreach (var kv in groups.OrderByDescending(k => k.Value.draws))
    {
        var (mode, p0, p1) = kv.Key;
        var (nd, nf, nu) = kv.Value;
        Console.WriteLine($"  {FogModeName(mode),-10} {p0,12:0.######} {p1,12:0.######}   {nd,6}   {nf,6}"
                          + (nu > 0 ? $"   ({nu} no-ucode)" : ""));
    }

    // LINEAR fog: RPCS3's fetch_fog_value does result.y = param1*fogc + (param0 - 1), clamped
    // [0,1]. The GCM helper builds param0 = far/(far-near), param1 = -1/(far-near) from a
    // begin/end pair, so (near,far) invert as: far = (param0-1)/param1 ... near = param0/param1.
    foreach (var kv in groups.Where(k => k.Key.Item1 is 0x2601 or 0x0804).OrderByDescending(k => k.Value.draws))
    {
        var (_, p0, p1) = kv.Key;
        if (System.Math.Abs(p1) > 1e-9f)
            Console.WriteLine($"\n  LINEAR invert: near = {p0 / p1,10:0.###}   far = {(p0 - 1f) / p1,10:0.###}   (units)");
    }

    // Area fingerprint: the shared atmosphere block from the first draw that carries it.
    var atmo = draws.FirstOrDefault(d => d.ConstWritten.Length > 111 && d.ConstWritten[111]);
    if (atmo != null)
    {
        Console.WriteLine("\natmosphere block (area fingerprint):");
        foreach (int i in new[] { 104, 106, 107, 108, 109, 110, 111 })
            if (atmo.ConstWritten[i])
            {
                var v = atmo.Constants[i];
                Console.WriteLine($"  c[{i}] = {v[0],11:0.######} {v[1],11:0.######} {v[2],11:0.######} {v[3],11:0.######}");
            }
    }
    return 0;
}

// ===== rrc-draws: replay a frame capture, name every draw's FP+VP, dump its constants =====

static int RrcDraws(string capturePath, string libDir, int? only, string? grep)
{
    if (!File.Exists(capturePath)) { Console.Error.WriteLine($"no such file: {capturePath}"); return 1; }
    if (!Directory.Exists(libDir)) { Console.Error.WriteLine($"not a directory: {libDir}"); return 1; }

    RrcCapture.Frame frame;
    try { frame = RrcCapture.Parse(capturePath, keepData: true); }
    catch (Exception e) { Console.Error.WriteLine($"parse failed: {e.Message}"); return 1; }

    var fpLib = LoadLibrary(libDir);
    var vpLib = LoadVpLibrary(libDir);
    Console.Error.WriteLine($"library: {fpLib.Count} fragment + {vpLib.Count} vertex programs");

    var draws = RrcInterp.Replay(frame);
    Console.WriteLine($"{System.IO.Path.GetFileName(capturePath)}: {draws.Count} draws\n");

    int fpNamed = 0, vpNamed = 0, fpMissing = 0;
    var byPair = new Dictionary<string, int>();

    foreach (var d in draws)
    {
        string fpName = "(no ucode)", fpTier = "";
        if (d.FpUcode == null) fpMissing++;
        else
        {
            try
            {
                var (_, fpFp) = RsxFp.Decode(d.FpUcode);
                var cap = FpCaptureFromFingerprint(fpFp);
                var best = fpLib.Select(l => (l, s: Score(l, cap))).OrderByDescending(x => x.s.Total).ThenBy(x => x.l.Name, StringComparer.Ordinal).First();
                fpName = best.l.Name; fpTier = best.s.Total >= 155 ? "HIGH" : best.s.Total >= 120 ? "MED" : "LOW";
                if (fpTier == "HIGH") fpNamed++;
            }
            catch { fpName = "(decode failed)"; }
        }

        string vpName = "(no ucode)", vpTier = "";
        if (d.VpUcode.Length >= 16)
        {
            try
            {
                var (_, vpFp) = RsxVp.Decode(d.VpUcode);
                var cap = VpCaptureFromFingerprint(vpFp);
                var ranked = vpLib.Select(l => (l, s: ScoreVp(l, cap))).OrderByDescending(x => x.s.Total).ThenBy(x => x.l.Name, StringComparer.Ordinal).ToList();
                string canon = VpCanonical(ranked[0].l.Name);
                int gap = ranked.Skip(1).FirstOrDefault(r => VpCanonical(r.l.Name) != canon) is { l: not null } nd ? ranked[0].s.Total - nd.s.Total : ranked[0].s.Total;
                vpName = canon;
                vpTier = ranked[0].s.Total >= 210 && gap >= 20 ? "HIGH" : ranked[0].s.Total >= 160 && gap >= 10 ? "MED" : "LOW";
                if (vpTier == "HIGH") vpNamed++;
            }
            catch { vpName = "(decode failed)"; }
        }

        var key = $"{fpName} | {vpName}";
        byPair[key] = byPair.GetValueOrDefault(key) + 1;

        bool show = only == d.Index || (grep != null && (fpName.Contains(grep, StringComparison.OrdinalIgnoreCase) || vpName.Contains(grep, StringComparison.OrdinalIgnoreCase)));
        if (only == null && grep == null) show = false;

        if (show || (only == null && grep == null && d.Index < 0))
        {
            Console.WriteLine($"#{d.Index,-4} {(d.Indexed ? "idx" : "arr")}  FP={fpName} [{fpTier}]  VP={vpName} [{vpTier}]  fpAddr=0x{d.FpAddr:X8}");
            DumpConstants(d);
            Console.WriteLine();
        }
    }

    if (only == null && grep == null)
    {
        Console.WriteLine("draw pairs (FP | VP) by count:");
        foreach (var kv in byPair.OrderByDescending(k => k.Value))
            Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
        Console.WriteLine();
    }
    Console.WriteLine($"draws {draws.Count}   FP HIGH {fpNamed}   VP HIGH {vpNamed}   FP ucode missing {fpMissing}");
    return 0;
}

// ===== rrc-tex: per-draw fragment-texture-unit state (format + sRGB-on-fetch gamma) =====
// Answers "does DeS gamma-decode the env cubemap / diffuse / lightmap on fetch?" straight from
// NV4097_SET_TEXTURE_FORMAT (0x681+u*8) and NV4097_SET_TEXTURE_ADDRESS (0x682+u*8, bits 20-23).
static int RrcTex(string capturePath, string libDir, string? grep, int? only)
{
    if (!File.Exists(capturePath)) { Console.Error.WriteLine($"no such file: {capturePath}"); return 1; }
    if (!Directory.Exists(libDir)) { Console.Error.WriteLine($"not a directory: {libDir}"); return 1; }

    RrcCapture.Frame frame;
    try { frame = RrcCapture.Parse(capturePath, keepData: true); }
    catch (Exception e) { Console.Error.WriteLine($"parse failed: {e.Message}"); return 1; }

    var fpLib = LoadLibrary(libDir);
    Console.Error.WriteLine($"library: {fpLib.Count} fragment programs");
    var draws = RrcInterp.Replay(frame);
    Console.WriteLine($"{System.IO.Path.GetFileName(capturePath)}: {draws.Count} draws  (grep: {grep ?? "HemEnv"})\n");

    grep ??= "HemEnv";

    // per-(shaderName, unit) tally of the gamma mask seen
    var roleGamma = new Dictionary<string, Dictionary<string, int>>();
    void Tally(string role, string key) { roleGamma.TryAdd(role, new()); roleGamma[role].TryGetValue(key, out int c); roleGamma[role][key] = c + 1; }
    int shown = 0, matched = 0;

    foreach (var d in draws)
    {
        if (d.FpUcode == null) continue;
        RsxFp.Fingerprint fp;
        string fpName;
        try
        {
            (_, fp) = RsxFp.Decode(d.FpUcode);
            var cap = FpCaptureFromFingerprint(fp);
            var best = fpLib.Select(l => (l, s: Score(l, cap))).OrderByDescending(x => x.s.Total).ThenBy(x => x.l.Name, StringComparer.Ordinal).First();
            fpName = best.l.Name;
        }
        catch { continue; }

        bool match = only == d.Index || (only == null && fpName.Contains(grep, StringComparison.OrdinalIgnoreCase));
        if (!match) continue;
        matched++;

        // op per unit from the FP's texture sequence (2D / Cube / Shd / ...)
        var opByUnit = new Dictionary<int, string>();
        foreach (var (op, unit) in fp.TextureSequence) opByUnit[unit] = op;

        var lines = new List<string>();
        foreach (int u in fp.TextureUnits.OrderBy(x => x))
        {
            uint fmtReg = d.TexFormat[u], addrReg = d.TexAddress[u], ctrl0 = d.TexControl0[u], ctrl1 = d.TexControl1[u];
            byte bf = RrcInterp.TexBaseFormat(fmtReg);
            bool cube = RrcInterp.TexIsCube(fmtReg);
            int mips = RrcInterp.TexMipCount(fmtReg);
            int gmask = RrcInterp.TexGammaMask(addrReg);
            int uremap = RrcInterp.TexUnsignedRemap(addrReg);
            int sremap = RrcInterp.TexSignedRemap(addrReg);
            bool gammaCap = RrcInterp.TexFormatGammaCapable(bf);
            bool srgb = gammaCap && (gmask & 0x7) != 0;   // any of R/G/B gamma-decoded
            string op = opByUnit.GetValueOrDefault(u, "?");
            string role = op == "Cube" ? "envCube" : u == 6 ? "lightmap(u6)" : op == "Shd" ? "shadow" : "diffuse/other";
            string flags = srgb ? "sRGB" : gmask != 0 ? $"gmask=0x{gmask:X}(ignored)" : "linear";
            if (uremap == 1) flags += "+BX2";
            if (sremap != 0) flags += $"+SNORM0x{sremap:X}";
            Tally(role, flags);
            lines.Add($"    u{u,-2} {op,-4} fmt={RrcInterp.TexFormatName(bf),-11} cube={(cube ? "Y" : "-")} mips={mips,-2}"
                    + $" g=0b{Convert.ToString(gmask, 2).PadLeft(4, '0')} uremap={uremap} sremap=0x{sremap:X} remapReg=0x{ctrl1:X8}"
                    + $" {(srgb ? "<sRGB>" : "")}");
        }

        if (only != null || shown < 24)
        {
            shown++;
            Console.WriteLine($"#{d.Index,-4} {fpName}");
            foreach (var l in lines) Console.WriteLine(l);
        }
    }

    Console.WriteLine($"\n{matched} draws matched '{grep}'.  Gamma-on-fetch by role:");
    foreach (var (role, tally) in roleGamma.OrderBy(k => k.Key))
        Console.WriteLine($"  {role,-16}  {string.Join("   ", tally.OrderByDescending(t => t.Value).Select(t => $"{t.Key}: {t.Value}"))}");
    return 0;
}

// ===== rrc-mine: systematic constant + render-state census across one frame capture =====
// Names every draw, classifies its pass (COLOR / DEPTH / SHADOW by SET_SURFACE state), and
// reports which vertex constant registers and fragment inline constants each (pass, shader)
// group touches, with per-frame vs per-draw scope. The durable input to the shadow/depth
// work and any later bank (HDR, colour-correct, the hand-rolled fog ramp).

static string DepthFmtName(byte f) => f switch { 1 => "Z16", 2 => "Z24S8", 0 => "(unset)", _ => $"fmt{f}" };

static (string Fp, string Vp) NameRrcDraw(RrcInterp.DrawState d,
    System.Collections.Generic.List<LibFp> fpLib, System.Collections.Generic.List<LibVp> vpLib)
{
    string fp = d.FpUcode == null ? "(no ucode)" : "(decode failed)";
    if (d.FpUcode != null)
        try
        {
            var (_, f) = RsxFp.Decode(d.FpUcode);
            var cap = FpCaptureFromFingerprint(f);
            var best = fpLib.Select(l => (l, s: Score(l, cap))).OrderByDescending(x => x.s.Total).ThenBy(x => x.l.Name, StringComparer.Ordinal).First();
            fp = best.s.Total >= 155 ? best.l.Name : best.s.Total >= 120 ? best.l.Name + "?" : "(low)";
        }
        catch { }

    string vp = "(no ucode)";
    if (d.VpUcode.Length >= 16)
        try
        {
            var (_, vf) = RsxVp.Decode(d.VpUcode);
            var cap = VpCaptureFromFingerprint(vf);
            var ranked = vpLib.Select(l => (l, s: ScoreVp(l, cap))).OrderByDescending(x => x.s.Total).ThenBy(x => x.l.Name, StringComparer.Ordinal).ToList();
            string canon = VpCanonical(ranked[0].l.Name);
            int gap = ranked.Skip(1).FirstOrDefault(r => VpCanonical(r.l.Name) != canon) is { l: not null } nd ? ranked[0].s.Total - nd.s.Total : ranked[0].s.Total;
            vp = ranked[0].s.Total >= 210 && gap >= 20 ? canon : ranked[0].s.Total >= 160 && gap >= 10 ? canon + "?" : "(low)";
        }
        catch { vp = "(decode failed)"; }
    return (fp, vp);
}

static bool Vec4Eq(float[] a, float[] b) =>
    BitConverter.SingleToInt32Bits(a[0]) == BitConverter.SingleToInt32Bits(b[0])
    && BitConverter.SingleToInt32Bits(a[1]) == BitConverter.SingleToInt32Bits(b[1])
    && BitConverter.SingleToInt32Bits(a[2]) == BitConverter.SingleToInt32Bits(b[2])
    && BitConverter.SingleToInt32Bits(a[3]) == BitConverter.SingleToInt32Bits(b[3]);

static int RrcMine(string capturePath, string libDir, string? jsonOut, bool vpConstDetail = false)
{
    if (!File.Exists(capturePath)) { Console.Error.WriteLine($"no such file: {capturePath}"); return 1; }
    if (!Directory.Exists(libDir)) { Console.Error.WriteLine($"not a directory: {libDir}"); return 1; }

    RrcCapture.Frame frame;
    try { frame = RrcCapture.Parse(capturePath, keepData: true); }
    catch (Exception e) { Console.Error.WriteLine($"parse failed: {e.Message}"); return 1; }

    var fpLib = LoadLibrary(libDir);
    var vpLib = LoadVpLibrary(libDir);
    var draws = RrcInterp.Replay(frame);
    string file = System.IO.Path.GetFileName(capturePath);
    Console.WriteLine($"{file}: {draws.Count} draws\n");

    // ---- area fingerprint (shared atmosphere block) ----
    var atmo = draws.FirstOrDefault(d => d.ConstWritten.Length > 111 && d.ConstWritten[111]);
    if (atmo != null)
    {
        Console.WriteLine("area fingerprint:");
        foreach (int i in new[] { 104, 106, 107, 108, 109, 110, 111 })
            if (atmo.ConstWritten[i])
            {
                var v = atmo.Constants[i];
                Console.WriteLine($"  c[{i}] = {v[0],11:0.######} {v[1],11:0.######} {v[2],11:0.######} {v[3],11:0.######}");
            }
        Console.WriteLine();
    }

    // ---- per-draw: name, and the constant registers its *vertex program* actually references ----
    // ConstRefs (from the VP microcode) is the real "this draw uses c[N]" signal - SET_TRANSFORM_
    // CONSTANT history is cumulative junk. Value at the draw is d.Constants[reg]. Skinned VPs also
    // hit c[A+n] bone matrices (IndexedConst) that don't show up as static refs.
    var info = draws.Select(d =>
    {
        var (fp, vp) = NameRrcDraw(d, fpLib, vpLib);
        int[] refs; bool indexed = false;
        try { var (_, f) = RsxVp.Decode(d.VpUcode); refs = f.ConstRefs.ToArray(); indexed = f.IndexedConst; }
        catch { refs = Array.Empty<int>(); }
        return (d, fp, vp, refs, indexed);
    }).ToList();

    // ---- pass breakdown ----
    Console.WriteLine("pass breakdown (SET_SURFACE / SET_VIEWPORT state):");
    Console.WriteLine("  kind     draws   RT sizes                viewport sizes           depth");
    foreach (var g in draws.GroupBy(d => d.Rs.Kind).OrderByDescending(g => g.Count()))
    {
        var sizes = string.Join(",", g.Select(d => $"{d.Rs.SurfaceW}x{d.Rs.SurfaceH}").Distinct().OrderBy(s => s));
        var vps = string.Join(",", g.Select(d => $"{d.Rs.ViewportW}x{d.Rs.ViewportH}").Distinct().OrderBy(s => s));
        var fmts = string.Join(",", g.Select(d => DepthFmtName(d.Rs.DepthFmt)).Distinct());
        Console.WriteLine($"  {g.Key,-7} {g.Count(),6}   {sizes,-22}  {vps,-22}  {fmts}");
    }
    Console.WriteLine();

    // ---- vertex constant register census (referenced, not merely uploaded) ----
    var byReg = Enumerable.Range(0, 468)
        .Select(r => (r, users: info.Where(x => x.refs.Contains(r)).ToList()))
        .Where(t => t.users.Count > 0).ToList();

    Console.WriteLine("vertex constant census (registers a VP microcode references):");
    Console.WriteLine("  reg      scope   #draws  passes            VP families                          value (frame-scope)");
    var registerJson = new System.Text.StringBuilder();
    foreach (var (reg, users) in byReg)
    {
        var v0 = users[0].d.Constants[reg];
        bool frameScope = users.All(x => Vec4Eq(x.d.Constants[reg], v0));
        var passes = string.Join(",", users.Select(x => x.d.Rs.Kind).Distinct().OrderBy(s => s));
        var fams = string.Join(",", users.Select(x => x.vp).Distinct().OrderBy(s => s).Take(4));
        string val = frameScope ? $"{v0[0]:0.######} {v0[1]:0.######} {v0[2]:0.######} {v0[3]:0.######}" : "per-draw";
        Console.WriteLine($"  c[{reg,3}]   {(frameScope ? "frame" : "draw "),-5}  {users.Count,6}  {passes,-16}  {fams,-35}  {val}");
        registerJson.Append($"{{\"reg\":{reg},\"scope\":\"{(frameScope ? "frame" : "draw")}\",\"draws\":{users.Count},\"passes\":\"{passes}\",\"value\":[{v0[0]:R},{v0[1]:R},{v0[2]:R},{v0[3]:R}]}},");
    }
    if (info.Any(x => x.indexed))
        Console.WriteLine($"  (+ {info.Count(x => x.indexed)} draws use indexed c[A+n] bone matrices, not shown)");
    Console.WriteLine();

    // ---- shader + constant detail per (pass, VP, FP) group; full detail for DEPTH/SHADOW ----
    foreach (var g in info.GroupBy(x => (x.d.Rs.Kind, x.vp, x.fp)).OrderBy(g => g.Key.Kind).ThenByDescending(g => g.Count()))
    {
        var members = g.ToList();
        var refs = members.SelectMany(x => x.refs).Distinct().OrderBy(r => r).ToList();
        var frameRefs = refs.Where(r => members.Where(x => x.refs.Contains(r)).Select(x => x.d.Constants[r])
            .All(cv => Vec4Eq(cv, members.First(x => x.refs.Contains(r)).d.Constants[r]))).ToList();
        var drawRefs = refs.Except(frameRefs).ToList();

        var fragC = new System.Collections.Generic.SortedDictionary<int, float[]>();
        var withUcode = members.FirstOrDefault(x => x.d.FpUcode != null);
        if (withUcode.d != null)
            try { foreach (var ins in RsxFp.Decode(withUcode.d.FpUcode!).Instrs.Where(i => i.Const != null)) fragC[ins.ConstIndex] = ins.Const!; }
            catch { }

        bool detail = g.Key.Kind is "SHADOW" or "DEPTH" || vpConstDetail;
        Console.WriteLine($"{g.Key.Kind,-6} VP={g.Key.vp}  FP={g.Key.fp}   {members.Count} draws  (RT {members[0].d.Rs.SurfaceW}x{members[0].d.Rs.SurfaceH}, vp {members[0].d.Rs.ViewportW}x{members[0].d.Rs.ViewportH})");
        Console.WriteLine($"       vp frame c[]:    {string.Join(" ", frameRefs)}");
        Console.WriteLine($"       vp per-draw c[]: {string.Join(" ", drawRefs)}{(members.Any(x => x.indexed) ? " +bone[A+n]" : "")}");
        if (detail)
            foreach (int r in frameRefs)
            {
                var v = members.First(x => x.refs.Contains(r)).d.Constants[r];
                Console.WriteLine($"         c[{r,3}] = {v[0]:0.######} {v[1]:0.######} {v[2]:0.######} {v[3]:0.######}");
            }
        Console.WriteLine($"       fp inline c[]:   {(fragC.Count == 0 ? "(none)" : string.Join("  ", fragC.Select(kv => $"#{kv.Key}=({kv.Value[0]:0.###},{kv.Value[1]:0.###},{kv.Value[2]:0.###},{kv.Value[3]:0.###})")))}");
        Console.WriteLine();
    }

    if (jsonOut != null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"{{\"file\":\"{file}\",\"draws\":{draws.Count},");
        sb.Append("\"passes\":[");
        sb.Append(string.Join(",", draws.GroupBy(d => d.Rs.Kind).Select(gr =>
            $"{{\"kind\":\"{gr.Key}\",\"draws\":{gr.Count()},\"sizes\":\"{string.Join(",", gr.Select(d => $"{d.Rs.SurfaceW}x{d.Rs.SurfaceH}").Distinct())}\",\"depth\":\"{string.Join(",", gr.Select(d => DepthFmtName(d.Rs.DepthFmt)).Distinct())}\"}}")));
        sb.Append("],\"registers\":[").Append(registerJson.ToString().TrimEnd(',')).Append("]}");
        File.WriteAllText(jsonOut, sb.ToString());
        Console.Error.WriteLine($"wrote {jsonOut}");
    }
    return 0;
}

// ===== rrc-shadow: resolve how DeS's shadow atlas is addressed and where the light matrices live =====

static string M4(float[] a, float[] b, float[] c, float[] d) =>
    $"[ {a[0],9:0.####} {a[1],9:0.####} {a[2],9:0.####} {a[3],9:0.####} ]\n"
  + $"    [ {b[0],9:0.####} {b[1],9:0.####} {b[2],9:0.####} {b[3],9:0.####} ]\n"
  + $"    [ {c[0],9:0.####} {c[1],9:0.####} {c[2],9:0.####} {c[3],9:0.####} ]\n"
  + $"    [ {d[0],9:0.####} {d[1],9:0.####} {d[2],9:0.####} {d[3],9:0.####} ]";

static string RoundKey(params float[][] rows) =>
    string.Join("|", rows.SelectMany(r => r).Select(f => MathF.Round(f, 2).ToString("0.##")));

static int RrcShadow(string capturePath, string libDir)
{
    if (!File.Exists(capturePath)) { Console.Error.WriteLine($"no such file: {capturePath}"); return 1; }
    RrcCapture.Frame frame;
    try { frame = RrcCapture.Parse(capturePath, keepData: true); }
    catch (Exception e) { Console.Error.WriteLine($"parse failed: {e.Message}"); return 1; }

    var fpLib = LoadLibrary(libDir);
    var vpLib = LoadVpLibrary(libDir);
    var draws = RrcInterp.Replay(frame);
    Console.WriteLine($"{System.IO.Path.GetFileName(capturePath)}: {draws.Count} draws\n");

    // ---- 1. the SHADOW-pass tiles ----
    var shadow = draws.Where(d => d.Rs.Kind == "SHADOW").ToList();
    Console.WriteLine($"SHADOW-pass draws: {shadow.Count}");
    Console.WriteLine("atlas tiles (SET_VIEWPORT origin+extent within the 2048^2 surface):");
    foreach (var t in shadow.GroupBy(d => (d.Rs.ViewportX, d.Rs.ViewportY, d.Rs.ViewportW, d.Rs.ViewportH)).OrderByDescending(g => g.Count()))
    {
        var mats = t.Select(d => RoundKey(d.Constants[0], d.Constants[1], d.Constants[2], d.Constants[3])).Distinct().Count();
        Console.WriteLine($"  tile x={t.Key.ViewportX,4} y={t.Key.ViewportY,4}  {t.Key.ViewportW}x{t.Key.ViewportH}   {t.Count(),4} draws   {mats} distinct c[0..3] matrix(es)");
    }
    Console.WriteLine();

    // c[0..3] and c[8..10] for one representative draw per tile
    foreach (var t in shadow.GroupBy(d => (d.Rs.ViewportX, d.Rs.ViewportY)).OrderBy(g => g.Key.ViewportY).ThenBy(g => g.Key.ViewportX))
    {
        var d0 = t.First();
        Console.WriteLine($"tile x={t.Key.ViewportX} y={t.Key.ViewportY}  (draw #{d0.Index})");
        Console.WriteLine($"  c[0..3]  (world->light-clip?) = {M4(d0.Constants[0], d0.Constants[1], d0.Constants[2], d0.Constants[3])}");
        Console.WriteLine($"  c[8..10] (caster model->world) = {M4(d0.Constants[8], d0.Constants[9], d0.Constants[10], new float[4])}");
        Console.WriteLine($"  c[467] = {string.Join(" ", d0.Constants[467].Select(f => f.ToString("0.######")))}");
        Console.WriteLine();
    }

    // ---- 2. the colour-pass shadow *receivers*: every frame-constant register a *_Sdw VP references ----
    var recv = draws.Select(d =>
    {
        var (fp, vp) = NameRrcDraw(d, fpLib, vpLib);
        int[] refs; try { refs = RsxVp.Decode(d.VpUcode).Fp.ConstRefs.ToArray(); } catch { refs = Array.Empty<int>(); }
        return (d, fp, vp, refs);
    }).Where(x => x.vp.Contains("_Sdw") || x.fp.Contains("Sdw") || x.fp.Contains("Csd")).ToList();

    Console.WriteLine($"colour-pass shadow receivers: {recv.Count} draws, {recv.Select(x => x.vp).Distinct().Count()} VP families");
    var allRefs = recv.SelectMany(x => x.refs).Distinct().OrderBy(r => r).ToList();
    Console.WriteLine($"  registers referenced by any receiver: {string.Join(" ", allRefs)}");
    foreach (int r in allRefs.Where(r => r >= 100))
    {
        var users = recv.Where(x => x.refs.Contains(r)).ToList();
        var v0 = users[0].d.Constants[r];
        bool frameScope = users.All(x => x.d.Constants[r].SequenceEqual(v0));
        Console.WriteLine($"  c[{r,3}] {(frameScope ? "frame" : "PER-DRAW"),-8} {v0[0]:0.######} {v0[1]:0.######} {v0[2]:0.######} {v0[3]:0.######}");
    }
    Console.WriteLine();

    // ---- 3. the Sdw_* fragment inline constants, laid out 4-per-row as candidate matrices ----
    foreach (var g in recv.Where(x => x.d.FpUcode != null).GroupBy(x => x.fp).OrderByDescending(g => g.Count()).Take(3))
    {
        float[][] ks;
        try { ks = RsxFp.Decode(g.First().d.FpUcode!).Instrs.Where(i => i.Const != null).Select(i => i.Const!).ToArray(); }
        catch { continue; }
        Console.WriteLine($"{g.Key}  ({g.Count()} draws) - {ks.Length} fp inline constants:");
        for (int i = 0; i < ks.Length; i++)
            Console.WriteLine($"  #{i,2} ({ks[i][0],9:0.####} {ks[i][1],9:0.####} {ks[i][2],9:0.####} {ks[i][3],9:0.####}){((i % 4 == 3) ? "   <- row group" : "")}");
        Console.WriteLine();
    }
    return 0;
}

static void DumpConstants(RrcInterp.DrawState d)
{
    var written = Enumerable.Range(0, 468).Where(i => d.ConstWritten[i]).ToList();
    if (written.Count == 0) { Console.WriteLine("    (no vertex constants written this frame before this draw)"); return; }
    Console.WriteLine($"    constants written: {written.Count} registers  [{string.Join(",", written)}]");
    foreach (int i in written)
    {
        var v = d.Constants[i];
        Console.WriteLine($"      c[{i,3}] = {v[0],12:0.######}  {v[1],12:0.######}  {v[2],12:0.######}  {v[3],12:0.######}");
    }
}

// A draw pulled from a .rrc has only microcode, no GLSL. These build a Capture/CaptureVp
// shaped fingerprint straight from the decoded microcode so the existing Score/ScoreVp
// (written against the shaderlog GLSL captures) can rank library entries against it.
static Capture FpCaptureFromFingerprint(RsxFp.Fingerprint fp) => Capture.FromRsxFp(fp);
static CaptureVp VpCaptureFromFingerprint(RsxVp.Fingerprint fp) => CaptureVp.FromRsxVp(fp);

static string MaskBits(ushort mask)
{
    if (mask == 0) return "(none)";
    var set = new List<int>();
    for (int i = 0; i < 16; i++)
        if ((mask & (1 << i)) != 0) set.Add(i);
    return string.Join(",", set);
}

static string Rel(string path)
{
    string cwd = Directory.GetCurrentDirectory();
    return path.StartsWith(cwd, StringComparison.Ordinal) ? path[(cwd.Length + 1)..] : path;
}
