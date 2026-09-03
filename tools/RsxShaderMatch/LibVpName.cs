namespace RsxShaderMatch;

/// <summary>
/// The feature vector encoded in a library vertex program's filename. Vertex names are
/// <c>DS_&lt;family&gt;_&lt;layout&gt;_&lt;texgen&gt;_&lt;pass&gt;.vpo</c>, e.g.
/// <c>DS_Phn_PINTT_DDL_Sdw</c>, <c>DS_Phn_PIWN_DL_Non</c>, <c>DS_Gst_PIN_D_Non</c>.
///
///  - layout  <c>PIN</c> / <c>PINT</c> / <c>PINTT</c> / <c>PIWN</c> / <c>PIWNT</c> / <c>PIWNTT</c>:
///    a <c>W</c> means bone weights (skinned); trailing <c>T</c>s are tangent-frame count.
///    Cross-checked against each file's <c>attributeInputMask</c>: <c>W</c> ⟺ bit 1 (in_weight),
///    one <c>T</c> ⟺ tc6, two ⟺ tc6+tc1+tc7.
///  - texgen  <c>D</c> / <c>DD</c> / <c>DL</c> / <c>DDL</c>: <c>D</c> count = diffuse UV sets,
///    trailing <c>L</c> = a lightmap UV set.
///  - pass    <c>Non</c> (opaque) / <c>Sdw</c> (shadow receiver, +1 projected tc) /
///    <c>Dep</c> / <c>DepAlp</c> (depth prepass - 9 instr, position only) /
///    <c>Nrm</c> / <c>PntNum</c> (Dbg wireframe) / <c>Skin</c> / <c>Tod</c> (Ghost).
/// </summary>
sealed record LibVpName
{
    public required string Family { get; init; }   // Phn, Gst, Ghost, Water, Dbg
    public bool Skinned { get; init; }
    public int TangentSets { get; init; }          // 0..2, trailing T-run in the layout token
    public int DiffuseUvSets { get; init; }        // D-count in the texgen token
    public bool HasLightmapUv { get; init; }       // trailing L in the texgen token

    public enum PassKind { Opaque, Shadow, Depth, DepthAlpha, Debug, Other }
    public PassKind Pass { get; init; }
    public bool DepthOnly => Pass is PassKind.Depth or PassKind.DepthAlpha;

    public static LibVpName Parse(string fileName)
    {
        string s = fileName;

        string family = "?";
        foreach (var f in new[] { "Phn", "Gst", "Ghost", "Water", "Dbg" })
            if (s.StartsWith($"DS_{f}", StringComparison.Ordinal)) { family = f; break; }

        // Split DS_<fam>_<rest> into _-delimited tokens; layout is the first, pass the last.
        var parts = s.Split('_');
        string layout = parts.Length > 2 ? parts[2] : "";
        string texgen = parts.Length > 3 ? parts[3] : "";
        string pass = parts.Length > 4 ? parts[^1] : (parts.Length > 3 ? parts[^1] : "");

        // Tangent count: trailing T-run of the layout token (PIN | PINT | PINTT | PIWNTT ...).
        int tSets = 0;
        for (int i = layout.Length - 1; i >= 0 && layout[i] == 'T'; i--) tSets++;

        PassKind pk = pass switch
        {
            "Non" => PassKind.Opaque,
            "Sdw" => PassKind.Shadow,
            "Dep" => PassKind.Depth,
            "DepAlp" => PassKind.DepthAlpha,
            "Nrm" or "PntNum" => PassKind.Debug,
            _ => PassKind.Other,
        };

        return new LibVpName
        {
            Family = family,
            Skinned = layout.Contains('W'),
            TangentSets = tSets,
            DiffuseUvSets = texgen.Count(c => c == 'D'),
            HasLightmapUv = texgen.EndsWith('L'),
            Pass = pk,
        };
    }
}
