using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SoulsFormats;
using P = SoulsFormats.FFXDLSE.Param;

namespace Archstone;

// Bounded billboard playback for verified DeS templates; not a general StateMap interpreter.
[Tool]
public partial class SfxPreview : Node3D
{
    // The sprite output reads the frame exposure; standalone previews have no map pipeline.
    public SfxPreview() => PostProcessPipeline.BindFallback();

    private sealed class Layer
    {
        public SfxBatchParticles Particles;
        public Transform3D Placement;
        public float Life;
        public Vector3 BaseGravity;
        public float WindPower;
        public int Capacity, Batch;
        public double Interval, Delay;
        public float MinDistance, MaxDistance;
        public bool Local;
        public QuadMesh Mesh;
        public ParticleProcessMaterial Process;
        public List<P> Emitter;
        public int EmitterId;
        public List<P> Motion;
        public ShaderMaterial Material;
        public int NativeTransparency;
        public float WindPreview; // |PreviewWindAcceleration|, used for coverage only
        public SfxBatchParticles.Appearance Appearance;
        public SfxBatchParticles.NativeEmit32 Emit32;
        public SfxBatchParticles.NativeMotion84 Motion84;
        public SfxBatchParticles.NativeDirection Direction;
        public SfxBatchParticles.NativeFinite Finite;
        public SfxBatchParticles.NativeCarrier Carrier;
        public float CarrierExtent;
        // Constant container rotation (action34), degrees/s, about SpinPivot.
        public Vector3 Spin;
        public Transform3D SpinPivot;

        public Layer CopyForPlacement()
        {
            var copy = (Layer)MemberwiseClone();
            copy.Particles = null;
            // Curves, textures, authored parameters and sampled appearance are read-only.
            // Wind and blend overrides mutate these outer resources, so isolate them.
            copy.Process = (ParticleProcessMaterial)Process.Duplicate();
            copy.Material = (ShaderMaterial)Material.Duplicate();
            copy.Mesh = (QuadMesh)Mesh.Duplicate();
            copy.Mesh.Material = copy.Material;
            return copy;
        }
    }
    // Map placements reuse the catalog's verified/prepared recipe. No disk reads, parsing,
    // template hashing, texture decoding or curve construction on movement into range.
    internal SfxPreview CreatePlacementPreview()
    {
        var copy = new SfxPreview {
            Name = Name, Diagnostics = Diagnostics, _blend = _blend,
            _previewWindAcceleration = _previewWindAcceleration,
            _zeroWaitPreviewHz = _zeroWaitPreviewHz, CoverageRadius = CoverageRadius
        };
        try
        {
            foreach (var layer in _layers) copy._layers.Add(layer.CopyForPlacement());
            return copy;
        }
        catch { copy.Free(); throw; }
    }
    private readonly List<Layer> _layers = new();
    private readonly List<string> _notes = new();
    private readonly HashSet<int> _actions = new();
    private int _visited;
    private double _age;
    private bool _playing;
    private int _blend;
    private const int MaxLayers = 32;
    private float _viewDistance;
    private bool _activated;
    private double _lodRefresh;
    [Export] public bool FollowCamera { get; set; } = true;
    public double StalledSeconds { get; private set; }
    internal int EditorViewportIndex { get; set; }
    public bool WarmUpOnActivation { get; set; }
    public uint PlacementSeed { get; set; }
    private Func<int, bool> _verifiedTemplate;
    private float _minDistance, _maxDistance = float.PositiveInfinity;
    private List<P> _schedule;
    private Transform3D _parentPlacement = Transform3D.Identity;
    // Outermost constant container rotation in scope and the placement it pivots about.
    private Vector3 _spin;
    private Transform3D _spinPivot = Transform3D.Identity;
    // Enclosing template2101 startup delay and destroy time (after that delay), for its children.
    private double _containerDelay, _containerLife = double.PositiveInfinity;
    private const int MaxNodes = 100000;

    [ExportGroup("VFX Playback")]
    [Export] public bool Playing
    {
        get => _playing;
        set
        {
            _playing = value;
            SetProcess(value && _layers.Count > 0);
            
        }
    }
    [ExportToolButton("Restart", Icon = "Reload")]
    public Callable RestartButton => Callable.From(RestartPreview);
    [ExportToolButton("Copy Accuracy Report", Icon = "ActionCopy")]
    public Callable CopyReportButton => Callable.From(() => DisplayServer.ClipboardSet(
        $"{Diagnostics}\n\nPreview age: {_age:F3}s; excess stall time paused: {StalledSeconds:F3}s; live particles: {LiveParticleCount}/{ParticleCapacity}; playing: {Playing}; blend override: {_blend} (-1 = automatic); preview wind acceleration: {PreviewWindAcceleration}; zero-wait preview clock: {ZeroWaitPreviewHz}Hz.\nObserved difference: "));
    [ExportToolButton("Clear Preview", Icon = "Remove")]
    public Callable ClearButton => Callable.From(() => { Playing = false; QueueFree(); });

