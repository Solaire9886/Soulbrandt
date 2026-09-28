using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SoulsFormats;
using P = SoulsFormats.FFXDLSE.Param;

namespace Archstone;

// Template2020 effect-instance emission. Its emitter actions 4/8/9/10/18 are the point, disk,
// square, sphere and box counterparts of billboard emitters 28–32, but each "particle" is a
// child effect instance. Behaviour is read from the game's own data: template2020's state
// machine and the Lua wrappers 0000004/8/9/10/18/51/52.lua (external research archive,
// OPEN_ENGINE_ACCURACY_PROBLEMS.md section E).
//
// Native matrices are row-vector (MulMM(A, B) applies A first; action10's sphere direction
// (0,0,1)·Rx·Rz only covers a sphere that way). Each child is oriented, then translated, so
// the disk and square positions are the unrotated (x, y, 0) in the local XY plane and the
// tilt/spin only orients the child. Lua's math.random(m, n) draws integers; its 1/500, 1/200
// and 1/100 grids are kept. Rotation signs follow the column form of the row matrices, which
// the box faces confirm (each face rotation maps +Z onto that face's normal).
public partial class SfxPreview
{
    // Base: the template's own placed frame; children's further placements compose inside each instance.
    private sealed record CarrierContext(SfxBatchParticles.NativeCarrier Schedule, float Extent, Transform3D Base);
    private CarrierContext _carrierContext;

    private static float Bias(RandomNumberGenerator r, float distribution)
    {
        float u = r.RandiRange(0, 500) / 500f;
        return distribution > 0 ? 1 - MathF.Pow(u, 1 - distribution) : MathF.Pow(u, 1 + distribution);
    }

    // Tilt by breadth·bias about X, then (only when tilted) a random spin about Z.
    private static Basis Tilt(RandomNumberGenerator r, float breadth, float angleDistribution)
    {
        float tilt = Mathf.DegToRad(Bias(r, angleDistribution) * breadth);
        float spin = tilt > 0 ? r.RandiRange(-314, 314) / 100f : 0;
        return new Basis(Vector3.Back, spin) * new Basis(Vector3.Right, tilt);
    }

    // emissionType 1/2 turn the child's +Z to face +Y/-Y; others leave it.
    private static Basis EmissionType(int type) => type switch {
        1 => new Basis(Vector3.Right, -Mathf.Pi / 2),
        2 => new Basis(Vector3.Right, Mathf.Pi / 2),
        _ => Basis.Identity
    };

    // Native pose → Godot (raw X mirrored): S·B·S and (-x, y, z).
    private static Transform3D Mirror(Basis b, Vector3 p)
    {
        var s = Basis.FromScale(new Vector3(-1, 1, 1));
        return new Transform3D(s * b * s, new Vector3(-p.X, p.Y, p.Z));
    }

