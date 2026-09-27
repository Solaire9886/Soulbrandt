using System.Collections.Generic;
using Godot;
using SoulsFormats;

namespace Archstone;

// DeS's frame post-process: eye adaptation and bloom (the DS_Fil_* chain), as a Nexus frame
// capture and the executable show it (ELF_ENGINE_ACCURACY_RESEARCH.md 13, external research
// archive). The engine renders geometry as saturate(colour * E / 2) into an RGBA8 buffer, reduces
// a 320x180 copy to a geometric-mean luminance, adapts E toward grayKey / clamp(L) with
// 1 - 0.98^(30 * adaptSpeed * dt) per frame, and blooms the same copy through a bright-pass and
// three Gaussian stages before DS_Fil_HDR_ColAdj composites everything.
//
// Here a second camera renders the scene at half the engine's resolution in that buffer encoding
// (materials switch on DES_MEASURE_LAYER), and a nest of SubViewports runs the stages - children
// render before their parents, so the whole chain completes inside one frame. The adapted E is a
// 1x1 float target every map material reads through the des_exposure_map global; the bloom is
// added over the main view by a full-screen quad. One pipeline drives the globals at a time: the
// most recently entered.
[Tool]
public partial class PostProcessPipeline : Node3D
{
	// Visual layer 21 marks the measurement camera (output_stage.gdshaderinc's DES_MEASURE_LAYER);
	// the bloom quad sits on layer 20, which that camera leaves out.
	private const uint MeasureLayer = 1u << 20;
	private const uint CompositeLayer = 1u << 19;
	private const int MeasureHeight = 360; // half the engine's 1280x720 frame

	// PostProcess.gdshader stage indices.
	private const int StageCopy = 0, StageLumInitial = 1, StageDownscale4x4 = 2, StageLumFinal = 3,
		StageAdapt = 4, StageBrightPass = 5, StageGauss5x5 = 6, StageBloom = 7;

	// The exposure standalone models (and the editor before any map loads) render with: the
	// default TONE_MAP_BANK row's grayKey 0.18 over its luminance floor 0.1, plus the 0.001 bias.
	private const float FallbackExposure = 0.18f / (0.1f + 0.001f);

	private static readonly Shader StageShader = GD.Load<Shader>("res://addons/archstone/shaders/post_process.gdshader");
	private static readonly Shader CompositeShader = GD.Load<Shader>("res://addons/archstone/shaders/bloom_composite.gdshader");
	private static ImageTexture _fallbackExposureMap;
	private static PostProcessPipeline _active;

	// The frame's TONE_MAP_BANK and TONE_CORRECT_BANK rows (neutral defaults until Configure).
	private float _grayKey = 0.18f, _minAdaptedLum = 0.1f, _maxAdaptedLum = 0.2f, _adaptSpeed = 1.0f;
	private float _bloomThreshold = 0.5f, _bloomMul = 0.5f;
	private Vector3 _brightness = Vector3.One, _contrast = Vector3.One;
	private float _saturation = 1.0f, _hueRadians = 0.0f;

	private readonly List<(SubViewport Viewport, int Width, int Height)> _scaled = new();
	private SubViewport _adapt, _bloomV, _innermost;
	private ShaderMaterial _adaptMaterial, _bloomVMaterial, _bloomHMaterial, _compositeMaterial;
	private Camera3D _camera;
	private Vector2I _mainSize;
	private bool _adapted;

	public void Configure(PARAM.Row toneMap, PARAM.Row toneCorrect)
	{
		if (toneMap != null)
		{
			_bloomThreshold = System.Convert.ToSingle(toneMap["bloomBegin"].Value) / 100f;
			_bloomMul = System.Convert.ToSingle(toneMap["bloomMul"].Value) / 100f;
			_grayKey = System.Convert.ToSingle(toneMap["grayKeyValue"].Value);
			_minAdaptedLum = System.Convert.ToSingle(toneMap["minAdaptedLum"].Value);
			_maxAdaptedLum = System.Convert.ToSingle(toneMap["maxAdapredLum"].Value);
			_adaptSpeed = System.Convert.ToSingle(toneMap["adaptSpeed"].Value);
		}
		if (toneCorrect != null)
		{
			_brightness = Row3(toneCorrect, "brightnessR", "brightnessG", "brightnessB");
			_contrast = Row3(toneCorrect, "contrastR", "contrastG", "contrastB");
			_saturation = System.Convert.ToSingle(toneCorrect["saturation"].Value);
			_hueRadians = Mathf.DegToRad(System.Convert.ToSingle(toneCorrect["hue"].Value));
		}
	}

