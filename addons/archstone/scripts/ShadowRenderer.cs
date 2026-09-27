using Godot;

namespace Archstone;

// A map's sun shadow from its SHADOW_BANK row. The game uses four perspective splits that follow
// the camera; this stand-in is one static orthographic depth pass over the lit casters, so the
// cast never moves and only the shader's distance fade depends on the camera (a camera-following
// projection made shadows slide). A hand-built SubViewport pass, because Godot's shadow term is
// only reachable in light(), after fragment() where the engine applies min(shadow, lightmap).
// See docs/ARCHITECTURE.md, "Sun shadows".
[Tool]
public partial class ShadowRenderer : Node
{
	// The game's full shadow surface (2x2 tiles of 1024), used here as one map.
	private const int AtlasSize = 2048;
	private const float Near = 0.05f;
	private const float RadiusCap = 200.0f; // 2048^2 has the headroom; ~0.2 m/texel worst case

	private static readonly Shader DepthShader =
		GD.Load<Shader>("res://addons/archstone/shaders/shadow_depth.gdshader");

	private SubViewport _viewport;
	private Camera3D _camera;

	// beginDist/endDist (the game's split range) are accepted but unused by the static region.
	public bool Setup(Godot.Collections.Array<MeshInstance3D> casters, Vector3 lightTowardDirection,
		float beginDist, float endDist, float fadeBegin, float fadeDist, float density, Color tint,
		float depthOffset, float volumeDepth)
	{
		Vector3 lightDir = -lightTowardDirection;
		if (lightDir.LengthSquared() < 1e-8f)
			lightDir = Vector3.Down;
		lightDir = lightDir.Normalized();

		if (!MergedCasterBounds(casters, out Aabb bounds))
			return false;

		// The casters' world bounds, capped (m01_00_00_00 spans the Nexus and the Old One area far below);
		// geometry past the cap is unshadowed.
		Vector3 center = bounds.GetCenter();
		float radius = Mathf.Min(0.5f * bounds.Size.Length() + 2.0f, RadiusCap);
		float volume = Mathf.Clamp(volumeDepth, 1.0f, 60.0f);
		float pullback = radius + volume + 5.0f;
		float far = 2.0f * radius + volume + 10.0f;

		Vector3 up = Mathf.Abs(lightDir.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
		var camXform = new Transform3D(Basis.LookingAt(lightDir, up), center - lightDir * pullback);
		var view = new Projection(camXform.AffineInverse());
		var proj = Projection.CreateOrthogonal(-radius, radius, -radius, radius, Near, far);
		Projection clip = proj * view;

		// cull_front already gives a large effective bias (~the mesh thickness); this is the small
		// numeric tiebreaker, kept roughly constant in world space regardless of `far`.
		float shadowBias = Mathf.Clamp(0.05f / far + Mathf.Abs(depthOffset) * 0.02f, 0.0003f, 0.01f);

		_viewport = new SubViewport
		{
			Name = "ShadowViewport",
			Size = new Vector2I(AtlasSize, AtlasSize),
			// ALWAYS: with ONCE an own-world SubViewport can come up blank in the editor.
			RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
			RenderTargetClearMode = SubViewport.ClearMode.Always,
			TransparentBg = true, // empty texels read 0.0, which sun_shadow() treats as "no occluder"
			OwnWorld3D = true,    // isolate the caster clones from the edited scene
			HandleInputLocally = false,
		};
		AddChild(_viewport);

		_camera = new Camera3D
		{
			Name = "ShadowCamera",
			Projection = Camera3D.ProjectionType.Orthogonal,
			Size = 2.0f * radius,
			Near = Near,
			Far = far,
			Current = true,
		};
		_camera.Transform = camXform;
		_viewport.AddChild(_camera);

		var depthMaterial = new ShaderMaterial { Shader = DepthShader };
		depthMaterial.SetShaderParameter("light_far", far);

		foreach (var caster in casters)
		{
			if (caster.Mesh == null)
				continue;
			var clone = new MeshInstance3D { Mesh = caster.Mesh, MaterialOverride = depthMaterial };
			_viewport.AddChild(clone);
			clone.Transform = WorldTransform(caster); // both hierarchies are flat, so world == local here
		}

		BindReceivers(casters, clip, view, far, shadowBias, density, tint, fadeBegin, fadeDist);
		return true;
	}

	// Pushes every shadow uniform once (the projection never changes), duplicating a shared base
	// material into a per-placement override where ApplyDrawParams made none.
	private void BindReceivers(Godot.Collections.Array<MeshInstance3D> casters, Projection clip,
		Projection view, float lightFar, float shadowBias, float density, Color tint,
		float fadeBegin, float fadeRange)
	{
		var shadowTexture = _viewport.GetTexture();
		var texel = new Vector2(1.0f / AtlasSize, 1.0f / AtlasSize);

		foreach (var caster in casters)
		{
			if (caster.Mesh == null)
				continue;
			for (int surface = 0; surface < caster.Mesh.GetSurfaceCount(); surface++)
			{
				bool isOverride = caster.GetSurfaceOverrideMaterial(surface) is ShaderMaterial;
				var material = (caster.GetSurfaceOverrideMaterial(surface)
					?? caster.Mesh.SurfaceGetMaterial(surface)) as ShaderMaterial;
				if (material == null || !FlverLoader.HasUniform(material.Shader, "shadow_strength"))
					continue;
				if (!isOverride)
				{
					material = (ShaderMaterial)material.Duplicate();
					caster.SetSurfaceOverrideMaterial(surface, material);
				}
				material.SetShaderParameter("shadow_map", shadowTexture);
				material.SetShaderParameter("shadow_light_clip", clip);
				material.SetShaderParameter("shadow_light_view", view);
				material.SetShaderParameter("shadow_light_far", lightFar);
				material.SetShaderParameter("shadow_texel", texel);
				material.SetShaderParameter("shadow_bias", shadowBias);
				material.SetShaderParameter("shadow_strength", 1.0f);
				material.SetShaderParameter("shadow_density", density);
				material.SetShaderParameter("shadow_tint", new Vector3(tint.R, tint.G, tint.B));
				material.SetShaderParameter("shadow_fade_begin", fadeBegin);
				material.SetShaderParameter("shadow_fade_range", Mathf.Max(fadeRange, 0.01f));
			}
		}
	}

	private static bool MergedCasterBounds(Godot.Collections.Array<MeshInstance3D> casters, out Aabb bounds)
	{
		bounds = default;
		bool any = false;
		foreach (var caster in casters)
		{
			if (caster.Mesh == null)
				continue;
			Aabb world = WorldTransform(caster) * caster.GetAabb();
			bounds = any ? bounds.Merge(world) : world;
			any = true;
		}
		return any;
	}

	// Local transforms composed up the parent chain: GlobalTransform is identity off-tree.
	private static Transform3D WorldTransform(Node3D node)
	{
		var transform = node.Transform;
		for (var parent = node.GetParent() as Node3D; parent != null; parent = parent.GetParent() as Node3D)
			transform = parent.Transform * transform;
		return transform;
	}
}
