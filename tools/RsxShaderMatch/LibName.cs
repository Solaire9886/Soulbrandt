namespace RsxShaderMatch;

/// <summary>
/// The feature vector encoded in a library shader's own filename. Fragment program
/// names are <c>DS_&lt;family&gt;_&lt;textures&gt;_&lt;shadow&gt;_&lt;lightingModel&gt;.fpo</c>,
/// <c>_</c>-padded to fixed columns - so every field is read by substring, never by
/// splitting on <c>_</c> (see tools/RsxShaderMatch/README.md and ShaderLibrary.cs).
/// </summary>
sealed record LibName
{
    public required string Family { get; init; }   // Phn, Gst, Dbg, Water, Ghost, ...

    public bool Col { get; init; }                  // vertex-colour multiply
    public bool Dif { get; init; }
    public bool Spc { get; init; }
    public bool Bmp { get; init; }
    public bool Mul { get; init; }                  // 2nd texture layer (terrain blend)
    public bool Lit { get; init; }                  // lightmap

    public enum ShadowKind { None, Sdw, Csd }
    public ShadowKind Shadow { get; init; }

    public enum Model { Unknown, None, HemDir3, HemEnv, HemEnvLerp, Pnt }
    public Model Lighting { get; init; }
    public int PointLights { get; init; }           // 0..4, the trailing S-run after "Pnt"

    /// 2D material textures the name implies (diffuse/spec/bump/2nd layer/lightmap),
    /// i.e. not the shadow map and not an env cubemap.
    public int MaterialTextureCount =>
        (Dif ? 1 : 0) + (Spc ? 1 : 0) + (Bmp ? 1 : 0) + (Mul ? 1 : 0) + (Lit ? 1 : 0);

    public bool WantsShadowSampler => Shadow != ShadowKind.None;
    public bool WantsCubeSampler => Lighting is Model.HemEnv or Model.HemEnvLerp;

    public static LibName Parse(string fileName)
    {
        // fileName is the name without extension, e.g. DS_Phn_DifSpcBmpMulLitCsd_HemEnvPntSS
        string s = fileName;

        string family = "?";
        foreach (var f in new[] { "Phn", "Gst", "Dbg", "Water", "Ghost", "Fil", "Sfx", "Menu" })
            if (s.StartsWith($"DS_{f}", StringComparison.Ordinal)) { family = f; break; }

        LibName.Model model =
            s.Contains("HemEnvLerp") ? Model.HemEnvLerp :
            s.Contains("HemEnv") ? Model.HemEnv :
            s.Contains("HemDir3") ? Model.HemDir3 :
            s.Contains("_Non") || s.EndsWith("Non") ? Model.None :
            s.Contains("Pnt") ? Model.Pnt :
            Model.Unknown;

        // Count the S-run immediately after the last "Pnt".
        int pl = 0;
        int pnt = s.LastIndexOf("Pnt", StringComparison.Ordinal);
        if (pnt >= 0)
        {
            int i = pnt + 3;
            while (i < s.Length && s[i] == 'S') { pl++; i++; }
        }

        ShadowKind shadow =
            s.Contains("Csd") ? ShadowKind.Csd :
            s.Contains("Sdw") ? ShadowKind.Sdw :
            ShadowKind.None;

        return new LibName
        {
            Family = family,
            Col = s.Contains("Col"),
            Dif = s.Contains("Dif"),
            Spc = s.Contains("Spc"),
            Bmp = s.Contains("Bmp"),
            Mul = s.Contains("Mul"),
            Lit = s.Contains("Lit"),
            Shadow = shadow,
            Lighting = model,
            PointLights = pl,
        };
    }
}