    private static Func<RandomNumberGenerator, Transform3D> CarrierPlacement(FFXDLSE.Param32 emit, out float extent, out int instances)
    {
        var a = emit.ParamList.Params;
        int expected = emit.ActionID switch { 4 => 5, 8 or 9 => 7, 10 => 6, 18 => 8, _ => -1 };
        if (expected < 0) throw new NotSupportedException($"Effect-instance emitter action{emit.ActionID} not implemented.");
        if (a.Count != expected || a.Skip(1).Any(IsCurve)) throw new NotSupportedException("Dynamic/malformed effect-instance emitter.");
        float F(int i) => Value(a[i], 0);
        switch (emit.ActionID)
        {
            case 4: // point
            {
                float breadth = F(1), angle = F(2); int type = Integer(a[4]);
                instances = Integer(a[3]); extent = 0;
                return r => Mirror(EmissionType(type) * Tilt(r, breadth, angle), Vector3.Zero);
            }
            case 8: // disk, radius·bias, uniform angle, local XY plane
            {
                float radius = F(1), breadth = F(2), angle = F(3), position = F(4); int type = Integer(a[6]);
                instances = Integer(a[5]); extent = Math.Abs(radius);
                return r =>
                {
                    var basis = EmissionType(type) * Tilt(r, breadth, angle);
                    float d = radius * Bias(r, position), theta = r.RandiRange(0, 1570) / 250f;
                    return Mirror(basis, new Vector3(d * MathF.Cos(theta), d * MathF.Sin(theta), 0));
                };
            }
            case 9: // square, half-side·(1 - u^(1-pd)) with random sign per axis, local XY plane
            {
                float half = F(1) / 2, breadth = F(2), angle = F(3), position = F(4); int type = Integer(a[6]);
                instances = Integer(a[5]); extent = Math.Abs(half) * MathF.Sqrt(2);
                return r =>
                {
                    float Skew() => 1 - MathF.Pow(r.RandiRange(0, 500) / 500f, 1 - position);
                    var basis = Tilt(r, breadth, angle);
                    float x = Skew(), y = Skew();
                    if (r.RandiRange(0, 1) == 1) x = -x;
                    if (r.RandiRange(0, 1) == 1) y = -y;
                    return Mirror(EmissionType(type) * basis, new Vector3(half * x, half * y, 0));
                };
            }
            case 10: // sphere: polar angle uniform in [0, pi] from +Z; surface, or radius·u when internal
            {
                float radius = F(1), breadth = F(2), angle = F(3); bool internalVolume = Integer(a[5]) != 0;
                instances = Integer(a[4]); extent = Math.Abs(radius);
                return r =>
                {
                    float tiltBias = Bias(r, angle);
                    float polar = r.RandiRange(0, 314) / 100f, azimuth = r.RandiRange(-314, 314) / 100f;
                    var toNormal = new Basis(Vector3.Back, azimuth) * new Basis(Vector3.Right, polar);
                    var normal = toNormal * Vector3.Back;
                    float d = internalVolume ? radius * r.RandiRange(0, 200) / 200f : radius;
                    // Tilt about (helper - normal), then spin about the normal (0000010.lua).
                    var helper = normal.Z != 0 ? new Vector3(0, 0, 1 / normal.Z)
                        : normal.Y != 0 ? new Vector3(0, 1 / normal.Y, 0) : Vector3.Right;
                    float tilt = Mathf.DegToRad(tiltBias * breadth);
                    float spin = tilt > 0 ? r.RandiRange(-314, 314) / 100f : 0;
                    var axis = helper - normal;
                    var basis = axis.Length() > 1e-6f && tilt != 0 ? new Basis(axis.Normalized(), tilt) * toNormal : toNormal;
                    if (spin != 0) basis = new Basis(normal, spin) * basis;
                    return Mirror(basis, normal * d);
                };
            }
            default: // 18, box: uniform face; depth ·u when internal; face rotation maps +Z to the face normal
            {
                var half = new Vector3(F(1), F(2), F(3)).Abs() / 2;
                float breadth = F(4), angle = F(5); bool internalVolume = Integer(a[7]) != 0;
                instances = Integer(a[6]); extent = half.Length();
                return r =>
                {
                    var basis = Tilt(r, breadth, angle);
                    int face = r.RandiRange(0, 5);
                    float s1 = r.RandiRange(-200, 200) / 200f, s2 = r.RandiRange(-200, 200) / 200f;
                    float w = internalVolume ? r.RandiRange(0, 200) / 200f : 1;
                    var (rotation, p) = face switch {
                        0 => (new Basis(Vector3.Right, Mathf.Pi / 2), new Vector3(s1 * half.X, -w * half.Y, s2 * half.Z)),
                        1 => (new Basis(Vector3.Right, -Mathf.Pi / 2), new Vector3(s1 * half.X, w * half.Y, s2 * half.Z)),
                        2 => (new Basis(Vector3.Right, Mathf.Pi), new Vector3(s1 * half.X, s2 * half.Y, -w * half.Z)),
                        3 => (Basis.Identity, new Vector3(s1 * half.X, s2 * half.Y, w * half.Z)),
                        4 => (new Basis(Vector3.Up, -Mathf.Pi / 2), new Vector3(-w * half.X, s1 * half.Y, s2 * half.Z)),
                        _ => (new Basis(Vector3.Up, Mathf.Pi / 2), new Vector3(w * half.X, s1 * half.Y, s2 * half.Z)),
                    };
                    return Mirror(rotation * basis, p);
                };
            }
        }
    }