    [ExportGroup("VFX Advanced")]
    [Export(PropertyHint.Enum, "Automatic (provisional):-1,Alpha:0,Additive:1,Subtractive:2")]
    public int BlendOverride
    {
        get => _blend;
        set
        {
            _blend = Math.Clamp(value, -1, 2);
            foreach (var l in _layers) l.Material.Shader = PreviewShader(_blend < 0 ? AutomaticBlend(l.NativeTransparency) : _blend);
        }
    }
    private Vector3 _previewWindAcceleration;
    private int _zeroWaitPreviewHz = 60;
    // These are explicit preview conventions, not recovered native environmental state.
    [Export] public Vector3 PreviewWindAcceleration
    {
        get => _previewWindAcceleration;
        set
        {
            if (!value.IsFinite()) return;
            value = value.LimitLength(20);
            if (value == _previewWindAcceleration) return;
            _previewWindAcceleration = value;
            foreach (var l in _layers) { l.Process.Gravity = l.BaseGravity + value * l.WindPower; l.WindPreview = value.Length(); }
            CoverageRadius = _layers.Count == 0 ? 0 : _layers.Max(Coverage);
            RestartWithSettings();
        }
    }
    [Export(PropertyHint.Enum, "30 Hz:30,60 Hz:60")] public int ZeroWaitPreviewHz
    {
        get => _zeroWaitPreviewHz;
        set
        {
            if (value is not (30 or 60) || value == _zeroWaitPreviewHz) return;
            _zeroWaitPreviewHz = value;
            RestartWithSettings();
        }
    }
    private void RestartWithSettings()
    {
        foreach (var l in _layers) { l.Particles?.Free(); l.Particles = null; }
        _age = 0; StalledSeconds = 0; _activated = false;
        if (IsInsideTree()) AttachLayers();
    }
    [Export(PropertyHint.MultilineText)] public string Diagnostics { get; set; } = "";
    private bool Selected(Layer l, float distance) => distance >= l.MinDistance && distance < l.MaxDistance;
    public int LayerCount => _layers.Count(l => Selected(l, _viewDistance));
    public int ParticleCapacity => _layers.Where(l => Selected(l, _viewDistance)).Sum(l => l.Capacity);
    public int LiveParticleCount => _layers.Sum(l => l.Particles?.LiveCount ?? 0);
    internal (int Systems, int Particles) Cost(float distance) =>
        (_layers.Count(l => Selected(l, distance)), _layers.Where(l => Selected(l, distance)).Sum(l => l.Capacity));
    internal void PrepareViewDistance(float distance)
    {
        _viewDistance = distance;
        // Template2117 re-enters selection and creates the new band's child; what happens to
        // the outgoing child is untraced. Preview convention: it stops emitting and its live
        // particles expire, so band crossings hand off without a pop. The emptied layer is
        // kept (no draw) to avoid node churn; see Park().
        foreach (var l in _layers)
            if (l.Particles != null && !Selected(l, distance)) l.Particles.Emitting = false;
    }
    public void SetViewDistance(float distance)
    {
        if (!float.IsFinite(distance) || distance < 0) return;
        PrepareViewDistance(distance);
        if (IsInsideTree()) AttachLayers();
    }
    // Conservative emitter coverage, including placement and lifetime travel. Used only for
    // preview selection; native LOD still uses distance to the effect origin.
    public float CoverageRadius { get; private set; }
    private static float Coverage(Layer l)
    {
        var p = l.Process;
        float life = Math.Min(l.Life, 60); // permanent sprites: bound travel like any other layer
        float extent = p.EmissionShape switch {
            ParticleProcessMaterial.EmissionShapeEnum.Box => p.EmissionBoxExtents.Length(),
            ParticleProcessMaterial.EmissionShapeEnum.Ring => p.EmissionRingRadius,
            ParticleProcessMaterial.EmissionShapeEnum.Sphere => p.EmissionSphereRadius,
            _ => 0
        };
        var scale = (CurveXyzTexture)p.ScaleCurve;
        float sprite = new Vector2(scale.CurveX.MaxValue, scale.CurveY.MaxValue).Length() * 0.5f;
        if (l.Emit32 != null)
            sprite *= new[] { l.Emit32.X.X, l.Emit32.X.Y, l.Emit32.Y.X, l.Emit32.Y.Y }.Max(Math.Abs);
        float motion84 = l.Motion84 == null ? 0 : Math.Abs(l.Motion84.Gravity) * life * life * 0.5f
            + Math.Abs(l.Motion84.Wind) * l.WindPreview * life;
        // A rotating container can swing the layer origin anywhere on its pivot sphere.
        float origin = l.Spin == Vector3.Zero ? l.Placement.Origin.Length()
            : l.SpinPivot.Origin.Length() + (l.SpinPivot.AffineInverse() * l.Placement).Origin.Length();
        return origin + l.CarrierExtent + extent + sprite + p.InitialVelocityMax * life
            + p.Gravity.Length() * life * life * 0.5f + motion84;
    }
    public double PreviewAge => _age;

    // Texture-matched native captures: candle transparency0 and firefly transparency4 use
    // SRC_ALPHA/ONE; dry-ice transparency2 uses SRC_ALPHA/ONE_MINUS_SRC_ALPHA.
    // Other values still use a provisional alpha fallback; this is not the MTD blend enum.
    private static int AutomaticBlend(int native) => native is 0 or 4 ? 1 : 0;

    private static Shader PreviewShader(int mode) => GD.Load<Shader>(
        $"res://addons/archstone/shaders/sfx_preview{(mode == 1 ? "_add" : mode == 2 ? "_sub" : "")}.gdshader");

    internal void Build(FFXDLSE.FXEffect effect, string bank, string hash, int blend, Func<int, Texture2D> texture, Func<int, bool> verifiedTemplate)
    {
        _blend = blend;
        _verifiedTemplate = verifiedTemplate;
        _notes.Add($"SUPPORTED BILLBOARD PLAYBACK — not the complete native effect.\n{bank}\nEffect {effect.ID}; SHA256 {hash}");
        _notes.Add("Verified template2117 selects exclusive distance variants; template2023 supplies constant batches, intervals and capacity. General activation/triggers are not executed. Unsupported schedules are skipped, never replaced by a fixed density.");
        _notes.Add("CPU scheduled instanced billboards support Compatibility. Tick assumed seconds; linear curve interpolation; Godot-style motion/distributions remain approximations. Type2 sprites intersect scene depth through their finite thickness; no atmosphere or output pass. Capture supports additive candles/fireflies and alpha fog; the full transparency enum remains provisional.");
        if (effect.AggregateChildren is { Count: > 0 })
            _notes.Add("UNSUPPORTED: aggregate envelope. No child selected automatically.");
        else
        {
            // Argument lists are definitions, not entry points. Resolve only references from
            // the initial state's creation actions, avoiding duplicate definition/execution walks.
            _rootArguments = effect.ParamList1.Params;
            for (int s = 0; s < Math.Min(1, effect.StateMap.States.Count); s++)
            {
                var state = effect.StateMap.States[s];
                Visit(state.Actions.Select(a => (P)new FFXDLSE.Param32 { ActionID = a.ID, ParamList = a.ParamList }).ToList(),
                    $"state[{s}]", 0, 0, texture);
            }
        }
        foreach (float d in _layers.Select(l => l.MinDistance).Distinct())
            if (Cost(d).Particles > 8192) throw new InvalidDataException("Selected effect exceeds the 8192-particle preview budget.");
        _notes.Add("Geometry: square and circle lie in the local XY plane and emit about +Z; box emits about each face normal; emissionType 1–3 use world frames (native). Sphere birth direction is unrecovered. Long editor stalls pause excess time; StalledSeconds reports it.");
        _notes.Add("Observed inline action IDs (not execution coverage): " + string.Join(", ", _actions.OrderBy(x => x)));
        _notes.Add($"Built {_layers.Count} layer(s). Session-only preview at an explicit user transform; no MSB anchor approximation.");
        Diagnostics = string.Join("\n\n", _notes);
        Playing = false;
    }

