using System;
using System.Collections.Generic;
using Godot;

namespace Archstone;

// CPU birth/death scheduling + one instanced draw per layer. GPU EmitParticle is unavailable
// in Compatibility. Arrays and capacity are fixed; no Node or material per particle.
[Tool]
public partial class SfxBatchParticles : Node3D
{
    private struct Particle
    {
        public double Birth, Time; // Time: when Position/Velocity were last integrated (motion84)
        public double Clock0; // start of the instance clock this particle was emitted on
        public double Death; // effect-lifetime kill (template2023 arg15 / template2020 lifetime); +inf if none
        public Vector3 Position, Velocity, Gravity;
        public Vector2 Scale;
        public float Angle;
    }
    // Native Emit32 (ELF_ENGINE_ACCURACY_RESEARCH.md 10.3): equal-weight box face, face
    // coordinate scaled by U when internal, and X/Y size multipliers drawn independently.
    internal sealed record NativeEmit32(Vector2 X, Vector2 Y, bool UniformXY, bool Internal);
    // Native motion84 (10.4–10.6), constant-sequence subset. Stepped on the preview clock.
    // Native birth direction (ELF_ENGINE_ACCURACY_RESEARCH.md 11): a cone about local +Z,
    // polar angle Bias(Concentration)·Breadth degrees, azimuth uniform. World, when set
    // (emissionType 1–3), replaces the emitter frame for velocity only; positions keep it.
    internal sealed record NativeDirection(float Breadth, float Concentration, Basis? World);
    internal sealed record NativeMotion84(float Gravity, float Drag, float Wind, float AngleDegrees, int Interval);
    // Finite template2023 schedule (template state machine; see the ELF/Lua research notes):
    // emission k at delay + k*interval happens while k < Emissions (if >= 0) and, for k > 0,
    // k*interval < EmitterLife (if >= 0). EffectLife (if >= 0) destroys the instance, and its
    // particles, at delay + EffectLife. All times are after the startup delay's clock reset.
    internal sealed record NativeFinite(int Emissions, double EmitterLife, double EffectLife);
    // Template2020 effect-instance emission: carrier j (one child effect instance) spawns at
    // Delay + j*Interval while j < Count (if >= 0) and, for j > 0, j*Interval < Life (if >= 0),
    // Instances per spawn, each placed by Place. Each carrier runs this layer's own schedule
    // from its spawn time. KillAtLife: children are deleted with the template at Delay + Life.
    internal sealed record NativeCarrier(double Delay, double Interval, int Count, double Life, int Instances,
        bool KillAtLife, Func<RandomNumberGenerator, Transform3D> Place);
    // Emitter sequences are evaluated when the emitter runs, on the instance clock (time since the
    // instance's startup). Tables sampled uniformly over [0, Span], held after it: speed (min, max)
    // and the X/Y size multipliers (xMin, xMax, yMin, yMax), drawn per particle.
    internal sealed record NativeEmitterCurves(float Span, Vector2[] Speed, Vector4[] Scale);
    // A cluster gravity sequence (downward scalar) on the instance clock, as motion84's (research
    // 10.5): G1/G2 are its first and second integrals, sampled like NativeEmitterCurves.
    internal sealed record NativeGravityCurve(float Span, float[] G1, float[] G2, float Last)
    {
        // Integrals at clock t; past Span the gravity holds at Last.
        internal (float V, float P) At(float t)
        {
            if (t <= 0) return (0, 0);
            float over = Math.Max(0, t - Span);
            float i = Math.Min(t, Span) / Span * (G1.Length - 1);
            int k = Math.Min((int)i, G1.Length - 2); float f = i - k;
            float v = Mathf.Lerp(G1[k], G1[k + 1], f), p = Mathf.Lerp(G2[k], G2[k + 1], f);
            return (v + Last * over, p + v * over + 0.5f * Last * over * over);
        }
    }
    private static T Sample<T>(T[] table, float span, float t, Func<T, T, float, T> lerp)
    {
        float i = Math.Clamp(t / span, 0, 1) * (table.Length - 1);
        int k = Math.Min((int)i, table.Length - 2);
        return lerp(table[k], table[k + 1], i - k);
    }
    private sealed class Carrier { public double Spawn, Death; public Transform3D Frame; public long Next; }
    private readonly List<Carrier> _carriers = new();
    private NativeFinite _finite;
    private NativeCarrier _carrier;
    private double _carrierInterval;
    private long _spawned;
    private readonly Queue<Particle> _live = new();
    private readonly RandomNumberGenerator _random = new();
    private ParticleProcessMaterial.EmissionShapeEnum _shape;
    private Vector3 _extents, _gravity;
    private float _radius, _speedMin, _speedMax, _angleMin, _angleMax;
    private NativeDirection _direction;
    private static readonly IComparer<(Particle Particle, Vector3 Position, float Depth)> DepthOrder =
        Comparer<(Particle Particle, Vector3 Position, float Depth)>.Create((a,b) => b.Depth.CompareTo(a.Depth));
    private MultiMesh _mesh;
    private MultiMeshInstance3D _instance;
    private float[] _buffer;
    // Prepared once per descriptor, shared read-only by placements and LOD reactivations.
    // Sampling Godot resources here used to repeat thousands of native calls per activation.
    internal sealed class Appearance
    {
        internal readonly Vector2[] Sizes = new Vector2[257];
        internal readonly Color[] Colors = new Color[257];
        internal readonly float[] Spin = new float[257];

