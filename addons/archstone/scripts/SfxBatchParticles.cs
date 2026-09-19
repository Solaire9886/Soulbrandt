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
        public double Birth;
        public Vector3 Position, Velocity, Gravity;
        public float Angle;
    }
    private readonly Queue<Particle> _live = new();
    private readonly RandomNumberGenerator _random = new();
    private ParticleProcessMaterial.EmissionShapeEnum _shape;
    private Vector3 _extents, _gravity;
    private float _radius, _speedMin, _speedMax, _angleMin, _angleMax, _spreadCos;
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
    private long _emission;
    private uint _seed;
    private bool _local;
    public int LiveCount => _live.Count;
    public long EmittedCount { get; private set; }
    public long CapacityDropped { get; private set; }
    public double Age => _age;

    internal void Configure(ParticleProcessMaterial process, QuadMesh mesh, float life, int capacity,
        int batch, double interval, double delay, uint seed, bool local, int zeroWaitHz, Appearance appearance)
    {
        // Zero-wait state loops are scheduled on a bounded preview clock, never on render
        // frames and never with a zero divisor. Validate here as well as at recipe decoding.
        if (zeroWaitHz is not (30 or 60) || !double.IsFinite(interval) ||
            (interval != 0 && interval < 1.0 / 120) || !double.IsFinite(delay) || delay < 0 || delay > 60 ||
            !float.IsFinite(life) || life < 0.01f || life > 60 || capacity < 1 || capacity > 2048 || batch < 1 || batch > capacity)
            throw new ArgumentOutOfRangeException(nameof(interval), "Invalid bounded particle schedule.");
        _authoredInterval = interval;
        if (interval == 0) interval = 1.0 / zeroWaitHz;
        _shape = process.EmissionShape; _extents = process.EmissionBoxExtents; _radius = _shape == ParticleProcessMaterial.EmissionShapeEnum.Ring ? process.EmissionRingRadius : process.EmissionSphereRadius;
        _gravity = process.Gravity; _speedMin = process.InitialVelocityMin; _speedMax = process.InitialVelocityMax;
        _angleMin = process.AngleMin; _angleMax = process.AngleMax; _spreadCos = MathF.Cos(Mathf.DegToRad(process.Spread));
        _live.EnsureCapacity(capacity);
        _life = life; _capacity = capacity; _batch = batch;
        _interval = interval; _delay = delay; _seed = seed; _local = local;
        _drag = process.DampingMin;
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
        _live.Clear(); _random.Seed = _seed; _age = 0; _emission = 0;
        EmittedCount = 0; CapacityDropped = 0;
        if (_mesh != null) _mesh.VisibleInstanceCount = 0;
    }

    // Used for ambient map previews after activation. Bounded to one authored lifetime;
    // it is an editor warm-up, not inferred native startup behavior.
    public void WarmUp()
    {
        int steps = (int)Math.Ceiling(_life / 0.25);
        for (int i = 0; i < steps; i++) Simulate(Math.Min(_life - _age, 0.25));
        Upload();
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
        while (_live.TryPeek(out var p) && p.Birth + _life <= at + 1e-8) _live.Dequeue();
    }

    private void Simulate(double delta)
    {
        _age += delta;
        double birth;
        while ((birth = _delay + _emission * _interval) <= _age + 1e-8)
        {
            Expire(birth);
            int count = Math.Min(_batch, _capacity - _live.Count);
            CapacityDropped += _batch - count;
            for (int i = 0; i < count; i++)
            {
                Vector3 position = EmissionPosition();
                Vector3 velocity = EmissionDirection() * _random.RandfRange(_speedMin, _speedMax);
                Vector3 gravity = _gravity;
                if (!_local && IsInsideTree())
                {
                    position = GlobalTransform * position;
                    velocity = GlobalBasis * velocity;
                    // Match the former particle material's world gravity convention.
                }
                _live.Enqueue(new Particle { Birth = birth, Position = position, Velocity = velocity, Gravity = gravity,
                    Angle = Mathf.DegToRad(_random.RandfRange(_angleMin, _angleMax)) });
                EmittedCount++;
            }
            _emission++;
        }
        Expire(_age);
    }

    private Vector3 EmissionPosition()
    {
        if (_shape == ParticleProcessMaterial.EmissionShapeEnum.Box)
        {
            var e = _extents;
            return new Vector3(_random.RandfRange(-e.X, e.X), _random.RandfRange(-e.Y, e.Y), _random.RandfRange(-e.Z, e.Z));
        }
        if (_shape == ParticleProcessMaterial.EmissionShapeEnum.Ring)
        {
            float radius = MathF.Sqrt(_random.Randf()) * _radius, angle = _random.Randf() * Mathf.Tau;
            return new Vector3(radius * MathF.Cos(angle), 0, radius * MathF.Sin(angle));
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

    private Vector3 EmissionDirection()
    {
        // Uniform solid angle about +Y. Native concentration/distribution flags are pending.
        float y = _random.RandfRange(_spreadCos, 1);
        float angle = _random.Randf() * Mathf.Tau, radius = MathF.Sqrt(Math.Max(0, 1 - y * y));
        return new Vector3(radius * MathF.Cos(angle), y, radius * MathF.Sin(angle));
    }

    private Vector3 PositionAt(Particle p)
    {
        float t = (float)(_age - p.Birth);
        // Preserve the preview's simple linear deceleration approximation. This is not
        // native motion84 turbulence, nor a claim about DeS's integration method.
        float speed = p.Velocity.Length();
        float moving = _drag > 0 ? Math.Min(t, speed / _drag) : t;
        Vector3 travel = speed > 0 ? p.Velocity * (moving - 0.5f * _drag * moving * moving / speed) : Vector3.Zero;
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
            float sample = Mathf.Clamp((float)((_age - d.Particle.Birth) / _life) * 256, 0, 256);
            int key = Math.Min((int)sample, 255); float fraction = sample - key;
            var size = _appearance.Sizes[key].Lerp(_appearance.Sizes[key + 1], fraction);
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
        ["capacity"] = _capacity, ["batch"] = _batch, ["interval"] = _interval, ["age"] = _age
    };
}
