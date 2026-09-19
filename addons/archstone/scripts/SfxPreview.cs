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
        public SfxBatchParticles.Appearance Appearance;

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
            foreach (var l in _layers) l.Process.Gravity = l.BaseGravity + value * l.WindPower;
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
        _age = 0; StalledSeconds = 0;
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
        foreach (var l in _layers)
            if (!Selected(l, distance)) { l.Particles?.Free(); l.Particles = null; }
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
        float extent = p.EmissionShape switch {
            ParticleProcessMaterial.EmissionShapeEnum.Box => p.EmissionBoxExtents.Length(),
            ParticleProcessMaterial.EmissionShapeEnum.Ring => p.EmissionRingRadius,
            ParticleProcessMaterial.EmissionShapeEnum.Sphere => p.EmissionSphereRadius,
            _ => 0
        };
        var scale = (CurveXyzTexture)p.ScaleCurve;
        float sprite = new Vector2(scale.CurveX.MaxValue, scale.CurveY.MaxValue).Length() * 0.5f;
        return l.Placement.Origin.Length() + extent + sprite + p.InitialVelocityMax * l.Life
            + p.Gravity.Length() * l.Life * l.Life * 0.5f;
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
        _notes.Add("CPU scheduled instanced billboards support Compatibility. Tick assumed seconds; linear curve interpolation; Godot-style motion/distributions remain approximations. No native depth-softening, atmosphere or output pass. Capture supports additive candles/fireflies and alpha fog; the full transparency enum remains provisional.");
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
                    $"state[{s}]", 0, texture);
            }
        }
        foreach (float d in _layers.Select(l => l.MinDistance).Distinct())
            if (Cost(d).Particles > 8192) throw new InvalidDataException("Selected effect exceeds the 8192-particle preview budget.");
        _notes.Add("Geometry: circle/square use horizontal disk/plane previews; exact native axes and distribution flags remain provisional. Long editor stalls pause excess time; StalledSeconds reports it.");
        _notes.Add("Observed inline action IDs (not execution coverage): " + string.Join(", ", _actions.OrderBy(x => x)));
        _notes.Add($"Built {_layers.Count} layer(s). Session-only preview at an explicit user transform; no MSB anchor approximation.");
        Diagnostics = string.Join("\n\n", _notes);
        Playing = false;
    }

    private List<P> _rootArguments;
    private void Visit(List<P> parameters, string path, int depth, Func<int, Texture2D> texture)
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
                        if (_minDistance < _maxDistance) Visit(new List<P> { args[branch] }, $"{childPath}:lod{branch}", depth + 1, texture);
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
                        if (Integer(args[6]) != -1 || Value(args[10], 0) >= 0 || Value(args[15], 0) >= 0)
                            throw new NotSupportedException("Finite emission/lifetime control is not supported yet.");
                        if (IsCurve(args[9]) || IsCurve(args[8]) || IsCurve(args[7]))
                            throw new NotSupportedException("Dynamic batch/interval/startup binding.");
                        if (_layers.Count >= MaxLayers) throw new InvalidDataException("Layer descriptor budget exceeded.");
                        if (args[4] is not FFXDLSE.Param32 { ActionID: 0 } || args[5] is not FFXDLSE.Param32 { ActionID: 0 })
                            throw new NotSupportedException("Additional setup/per-emission action.");
                        _schedule = args;
                        var placement = args[3] as FFXDLSE.Param32;
                        if (placement?.ActionID is not (0 or 35)) throw new NotSupportedException("Unsupported placement action.");
                        var motion = args[2] as FFXDLSE.Param32;
                        if (motion?.ActionID is not (0 or 55 or 84)) throw new NotSupportedException("Unsupported motion action.");
                        AddLayer(setup.ParamList.Params, emitter, placement.ActionID == 35 ? placement.ParamList.Params : null,
                            motion.ActionID == 55 ? motion.ParamList.Params : null, childPath, texture);
                        if (motion.ActionID == 84) Note("Motion84 turbulence remains unsupported; particles use authored birth velocity and spin.");
                        // Startup action16 is a child creator, not another emission recipe.
                        Visit(new List<P> { args[16] }, childPath + ":startup", depth + 1, texture);
                    }
                    catch (Exception e) { Note($"SKIPPED {childPath}: {e.Message}"); }
                    finally { _schedule = null; }
                }
                else if (f.EffectID == 2101)
                {
                    // The geometry container's verified startup action invokes argument8 after
                    // placement. Preserve its billboard child even though its geometry is absent.
                    if (args.Count != 9 || Value(args[7], 0) != 0 || Value(args[4], 0) >= 0 ||
                        args[1] is not FFXDLSE.Param32 { ActionID: 0 } || args[3] is not FFXDLSE.Param32 { ActionID: 0 })
                    { Note($"SKIPPED {childPath}: unsupported geometry-container lifecycle/motion."); continue; }
                    var parent = _parentPlacement;
                    try
                    {
                        if (args[2] is FFXDLSE.Param32 { ActionID: 35 } placement)
                            _parentPlacement *= PlacementTransform(placement.ParamList.Params);
                        else if (args[2] is not FFXDLSE.Param32 { ActionID: 0 })
                            throw new NotSupportedException("Unsupported container placement.");
                        Note("Template2101 geometry setup is omitted; only its startup billboard child is supported.");
                        Visit(new List<P> { args[8] }, childPath + ":startup", depth + 1, texture);
                    }
                    finally { _parentPlacement = parent; }
                }
                else if (f.EffectID is 2121 or 2123)
                {
                    int count = f.EffectID == 2121 ? 14 : 7;
                    if (args.Count != count || Value(args[^1], 0) != 0)
                    { Note($"SKIPPED {childPath}: delayed/malformed container."); continue; }
                    if (f.EffectID == 2121)
                    {
                        if (args[8] is FFXDLSE.Param32 { ActionID: not 0 }) Note("Container movement/rotation remains unsupported.");
                        Visit(args.Take(8).ToList(), childPath + ":children", depth + 1, texture);
                    }
                    else
                    {
                        if (args[1] is FFXDLSE.Param32 { ActionID: not 0 })
                            Note("Template2123 container movement/rotation remains unsupported; child emitters keep their own placement.");
                        // Template2123 invokes its placement argument before the child creator.
                        // Preserve constant translation through nested containers (e.g.99100 +0.5Y).
                        // Rotation/native parent-follow semantics remain a separate preview limit.
                        var parent = _parentPlacement;
                        try
                        {
                            if (args[2] is FFXDLSE.Param32 { ActionID: 35 } placement)
                            {
                                var values = placement.ParamList.Params;
                                if (values.Count != 6 || values.Any(IsCurve))
                                    throw new NotSupportedException("Dynamic/malformed container placement.");
                                if (values.Skip(3).Any(v => Value(v, 0) != 0))
                                    Note("Template2123 container rotation remains omitted; constant translation is retained.");
                                _parentPlacement *= new Transform3D(Basis.Identity,
                                    new Vector3(-Value(values[0], 0), Value(values[1], 0), Value(values[2], 0)));
                            }
                            else if (args[2] is not FFXDLSE.Param32 { ActionID: 0 })
                                throw new NotSupportedException("Unsupported container placement.");
                            Visit(new List<P> { args[0] }, childPath + ":create", depth + 1, texture);
                        }
                        catch (NotSupportedException e) { Note($"SKIPPED {childPath}: {e.Message}"); }
                        finally { _parentPlacement = parent; }
                    }
                }
                else Note($"SKIPPED {childPath}: template {f.EffectID} not implemented.");
            }
            else if (parameter is FFXDLSE.Param32 action)
            {
                _actions.Add(action.ActionID);
                if (action.ActionID is 14 or 79 or 87)
                    Visit(action.ParamList.Params, childPath + $":action{action.ActionID}", depth + 1, texture);
                else if (action.ActionID is not (0 or 17)) Note($"Omitted action {action.ActionID} at {childPath}.");
            }
        }
    }

    private void Note(string text) { if (_notes.Count < 128) _notes.Add(text); }

    private void AddLayer(List<P> setup, FFXDLSE.Param32 emitter, List<P> placement, List<P> motion,
        string path, Func<int, Texture2D> texture)
    {
        if (setup.Count is not (28 or 29)) throw new NotSupportedException($"Action71 arity {setup.Count}, expected observed DeS 28/29.");
        int expected = emitter.ActionID == 28 ? 12 : emitter.ActionID == 32 ? 15 : 13;
        if (emitter.ParamList.Params.Count != expected) throw new NotSupportedException("Emitter arity mismatch.");
        if (setup[1] is not FFXDLSE.Param34 resource) throw new NotSupportedException("Bound/dynamic texture ID.");
        if (_schedule == null || setup[0] is not FFXDLSE.Param38 { ActionID: 5, ArgIndex: 0 } ||
            emitter.ParamList.Params[0] is not FFXDLSE.Param38 { ActionID: 5, ArgIndex: 0 })
            throw new NotSupportedException($"Unverified setup/emission runtime binding ({setup[0].GetType().Name}/{emitter.ParamList.Params[0].GetType().Name}); Param66 conversion is unresolved.");
        int capacity = Integer(_schedule[13]), batch = Integer(_schedule[9]);
        double interval = Value(_schedule[8], 0), delay = Value(_schedule[7], 0);
        if (capacity < 1 || capacity > 2048 || batch < 1 || batch > capacity ||
            (interval != 0 && interval < 1.0 / 120) || delay < 0 || delay > 60)
            throw new NotSupportedException($"Schedule exceeds bounded playback limits: capacity={capacity}, batch={batch}, interval={interval}, delay={delay}.");
        if (interval == 0) Note("Zero-wait state repetition uses ZeroWaitPreviewHz (default60), independent of render FPS. Native update cadence remains unverified; capacity is still enforced.");
        var tex = texture(resource.TextureID);
        if (IsCurve(setup[3])) throw new NotSupportedException("Dynamic particle lifetime.");
        float life = Value(setup[3], 0);
        if (life < 0.01f || life > 60) throw new NotSupportedException($"Lifetime {life} outside preview range 0.01–60.");
        int columns = Integer(setup[6]), frames = Integer(setup[7]);
        int transparency = Integer(setup[5]);
        if (columns < 1 || columns > 256 || frames < 1 || frames > 4096)
            throw new InvalidDataException("Invalid atlas dimensions.");
        if (Integer(setup[4]) != 0) throw new NotSupportedException("Y-axis-constrained billboarding not implemented.");
        var e = emitter.ParamList.Params;
        int speedIndex = emitter.ActionID == 28 ? 3 : emitter.ActionID == 32 ? 6 : 4;
        var process = new ParticleProcessMaterial {
            Gravity = Vector3.Zero, Direction = Vector3.Up, ScaleMin = 1, ScaleMax = 1,
            ScaleCurve = new CurveXyzTexture {
                CurveX = Curve(t => Value(setup[10], t) * Value(e[speedIndex + 2], 0), life),
                CurveY = Curve(t => Value(setup[11], t) * Value(e[speedIndex + 4], 0), life),
                CurveZ = Curve(_ => 1, life) },
            Color = ColorValue(e[speedIndex + 6], 0), ColorRamp = ColorRamp(setup[12], life),
            AngleMin = -Value(setup[13], 0) - Math.Abs(Value(setup[14], 0)),
            AngleMax = -Value(setup[13], 0) + Math.Abs(Value(setup[14], 0)),
            AngularVelocityMin = 1, AngularVelocityMax = 1,
            AngularVelocityCurve = new CurveTexture { Curve = Curve(t => -Value(setup[15], t), life) }
        };
        // Keep sampled frame numbers in their own texture: Godot's animation-speed parameter
        // isn't equivalent to an authored discrete frame sequence.
        var material = new ShaderMaterial { Shader = PreviewShader(_blend < 0 ? AutomaticBlend(transparency) : _blend) };
        material.SetShaderParameter("diffuse_tex", tex);
        material.SetShaderParameter("frame_curve", FrameTexture(setup[9], life, frames));
        material.SetShaderParameter("frame_columns", columns);
        material.SetShaderParameter("frame_count", frames);
        var quad = new QuadMesh { Size = Vector2.One, Material = material };
        var transform = _parentPlacement * PlacementTransform(placement);
        var layer = new Layer { Process = process, Emitter = e, EmitterId = emitter.ActionID,
            Motion = motion, Material = material, NativeTransparency = transparency,
            Placement = transform, Life = life, Capacity = capacity, Batch = batch, Interval = interval, Delay = delay,
            MinDistance = _minDistance, MaxDistance = _maxDistance, Local = Integer(setup[17]) != 0, Mesh = quad };
        UpdateEmitter(layer, 0);
        layer.BaseGravity = process.Gravity;
        process.Gravity += _previewWindAcceleration * layer.WindPower;
        if (motion != null) Note($"Action55 preview: gravity is a downward scalar (negative rises); wind multiplier {layer.WindPower}. PreviewWindAcceleration defaults to still air. Native integration/sign and map wind remain unverified.");
        if (e.Concat(motion ?? new List<P>()).Any(IsCurve)) throw new NotSupportedException("Dynamic emitter/motion curves require scheduled sampling support.");
        layer.Appearance = new SfxBatchParticles.Appearance(process, life);
        _layers.Add(layer);
        CoverageRadius = Math.Max(CoverageRadius, Coverage(layer));
        Note($"LAYER {_layers.Count - 1}: {path}; texture {resource.TextureID}; distance [{_minDistance}, {_maxDistance}); " +
            $"lifetime {life}; batch {batch} / {interval:F6}s; capacity {capacity}; delay {delay}. " +
            "Unimplemented: bump, random spin flag, fog/light/blur, volume/output, emitter concentration/distribution flags and scale randomness/linkage.");
    }

    private static Transform3D PlacementTransform(List<P> placement)
    {
        var transform = Transform3D.Identity;
        if (placement == null) return transform;
        if (placement.Count != 6 || placement.Any(IsCurve)) throw new NotSupportedException("Dynamic/malformed placement.");
        // Serialized Lua35 inputs are left/up/front, i.e. X/Y/Z. The wrapper reorders them
        // for RotateAndTranslatePlacement's front/up/left API; that is not a world-axis swap.
        // Read the serialized inputs directly, then mirror X like MSB/FLVER positions.
        transform.Origin = new Vector3(-Value(placement[0], 0), Value(placement[1], 0), Value(placement[2], 0));
        var angles = Enumerable.Range(3, 3).Select(i => Value(placement[i], 0)).ToArray();
        if (angles.Count(v => v != 0) > 1) throw new NotSupportedException("Unverified multi-axis placement rotation order.");
        transform.Basis = Basis.FromEuler(new Vector3(angles[1], -angles[0], angles[2]) * (Mathf.Pi / 180));
        return transform;
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
        p.Spread = Math.Clamp(Value(e[speed - 2], time), 0, 180);
        if (layer.EmitterId == 32 || layer.EmitterId == 29)
        {
            p.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box;
            var dimensions = layer.EmitterId == 32
                ? new Vector3(Value(e[1], time), Value(e[2], time), Value(e[3], time))
                : new Vector3(Value(e[1], time), 0, Value(e[1], time));
            p.EmissionBoxExtents = dimensions.Abs() * 0.5f;
        }
        else if (layer.EmitterId == 30)
        {
            // Native action30 is a circle, not a sphere. Horizontal disk is our explicit
            // preview convention until native plane/distribution flags are recovered.
            p.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring;
            p.EmissionRingAxis = Vector3.Up;
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
            if (l.Particles != null) continue;
            var particles = new SfxBatchParticles { Name = $"Billboards_{_layers.IndexOf(l)}", Transform = l.Placement };
            try
            {
                particles.Configure(l.Process, l.Mesh, l.Life, l.Capacity, l.Batch, l.Interval, l.Delay,
                    (uint)(_layers.IndexOf(l) + 1) + PlacementSeed, l.Local, ZeroWaitPreviewHz, l.Appearance);
                AddChild(particles);
                l.Particles = particles;
                if (WarmUpOnActivation) particles.WarmUp();
            }
            catch { particles.Free(); throw; }
        }
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
        foreach (var l in _layers) l.Particles?.Advance(delta);
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