        internal Appearance(ParticleProcessMaterial process, float life)
        {
            var scale = (CurveXyzTexture)process.ScaleCurve;
            var color = (GradientTexture1D)process.ColorRamp;
            var spin = (CurveTexture)process.AngularVelocityCurve;
            for (int i = 0; i <= 256; i++)
            {
                float t = i / 256f;
                Sizes[i] = new Vector2(scale.CurveX.Sample(t), scale.CurveY.Sample(t));
                Colors[i] = color.Gradient.Sample(t) * process.Color;
                if (i > 0) Spin[i] = Spin[i - 1] + Mathf.DegToRad(
                    (spin.Curve.Sample((i - 1) / 256f) + spin.Curve.Sample(t)) * 0.5f * life / 256);
            }
        }
    }
    private Appearance _appearance;
    private (Particle Particle, Vector3 Position, float Depth)[] _draw;
    private float _life, _drag;
    private int _capacity, _batch;
    private double _interval, _authoredInterval, _delay, _age;
    private uint _seed;
    private bool _local;
    private NativeEmit32 _emit32;
    private NativeMotion84 _motion84;
    private NativeEmitterCurves _curves;
    private NativeGravityCurve _gravityCurve;
    private Vector3 _wind, _stepWind; // _stepWind: k*wind in particle storage space
    private double _tickSeconds;
    private long _ticks;
    private int _perturbCounter;
    // Lifetime given to effectively permanent sprites; appearance is sampled over min(life, 60).
    internal const float PermanentLife = 1e6f;
    // False while an outgoing LOD band expires: the schedule keeps time but spawns nothing.
    internal bool Emitting = true;
    public int LiveCount => _live.Count;
    public long EmittedCount { get; private set; }
    public long CapacityDropped { get; private set; }
    public double Age => _age;

