using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SoulsFormats;

namespace Archstone;

// Read-only access to mounted/sfx/<bank>/ - loose files AssetExtractor already unpacked from
// the user's plain FFXBND archives (see AssetExtractor.KnownCategories/FallbackEntryOutputPath;
// each bank keeps its own folder so identically-named entries in different banks, e.g. both
// main and commoneffects ship a real, different f0000512.ffx, never collide on disk). No
// archive reading, executable scripts, process-global cache, or edits to SoulsFormats here.
public partial class SfxLoader : RefCounted
{
    private const int MaxEntryBytes = 8 * 1024 * 1024;
    private static readonly object FfxReadGate = new(); // All new FFX reads use this gate.
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.Ordinal);
    private long _textureBytes;

    public string LastError { get; private set; } = "";

    private static string EffectFileName(int id) => $"f{id:D7}.ffx";

    public int[] GetEffectIds(string bankDir)
    {
        LastError = "";
        try
        {
            string dir = ProjectSettings.GlobalizePath(bankDir);
            if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"Not a mounted SFX bank folder: {dir}");
            // Effect IDs aren't all 7 digits (e.g. f100000028.ffx) - match by prefix/suffix only,
            // not a fixed length, the same way the original bank-entry scan did.
            return Directory.GetFiles(dir, "*.ffx")
                .Select(p => Path.GetFileNameWithoutExtension(p))
                .Where(n => n.Length > 1 && n[0] is 'f' or 'F')
                .Select(n => int.TryParse(n.AsSpan(1), out var id) ? id : -1)
                .Where(id => id >= 0).Distinct().OrderBy(id => id).ToArray();
        }
        catch (Exception e) { LastError = e.Message; return Array.Empty<int>(); }
    }

    // previewAmount remains for existing callers; authored schedules now determine counts.
    public SfxPreview InstantiateLayerPreview(string bankDir, int effectId, int previewAmount = 32, int blendMode = -1)
    {
        LastError = "";
        SfxPreview preview = null;
        try
        {
            if (effectId < 0 || previewAmount < 1 || previewAmount > 256 || blendMode < -1 || blendMode > 2)
                throw new ArgumentOutOfRangeException(nameof(effectId), "Invalid ID, amount (1–256), or preview blend (-1–2).");
            var dir = Path.GetFullPath(ProjectSettings.GlobalizePath(bankDir));
            var resolvedDir = dir;
            var path = Path.Combine(dir, EffectFileName(effectId));
            if (!File.Exists(path))
            {
                resolvedDir = Path.Combine(Path.GetDirectoryName(dir)!, "ds_sfxbnd_commoneffects");
                path = Path.Combine(resolvedDir, EffectFileName(effectId));
            }
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxEntryBytes || info.Length < 4)
                throw new FileNotFoundException($"Effect {effectId} not found in selected bank or commoneffects.");
            var bytes = File.ReadAllBytes(path);
            if (System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "DLSE")
                throw new NotSupportedException("Only DeS DLSE effects are supported by this preview.");
            FFXDLSE.FXEffect effect;
            lock (FfxReadGate) effect = FFXDLSE.Read(bytes).Effect;
            preview = new SfxPreview { Name = $"SFX_{effectId}_LAYER_PREVIEW" };
            preview.Build(effect, resolvedDir, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                blendMode, id => Texture(resolvedDir, id), id => VerifiedTemplate(resolvedDir, id));
            return preview;
        }
        catch (Exception e)
        {
            preview?.Free();
            LastError = $"SFX {effectId}: {e.Message}";
            return null;
        }
    }

    // Fingerprint the audited template code, not the effect's arguments. Modified templates
    // must be audited before assigning these semantics; user-authored arguments remain data.
    private bool VerifiedTemplate(string bank, int id)
    {
        string expected = id switch {
            2101 => "635A95669A2177C1FB0921A40372FE1CB0B43B83D75937A025C8B24C2B6D284C",
            2117 => "A9F384F23C14CFD2096B512ED6468E622FBB6CBCE02BCCC8BDC3BE1AFCB2DA2B",
            2023 => "68BDDF1D5B462DE5BAEB280D0E36AE8801B569DA2B3B2BC940730209CE8FF532",
            2121 => "508BF8A2EF50EDB0D8C88C419714129FEDAE5E365F00EB97AE2A6F2E9D0DC07C",
            2123 => "4CB5E76551BED84D611E8C2F8787D03801FD86AB0B8847FD5F3516A5DE7EDCBA",
            _ => ""
        };
        if (expected == "") return false;
        string path = Path.Combine(bank, EffectFileName(id));
        if (!File.Exists(path)) path = Path.Combine(Path.GetDirectoryName(bank)!, "ds_sfxbnd_commoneffects", EffectFileName(id));
        if (!File.Exists(path) || new FileInfo(path).Length > MaxEntryBytes) return false;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) == expected;
    }

    private Texture2D Texture(string effectBankDir, int id)
    {
        if (id == 0) throw new InvalidDataException("Texture 0 is absent; no invented white-texture fallback.");
        foreach (var dir in new[] { effectBankDir, Path.Combine(Path.GetDirectoryName(effectBankDir)!, "ds_sfxbnd_commonresources") }.Distinct())
        {
            if (!Directory.Exists(dir)) continue;
            // Parse numeric resource stems; padding varies. Don't search unrelated banks.
            var matches = Directory.GetFiles(dir, "*.tpf").Where(p => {
                var name = Path.GetFileNameWithoutExtension(p);
                return name.Length > 1 && int.TryParse(name.AsSpan(1), out var resourceId) && resourceId == id;
            }).ToArray();
            if (matches.Length == 0) continue;
            if (matches.Length != 1) throw new InvalidDataException($"Ambiguous texture resource {id} in {dir}");
            var key = matches[0];
            if (_textures.TryGetValue(key, out var result)) return result;
            var entryInfo = new FileInfo(matches[0]);
            if (entryInfo.Length > MaxEntryBytes) throw new InvalidDataException($"Texture resource {id} exceeds preview size limit.");
            var tpf = TPF.Read(File.ReadAllBytes(matches[0]));
            if (tpf.Textures.Count != 1 || tpf.Textures[0].Type != TPF.TexType.Texture)
                throw new NotSupportedException($"Texture resource {id}: preview requires exactly one 2D texture.");
            var dds = Headerizer.Headerize(tpf.Textures[0], out _);
            if (dds.Length < 128) throw new InvalidDataException("Short DDS after headerization.");
            int height = BitConverter.ToInt32(dds, 12), width = BitConverter.ToInt32(dds, 16);
            if (width < 1 || height < 1 || width > 4096 || height > 4096)
                throw new InvalidDataException("SFX texture dimensions exceed preview limits.");
            long estimate = checked((long)width * height * 4 * 4 / 3);
            if (_textureBytes + estimate > 64 * 1024 * 1024)
                throw new InvalidDataException("SFX texture cache budget exceeded.");
            using var stream = new MemoryStream(dds);
            using var decoded = Pfim.Pfimage.FromStream(stream);
            int channels = decoded.Format == Pfim.ImageFormat.Rgba32 ? 4 : decoded.Format == Pfim.ImageFormat.Rgb24 ? 3 : 0;
            if (channels == 0 || decoded.Width != width || decoded.Height != height)
                throw new NotSupportedException($"Unsupported decoded texture {id}: {decoded.Format}");
            var rgba = new byte[checked(width * height * 4)];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int src = checked(y * decoded.Stride + x * channels), dst = (y * width + x) * 4;
                    rgba[dst] = decoded.Data[src + 2]; rgba[dst + 1] = decoded.Data[src + 1];
                    rgba[dst + 2] = decoded.Data[src]; rgba[dst + 3] = channels == 4 ? decoded.Data[src + 3] : (byte)255;
                }
            using var image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);
            image.GenerateMipmaps(); // Explicit preview approximation, like the existing 2D loader.
            result = ImageTexture.CreateFromImage(image);
            _textures.Add(key, result); _textureBytes += estimate;
            return result;
        }
        throw new FileNotFoundException($"Texture resource {id} absent in effect bank/commonresources.");
    }

    // Includes unresolved events, so callers can report omissions without manufacturing anchors.
    public Godot.Collections.Array<Godot.Collections.Dictionary> ReadEvents(string msbPath)
    {
        var result = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        LastError = "";
        try
        {
            var map = MSBD.Read(ProjectSettings.GlobalizePath(msbPath));
            foreach (var e in map.Events.SFX)
            {
                var region = MsbLoader.SfxRegion(map, e);
                result.Add(new Godot.Collections.Dictionary {
                    ["name"] = e.Name, ["entity_id"] = e.EntityID, ["effect_id"] = e.EffectID, ["part"] = e.PartName ?? "",
                    ["region"] = e.RegionName ?? "", ["unknown_t00"] = e.UnkT00,
                    ["placement_region"] = region?.Name ?? "",
                    ["position"] = region == null ? default(Vector3) : new Vector3(-region.Position.X, region.Position.Y, region.Position.Z),
                    ["rotation_degrees"] = region == null ? default(Vector3) : new Vector3(region.Rotation.X, -region.Rotation.Y, -region.Rotation.Z),
                    ["placement_status"] = region == null ? "Unresolved SFX region index" : "Resolved SFX region index" });
            }
        }
        catch (Exception e) { LastError = e.Message; }
        return result;
    }

    public void ClearCache() { _textures.Clear(); _textureBytes = 0; }
}
