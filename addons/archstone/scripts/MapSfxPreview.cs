using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Archstone;

// Optional, session-only layer exploration at authored MSB regions. This bounds the
// renderer's cost; it does not substitute for DeS's effect/activation state machines.
[Tool]
public partial class MapSfxPreview : Node3D
{
    private const int MaxSystems = 64;
    private const int MaxParticles = 8192;
    private const int BuildsPerRefresh = 2;
    private readonly SfxLoader _loader = new();
    private readonly Dictionary<int, SfxPreview> _active = new();
    // Evicted previews, hidden and paused; bounded, farthest freed first.
    private readonly Dictionary<int, SfxPreview> _parked = new();
    private const int MaxParked = 128;
    private readonly Dictionary<int, string> _unsupported = new();
    private readonly Dictionary<int, SfxPreview> _catalog = new();
    private int _omitted;
    private sealed class Placement
    {
        public int EffectId, EntityId = -1, DummyId = -1;
        public string Name = "", RegionName = "", Source = "";
        public Transform3D Transform = Transform3D.Identity, Attachment = Transform3D.Identity;
        public Node3D Anchor;
        public bool Object, Camera, Reviewed, Available = true;
        public int CameraSlot = -1;
    }
    private readonly Dictionary<int, Placement> _placements = new();
    private int _nextPlacement;
    private int _regionCount;
    private string _bank = "";
    private int _unresolved;
    private double _refresh;
    private bool _enabled;
    private string _mapPath = "";