    internal void Configure(ParticleProcessMaterial process, QuadMesh mesh, float life, int capacity,
        int batch, double interval, double delay, uint seed, bool local, int zeroWaitHz, Appearance appearance,
        NativeEmit32 emit32 = null, NativeMotion84 motion84 = null, Vector3 wind = default,
        NativeFinite finite = null, NativeCarrier carrier = null, NativeDirection direction = null,
        NativeEmitterCurves curves = null, NativeGravityCurve gravityCurve = null)
    {
        // Zero-wait state loops are scheduled on a bounded preview clock, never on render
        // frames and never with a zero divisor. Validate here as well as at recipe decoding.
        if (zeroWaitHz is not (30 or 60) || !double.IsFinite(interval) ||
            (interval != 0 && interval < 1.0 / 120) || !double.IsFinite(delay) || delay < 0 || delay > 60 ||
            !float.IsFinite(life) || life < 0.01f || life > 60 && life != PermanentLife || capacity < 1 || capacity > 2048 || batch < 1 || batch > capacity)
            throw new ArgumentOutOfRangeException(nameof(interval), "Invalid bounded particle schedule.");
        _authoredInterval = interval;
        if (interval == 0) interval = 1.0 / zeroWaitHz;
        _shape = process.EmissionShape; _extents = process.EmissionBoxExtents; _radius = _shape == ParticleProcessMaterial.EmissionShapeEnum.Ring ? process.EmissionRingRadius : process.EmissionSphereRadius;
        _gravity = process.Gravity; _speedMin = process.InitialVelocityMin; _speedMax = process.InitialVelocityMax;
        _angleMin = process.AngleMin; _angleMax = process.AngleMax; _direction = direction ?? new NativeDirection(0, 0, null);
        _live.EnsureCapacity(capacity);
        _life = life; _capacity = capacity; _batch = batch;
        _interval = interval; _delay = delay; _seed = seed; _local = local;
        _drag = process.DampingMin;
        _emit32 = emit32; _motion84 = motion84; _wind = wind; _stepWind = wind * (motion84?.Wind ?? 0);
        _finite = finite; _carrier = carrier; _curves = curves; _gravityCurve = gravityCurve;
        if (carrier != null && (carrier.Instances is < 1 or > 64 || !double.IsFinite(carrier.Interval) || carrier.Interval < 0 ||
            !double.IsFinite(carrier.Delay) || carrier.Delay is < 0 or > 60))
            throw new ArgumentOutOfRangeException(nameof(carrier), "Invalid bounded carrier schedule.");
        _carrierInterval = carrier == null ? 0 : carrier.Interval == 0 ? 1.0 / zeroWaitHz : carrier.Interval;
        // Native motion update cadence is unrecovered; share the explicit preview clock.
        _tickSeconds = 1.0 / zeroWaitHz;
        _draw = new (Particle, Vector3, float)[capacity];
        _buffer = new float[capacity * 20];
        _mesh = new MultiMesh {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true, UseCustomData = true, Mesh = mesh, InstanceCount = capacity, VisibleInstanceCount = 0
        };
        _instance = new MultiMeshInstance3D { Multimesh = _mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_instance);
        _appearance = appearance ?? throw new ArgumentNullException(nameof(appearance));
        Reset();
    }

    public void Reset()
    {
        _live.Clear(); _random.Seed = _seed; _age = 0; _ticks = 0; _perturbCounter = 0;
        EmittedCount = 0; CapacityDropped = 0;
        _carriers.Clear(); _spawned = 0; _nextDeath = double.PositiveInfinity;
        // Without template2020 the layer is its own single instance, placed at the layer origin.
        if (_carrier == null)
        {
            var self = new Carrier { Frame = Transform3D.Identity, Death = InstanceDeath(0) };
            if (Emits(self)) _carriers.Add(self);
        }
        if (_mesh != null) _mesh.VisibleInstanceCount = 0;
    }

    // Used for ambient map previews after activation. Bounded to one authored lifetime;
    // it is an editor warm-up, not inferred native startup behavior.
    // Finite effects (a burst that ends) are not warmed up: they play from activation.
    public void WarmUp()
    {
        if (!Indefinite) return;
        double span = _life + (_carrier == null ? 0 : _delay + ChildSpan());
        span = Math.Min(span, 60);
        while (_age < span - 1e-9) Simulate(Math.Min(span - _age, 0.25));
        Upload();
    }