    private List<P> _rootArguments;
    // templates counts enclosing Param31 template references; native Param66 walks that many
    // parent contexts (ELF_ENGINE_ACCURACY_RESEARCH.md 2–5).
    private void Visit(List<P> parameters, string path, int depth, int templates, Func<int, Texture2D> texture)
    {
        if (depth > 64 || (_visited += parameters.Count) > MaxNodes)
            throw new InvalidDataException("SFX traversal budget exceeded.");
        for (int i = 0; i < parameters.Count; i++)
        {
            P parameter = parameters[i];
            if (parameter is FFXDLSE.Param53 { Unk04: 2 } reference)
            {
                if (reference.Unk08 < 0 || reference.Unk08 >= _rootArguments.Count)
                    throw new InvalidDataException("Invalid root effect argument reference.");
                parameter = _rootArguments[reference.Unk08];
            }
            string childPath = $"{path}/{i}";
            if (parameter is FFXDLSE.Param31 f)
            {
                if (f.EffectID == 0) continue;
                if (!_verifiedTemplate(f.EffectID)) { Note($"SKIPPED {childPath}: unverified template {f.EffectID}."); continue; }
                var args = f.ParamList.Params;
                if (f.EffectID == 2117)
                {
                    if (args.Count != 9) throw new InvalidDataException("Template2117 arity mismatch.");
                    float min = _minDistance, max = _maxDistance;
                    var limits = args.Skip(5).Select(Integer).ToArray();
                    if (limits[0] < 0 || limits.Zip(limits.Skip(1), (a,b) => a > b).Any(v => v))
                        throw new InvalidDataException("Invalid LOD thresholds.");
                    for (int branch = 0; branch < 5; branch++)
                    {
                        _minDistance = Math.Max(min, branch == 0 ? 0 : limits[branch - 1]);
                        _maxDistance = Math.Min(max, branch == 4 ? float.PositiveInfinity : limits[branch]);
                        if (_minDistance < _maxDistance) Visit(new List<P> { args[branch] }, $"{childPath}:lod{branch}", depth + 1, templates + 1, texture);
                    }
                    _minDistance = min; _maxDistance = max;
                }
                else if (f.EffectID == 2023)
                {
                    try
                    {
                        if (args.Count != 17 || args[0] is not FFXDLSE.Param32 { ActionID: 71 } setup ||
                            args[1] is not FFXDLSE.Param32 emitter || emitter.ActionID is < 28 or > 32)
                            throw new NotSupportedException("Not a supported template2023 billboard recipe.");
                        if (new[] { 6, 7, 8, 9, 10, 12, 13, 15 }.Any(k => IsCurve(args[k])))
                            throw new NotSupportedException("Dynamic batch/interval/startup binding.");
                        if (_layers.Count >= MaxLayers) throw new InvalidDataException("Layer descriptor budget exceeded.");
                        // Action75 is AssignSound (0000075.lua): audio only, nothing to draw.
                        if (args[4] is not FFXDLSE.Param32 { ActionID: 0 or 75 } || args[5] is not FFXDLSE.Param32 { ActionID: 0 or 75 })
                            throw new NotSupportedException("Additional setup/per-emission action.");
                        _schedule = args;
                        var placement = args[3] as FFXDLSE.Param32;
                        if (placement?.ActionID is not (0 or 35)) throw new NotSupportedException("Unsupported placement action.");
                        var motion = args[2] as FFXDLSE.Param32;
                        if (motion?.ActionID is not (0 or 55 or 84)) throw new NotSupportedException("Unsupported motion action.");
                        AddLayer(setup.ParamList.Params, emitter, placement.ActionID == 35 ? placement.ParamList.Params : null,
                            motion.ActionID == 55 ? motion.ParamList.Params : null, motion.ActionID == 84 ? motion.ParamList.Params : null,
                            childPath, templates + 1, texture);
                        // Startup action16 is a child creator, not another emission recipe.
                        Visit(new List<P> { args[16] }, childPath + ":startup", depth + 1, templates + 1, texture);
                    }
                    catch (Exception e) { Note($"SKIPPED {childPath}: {e.Message}"); }
                    finally { _schedule = null; }
                }
                else if (f.EffectID == 2101)
                {
                    // Template2101: delay 7, then geometry 0, follow/delete 5/6, placement 2, motion 1,
                    // action 3 and startup child 8; destroyed after lifetime 4 when >= 0.
                    if (args.Count != 9) { Note($"SKIPPED {childPath}: malformed geometry container."); continue; }
                    bool child = args[8] is FFXDLSE.Param32 { ActionID: not (0 or 17) };
                    string geometry = args[0] is FFXDLSE.Param32 { ActionID: not 0 } g ? $"geometry primitive action{g.ActionID}" : null;
                    if (!child)
                    {
                        Note(geometry != null ? $"Omitted {childPath}: {geometry} is not implemented and the container has no billboard child."
                            : $"{childPath}: empty geometry container (no geometry, no child).");
                        continue;
                    }
                    if (args[3] is not FFXDLSE.Param32 { ActionID: 0 } || IsCurve(args[4]) || IsCurve(args[7]))
                    { Note($"SKIPPED {childPath}: geometry-container extra action or dynamic lifetime."); continue; }
                    var parent = _parentPlacement;
                    var spin = (_spin, _spinPivot);
                    var lifecycle = (_containerDelay, _containerLife);
                    try
                    {
                        // Children start after the container's delay; its destroy time is relative to
                        // that start (the startup state resets its clock).
                        double life = Value(args[4], 0);
                        _containerLife = Math.Min(_containerLife - Value(args[7], 0), life >= 0 ? life : double.PositiveInfinity);
                        _containerDelay += Value(args[7], 0);
                        _parentPlacement *= PlacementAction(args[2]);
                        ApplyMotion(args[1], childPath);
                        if (geometry != null) Note($"Template2101 {geometry} is omitted; only its startup billboard child is supported.");
                        Visit(new List<P> { args[8] }, childPath + ":startup", depth + 1, templates + 1, texture);
                    }
                    catch (NotSupportedException e) { Note($"SKIPPED {childPath}: {e.Message}"); }
                    finally { _parentPlacement = parent; (_spin, _spinPivot) = spin; (_containerDelay, _containerLife) = lifecycle; }
                }
                else if (f.EffectID is 2121 or 2123)
                {
                    int count = f.EffectID == 2121 ? 14 : 7;
                    if (args.Count != count || Value(args[^1], 0) != 0)
                    { Note($"SKIPPED {childPath}: delayed/malformed container."); continue; }
                    // Both containers apply their placement, then their motion, then create children:
                    // 2121 placement 9, motion 8, children 0-7; 2123 placement 2, motion 1, child 0.
                    var parent = _parentPlacement;
                    var spin = (_spin, _spinPivot);
                    try
                    {
                        _parentPlacement *= PlacementAction(args[f.EffectID == 2121 ? 9 : 2]);
                        ApplyMotion(args[f.EffectID == 2121 ? 8 : 1], childPath);
                        if (f.EffectID == 2121) Visit(args.Take(8).ToList(), childPath + ":children", depth + 1, templates + 1, texture);
                        else Visit(new List<P> { args[0] }, childPath + ":create", depth + 1, templates + 1, texture);
                    }
                    catch (NotSupportedException e) { Note($"SKIPPED {childPath}: {e.Message}"); }
                    finally { _parentPlacement = parent; (_spin, _spinPivot) = spin; }
                }
                else if (f.EffectID == 2020)
                {
                    try { VisitTemplate2020(args, childPath, depth, templates, texture); }
                    catch (NotSupportedException e) { Note($"SKIPPED {childPath}: {e.Message}"); }
                }
                else Note($"SKIPPED {childPath}: template {f.EffectID} not implemented.");
            }
            else if (parameter is FFXDLSE.Param32 action)
            {
                _actions.Add(action.ActionID);
                if (action.ActionID is 14 or 79 or 87)
                    Visit(action.ParamList.Params, childPath + $":action{action.ActionID}", depth + 1, templates, texture);
                // Action104's camera-distance fade reaches only the effect instance's faded colour
                // word, which billboard clusters never read (ELF research 10.7, 14.5).
                else if (action.ActionID == 104)
                    Note($"Action104 at {childPath}: distance fade does not apply to billboard clusters.");
                else if (action.ActionID is not (0 or 17)) Note($"Omitted action {action.ActionID} at {childPath}.");
            }
        }
    }

