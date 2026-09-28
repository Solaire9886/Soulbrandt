using System.Collections.Generic;
using Godot;
using SoulsFormats;

namespace Archstone;

// MSB Collision and Object parts as static bodies: each part's .hkx collision, mirrored into Godot
// space like the FLVERs. Meshes become one concave shape per surface type, each CollisionShape3D
// carrying its "surface_type" as metadata (see docs/ARCHITECTURE.md, "Havok collision"); boxes,
// spheres, capsules, cylinders and hulls become the matching Godot shapes.
public static class MapCollision
{
	private readonly record struct PartShape(string Name, uint SurfaceType, Shape3D Shape, Transform3D Transform);

	public static Node3D Build(string name, List<MsbPlacement> parts)
	{
		var root = new Node3D { Name = name };
		var shapes = new Dictionary<string, List<PartShape>>();
		foreach (var part in parts)
		{
			if (!shapes.TryGetValue(part.ModelPath, out var partShapes))
				shapes[part.ModelPath] = partShapes = BuildShapes(part.ModelPath);
			if (partShapes.Count == 0) continue;

			var body = new StaticBody3D { Name = part.Name };
			FlverLoader.ApplyPartTransform(body, part);
			foreach (var shape in partShapes)
			{
				var node = new CollisionShape3D { Name = shape.Name, Shape = shape.Shape, Transform = shape.Transform };
				node.SetMeta("surface_type", shape.SurfaceType);
				body.AddChild(node);
			}
			root.AddChild(body);
		}
		return root;
	}

	private static List<PartShape> BuildShapes(string hkxPath)
	{
		var result = new List<PartShape>();
		var skipped = new List<string>();
		List<HKX.CollisionShape> shapes;
		try
		{
			shapes = HKX.Read(ProjectSettings.GlobalizePath(hkxPath)).ReadCollisionShapes(skipped);
		}
		catch (System.Exception e)
		{
			GD.PushWarning($"MapCollision: cannot read '{hkxPath}': {e.Message}");
			return result;
		}
		if (skipped.Count > 0)
			GD.PushWarning($"MapCollision: '{hkxPath}' skips unsupported shapes: {string.Join(", ", skipped)}");

		// X is negated, so each triangle's winding is swapped to keep its facing.
		var facesBySurface = new SortedDictionary<uint, List<Vector3>>();
		foreach (var shape in shapes)
		{
			var m = shape.Transform;
			switch (shape.Kind)
			{
				case HKX.CollisionShapeKind.Mesh:
					if (!facesBySurface.TryGetValue(shape.Material, out var faces))
						facesBySurface[shape.Material] = faces = new List<Vector3>();
					for (int i = 0; i + 2 < shape.Indices.Count; i += 3)
					{
						faces.Add(ToGodot(shape.Vertices[shape.Indices[i]], m));
						faces.Add(ToGodot(shape.Vertices[shape.Indices[i + 2]], m));
						faces.Add(ToGodot(shape.Vertices[shape.Indices[i + 1]], m));
					}
					break;
				case HKX.CollisionShapeKind.Box:
					var e = shape.HalfExtents;
					result.Add(new PartShape($"Box{result.Count}", shape.Material,
						new BoxShape3D { Size = new Vector3(e.X, e.Y, e.Z) * 2 }, ToGodot(m)));
					break;
				case HKX.CollisionShapeKind.Sphere:
					result.Add(new PartShape($"Sphere{result.Count}", shape.Material,
						new SphereShape3D { Radius = shape.Radius }, ToGodot(m)));
					break;
				case HKX.CollisionShapeKind.Capsule:
				case HKX.CollisionShapeKind.Cylinder:
				{
					// Godot's capsule and cylinder run along local Y, their heights end to end.
					Vector3 a = ToGodot(shape.PointA, m), b = ToGodot(shape.PointB, m);
					float length = a.DistanceTo(b);
					var basis = length > 0 ? new Basis(new Quaternion(Vector3.Up, (b - a) / length)) : Basis.Identity;
					var transform = new Transform3D(basis, (a + b) / 2);
					if (shape.Kind == HKX.CollisionShapeKind.Capsule)
						result.Add(new PartShape($"Capsule{result.Count}", shape.Material,
							new CapsuleShape3D { Radius = shape.Radius, Height = length + 2 * shape.Radius }, transform));
					else
						result.Add(new PartShape($"Cylinder{result.Count}", shape.Material,
							new CylinderShape3D { Radius = shape.Radius, Height = length + 2 * shape.ConvexRadius }, transform));
					break;
				}
				case HKX.CollisionShapeKind.ConvexHull:
					var points = new Vector3[shape.Vertices.Count];
					for (int i = 0; i < points.Length; i++)
						points[i] = ToGodot(shape.Vertices[i], m);
					result.Add(new PartShape($"Hull{result.Count}", shape.Material,
						new ConvexPolygonShape3D { Points = points }, Transform3D.Identity));
					break;
			}
		}
		foreach (var (surfaceType, faces) in facesBySurface)
		{
			if (faces.Count == 0) continue;
			var shape = new ConcavePolygonShape3D();
			shape.SetFaces(faces.ToArray());
			result.Add(new PartShape($"Surface{surfaceType}", surfaceType, shape, Transform3D.Identity));
		}
		return result;
	}

	private static Vector3 ToGodot(System.Numerics.Vector3 v, System.Numerics.Matrix4x4 m)
	{
		var p = System.Numerics.Vector3.Transform(v, m);
		return new Vector3(-p.X, p.Y, p.Z);
	}

	// The mirrored transform is still a rotation, which suits a shape symmetric about its own X
	// (box, sphere).
	private static Transform3D ToGodot(System.Numerics.Matrix4x4 m) => FlverModelBuilder.ToGodot(m);
}