    private bool Indefinite => _carrier == null
        ? _finite == null || _finite.Emissions < 0 && _finite.EmitterLife < 0 && _finite.EffectLife < 0
        : _carrier.Count < 0 && _carrier.Life < 0;

    // How long one child instance keeps emitting (0 when unbounded; only used for warm-up length).
    private double ChildSpan()
    {
        if (_finite == null) return 0;
        double span = double.PositiveInfinity;
        if (_finite.Emissions >= 0) span = Math.Max(0, _finite.Emissions - 1) * _interval;
        if (_finite.EmitterLife >= 0) span = Math.Min(span, _finite.EmitterLife);
        if (_finite.EffectLife >= 0) span = Math.Min(span, _finite.EffectLife);
        return double.IsFinite(span) ? span : 0;
    }

    private double InstanceDeath(double spawn)
    {
        double death = _finite?.EffectLife >= 0 ? spawn + _delay + _finite.EffectLife : double.PositiveInfinity;
        if (_carrier is { KillAtLife: true, Life: >= 0 }) death = Math.Min(death, _carrier.Delay + _carrier.Life);
        return death;
    }

    // Startup evaluates the same stop triggers, so a zero count or lifespan emits nothing.
    private bool Emits(Carrier c) => _finite == null ||
        (_finite.Emissions < 0 || c.Next < _finite.Emissions) && (_finite.EmitterLife < 0 || c.Next * _interval < _finite.EmitterLife - 1e-9);

    private double NextSpawn()
    {
        if (_carrier == null || _carrier.Count >= 0 && _spawned >= _carrier.Count) return double.PositiveInfinity;
        if (_carrier.Life >= 0 && _spawned * _carrierInterval >= _carrier.Life - 1e-9) return double.PositiveInfinity;
        return _carrier.Delay + _spawned * _carrierInterval;
    }

    public void Advance(double delta)
    {
        if (!double.IsFinite(delta) || delta < 0) return;
        // Pause excess wall time after a long editor stall, avoiding unbounded catch-up work.
        Simulate(Math.Min(delta, 0.25));
        Upload();
    }

    private void Expire(double at)
    {
        while (_live.TryPeek(out var p) && Math.Min(p.Birth + _life, p.Death) <= at + 1e-8) _live.Dequeue();
        // Effect-lifetime kills are not in birth order; sweep only when one is due.
        if (_nextDeath > at + 1e-8) return;
        _nextDeath = double.PositiveInfinity;
        for (int i = _live.Count; i > 0; i--)
        {
            var p = _live.Dequeue();
            if (Math.Min(p.Birth + _life, p.Death) <= at + 1e-8) continue;
            _live.Enqueue(p);
            _nextDeath = Math.Min(_nextDeath, p.Death);
        }
    }
    private double _nextDeath = double.PositiveInfinity;

