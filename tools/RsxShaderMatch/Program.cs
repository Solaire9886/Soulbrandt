using RsxShaderMatch;

// Standalone research tool for naming the anonymous RPCS3 shader-log captures by
// matching them against Demon's Souls' own named shader library. See README.md.
// Scope of the current build: fragment programs. Vertex programs are a separate,
// not-yet-built pass (different ISA).

if (args.Length == 0)
{
    Console.Error.WriteLine("""
        usage:
          rsxshadermatch dump   <file.fpo|file.vpo>            inspect one program's container
          rsxshadermatch fp     <file.fpo>                     decode fragment microcode + print fingerprint
          rsxshadermatch disasm <file.fpo>                     readable RSX assembly listing
          rsxshadermatch sweep  <dir> [dir ...]                parse every .fpo/.vpo under dir(s), report anomalies
          rsxshadermatch match  <lib-dir> <shaderlog-dir> [--only=FragmentProgramN] [--json=out.json]
                                                               rank library shaders against capture(s)
          rsxshadermatch verify <lib-dir> <shaderlog-dir>      re-check the known-good pairs, exit 1 on regression
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

    case "disasm":
        if (args.Length != 2) { Console.Error.WriteLine("disasm takes exactly one path"); return 1; }
        return Disasm(args[1]);

    case "match":
    {
        if (args.Length < 3) { Console.Error.WriteLine("match takes <lib-dir> <shaderlog-dir> [--only=...] [--json=...]"); return 1; }
        string? only = args.Skip(3).FirstOrDefault(a => a.StartsWith("--only=", StringComparison.Ordinal))?["--only=".Length..];
        string? json = args.Skip(3).FirstOrDefault(a => a.StartsWith("--json=", StringComparison.Ordinal))?["--json=".Length..];
        return Match(args[1], args[2], only, json);
    }

    case "verify":
        if (args.Length != 3) { Console.Error.WriteLine("verify takes <lib-dir> <shaderlog-dir>"); return 1; }
        return Verify(args[1], args[2]);

    case "coverage":
        if (args.Length != 3) { Console.Error.WriteLine("coverage takes <lib-dir> <shaderlog-dir>"); return 1; }
        return Coverage(args[1], args[2]);

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
    if (p.Kind != CgProgram.ShaderKind.Fragment)
    {
        Console.Error.WriteLine($"{p.Name} is a {p.Kind} program; disasm handles fragment programs only");
        return 1;
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
