using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace Archstone;

public partial class MapSfxPreview
{
    private const int MaxCameraSlots = 8;
    private sealed class CameraSource
    {
        public int PlacementIndex;
        public MsbLoader.RegionBox[] Regions = Array.Empty<MsbLoader.RegionBox>();
        public int InsideCount;
    }
    private readonly List<CameraSource> _cameraSources = new();
    private readonly Dictionary<int, MsbLoader.RegionBox> _cameraRegions = new();
    private readonly List<string> _cameraNotes = new();
    private bool _cameraRegionsRead, _previewCameraRegionEffects;
    private ulong _lastCameraId;
    private NodePath _cameraRegionProbe = new NodePath("");

    [ExportGroup("Camera Region Effects")]
    [Export] public bool PreviewCameraRegionEffects
    {
        get => _previewCameraRegionEffects;
        set
        {
            if (_previewCameraRegionEffects == value) return;
            _previewCameraRegionEffects = value;
            if (!IsInsideTree()) return;
            foreach (var source in _cameraSources.Where(s => _placements[s.PlacementIndex].Reviewed).ToArray())
            {
                RemovePlacement(source.PlacementIndex);
                _cameraSources.Remove(source);
            }
            if (value) AddReviewedCameraEffects();
            _refresh = 0;
            Report();
        }
    }
    // Empty: editor/runtime camera is a deliberate player-position stand-in. A supplied
    // Node3D can be the eventual player; a broken path disables region activation.
    [Export] public NodePath CameraRegionProbe
    {
        get => _cameraRegionProbe;
        set { _cameraRegionProbe = value ?? new NodePath(""); _refresh = 0; }
    }

    // Explicit slot requests override the reviewed region producer until removed.
    // These use the same catalog and allocation budgets as every other source.
    public bool RequestCameraEffect(int slot, int effectId)
    {
        if (!IsInsideTree() || slot < 0 || slot >= MaxCameraSlots || effectId <= 0) return false;
        var existing = _cameraSources.FirstOrDefault(s => !_placements[s.PlacementIndex].Reviewed &&
            _placements[s.PlacementIndex].CameraSlot == slot);
        if (existing != null)
        {
            if (_placements[existing.PlacementIndex].EffectId == effectId) return true;
            RemovePlacement(existing.PlacementIndex);
            _cameraSources.Remove(existing);
        }
        AddCameraSource(slot, effectId, false, Array.Empty<MsbLoader.RegionBox>(), "Explicit camera slot request");
        return true;
    }

    public void RemoveCameraEffect(int slot)
    {
        foreach (var source in _cameraSources.Where(s => !_placements[s.PlacementIndex].Reviewed &&
                     _placements[s.PlacementIndex].CameraSlot == slot).ToArray())
        {
            RemovePlacement(source.PlacementIndex);
            _cameraSources.Remove(source);
        }
        _refresh = 0;
        Report();
    }

    private void AddCameraSource(int slot, int effect, bool reviewed, MsbLoader.RegionBox[] regions, string provenance)
    {
        int index = _nextPlacement++;
        _placements.Add(index, new Placement { Camera = true, CameraSlot = slot, EffectId = effect,
            Reviewed = reviewed, Available = false, Name = $"CameraSlot_{slot}", Source = provenance,
            RegionName = string.Join(", ", regions.Select(r => $"{r.EntityID}: {r.Name}")) });
        _cameraSources.Add(new CameraSource { PlacementIndex = index, Regions = regions });
        _refresh = 0;
    }

    private void ResetCameraSources()
    {
        _cameraSources.Clear();
        _cameraRegions.Clear();
        _cameraNotes.Clear();
        _cameraRegionsRead = false;
        _lastCameraId = 0;
    }

    private void AddReviewedCameraEffects()
    {
        string block = Path.GetFileNameWithoutExtension(MapPath);
        var association = block switch {
            "m04_01_00_00" => (Effect: 94200, Regions: new[] { 2220 }, Lines: "718/721,2284/2295"),
            "m05_01_00_00" => (Effect: 95202, Regions: new[] { 2260 }, Lines: "586/588,2065/2075"),
            "m06_00_00_00" => (Effect: 96000, Regions: new[] { 2300, 2301, 2302 }, Lines: "872–878,3397–3414"),
            _ => (Effect: 0, Regions: Array.Empty<int>(), Lines: "")
        };
        if (association.Effect == 0) { CameraNote("No reviewed camera-region association for this map."); return; }
        try
        {
            if (!_cameraRegionsRead)
            {
                foreach (var group in new MsbLoader().ReadRegionBoxes(MapPath).GroupBy(r => r.EntityID))
                    if (group.Count() == 1) _cameraRegions.Add(group.Key, group.Single());
                _cameraRegionsRead = true;
            }
            if (association.Regions.Any(id => !_cameraRegions.ContainsKey(id)))
            { CameraNote("Camera source skipped: missing, invalid, non-box or ambiguous trigger region."); return; }
            AddCameraSource(0, association.Effect, true, association.Regions.Select(id => _cameraRegions[id]).ToArray(),
                $"{block}.lua:{association.Lines}; inside any listed region, one effect in slot0");
            if (association.Effect == 95202)
                CameraNote("Fly effect95202 remains unsupported (template2020); its region can be tested but no substitute particles are emitted.");
        }
        catch (Exception e) { CameraNote("Cannot read camera regions: " + e.Message); }
    }