    private void Simulate(double delta)
    {
        double end = _age + delta;
        while (true)
        {
            // Carrier spawns, births and motion84 ticks are processed in time order.
            double spawn = NextSpawn();
            Carrier next = null;
            double birth = double.PositiveInfinity;
            foreach (var c in _carriers)
            {
                double t = c.Spawn + _delay + c.Next * _interval;
                if (t < birth) { birth = t; next = c; }
            }
            double tick = _motion84 == null ? double.PositiveInfinity : _ticks * _tickSeconds;
            if (Math.Min(Math.Min(birth, tick), spawn) > end + 1e-8) break;
            if (tick < birth && tick <= spawn)
            {
                Expire(tick);
                StepMotion84(tick);
                _ticks++;
                continue;
            }
            if (spawn <= birth)
            {
                // ponytail: carriers are linear-scanned; fine for the tens a 2020 keeps alive.
                for (int i = 0; i < _carrier.Instances && _carriers.Count < 4096; i++)
                {
                    var c = new Carrier { Spawn = spawn, Frame = _carrier.Place(_random), Death = InstanceDeath(spawn) };
                    if (Emits(c)) _carriers.Add(c);
                }
                _spawned++;
                continue;
            }
            Expire(birth);
            var frame = next.Frame;
            bool alive = birth < next.Death - 1e-9;
            int count = Emitting && alive ? Math.Min(_batch, _capacity - _live.Count) : 0;
            if (Emitting && alive) CapacityDropped += _batch - count;
            for (int i = 0; i < count; i++)
            {
                Vector3 position = frame * EmissionPosition(out var axis);
                float clock = (float)(birth - next.Spawn - _delay);
                var speed = _curves == null ? new Vector2(_speedMin, _speedMax) : Sample(_curves.Speed, _curves.Span, clock, (a, b, f) => a.Lerp(b, f));
                Vector3 velocity = EmissionDirection(axis) * _random.RandfRange(speed.X, speed.Y);
                Vector3 gravity = _gravity;
                bool world = _direction.World.HasValue;
                if (world) velocity = _direction.World.Value * velocity;
                else velocity = frame.Basis * velocity;
                if (IsInsideTree())
                {
                    if (!_local) position = GlobalTransform * position;
                    // Emitter-frame velocity follows the node; world-frame velocity already is world.
                    if (!_local && !world) velocity = GlobalBasis * velocity;
                    else if (_local && world) velocity = GlobalBasis.Inverse() * velocity;
                }
                var scale = Vector2.One;
                if (_emit32 != null || _curves != null)
                {
                    var r = _curves == null ? new Vector4(_emit32.X.X, _emit32.X.Y, _emit32.Y.X, _emit32.Y.Y)
                        : Sample(_curves.Scale, _curves.Span, clock, (a, b, f) => a.Lerp(b, f));
                    scale.X = Mathf.Lerp(r.X, r.Y, _random.Randf());
                    scale.Y = _emit32?.UniformXY == true ? scale.X : Mathf.Lerp(r.Z, r.W, _random.Randf());
                }
                _live.Enqueue(new Particle { Birth = birth, Time = birth, Clock0 = next.Spawn + _delay, Position = position, Velocity = velocity, Gravity = gravity,
                    Scale = scale, Angle = Mathf.DegToRad(_random.RandfRange(_angleMin, _angleMax)), Death = next.Death });
                _nextDeath = Math.Min(_nextDeath, next.Death);
                EmittedCount++;
            }
            next.Next++;
            if (!alive || !Emits(next)) _carriers.Remove(next);
        }
        _age = end;
        Expire(_age);
    }

    // Native order: perturbation of every live velocity (every Interval calls, one shared
    // counter), then explicit Euler: p += dt*(v + k*wind); v += dt*gravity; linear speed drag.
    // Gravity and wind are world vectors; perturbation acts in particle space (10.5/10.6).
    private void StepMotion84(double time)
    {
        var m = _motion84;
        bool perturb = m.AngleDegrees != 0 && ++_perturbCounter >= m.Interval;
        if (perturb) _perturbCounter = 0;
        Vector3 gravity = new(0, -m.Gravity, 0), wind = _wind * m.Wind;
        Basis toWorld = Basis.Identity;
        if (IsInsideTree())
        {
            toWorld = GlobalBasis.Orthonormalized();
            if (_local) { gravity = toWorld.Inverse() * gravity; wind = toWorld.Inverse() * wind; }
        }
        _stepWind = wind;
        for (int i = _live.Count; i > 0; i--)
        {
            var p = _live.Dequeue(); // ponytail: requeue to mutate in order; ring buffer if n grows
            if (perturb)
            {
                float ax = Mathf.DegToRad((2 * _random.Randf() - 1) * m.AngleDegrees);
                float ay = Mathf.DegToRad((2 * _random.Randf() - 1) * m.AngleDegrees);
                // Native Qy(ay)·Qx(ax) under the raw-X mirror becomes Qy(-ay)·Qx(ax).
                var r = new Basis(Vector3.Up, -ay) * new Basis(Vector3.Right, ax);
                p.Velocity = _local ? r * p.Velocity : toWorld * (r * (toWorld.Inverse() * p.Velocity));
            }
            float dt = (float)(time - p.Time);
            p.Position += dt * (p.Velocity + wind);
            p.Velocity += dt * gravity;
            float speed = p.Velocity.Length();
            if (m.Drag != 0 && speed > 0) p.Velocity *= Math.Max(0, speed - dt * m.Drag) / speed;
            p.Time = time;
            _live.Enqueue(p);
        }
    }

