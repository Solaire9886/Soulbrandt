using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SoulsFormats;

namespace Archstone;

public partial class MapSfxPreview
{
    private const int MaxObjectRequests = 128;
    private readonly Dictionary<int, MsbPlacement> _objects = new();
    private readonly Dictionary<string, Dictionary<int, Transform3D>> _dummyCache = new();
    private readonly List<string> _attachmentNotes = new();
    private bool _previewReviewedObjectEffects;

    [ExportGroup("Script Object Effects")]
    [Export] public bool PreviewReviewedObjectEffects
    {
        get => _previewReviewedObjectEffects;
        set
        {
            if (_previewReviewedObjectEffects == value) return;
            _previewReviewedObjectEffects = value;
            if (!IsInsideTree()) return;
            foreach (int key in _placements.Where(p => p.Value.Object && p.Value.Reviewed).Select(p => p.Key).ToArray())
                RemovePlacement(key);
            if (value) AddReviewedObjectEffects();
            _refresh = 0;
            Report();
        }
    }

    // Map-scoped, idempotent request API. -1 means the object origin, not an inferred dummy.
    // General Lua activation is a future producer of these same requests.
    public bool RequestObjectEffect(int entityId, int effectId, int dummyId = -1) =>
        AddObjectEffect(entityId, effectId, dummyId, false, "Explicit object-effect request");

    public void RemoveObjectEffect(int entityId, int effectId, int dummyId = -1)
    {
        foreach (int key in _placements.Where(p => p.Value.Object && p.Value.EntityId == entityId &&
                     p.Value.EffectId == effectId && p.Value.DummyId == dummyId).Select(p => p.Key).ToArray())
            RemovePlacement(key);
        _refresh = 0;
        Report();
    }

    private void RemovePlacement(int key)
    {
        if (_active.Remove(key, out var preview) && GodotObject.IsInstanceValid(preview)) preview.Free();
        if (_placements.Remove(key, out var placement) && !_placements.Values.Any(p => p.EffectId == placement.EffectId))
        {
            if (_catalog.Remove(placement.EffectId, out var description)) description.Free();
            _unsupported.Remove(placement.EffectId);
        }
    }

    private bool AddObjectEffect(int entityId, int effectId, int dummyId, bool reviewed, string source)
    {
        if (entityId < 0 || effectId <= 0 || dummyId < -1 || !IsInsideTree()) return false;
        var existing = _placements.Values.FirstOrDefault(p => p.Object && p.EntityId == entityId &&
            p.EffectId == effectId && p.DummyId == dummyId);
        if (existing != null)
        {
            // An explicit caller can take ownership of an existing reviewed request.
            if (!reviewed) { existing.Reviewed = false; existing.Source = source; }
            return true;
        }
        if (_placements.Values.Count(p => p.Object) >= MaxObjectRequests) return false;
        if (!_objects.TryGetValue(entityId, out var target))
        { AttachmentNote($"Missing/ambiguous object entity {entityId} in this map."); return false; }
        var anchor = GetParent()?.GetNodeOrNull<Node3D>(new NodePath(target.Name));
        if (anchor == null)
        { AttachmentNote($"Object entity {entityId}: load a fresh map to resolve {target.Name}."); return false; }
        try
        {
            Transform3D attachment = Transform3D.Identity;
            if (dummyId >= 0 && !ReadDummyPlacements(target.ModelPath).TryGetValue(dummyId, out attachment))
            { AttachmentNote($"Object entity {entityId}: dummy {dummyId} missing or unsupported."); return false; }
            _placements.Add(_nextPlacement++, new Placement {
                Object = true, Reviewed = reviewed, EffectId = effectId, EntityId = entityId, DummyId = dummyId,
                Name = target.Name, Source = source, Anchor = anchor, Attachment = attachment
            });
            _refresh = 0;
            UpdateObjectTransforms();
            return true;
        }
        catch (Exception e)
        { AttachmentNote($"Object entity {entityId}: {e.Message}"); return false; }
    }