    // Live population bound across overlapping child instances: instances alive at once ×
    // particles one instance holds. Zero intervals assume the fastest (60 Hz) preview clock.
    private int CarrierCapacity(SfxBatchParticles.NativeCarrier c, SfxBatchParticles.NativeFinite f,
        int childCapacity, int batch, double interval, double delay, float life)
    {
        double step = interval == 0 ? 1.0 / 60 : interval, spawnStep = c.Interval == 0 ? 1.0 / 60 : c.Interval;
        long emissions = Emissions(f, step);
        long perChild = emissions < 0 ? childCapacity : Math.Min(childCapacity, emissions * batch);
        double active = emissions < 0 ? double.PositiveInfinity : delay + Math.Max(0, emissions - 1) * step + life;
        if (f?.EffectLife >= 0) active = Math.Min(active, delay + f.EffectLife);
        long spawns = c.Count >= 0 ? c.Count : c.Life >= 0 ? (long)Math.Ceiling(c.Life / spawnStep - 1e-9) : -1;
        if (spawns < 0 && !double.IsFinite(active))
            throw new NotSupportedException("Unbounded effect-instance emission: unlimited child instances that never stop emitting.");
        long overlap = double.IsFinite(active) ? (long)Math.Floor(active / spawnStep) + 1 : long.MaxValue;
        long total = Math.Min(spawns < 0 ? long.MaxValue : spawns, overlap) * c.Instances * perChild;
        if (total > 2048)
        {
            Note($"Effect-instance layer needs up to {total} particles; clamped to the 2048 per-layer preview limit (excess births are dropped and counted).");
            total = 2048;
        }
        return (int)Math.Max(1, total);
    }

    // Template2020 arguments: 0 emitted effect, 1 effect-instance emitter, 2 motion,
    // 3 placement, 4 per-emission action, 5 emission count (-1 unlimited), 6 startup delay,
    // 7 interval, 8 lifetime (<0 unlimited), 9 follow parent, 10 delete with parent, 11 drain
    // when the parent is gone.
    private void VisitTemplate2020(List<P> args, string path, int depth, int templates, Func<int, Texture2D> texture)
    {
        if (args.Count != 12) throw new NotSupportedException("Template2020 arity mismatch.");
        if (_carrierContext != null) throw new NotSupportedException("Nested effect-instance emission.");
        if (args[0] is not FFXDLSE.Param31 { EffectID: 2023 or 2101 })
            throw new NotSupportedException($"Emitted effect is {(args[0] is FFXDLSE.Param31 f ? $"template{f.EffectID}" : args[0].GetType().Name)}; only template2023/2101 children are supported.");
        if (args[1] is not FFXDLSE.Param32 emit) throw new NotSupportedException("Missing effect-instance emitter.");
        if (args[4] is not FFXDLSE.Param32 { ActionID: 0 }) throw new NotSupportedException("Per-emission action.");
        if (args.Skip(5).Any(IsCurve)) throw new NotSupportedException("Dynamic template2020 schedule.");
        var place = CarrierPlacement(emit, out float extent, out int instances);
        int count = Integer(args[5]);
        double delay = Value(args[6], 0), interval = Value(args[7], 0), life = Value(args[8], 0);
        if (instances is < 1 or > 64 || delay is < 0 or > 60 || interval is < 0 or > 60)
            throw new NotSupportedException($"Template2020 schedule outside bounded playback: instances={instances}, delay={delay}, interval={interval}.");
        var parent = _parentPlacement;
        var spin = (_spin, _spinPivot, _cameraAttach);
        try
        {
            _parentPlacement *= PlacementAction(args[3]);
            ApplyMotion(args[2], path);
            _carrierContext = new CarrierContext(new SfxBatchParticles.NativeCarrier(delay, interval, count, life, instances, false, place), extent, _parentPlacement);
            Note($"Template2020 at {path}: action{emit.ActionID} emits {instances} child instance(s) " +
                 $"every {(interval == 0 ? "update" : $"{interval:0.###}s")}, {(count < 0 ? "unlimited" : $"{count} time(s)")}" +
                 $"{(life >= 0 ? $", for {life:0.###}s" : "")}. Child orientation beyond zero tilt, and each child's own capacity limit, are approximations.");
            Visit(new List<P> { args[0] }, path + ":particle", depth + 1, templates + 1, texture);
        }
        finally { _parentPlacement = parent; (_spin, _spinPivot, _cameraAttach) = spin; _carrierContext = null; }
    }
}