    // axis: the birth direction's cone axis. Emit32 aims it along the birth face's outward
    // normal (native face table, research 11); every other emitter uses local +Z.
    private Vector3 EmissionPosition(out Vector3 axis)
    {
        axis = Vector3.Back;
        if (_shape == ParticleProcessMaterial.EmissionShapeEnum.Box && _emit32 != null)
        {
            var e = _extents;
            float a = _random.RandfRange(-1, 1), b = _random.RandfRange(-1, 1), c = _emit32.Internal ? _random.Randf() : 1;
            int face = _random.RandiRange(0, 5);
            axis = face switch { 0 => Vector3.Right, 1 => Vector3.Left, 2 => Vector3.Up, 3 => Vector3.Down, 4 => Vector3.Back, _ => Vector3.Forward };
            return face switch {
                0 => new Vector3(e.X * c, e.Y * a, e.Z * b), 1 => new Vector3(-e.X * c, e.Y * a, e.Z * b),
                2 => new Vector3(e.X * a, e.Y * c, e.Z * b), 3 => new Vector3(e.X * a, -e.Y * c, e.Z * b),
                4 => new Vector3(e.X * a, e.Y * b, e.Z * c), _ => new Vector3(e.X * a, e.Y * b, -e.Z * c)
            };
        }
        if (_shape == ParticleProcessMaterial.EmissionShapeEnum.Box)
        {
            var e = _extents;
            return new Vector3(_random.RandfRange(-e.X, e.X), _random.RandfRange(-e.Y, e.Y), _random.RandfRange(-e.Z, e.Z));
        }
        if (_shape == ParticleProcessMaterial.EmissionShapeEnum.Ring)
        {
            // Native radius is R·u (radial distribution argument absent/0), not area-uniform.
            float radius = _random.Randf() * _radius, angle = _random.Randf() * Mathf.Tau;
            return new Vector3(radius * MathF.Cos(angle), radius * MathF.Sin(angle), 0);
        }
        if (_shape == ParticleProcessMaterial.EmissionShapeEnum.Sphere)
            return UnitSphere() * (MathF.Cbrt(_random.Randf()) * _radius);
        return Vector3.Zero;
    }

    private Vector3 UnitSphere()
    {
        float y = _random.RandfRange(-1, 1), angle = _random.Randf() * Mathf.Tau;
        float radius = MathF.Sqrt(Math.Max(0, 1 - y * y));
        return new Vector3(radius * MathF.Cos(angle), y, radius * MathF.Sin(angle));
    }

    private Vector3 EmissionDirection(Vector3 axis)
    {
        float u = _random.Randf(), c = _direction.Concentration;
        float bias = c <= 0 ? MathF.Pow(u, 1 + c) : 1 - MathF.Pow(u, 1 - c);
        float polar = Mathf.DegToRad(bias * _direction.Breadth), azimuth = _random.Randf() * Mathf.Tau;
        var cone = new Vector3(MathF.Sin(polar) * MathF.Cos(azimuth), MathF.Sin(polar) * MathF.Sin(azimuth), MathF.Cos(polar));
        // Rotate the +Z cone onto the axis; the cone is symmetric, so any such rotation will do.
        if (axis == Vector3.Back) return cone;
        if (axis == Vector3.Forward) return -cone;
        return new Basis(Vector3.Back.Cross(axis), Mathf.Pi / 2) * cone;
    }