	private static Vector3 Row3(PARAM.Row row, string x, string y, string z) => new(
		System.Convert.ToSingle(row[x].Value), System.Convert.ToSingle(row[y].Value), System.Convert.ToSingle(row[z].Value));

	// Binds the fixed fallback exposure and a neutral tone correction, unless a pipeline is active.
	public static void BindFallback()
	{
		if (_active != null)
			return;
		if (_fallbackExposureMap == null)
		{
			var image = Image.CreateEmpty(1, 1, false, Image.Format.Rf);
			image.SetPixel(0, 0, new Color(FallbackExposure, 0, 0));
			_fallbackExposureMap = ImageTexture.CreateFromImage(image);
		}
		RenderingServer.GlobalShaderParameterSet("des_exposure_map", _fallbackExposureMap);
		BindTone(Vector3.One, Vector3.One, 1.0f, 0.0f);
	}

	private static void BindTone(Vector3 brightness, Vector3 contrast, float saturation, float hueRadians)
	{
		RenderingServer.GlobalShaderParameterSet("des_tone_brightness", brightness);
		RenderingServer.GlobalShaderParameterSet("des_tone_contrast", contrast);
		RenderingServer.GlobalShaderParameterSet("des_tone_saturation", saturation);
		RenderingServer.GlobalShaderParameterSet("des_tone_hue_radians", hueRadians);
	}

	public override void _Ready()
	{
		if (_camera == null)
			Build();
	}

	public override void _EnterTree()
	{
		_active = this;
		_adapted = false;
		if (_camera != null)
			Activate();
	}

	public override void _ExitTree()
	{
		if (_active != this)
			return;
		_active = null;
		BindFallback();
	}

	private void Activate()
	{
		RenderingServer.GlobalShaderParameterSet("des_exposure_map", _adapt.GetTexture());
		BindTone(_brightness, _contrast, _saturation, _hueRadians);
	}

	// Stages in render order; each nests the one before it, so every reader renders after its source.
	private void Build()
	{
		var scene = new SubViewport { Name = "SceneBuffer", Msaa3D = Viewport.Msaa.Disabled };
		_camera = new Camera3D { Name = "MeasureCamera", CullMask = ((1u << 20) - 1) & ~CompositeLayer | MeasureLayer, Current = true };
		scene.AddChild(_camera);
		Track(scene, 1, 1);
		_innermost = scene;

		// DS_Fil_Dof_DownSample as a copy: one bilinear tap per 320x180 texel.
		var sample = Stage("SceneSample", StageCopy, scene, hdr: false, 2, 2);
		var lum64 = Stage("LumInitial", StageLumInitial, sample, hdr: true, 0, 0, 64, 64);
		var lum16 = Stage("LumDownscale16", StageDownscale4x4, lum64, hdr: true, 0, 0, 16, 16);
		var lum4 = Stage("LumDownscale4", StageDownscale4x4, lum16, hdr: true, 0, 0, 4, 4);
		var lumFinal = Stage("LumFinal", StageLumFinal, lum4, hdr: true, 0, 0, 1, 1);
		_adapt = Stage("AdaptedExposure", StageAdapt, lumFinal, hdr: true, 0, 0, 1, 1);
		_adapt.RenderTargetClearMode = SubViewport.ClearMode.Once;
		_adaptMaterial = StageMaterial(_adapt);
		_adaptMaterial.SetShaderParameter("gray_key", _grayKey);
		_adaptMaterial.SetShaderParameter("min_adapted_lum", _minAdaptedLum);
		_adaptMaterial.SetShaderParameter("max_adapted_lum", _maxAdaptedLum);
		var bright = Stage("BrightPass", StageBrightPass, sample, hdr: false, 2, 2);
		StageMaterial(bright).SetShaderParameter("bloom_threshold", _bloomThreshold);
		var gauss320 = Stage("Gauss320", StageGauss5x5, bright, hdr: false, 2, 2);
		var down160 = Stage("Downsample160", StageCopy, gauss320, hdr: false, 4, 4);
		var gauss160 = Stage("Gauss160", StageGauss5x5, down160, hdr: false, 4, 4);
		_bloomV = Stage("BloomVertical", StageBloom, gauss160, hdr: false, 4, 4);
		_bloomVMaterial = StageMaterial(_bloomV);
		var bloomH = Stage("BloomHorizontal", StageBloom, _bloomV, hdr: false, 4, 4);
		_bloomHMaterial = StageMaterial(bloomH);
		AddChild(bloomH);

		_compositeMaterial = new ShaderMaterial { Shader = CompositeShader, RenderPriority = (int)Material.RenderPriorityMax };
		_compositeMaterial.SetShaderParameter("bloom", bloomH.GetTexture());
		_compositeMaterial.SetShaderParameter("bloom_mul", _bloomMul);
		AddChild(new MeshInstance3D
		{
			Name = "BloomComposite",
			Mesh = new QuadMesh { Size = new Vector2(2, 2) },
			MaterialOverride = _compositeMaterial,
			Layers = CompositeLayer,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			// Never frustum-culled: the vertex shader places it over the whole view.
			CustomAabb = new Aabb(new Vector3(-1e6f, -1e6f, -1e6f), new Vector3(2e6f, 2e6f, 2e6f)),
		});

		if (_active == this)
			Activate();
	}