    private void UpdateObjectTransforms()
    {
        if (!IsInsideTree()) return;
        // Active attachments follow editor object transforms each frame, without parsing assets.
        // Nonlocal particle layers retain world-space births in SfxBatchParticles.
        bool invertible = Math.Abs(GlobalBasis.Determinant()) > 0.000001f;
        var inverse = invertible ? GlobalTransform.AffineInverse() : Transform3D.Identity;
        foreach (var pair in _placements)
        {
            var p = pair.Value;
            if (!p.Object) continue;
            p.Available = invertible && GodotObject.IsInstanceValid(p.Anchor) && p.Anchor.IsInsideTree() &&
                p.Anchor.IsVisibleInTree() && p.Anchor.GlobalTransform.IsFinite() &&
                Math.Abs(p.Anchor.GlobalBasis.Determinant()) > 0.000001f;
            if (p.Available) p.Transform = inverse * p.Anchor.GlobalTransform * p.Attachment;
            if (_active.TryGetValue(pair.Key, out var active) && GodotObject.IsInstanceValid(active))
            {
                if (p.Available) active.Transform = p.Transform;
                else { active.Free(); _active.Remove(pair.Key); }
            }
        }
    }

    private Dictionary<int, Transform3D> ReadDummyPlacements(string path)
    {
        if (_dummyCache.TryGetValue(path, out var cached)) return cached;
        string real = ProjectSettings.GlobalizePath(path);
        if (new FileInfo(real).Length > 64 * 1024 * 1024) throw new InvalidDataException("Attachment model exceeds 64 MiB limit.");
        var model = FLVER0.Read(real);
        if (model.Dummies.Count > 4096 || model.Nodes.Count > 4096)
            throw new InvalidDataException("Attachment node/dummy budget exceeded.");
        var result = new Dictionary<int, Transform3D>();
        foreach (var group in model.Dummies.GroupBy(d => (int)d.ReferenceID))
        {
            // Never select an arbitrary duplicate or silently use the object origin.
            if (group.Count() != 1) continue;
            var dummy = group.Single();
            if (dummy.AttachBoneIndex != -1) continue; // Animated attachment semantics not implemented.
            var transform = System.Numerics.Matrix4x4.Identity;
            int node = dummy.ParentBoneIndex;
            var seen = new HashSet<int>();
            while (node >= 0)
            {
                if (node >= model.Nodes.Count || !seen.Add(node))
                    throw new InvalidDataException("Invalid/cyclic dummy parent hierarchy.");
                transform *= model.Nodes[node].ComputeLocalTransform();
                node = model.Nodes[node].ParentIndex;
            }
            var position = System.Numerics.Vector3.Transform(dummy.Position, transform);
            var godotPosition = new Vector3(-position.X, position.Y, position.Z);
            if (!godotPosition.IsFinite()) throw new InvalidDataException("Non-finite dummy position.");
            // Preserve position in the same static model space as rigid FLVER meshes. Native
            // dummy orientation/follow flags need verification; don't invent a forward-axis mapping.
            result.Add(group.Key, new Transform3D(Basis.Identity, godotPosition));
        }
        _dummyCache.Add(path, result);
        AttachmentNote("Dummy attachments use static FLVER parent transforms; dummy orientation and animated attach bones remain unsupported.");
        return result;
    }

    private void AttachmentNote(string note)
    {
        if (_attachmentNotes.Count < 32 && !_attachmentNotes.Contains(note)) _attachmentNotes.Add(note);
    }

    private void AddReviewedObjectEffects()
    {
        // Deliberately reviewed examples, NOT inferred runtime state or a Lua parser. The
        // inspector toggle explicitly previews their enabled state. Provenance:
        // LUA_FIRST_PASS.md, external research archive (~/godot/research-dump/), not tracked
        // in this repo.
        string block = Path.GetFileNameWithoutExtension(MapPath);
        void Add(int entity, int effect, int dummy, string condition) =>
            AddObjectEffect(entity, effect, dummy, true, $"{block}.lua:{condition}");
        switch (block)
        {
            case "m01_00_00_00":
                Add(1141, 99100, -1, "396; final-boss sequence completed");
                Add(1146, 99100, -1, "399; final-boss sequence completed");
                break;
            case "m02_00_00_00":
                Add(1983, 99100, -1, "169; starting archstone");
                Add(1982, 99100, -1, "553; boss defeated");
                break;
            case "m02_02_00_00":
                for (int dummy = 1; dummy <= 3; dummy++)
                    Add(1495, 1400, dummy, $"{647 + dummy}; wagon fire persists after event");
                Add(1496, 1400, 1, "854; burning straw initialization");
                Add(1497, 1400, 1, "872; burning straw initialization");
                break;
            case "m03_02_00_00":
                for (int entity = 1480; entity <= 1485; entity++)
                    Add(entity, 93000, 1, "385–393; wisp not exploded");
                break;
            default:
                AttachmentNote("No reviewed object-effect examples for this map; explicit requests are still supported.");
                break;
        }
    }
}