    private Vector3 PositionAt(Particle p)
    {
        if (_motion84 != null)
            return p.Position + (p.Velocity + _stepWind) * (float)(_age - p.Time);
        float t = (float)(_age - p.Birth);
        // Preserve the preview's simple linear deceleration approximation. This is not
        // native motion84 turbulence, nor a claim about DeS's integration method.
        float speed = p.Velocity.Length();
        float moving = _drag > 0 ? Math.Min(t, speed / _drag) : t;
        Vector3 travel = speed > 0 ? p.Velocity * (moving - 0.5f * _drag * moving * moving / speed) : Vector3.Zero;
        if (_gravityCurve != null)
        {
            // Displacement under g(clock) from birth: G2(now) - G2(birth) - G1(birth) * t, downward.
            var (vb, pb) = _gravityCurve.At((float)(p.Birth - p.Clock0));
            var (_, pn) = _gravityCurve.At((float)(_age - p.Clock0));
            travel.Y -= pn - pb - vb * t;
        }
        return p.Position + travel + p.Gravity * (0.5f * t * t);
    }

    private void Upload()
    {
        if (!IsInsideTree()) return;
        Camera3D camera = GetViewport().GetCamera3D();
#if TOOLS
        if (Engine.IsEditorHint())
        {
            var preview = GetParent() as SfxPreview;
            camera = EditorInterface.Singleton.GetEditorViewport3D(preview?.EditorViewportIndex ?? 0).GetCamera3D();
        }
#endif
        var inverse = GlobalTransform.AffineInverse();
        int n = 0;
        foreach (var p in _live)
        {
            Vector3 position = PositionAt(p);
            var world = _local ? GlobalTransform * position : position;
            _draw[n++] = (p, _local ? position : inverse * position,
                camera == null ? 0 : (world - camera.GlobalPosition).Dot(-camera.GlobalBasis.Z));
        }
        // MultiMesh instances have no particle view-depth sorting. Sort back to front ourselves.
        Array.Sort(_draw, 0, n, DepthOrder);
        for (int i = 0; i < n; i++)
        {
            var d = _draw[i];
            float sample = Mathf.Clamp((float)((_age - d.Particle.Birth) / Math.Min(_life, 60)) * 256, 0, 256);
            int key = Math.Min((int)sample, 255); float fraction = sample - key;
            var size = _appearance.Sizes[key].Lerp(_appearance.Sizes[key + 1], fraction) * d.Particle.Scale;
            var color = _appearance.Colors[key].Lerp(_appearance.Colors[key + 1], fraction);
            int at = i * 20;
            _buffer[at] = size.X; _buffer[at + 3] = d.Position.X;
            _buffer[at + 5] = size.Y; _buffer[at + 7] = d.Position.Y;
            _buffer[at + 10] = 1; _buffer[at + 11] = d.Position.Z;
            _buffer[at + 12] = color.R; _buffer[at + 13] = color.G; _buffer[at + 14] = color.B; _buffer[at + 15] = color.A;
            _buffer[at + 16] = d.Particle.Angle + Mathf.Lerp(_appearance.Spin[key], _appearance.Spin[key + 1], fraction);
            _buffer[at + 17] = sample / 256;
        }
        _mesh.Buffer = _buffer;
        _mesh.VisibleInstanceCount = n;
    }

    public Godot.Collections.Dictionary GetSummary() => new() {
        ["live"] = LiveCount, ["emitted"] = EmittedCount, ["dropped"] = CapacityDropped,
        ["authored_interval"] = _authoredInterval, ["acceleration"] = _gravity,
        ["capacity"] = _capacity, ["batch"] = _batch, ["interval"] = _interval, ["age"] = _age,
        ["carriers"] = _carriers.Count, ["carriers_spawned"] = _spawned
    };
}