    [Export(PropertyHint.File, "*.msb")] public string MapPath
    {
        get => _mapPath;
        set
        {
            if (_mapPath == value) return;
            _mapPath = value;
            if (IsInsideTree()) ReadMap();
        }
    }
    [Export] public bool PreviewEnabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            _refresh = 0;
            if (!value) ClearActive();
            SetProcess(value);
            Report();
        }
    }
    [Export(PropertyHint.Range, "1,100,1")] public float PreviewDistance { get; set; } = 40;
    [Export(PropertyHint.Range, "0,3,1")] public int EditorViewport { get; set; }
    private Vector3 _previewWindAcceleration;
    private int _zeroWaitPreviewHz = 60;
    [ExportGroup("VFX Preview Motion")]
    [Export] public Vector3 PreviewWindAcceleration
    {
        get => _previewWindAcceleration;
        set
        {
            if (!value.IsFinite()) return;
            value = value.LimitLength(20);
            if (value == _previewWindAcceleration) return;
            _previewWindAcceleration = value;
            foreach (var p in _catalog.Values.Concat(_active.Values).Concat(_parked.Values))
                if (GodotObject.IsInstanceValid(p)) p.PreviewWindAcceleration = value;
            _refresh = 0;
        }
    }
    [Export(PropertyHint.Enum, "30 Hz:30,60 Hz:60")] public int ZeroWaitPreviewHz
    {
        get => _zeroWaitPreviewHz;
        set
        {
            if (value is not (30 or 60) || value == _zeroWaitPreviewHz) return;
            _zeroWaitPreviewHz = value;
            foreach (var p in _catalog.Values.Concat(_active.Values).Concat(_parked.Values))
                if (GodotObject.IsInstanceValid(p)) p.ZeroWaitPreviewHz = value;
        }
    }
    [Export(PropertyHint.MultilineText)] public string Diagnostics { get; set; } =
        "Enable nearby billboard playback. Maximum 64 systems and 8192 allocated particles. " +
        "Supported distance variants and constant emission schedules are decoded; general native activation is not implemented.";

    public override void _Ready() => ReadMap();

    private void ReadMap()
    {
        ClearActive();
        _placements.Clear();
        ResetCameraSources();
        _unresolved = 0;
        _regionCount = 0;
        _refresh = 0;
        SetProcess(PreviewEnabled);
        try
        {
            _nextPlacement = 0;
            _objects.Clear();
            _dummyCache.Clear();
            _attachmentNotes.Clear();
            var reader = new MsbLoader();
            foreach (var p in reader.ReadSfxPlacements(MapPath))
                _placements.Add(_nextPlacement++, new Placement { EffectId = p.EffectId, EntityId = p.EntityID,
                    Name = p.Name, RegionName = p.RegionName,
                    // Native region→SFX pose is Ry·Rz·Rx (ELF research 9.5), not Godot's default YXZ.
                    Transform = new Transform3D(Basis.FromEuler(p.RotationDegrees * (Mathf.Pi / 180), EulerOrder.Yzx), p.Position) });
            _regionCount = _placements.Count;
            _unresolved = _loader.ReadEvents(MapPath).Count - _regionCount;
            foreach (var group in reader.ReadObjects(MapPath).Where(p => p.EntityID >= 0).GroupBy(p => p.EntityID))
                if (group.Count() == 1) _objects.Add(group.Key, group.Single());
                else AttachmentNote($"Ambiguous object entity {group.Key}; attachments skipped.");
            string block = System.IO.Path.GetFileNameWithoutExtension(MapPath);
            _bank = $"res://mounted/sfx/ds_sfxbnd_{block.Split('_')[0]}";
            if (PreviewReviewedObjectEffects) AddReviewedObjectEffects();
            if (PreviewCameraRegionEffects) AddReviewedCameraEffects();
            Report();
        }
        catch (Exception e)
        {
            PreviewEnabled = false;
            Diagnostics = "Cannot read map VFX: " + e.Message;
        }
    }

    public override void _Process(double delta)
    {
        if (!PreviewEnabled) return;
        UpdateObjectTransforms();
        _refresh -= delta;
        // Keep the existing quarter-second viewport lookup when no camera sources exist.
        if (_cameraSources.Count == 0 && _refresh > 0) return;
        Camera3D camera = GetViewport().GetCamera3D();
#if TOOLS
        if (Engine.IsEditorHint())
            camera = EditorInterface.Singleton.GetEditorViewport3D(Math.Clamp(EditorViewport, 0, 3)).GetCamera3D();
#endif
        UpdateCameraSources(camera);
        if (_refresh > 0) return;
        _refresh = 0.25;
        if (camera == null || !IsVisibleInTree())
        {
            ClearActive();
            Report();
            return;
        }
        // Compile a bounded number of effect descriptions per refresh, without particle arrays
        // or render nodes. Their coverage/cost is available before any placement competes for budget.
        int descriptions = 0;
        foreach (int id in _placements.Values.Select(p => p.EffectId).Distinct())
        {
            if (_catalog.ContainsKey(id) || _unsupported.ContainsKey(id)) continue;
            if (descriptions++ >= BuildsPerRefresh) break;
            var description = _loader.InstantiateLayerPreview(_bank, id);
            if (description == null || description.CoverageRadius == 0 && description.LayerCount == 0)
            {
                _unsupported[id] = description == null ? _loader.LastError :
                    string.Join(" ", description.Diagnostics.Split('\n').Where(line => line.StartsWith("SKIPPED")).Take(1));
                if (string.IsNullOrEmpty(_unsupported[id])) _unsupported[id] = "No supported initial-state billboard creation; see the single-effect accuracy report.";
                description?.Free();
            }
            else
            {
                description.PreviewWindAcceleration = PreviewWindAcceleration;
                description.ZeroWaitPreviewHz = ZeroWaitPreviewHz;
                _catalog.Add(id, description);
            }
        }
        foreach (int index in _active.Keys.Where(i => !GodotObject.IsInstanceValid(_active[i])).ToArray())
            _active.Remove(index);
        foreach (int index in _parked.Keys.Where(i => !GodotObject.IsInstanceValid(_parked[i]) || !_placements.ContainsKey(i)).ToArray())
            _parked.Remove(index);
        float distance = Math.Clamp(PreviewDistance, 1, 100);
        var candidates = _placements.Keys
            .Where(i => _placements[i].Available && _catalog.ContainsKey(_placements[i].EffectId))
            .Select(i => {
                var description = _catalog[_placements[i].EffectId];
                float origin = camera.GlobalPosition.DistanceTo(ToGlobal(_placements[i].Transform.Origin));
                var scale = (GlobalBasis * _placements[i].Transform.Basis).Scale.Abs();
                float radius = description.CoverageRadius * Math.Max(scale.X, Math.Max(scale.Y, scale.Z));
                return (Index: i, Origin: origin, Coverage: Math.Max(0, origin - radius), Radius: radius, Cost: description.Cost(origin));
            })
            .Where(p => p.Coverage <= distance + (_active.ContainsKey(p.Index) ? 2 : 0) && p.Cost.Systems > 0)
            // Budget priority is apparent size, so a large nearby field outranks small closer
            // effects (Nexus ground mist was evicted by stair candles). Active effects keep a
            // 10% margin against churn at nearly equal priorities.
            .OrderBy(p => p.Origin / (p.Radius + 1) * (_active.ContainsKey(p.Index) ? 0.9f : 1))
            .ThenBy(p => p.Origin).ThenBy(p => p.Index).ToArray();
        var wanted = new HashSet<int>();
        int systemsLeft = MaxSystems, particlesLeft = MaxParticles;
        _omitted = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Cost.Systems > systemsLeft || candidate.Cost.Particles > particlesLeft) { _omitted++; continue; }
            wanted.Add(candidate.Index);
            systemsLeft -= candidate.Cost.Systems; particlesLeft -= candidate.Cost.Particles;
        }
        foreach (int index in _active.Keys.Where(i => !wanted.Contains(i)).ToArray())
        {
            _active[index].Park(); _parked[index] = _active[index]; _active.Remove(index);
        }
        while (_parked.Count > MaxParked)
        {
            int far = _parked.Keys.MaxBy(i => camera.GlobalPosition.DistanceSquaredTo(ToGlobal(_placements[i].Transform.Origin)));
            _parked[far].Free(); _parked.Remove(far);
        }
        // ponytail: caps count selected LOD bands only; outgoing bands keep their (emptying)
        // buffers while the effect stays active, so allocation can exceed them up to 5x.
        foreach (var c in candidates.Where(c => wanted.Contains(c.Index) && _active.ContainsKey(c.Index)))
        {
            var active = _active[c.Index];
            active.EditorViewportIndex = Math.Clamp(EditorViewport, 0, 3);
            active.SetViewDistance(c.Origin);
        }
        int builds = 0;
        foreach (var candidate in candidates)
        {
            int index = candidate.Index;
            var placement = _placements[index];
            if (!wanted.Contains(index) || _active.ContainsKey(index)) continue;
            if (builds++ >= BuildsPerRefresh) break;
            if (_parked.Remove(index, out var parked))
            {
                parked.Transform = placement.Transform;
                parked.Resume(candidate.Origin);
                _active.Add(index, parked);
                continue;
            }
            var preview = _catalog[placement.EffectId].CreatePlacementPreview();
            preview.PreviewWindAcceleration = PreviewWindAcceleration;
            preview.ZeroWaitPreviewHz = ZeroWaitPreviewHz;
            preview.FollowCamera = false;
            preview.SetViewDistance(candidate.Origin);
            preview.WarmUpOnActivation = true;
            preview.PlacementSeed = (uint)(index * 7919);
            preview.EditorViewportIndex = Math.Clamp(EditorViewport, 0, 3);
            preview.Name = $"SFX_{placement.EffectId}_{index}";
            preview.Transform = placement.Transform;
            preview.SetMeta("sfx_camera_slot", placement.CameraSlot);
            preview.SetMeta("sfx_entity_id", placement.EntityId);
            preview.SetMeta("sfx_dummy_id", placement.DummyId);
            preview.SetMeta("sfx_source", placement.Source);
            preview.SetMeta("sfx_event", placement.Name);
            preview.SetMeta("sfx_region", placement.RegionName);
            AddChild(preview);
            preview.Playing = true;
            _active.Add(index, preview);
        }
        Report();
    }

    private void ClearActive()
    {
        _omitted = 0;
        foreach (var preview in _active.Values.Concat(_parked.Values))
            if (GodotObject.IsInstanceValid(preview)) preview.Free();
        _active.Clear();
        _parked.Clear();
        _loader.ClearCache();
        _unsupported.Clear();
        foreach (var description in _catalog.Values) description.Free();
        _catalog.Clear();
    }

    public override void _Notification(int what)
    {
        if (what != NotificationPredelete) return;
        // Godot frees owned child nodes. Drop managed resource references as well.
        _active.Clear();
        _parked.Clear();
        foreach (var description in _catalog.Values) description.Free();
        _catalog.Clear();
        _loader.ClearCache();
    }

    // Read by the debug overlay; same figures as Diagnostics' budget line.
    public Godot.Collections.Dictionary GetSummary() => new() {
        ["enabled"] = PreviewEnabled, ["placements"] = _placements.Count, ["unresolved"] = _unresolved,
        ["active"] = _active.Count, ["parked"] = _parked.Count, ["omitted"] = _omitted,
        ["systems"] = _active.Values.Sum(p => p.LayerCount), ["max_systems"] = MaxSystems,
        ["particles"] = _active.Values.Sum(p => p.ParticleCapacity), ["max_particles"] = MaxParticles,
        ["live"] = _active.Values.Sum(p => p.LiveParticleCount),
        ["catalog"] = _catalog.Count, ["unsupported"] = _unsupported.Count
    };

    private void Report() => Diagnostics =
        $"BILLBOARD PREVIEW ({(PreviewEnabled ? "enabled" : "disabled")}) — supported LOD and constant schedules; general native activation is not executed.\n" +
        $"{_regionCount} authored region placements; {_unresolved} unresolved; {_placements.Values.Count(p => p.Object)} object requests; {_placements.Values.Count(p => p.Camera)} camera requests.\n" +
        $"{_active.Count} active previews; {_active.Values.Sum(p => p.LayerCount)}/{MaxSystems} systems; {_active.Values.Sum(p => p.ParticleCapacity)}/{MaxParticles} allocated particles. {_omitted} nearby effects omitted by budget.\n" +
        "Selection includes emitter coverage. Ambient particles warm up on activation; evicted effects restart on return.\n" +
        $"Motion remains approximate. Preview wind acceleration: {PreviewWindAcceleration}; zero-wait preview clock: {ZeroWaitPreviewHz}Hz (native cadence unverified).\n" +
        string.Join("\n", _unsupported.Select(p => $"Unsupported effect {p.Key}: {p.Value}")) + "\n" +
        "Object preview uses explicit requests; reviewed examples assume their effects are enabled, without executing Lua or changing game flags.\n" +
        string.Join("\n", _placements.Values.Where(p => p.Object).Select(p =>
            $"Object {p.EntityId}, dummy {p.DummyId}, effect {p.EffectId}: {(p.Available ? "resolved" : "hidden/missing target")}; {p.Source}")) + "\n" +
        string.Join("\n", _attachmentNotes) + "\n" + CameraDiagnostics();
}