    private void CameraNote(string note)
    {
        if (_cameraNotes.Count < 16 && !_cameraNotes.Contains(note)) _cameraNotes.Add(note);
    }

    private void UpdateCameraSources(Camera3D camera)
    {
        if (_cameraSources.Count == 0) return;
        bool validCamera = GodotObject.IsInstanceValid(camera) && camera.IsInsideTree() &&
            camera.GlobalTransform.IsFinite() && Math.Abs(camera.GlobalBasis.Determinant()) > 0.000001f;
        ulong cameraId = validCamera ? camera.GetInstanceId() : 0;
        bool changedCamera = cameraId != _lastCameraId;
        _lastCameraId = cameraId;
        bool validRoot = GlobalTransform.IsFinite() && Math.Abs(GlobalBasis.Determinant()) > 0.000001f;
        var inverse = validRoot ? GlobalTransform.AffineInverse() : Transform3D.Identity;
        Node3D probe = CameraRegionProbe.IsEmpty ? camera : GetNodeOrNull<Node3D>(CameraRegionProbe);
        bool validProbe = GodotObject.IsInstanceValid(probe) && probe.IsInsideTree() && probe.GlobalPosition.IsFinite();
        Vector3 probePosition = validProbe && validRoot ? inverse * probe.GlobalPosition : Vector3.Zero;
        // Authored camera effects place positive-front emitters ahead of the view. Our
        // decoded placements already mirror X; Godot cameras look down -Z, so flip Z here.
        // This is an explicit camera-space preview convention, not recovered native matrices.
        var cameraTransform = validCamera && validRoot
            ? inverse * new Transform3D(camera.GlobalBasis.Orthonormalized() * Basis.FromScale(new Vector3(1, 1, -1)), camera.GlobalPosition)
            : Transform3D.Identity;
        foreach (var source in _cameraSources)
        {
            var p = _placements[source.PlacementIndex];
            source.InsideCount = validRoot && validProbe ? source.Regions.Count(r => r.Contains(probePosition)) : 0;
            bool overridden = p.Reviewed && _cameraSources.Any(s => !_placements[s.PlacementIndex].Reviewed &&
                _placements[s.PlacementIndex].CameraSlot == p.CameraSlot);
            bool available = validCamera && validRoot && IsVisibleInTree() && !overridden &&
                (!p.Reviewed || PreviewCameraRegionEffects && source.InsideCount > 0);
            if (available != p.Available || changedCamera) _refresh = 0;
            p.Available = available;
            p.Transform = cameraTransform;
            if (_active.TryGetValue(source.PlacementIndex, out var active) && GodotObject.IsInstanceValid(active))
            {
                // A camera/viewport switch must not carry a trail from the old camera.
                if (!available || changedCamera) { active.Free(); _active.Remove(source.PlacementIndex); }
                else active.Transform = cameraTransform;
            }
        }
    }

    public Godot.Collections.Array<Godot.Collections.Dictionary> GetCameraRegionSummary()
    {
        var result = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var source in _cameraSources)
        {
            var p = _placements[source.PlacementIndex];
            foreach (var region in source.Regions)
                result.Add(new Godot.Collections.Dictionary { ["entity_id"] = region.EntityID, ["name"] = region.Name,
                    ["transform"] = region.Transform, ["size"] = region.Size, ["effect_id"] = p.EffectId,
                    ["slot"] = p.CameraSlot, ["inside_count"] = source.InsideCount, ["requested"] = p.Available });
        }
        return result;
    }

    private string CameraDiagnostics() =>
        "Camera regions: " + (PreviewCameraRegionEffects ? "enabled" : "disabled") + "; probe: " +
        (CameraRegionProbe.IsEmpty ? "selected camera (player stand-in)" : CameraRegionProbe.ToString()) + ".\n" +
        "Camera pose/forward mapping and immediate exit cleanup are preview conventions; native attachment/lifetime details remain unverified.\n" +
        string.Join("\n", _cameraSources.Select(s => {
            var p = _placements[s.PlacementIndex];
            return $"Camera slot {p.CameraSlot}, effect {p.EffectId}: {(p.Available ? "requested" : "inactive")}, " +
                $"inside {s.InsideCount}/{s.Regions.Length} regions; {p.Source}";
        })) + "\n" + string.Join("\n", _cameraNotes);
}