    private void Note(string text) { if (_notes.Count < 128) _notes.Add(text); }

    private void AddLayer(List<P> setup, FFXDLSE.Param32 emitter, List<P> placement, List<P> motion, List<P> motion84,
        string path, int templates, Func<int, Texture2D> texture)
    {
        if (setup.Count is not (28 or 29)) throw new NotSupportedException($"Action71 arity {setup.Count}, expected observed DeS 28/29.");
        int expected = emitter.ActionID == 28 ? 12 : emitter.ActionID == 32 ? 15 : 13;
        if (emitter.ParamList.Params.Count != expected) throw new NotSupportedException("Emitter arity mismatch.");
        if (setup[1] is not FFXDLSE.Param34 resource) throw new NotSupportedException("Bound/dynamic texture ID.");
        if (_schedule == null || !RuntimeSlot0(setup[0], templates) || !RuntimeSlot0(emitter.ParamList.Params[0], templates))
            throw new NotSupportedException($"Unverified setup/emission runtime binding ({setup[0].GetType().Name}/{emitter.ParamList.Params[0].GetType().Name}).");
        int capacity = Capacity54(_schedule), batch = Integer(_schedule[9]);
        double interval = Value(_schedule[8], 0), delay = Value(_schedule[7], 0);
        var finite = Finite(_schedule);
        delay += _containerDelay;
        if (double.IsFinite(_containerLife) && Integer(_schedule[12]) != 0)
        {
            // Deleted with its destroyed container: an effect lifetime counted from this layer's start.
            double left = _containerLife - Value(_schedule[7], 0);
            if (left <= 0) throw new NotSupportedException("Destroyed with its container before its first emission.");
            finite = finite == null ? new(-1, -1, left) : finite with { EffectLife = finite.EffectLife < 0 ? left : Math.Min(finite.EffectLife, left) };
        }
        if (capacity < 1 || capacity > 2048 || batch < 1 || batch > capacity ||
            (interval != 0 && interval < 1.0 / 120) || delay < 0 || delay > 60)
            throw new NotSupportedException($"Schedule exceeds bounded playback limits: capacity={capacity}, batch={batch}, interval={interval}, delay={delay}.");
        if (interval == 0) Note("Zero-wait state repetition uses ZeroWaitPreviewHz (default60), independent of render FPS. Native update cadence remains unverified; capacity is still enforced.");
        var tex = texture(resource.TextureID);
        if (IsCurve(setup[3])) throw new NotSupportedException("Dynamic particle lifetime.");
        // Lifetimes of hours or more (river mist 92104: 1,666,666 s) are permanent sprites. Their
        // curves are keyed in seconds, so appearance is sampled over the first 60 s either way.
        bool permanent = setup[3] is FFXDLSE.Param64 { Tick: > 10000 and < 1e8f };
        float life = permanent ? SfxBatchParticles.PermanentLife : Value(setup[3], 0);
        if (life < 0.01f || life > 60 && !permanent) throw new NotSupportedException($"Lifetime {life} outside preview range 0.01–60.");
        float span = Math.Min(life, 60);
        if (permanent) Note("Particle lifetime is effectively unlimited; particles persist and hold their 60 s appearance.");
        int columns = Integer(setup[6]), frames = Integer(setup[7]);
        int transparency = Integer(setup[5]);
        if (columns < 1 || columns > 256 || frames < 1 || frames > 4096)
            throw new InvalidDataException("Invalid atlas dimensions.");
        if (Integer(setup[4]) != 0) throw new NotSupportedException("Y-axis-constrained billboarding not implemented.");
        var e = emitter.ParamList.Params;
        int speedIndex = emitter.ActionID == 28 ? 3 : emitter.ActionID == 32 ? 6 : 4;
        SfxBatchParticles.NativeEmit32 emit32 = null;
        if (emitter.ActionID == 32)
        {
            // Native Emit32 draws each particle's X/Y size multiplier from base ± range (10.3),
            // so the base multiplier leaves the shared size curve.
            if (e.Skip(8).Take(4).Any(IsCurve)) throw new NotSupportedException("Dynamic Emit32 size range.");
            float x = Value(e[8], 0), xr = Value(e[9], 0), y = Value(e[10], 0), yr = Value(e[11], 0);
            emit32 = new SfxBatchParticles.NativeEmit32(new Vector2(x - xr, x + xr), new Vector2(y - yr, y + yr),
                Integer(e[14]) != 0, Integer(e[13]) != 0);
        }
        var process = new ParticleProcessMaterial {
            Gravity = Vector3.Zero, Direction = Vector3.Up, ScaleMin = 1, ScaleMax = 1,
            ScaleCurve = new CurveXyzTexture {
                CurveX = Curve(t => Value(setup[10], t) * (emit32 != null ? 1 : Value(e[speedIndex + 2], 0)), span),
                CurveY = Curve(t => Value(setup[11], t) * (emit32 != null ? 1 : Value(e[speedIndex + 4], 0)), span),
                CurveZ = Curve(_ => 1, span) },
            Color = ColorValue(e[speedIndex + 6], 0), ColorRamp = ColorRamp(setup[12], span),
            AngleMin = -Value(setup[13], 0) - Math.Abs(Value(setup[14], 0)),
            AngleMax = -Value(setup[13], 0) + Math.Abs(Value(setup[14], 0)),
            AngularVelocityMin = 1, AngularVelocityMax = 1,
            AngularVelocityCurve = new CurveTexture { Curve = Curve(t => -Value(setup[15], t), span) }
        };
        // Keep sampled frame numbers in their own texture: Godot's animation-speed parameter
        // isn't equivalent to an authored discrete frame sequence.
        var material = new ShaderMaterial { Shader = PreviewShader(_blend < 0 ? AutomaticBlend(transparency) : _blend) };
        material.SetShaderParameter("diffuse_tex", tex);
        material.SetShaderParameter("frame_curve", FrameTexture(setup[9], span, frames));
        material.SetShaderParameter("frame_columns", columns);
        material.SetShaderParameter("frame_count", frames);
        // Setup71's shader type: bump absent/present x volume flag (arg22) unset/set gives
        // Type0/1/2/3 (research 10.8). Type2 intersects scene depth (10.9-10.11); Type0 alone
        // applies exposure.
        bool bumpAbsent = setup[2] is FFXDLSE.Param34 { TextureID: 0 }, volume = Integer(setup[22]) != 0;
        material.SetShaderParameter("volume_depth", bumpAbsent && volume);
        material.SetShaderParameter("apply_exposure", bumpAbsent && !volume);
        var quad = new QuadMesh { Size = Vector2.One, Material = material };
        var transform = _parentPlacement * PlacementTransform(placement);
        SfxBatchParticles.NativeCarrier carrier = null;
        if (_carrierContext != null)
        {
            // Each child instance is placed by template2020's emitter, then by its own placement.
            var c = _carrierContext.Schedule;
            var own = _carrierContext.Base.AffineInverse() * _parentPlacement * PlacementTransform(placement);
            carrier = c with {
                KillAtLife = c.Life >= 0 && Integer(_schedule[12]) != 0,
                Place = r => c.Place(r) * own
            };
            transform = _carrierContext.Base;
            capacity = CarrierCapacity(c, finite, capacity, batch, interval, delay, life);
        }
        var layer = new Layer { Process = process, Emitter = e, EmitterId = emitter.ActionID, Emit32 = emit32,
            Motion84 = motion84 == null ? null : Motion84(motion84), WindPreview = _previewWindAcceleration.Length(),
            Motion = motion, Material = material, NativeTransparency = transparency,
            Placement = transform, Life = life, Capacity = capacity, Batch = batch, Interval = interval, Delay = delay,
            MinDistance = _minDistance, MaxDistance = _maxDistance, Local = Integer(setup[17]) != 0, Mesh = quad,
            Finite = finite, Carrier = carrier, CarrierExtent = _carrierContext?.Extent ?? 0, Spin = _spin, SpinPivot = _spinPivot };
        if (finite != null) Note($"Finite template2023: {(finite.Emissions < 0 ? "unlimited" : finite.Emissions)} emission(s), " +
            $"emitter lifespan {(finite.EmitterLife < 0 ? "unlimited" : $"{finite.EmitterLife:0.###}s")}, " +
            $"effect lifetime {(finite.EffectLife < 0 ? "unlimited" : $"{finite.EffectLife:0.###}s")}" +
            (carrier == null ? "; plays once from activation." : "; per child instance."));
        UpdateEmitter(layer, 0);
        layer.BaseGravity = process.Gravity;
        process.Gravity += _previewWindAcceleration * layer.WindPower;
        if (motion != null) Note($"Action55 preview: gravity is a downward scalar (negative rises); wind multiplier {layer.WindPower}. PreviewWindAcceleration defaults to still air. Native integration/sign and map wind remain unverified.");
        if (layer.Motion84 != null) Note($"Motion84: native explicit-Euler order, world gravity {-layer.Motion84.Gravity} (negative rises), " +
            $"linear drag {layer.Motion84.Drag}, velocity perturbation ±{layer.Motion84.AngleDegrees}° every {layer.Motion84.Interval} updates. " +
            $"Update cadence uses ZeroWaitPreviewHz and PreviewWindAcceleration is applied as wind velocity ×{layer.Motion84.Wind}; " +
            "native cadence and live map wind remain unrecovered.");
        if (e.Concat(motion ?? new List<P>()).Any(IsCurve)) throw new NotSupportedException("Dynamic emitter/motion curves require scheduled sampling support.");
        layer.Appearance = new SfxBatchParticles.Appearance(process, span);
        _layers.Add(layer);
        CoverageRadius = Math.Max(CoverageRadius, Coverage(layer));
        Note($"LAYER {_layers.Count - 1}: {path}; emitter action{layer.EmitterId}; texture {resource.TextureID}; distance [{_minDistance}, {_maxDistance}); " +
            $"lifetime {life}; batch {batch} / {interval:F6}s; capacity {capacity}; delay {delay}. " +
            "Unimplemented: bump, random spin flag, fog/light/blur, volume/output (atmosphere, Type2 near-plane branch), sphere (action31) birth direction" +
            (emit32 != null ? "." : " and scale randomness/linkage."));
    }

    // Compiled action35 (ELF_ENGINE_ACCURACY_RESEARCH.md 9.2/9.6), which the game dispatches
    // ahead of the archived Lua35 route. Native local pose for (L,U,F,A,B,C): translation
    // (-L,U,F), rotation Ry(B)·Rx(-A)·Rz(C). After the raw-X mirror S·R·S used for all map
    // data, that is translation (L,U,F) and Ry(-B)·Rx(-A)·Rz(-C). Callers compose
    // parent * local: translation uses the old orientation, rotation post-multiplies.
    // Constant-sequence subset of native motion84 (Sequence gravity, Sequence drag, float wind
    // coefficient, Sequence perturbation angle, int interval).
    private static SfxBatchParticles.NativeMotion84 Motion84(List<P> m)
    {
        if (m.Count != 5 || m.Any(IsCurve)) throw new NotSupportedException("Dynamic/malformed motion84.");
        var result = new SfxBatchParticles.NativeMotion84(Value(m[0], 0), Value(m[1], 0), Value(m[2], 0), Value(m[3], 0), Integer(m[4]));
        if (result.Interval is < 1 or > 10000 || Math.Abs(result.AngleDegrees) > 360 || Math.Abs(result.Wind) > 100)
            throw new NotSupportedException("Motion84 outside bounded preview range.");
        return result;
    }

    // Runtime argument slot 0, which template2023 binds to capacity (setup) and batch (emission).
    // Param66 is native FXIntParamParentRef: kind 5 reads the same global runtime-argument
    // table as Param38 after walking Unk08 parent contexts. Every mounted Param66 walks exactly
    // its enclosing template depth; anything else could reach the unrecovered null fallback.
    private static bool RuntimeSlot0(P p, int templates) =>
        p is FFXDLSE.Param38 { ActionID: 5, ArgIndex: 0 } ||
        p is FFXDLSE.Param66 { Unk04: 5, ArgIndex: 0 } parentRef && parentRef.Unk08 == templates;

    private static Transform3D PlacementTransform(List<P> placement)
    {
        if (placement == null) return Transform3D.Identity;
        if (placement.Count != 6 || placement.Any(IsCurve)) throw new NotSupportedException("Dynamic/malformed placement.");
        var v = placement.Select(p => Value(p, 0)).ToArray();
        var angles = new Vector3(-v[3], -v[4], -v[5]) * (Mathf.Pi / 180);
        return new Transform3D(Basis.FromEuler(angles, EulerOrder.Yxz), new Vector3(v[0], v[1], v[2]));
    }

    // Container/template placement argument: none, action35, or action36 (action35 plus
    // per-instance random ranges; the ranges are omitted, as the preview is built once per effect).
    private Transform3D PlacementAction(P p)
    {
        if (p is FFXDLSE.Param32 { ActionID: 0 }) return Transform3D.Identity;
        if (p is FFXDLSE.Param32 { ActionID: 35 } a35) return PlacementTransform(a35.ParamList.Params);
        if (p is FFXDLSE.Param32 { ActionID: 36 } a36 && a36.ParamList.Params.Count == 12)
        {
            Note("Action36 random placement uses its base pose; per-instance random ranges are omitted.");
            return PlacementTransform(a36.ParamList.Params.Take(6).ToList());
        }
        throw new NotSupportedException($"Unsupported container placement {p.GetType().Name}{(p is FFXDLSE.Param32 a ? $" action{a.ActionID}" : "")}.");
    }

    // Container motion argument. Action34 (0000034.lua: MoveRotation(yaw, pitch, roll) speeds)
    // with constant speeds rotates everything beneath the container about its placed origin.
    // Yaw/pitch/roll are taken as Y/X/Z in that order, from the wrapper's names; the native
    // axis order and signs are not yet read. Other motion keeps the children static, noted.
    private void ApplyMotion(P p, string path)
    {
        if (p is not FFXDLSE.Param32 { ActionID: not 0 } motion) return;
        if (motion.ActionID == 75) return; // AssignSound: audio only
        if (motion.ActionID != 34) { Note($"Container motion action{motion.ActionID} at {path} is not implemented; children stay at the container's placed pose."); return; }
        var a = motion.ParamList.Params;
        if (a.Count != 3 || a.Any(IsCurve)) { Note($"Dynamic container rotation at {path} is not implemented."); return; }
        Vector3 speed;
        try { speed = new Vector3(Value(a[0], 0), Value(a[1], 0), Value(a[2], 0)); }
        catch (InvalidDataException) { Note($"Container rotation at {path} is outside the bounded range (e.g. 100000 deg/s); omitted."); return; }
        if (speed == Vector3.Zero) return;
        if (_spin != Vector3.Zero) { Note($"Nested container rotation at {path} ignored; the outer rotation is kept."); return; }
        if (_carrierContext != null) { Note($"Rotation of emitted child instances at {path} is not implemented."); return; }
        _spin = speed; _spinPivot = _parentPlacement;
        Note($"Container rotation at {path}: yaw/pitch/roll {speed.X}/{speed.Y}/{speed.Z} deg/s (axis mapping inferred from names).");
    }

    // Degrees for yaw/pitch/roll → Godot basis, after the raw-X mirror (S·R·S flips Y and Z turns).
    private static Basis SpinBasis(Vector3 degrees) =>
        Basis.FromEuler(new Vector3(degrees.Y, -degrees.X, -degrees.Z) * (Mathf.Pi / 180), EulerOrder.Yxz);

    private void UpdateSpin(Layer l)
    {
        if (l.Spin == Vector3.Zero || l.Particles == null) return;
        l.Particles.Transform = l.SpinPivot * new Transform3D(SpinBasis(l.Spin * (float)_age), Vector3.Zero) *
            (l.SpinPivot.AffineInverse() * l.Placement);
    }

    // Action54 (0000054.lua): particle capacity of one template2023 instance.
    private static int Capacity54(List<P> a)
    {
        int reps = Integer(a[6]), max = Integer(a[13]), batch = Integer(a[9]);
        double life = Value(a[10], 0) - Value(a[7], 0), wait = Value(a[8], 0);
        double capacity = max > -1 ? max
            : life < 0 ? (reps < 0 ? 1000 : reps * batch)
            : wait <= 0 ? 1000
            : (reps < 0 ? life / wait : Math.Min(Math.Floor(life / wait), reps)) * batch;
        return capacity <= 0 ? 100 : (int)capacity;
    }

    // Template2023 finite fields: emission count 6, emitter lifespan 10, effect lifetime 15.
    private static SfxBatchParticles.NativeFinite Finite(List<P> a)
    {
        int reps = Integer(a[6]);
        double emitterLife = Value(a[10], 0), effectLife = Value(a[15], 0);
        return reps < 0 && emitterLife < 0 && effectLife < 0 ? null : new(reps, emitterLife, effectLife);
    }

    // Emissions one instance makes, or -1 if unlimited.
    private static long Emissions(SfxBatchParticles.NativeFinite f, double interval)
    {
        if (f == null) return -1;
        double span = double.PositiveInfinity;
        if (f.EmitterLife >= 0) span = f.EmitterLife;
        if (f.EffectLife >= 0) span = Math.Min(span, f.EffectLife);
        long bySpan = double.IsFinite(span) ? (long)Math.Ceiling(span / interval - 1e-9) : -1;
        if (f.Emissions < 0) return bySpan;
        return bySpan < 0 ? f.Emissions : Math.Min(f.Emissions, bySpan);
    }

    private static int Integer(P p)
    {
        float v = Value(p, 0);
        if (v != MathF.Truncate(v) || Math.Abs(v) > 1000000) throw new InvalidDataException("Expected bounded integer.");
        return (int)v;
    }

    private static bool IsCurve(P p) => p switch {
        FFXDLSE.Param3 v => v.TickInts.Count != 1, FFXDLSE.Param5 v => v.TickInts.Count != 1,
        FFXDLSE.Param6 v => v.TickInts.Count != 1, FFXDLSE.Param9 v => v.TickFloats.Count != 1,
        FFXDLSE.Param11 v => v.TickFloats.Count != 1, FFXDLSE.Param17 v => v.TickColors.Count != 1,
        _ => false
    };

    // Linear float interpolation and step integer interpolation are PREVIEW choices.
    private static float Value(P p, float time)
    {
        float result = p switch {
            FFXDLSE.Param1 v => v.Int, FFXDLSE.Param7 v => v.Float, FFXDLSE.Param64 v => v.Tick,
            FFXDLSE.Param9 v => Sample(v.TickFloats.Select(k => (k.Tick, k.Float)).ToArray(), time, false),
            FFXDLSE.Param11 v => Sample(v.TickFloats.Select(k => (k.Tick, k.Float)).ToArray(), time, false),
            FFXDLSE.Param3 v => Sample(v.TickInts.Select(k => (k.Tick, (float)k.Int)).ToArray(), time, true),
            FFXDLSE.Param5 v => Sample(v.TickInts.Select(k => (k.Tick, (float)k.Int)).ToArray(), time, true),
            FFXDLSE.Param6 v => Sample(v.TickInts.Select(k => (k.Tick, (float)k.Int)).ToArray(), time, true),
            _ => throw new NotSupportedException($"Unresolved value/binding {p.GetType().Name} (not substituted).")
        };
        if (!float.IsFinite(result) || Math.Abs(result) > 10000) throw new InvalidDataException("Non-finite or excessive scalar.");
        return result;
    }

    private static float Sample((float t, float v)[] keys, float time, bool step)
    {
        if (keys.Length == 0 || keys.Length > 4096) throw new InvalidDataException("Invalid curve key count.");
        for (int i = 0; i < keys.Length; i++)
            if (!float.IsFinite(keys[i].t) || !float.IsFinite(keys[i].v) || (i > 0 && keys[i].t <= keys[i - 1].t))
                throw new InvalidDataException("Non-finite, duplicate or unordered curve keys.");
        if (time <= keys[0].t) return keys[0].v;
        for (int i = 1; i < keys.Length; i++)
            if (time < keys[i].t)
                return step ? keys[i - 1].v : Mathf.Lerp(keys[i - 1].v, keys[i].v, (time - keys[i - 1].t) / (keys[i].t - keys[i - 1].t));
        return keys[^1].v;
    }

    private static Color ColorValue(P p, float time)
    {
        if (p is FFXDLSE.Param13 c) return ToColor(c.Color);
        if (p is not FFXDLSE.Param17 seq) throw new NotSupportedException($"Unsupported color {p.GetType().Name}.");
        float Channel(Func<FFXDLSE.PrimitiveColor, float> pick) => Sample(seq.TickColors.Select(k => (k.Tick, pick(k.Color))).ToArray(), time, false);
        return new Color(Channel(c => c.R), Channel(c => c.G), Channel(c => c.B), Channel(c => c.A));
    }

    private static Color ToColor(FFXDLSE.PrimitiveColor c)
    {
        if (!float.IsFinite(c.R) || !float.IsFinite(c.G) || !float.IsFinite(c.B) || !float.IsFinite(c.A))
            throw new InvalidDataException("Non-finite color.");
        return new Color(c.R, c.G, c.B, c.A);
    }

    private static Curve Curve(Func<float, float> sample, float life)
    {
        var values = Enumerable.Range(0, 65).Select(i => sample(life * i / 64)).ToArray();
        if (values.Any(v => !float.IsFinite(v) || Math.Abs(v) > 10000)) throw new InvalidDataException("Invalid sampled curve.");
        var curve = new Curve { MinValue = Math.Min(0, values.Min()), MaxValue = Math.Max(1, values.Max()) };
        for (int i = 0; i < values.Length; i++)
            curve.AddPoint(new Vector2(i / 64f, values[i]), 0, 0, Godot.Curve.TangentMode.Linear, Godot.Curve.TangentMode.Linear);
        return curve;
    }

    private static GradientTexture1D ColorRamp(P p, float life)
    {
        var gradient = new Gradient {
            Offsets = Enumerable.Range(0, 65).Select(i => i / 64f).ToArray(),
            Colors = Enumerable.Range(0, 65).Select(i => ColorValue(p, life * i / 64)).ToArray()
        };
        return new GradientTexture1D { Gradient = gradient, Width = 256, UseHdr = true };
    }

    private static Texture2D FrameTexture(P p, float life, int frames)
    {
        var bytes = new byte[256 * 4];
        for (int i = 0; i < 256; i++)
            BitConverter.GetBytes(Math.Clamp(Value(p, life * i / 255), 0, frames - 1)).CopyTo(bytes, i * 4);
        using var image = Image.CreateFromData(256, 1, false, Image.Format.Rf, bytes);
        return ImageTexture.CreateFromImage(image);
    }

    private static void UpdateEmitter(Layer layer, float time)
    {
        var p = layer.Process; var e = layer.Emitter;
        int speed = layer.EmitterId == 28 ? 3 : layer.EmitterId == 32 ? 6 : 4;
        float velocity = Value(e[speed], time), range = Math.Abs(Value(e[speed + 1], time));
        p.InitialVelocityMin = Math.Max(0, velocity - range);
        p.InitialVelocityMax = Math.Max(0, velocity + range);
        // Breadth and concentration precede speed in every billboard emitter. emissionType
        // (28: arg10, 29/30: arg11; 31/32 pass 0) picks the velocity frame natively:
        // 0 the emitter frame, 1/2 world +Z turned onto +Y/-Y, 3 world axes (research 11).
        int type = layer.EmitterId switch { 28 => Integer(e[10]), 29 or 30 => Integer(e[11]), _ => 0 };
        layer.Direction = new SfxBatchParticles.NativeDirection(Math.Clamp(Value(e[speed - 2], time), -3600, 3600),
            Math.Clamp(Value(e[speed - 1], time), -1, 1), type is >= 1 and <= 3 ? EmissionType(type) : null);
        if (layer.EmitterId == 32 || layer.EmitterId == 29)
        {
            p.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box;
            // Native square: local XY plane, emitting along +Z.
            var dimensions = layer.EmitterId == 32
                ? new Vector3(Value(e[1], time), Value(e[2], time), Value(e[3], time))
                : new Vector3(Value(e[1], time), Value(e[1], time), 0);
            p.EmissionBoxExtents = dimensions.Abs() * 0.5f;
        }
        else if (layer.EmitterId == 30)
        {
            // Native action30: a circle in the local XY plane emitting along +Z (research 11);
            // map circles carry action35 A=90, which lays it flat and points +Z up.
            p.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring;
            p.EmissionRingAxis = Vector3.Back;
            p.EmissionRingHeight = 0;
            p.EmissionRingInnerRadius = 0;
            p.EmissionRingRadius = Math.Abs(Value(e[1], time));
        }
        else if (layer.EmitterId == 31)
        {
            p.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere;
            p.EmissionSphereRadius = Math.Abs(Value(e[1], time));
        }
        else p.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Point;
        if (layer.Motion != null)
        {
            if (layer.Motion.Count != 4) throw new NotSupportedException("Motion arity mismatch.");
            if (Integer(layer.Motion[2]) != 0)
                throw new NotSupportedException("Gravity-object selection requires an external system.");
            layer.WindPower = Value(layer.Motion[3], time);
            if (Math.Abs(layer.WindPower) > 1) throw new NotSupportedException("Wind multiplier outside bounded preview range.");
            // Gravity is treated as a downward scalar: the negative inputs on rising smoke
            // and fire imply upward acceleration. This is a documented preview inference,
            // not a recovered native integration equation. Wind is supplied explicitly.
            p.Gravity = new Vector3(0, -Value(layer.Motion[0], time), 0);
            p.DampingMin = p.DampingMax = Math.Max(0, Value(layer.Motion[1], time));
        }
    }

    public override void _Ready() => AttachLayers();

    private void AttachLayers()
    {
        if (!IsInsideTree() || IsQueuedForDeletion()) return;
        PrepareViewDistance(_viewDistance);
        foreach (var l in _layers)
        {
            if (!Selected(l, _viewDistance)) continue;
            if (l.Particles != null) { l.Particles.Emitting = true; continue; }
            var particles = new SfxBatchParticles { Name = $"Billboards_{_layers.IndexOf(l)}", Transform = l.Placement };
            try
            {
                particles.Configure(l.Process, l.Mesh, l.Life, l.Capacity, l.Batch, l.Interval, l.Delay,
                    (uint)(_layers.IndexOf(l) + 1) + PlacementSeed, l.Local, ZeroWaitPreviewHz, l.Appearance,
                    l.Emit32, l.Motion84, _previewWindAcceleration, l.Finite, l.Carrier, l.Direction);
                AddChild(particles);
                l.Particles = particles;
                UpdateSpin(l);
                // Warm up on activation only; a later band starts cold beside the expiring one.
                if (WarmUpOnActivation && !_activated) particles.WarmUp();
            }
            catch { particles.Free(); throw; }
        }
        _activated = true;
    }

    public override void _Process(double delta)
    {
        if (!Playing) return;
        if (!double.IsFinite(delta) || delta < 0) return;
        StalledSeconds += Math.Max(0, delta - 0.25);
        delta = Math.Min(delta, 0.25);
        _age += delta;
        if (FollowCamera && (_lodRefresh -= delta) <= 0)
        {
            _lodRefresh = 0.25;
            Camera3D camera = GetViewport().GetCamera3D();
#if TOOLS
            if (Engine.IsEditorHint()) camera = EditorInterface.Singleton.GetEditorViewport3D(EditorViewportIndex).GetCamera3D();
#endif
            if (camera != null) SetViewDistance(camera.GlobalPosition.DistanceTo(GlobalPosition));
        }
        foreach (var l in _layers)
        {
            UpdateSpin(l);
            l.Particles?.Advance(delta);
        }
    }

    // Map eviction hides and pauses instead of freeing: in the editor, every node added or
    // removed under the edited scene makes the Scene dock re-walk the whole scene.
    internal void Park()
    {
        Playing = false;
        Visible = false;
    }

    // Returns as a fresh ambient activation, reusing the existing layer nodes.
    internal void Resume(float distance)
    {
        _viewDistance = distance; _age = 0; StalledSeconds = 0;
        foreach (var l in _layers)
        {
            if (l.Particles == null) continue;
            l.Particles.Reset();
            l.Particles.Emitting = Selected(l, distance);
            if (l.Particles.Emitting && WarmUpOnActivation) l.Particles.WarmUp();
        }
        _activated = false;
        AttachLayers();
        Visible = true;
        Playing = true;
    }

    public void RestartPreview()
    {
        _age = 0; StalledSeconds = 0;
        foreach (var l in _layers) l.Particles?.Reset();
    }

    public Godot.Collections.Dictionary GetSummary() => new() {
        ["layer_count"] = LayerCount, ["playing"] = Playing, ["age"] = _age,
        ["particle_capacity"] = ParticleCapacity, ["live_particles"] = LiveParticleCount,
        ["preview_wind_acceleration"] = PreviewWindAcceleration, ["zero_wait_preview_hz"] = ZeroWaitPreviewHz,
        ["stalled_seconds"] = StalledSeconds, ["distance"] = _viewDistance, ["full_effect_playback"] = false, ["diagnostics"] = Diagnostics
    };
}
