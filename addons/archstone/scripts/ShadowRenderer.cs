using Godot;

namespace Archstone;

// Sun-shadow for a loaded map, driven by the map's SHADOW_BANK row (docs/context.md parts 39/49).
// DeS uses 4-split PERSPECTIVE shadow maps (the ShadowBank "PSM" fields calibulateFar /
// persedDepthOffset / radFactor; the captured cast matrices are perspective and re-warp around the
// camera each frame).
//
// This is v2, deliberately simpler: ONE STATIC orthographic depth pass covering the lit casters'
// bounds. The shadow projection is fixed in the world - a fixed sun + fixed geometry cast a fixed
// shadow - and does NOT follow the editor camera. The only camera-dynamic part is the shader-side
// distance fade in sun_shadow() (SHADOW_BANK fadeBeginDist/fadeDist vs view_distance), which is how
// DeS makes "how near the camera shadows are drawn" dynamic without touching the cast itself. The
// 4-split camera-relative atlas + the real PSM warp are layered on this foundation later
// (docs/PLAN.md's shadow item).
//
// Compatibility has no shadow-only light and no shadow term outside light() (DeS composes
// min(shadow, lightmap) in fragment()), so this is a hand-rolled SubViewport depth pass, not
// Godot's own shadow system (spike: docs/context.md part 37). Receivers gate their env/directional
// term by min(shadowTerm, lightmap) in hemisphere_ambient.gdshaderinc.
[Tool]
public partial class ShadowRenderer : Node
{
	// 2048 = DeS's full shadow surface (it splits this into a 2x2 grid of 1024^2 cascade tiles; we
	// use it as one map, since v2 is a single static region, not the 4-split - see docs/PLAN.md).
	private const int AtlasSize = 2048;
	private const float Near = 0.05f;
	private const float RadiusCap = 200.0f; // 2048^2 has the headroom; ~0.2 m/texel worst case

	private static readonly Shader DepthShader =
		GD.Load<Shader>("res://addons/archstone/shaders/shadow_depth.gdshader");

	private SubViewport _viewport;
	private Camera3D _camera;

	// beginDist/endDist are accepted (they're SHADOW_BANK fields and the 4-split will need them as
	// cascade split ranges) but unused by the static v2 region, which sizes itself from the casters.
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

		// Static ortho region from the casters' own world bounds, capped so one pathological MSB
		// (m01_00_00_00 spans the Nexus hub AND the Old One area far below) can't blow the 2048^2
		// atlas texel size out. Geometry past the cap goes unshadowed until the 4-split
		// camera-relative atlas exists - a deliberate v2 limit, not a bug.
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
			// Content is static; ALWAYS just guarantees the texture stays populated in the editor
			// (an own_world_3d SubViewport with ONCE can come up blank until something forces a redraw).
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

	// All shadow uniforms, pushed once - the projection never changes, so there is nothing to update
	// per frame. A placement whose LightID resolved already has a per-instance override material
	// (FlverLoader's ApplyDrawParams); one that didn't shares the cached base material, so duplicate
	// it to an override first - same pattern ApplyDrawParams uses.
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

	// Product of local Transforms up the parent chain - Node3D.GlobalTransform isn't reliable for a
	// subtree that hasn't entered the SceneTree yet, which is when FlverLoader calls this.
	private static Transform3D WorldTransform(Node3D node)
	{
		var transform = node.Transform;
		for (var parent = node.GetParent() as Node3D; parent != null; parent = parent.GetParent() as Node3D)
			transform = parent.Transform * transform;
		return transform;
	}
}