	// A stage reading `source`, sized as the scene buffer divided by divideX/divideY, or fixed at
	// width x height when divideX is 0.
	private SubViewport Stage(string name, int stage, SubViewport source, bool hdr, int divideX, int divideY,
		int width = 1, int height = 1)
	{
		var viewport = new SubViewport { Name = name, UseHdr2D = hdr, Disable3D = true, Size = new Vector2I(width, height) };
		var material = new ShaderMaterial { Shader = StageShader };
		material.SetShaderParameter("stage", stage);
		material.SetShaderParameter("source_nearest", source.GetTexture());
		material.SetShaderParameter("source_bilinear", source.GetTexture());
		var rect = new ColorRect { Material = material };
		rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		viewport.AddChild(rect);
		viewport.AddChild(_innermost);
		_innermost = viewport;
		Track(viewport, divideX, divideY);
		return viewport;
	}

	private static ShaderMaterial StageMaterial(SubViewport viewport) =>
		(ShaderMaterial)viewport.GetChild<ColorRect>(0).Material;

	private void Track(SubViewport viewport, int divideX, int divideY)
	{
		viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
		if (divideX > 0)
			_scaled.Add((viewport, divideX, divideY));
	}

	public override void _Process(double delta)
	{
		if (_active != this || _camera == null)
			return;
		Camera3D main = MainCamera(out Vector2I mainSize);
		if (main == null || mainSize.X <= 0 || mainSize.Y <= 0)
			return;
		if (mainSize != _mainSize)
			Resize(mainSize);

		_camera.GlobalTransform = main.GlobalTransform;
		_camera.Projection = main.Projection;
		_camera.KeepAspect = main.KeepAspect;
		_camera.Fov = main.Fov;
		_camera.Size = main.Size;
		_camera.Near = main.Near;
		_camera.Far = main.Far;
		_camera.HOffset = main.HOffset;
		_camera.VOffset = main.VOffset;

		// The first frame after (re)entering starts at the target: rate 1.
		_adaptMaterial.SetShaderParameter("adapt_amount", _adapted ? _adaptSpeed * (float)delta : 1e4f);
		_adapted = true;
	}

	private Camera3D MainCamera(out Vector2I size)
	{
#if TOOLS
		if (Engine.IsEditorHint())
		{
			var editorViewport = EditorInterface.Singleton.GetEditorViewport3D(0);
			size = editorViewport.Size;
			return editorViewport.GetCamera3D();
		}
#endif
		size = (Vector2I)GetViewport().GetVisibleRect().Size;
		return GetViewport().GetCamera3D();
	}

	// The measurement keeps the main view's aspect (so the bloom lines up over it) at a fixed
	// 360-line height, the engine's 1280x720 frame halved.
	private void Resize(Vector2I mainSize)
	{
		_mainSize = mainSize;
		int width = Mathf.Max(4, Mathf.RoundToInt(MeasureHeight * (float)mainSize.X / mainSize.Y));
		foreach (var (viewport, divideX, divideY) in _scaled)
			viewport.Size = new Vector2I(Mathf.Max(1, width / divideX), Mathf.Max(1, MeasureHeight / divideY));
		_bloomVMaterial.SetShaderParameter("bloom_step", new Vector2(0, 1.0f / _bloomV.Size.Y));
		_bloomHMaterial.SetShaderParameter("bloom_step", new Vector2(1.0f / _bloomV.Size.X, 0));
	}
}
