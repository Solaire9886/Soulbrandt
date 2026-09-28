using System.Collections.Generic;
using System.Linq;
using Godot;
using SoulsFormats;

namespace Archstone;

// A model's Havok animations as one AnimationLibrary whose tracks drive its Skeleton3D (see
// docs/ARCHITECTURE.md, "Skeletons and animation"). Characters keep loose files under
// <model dir>/hkx (the player's in per-category subfolders); objects keep an archive,
// obj/<id>/hkx/<id>.anibnd. Both hold skeleton.hkx and one clip per a*.hkx.
public static class ModelAnimation
{
	private readonly record struct Entry(string Name, System.Func<byte[]> Read);

	// The node path of the extracted-motion tracks. Played as is, they move the skeleton, and the
	// skinned mesh with it, within the model; a controller that moves the model instead sets
	// AnimationMixer.RootMotionTrack to this and applies GetRootMotionPosition/Rotation.
	public static readonly NodePath RootMotionTrack = "Skeleton";

	public static bool HasSource(string flverPath) => FindSource(flverPath) != null;

	// Null when the model has no Havok skeleton or no readable clip.
	public static AnimationLibrary Build(string flverPath, FlverModelBuilder.FlverSkeleton skeleton)
	{
		string source = FindSource(flverPath);
		if (source == null)
			return null;
		List<Entry> entries;
		try
		{
			entries = ListEntries(source);
		}
		catch (System.Exception e)
		{
			GD.PushWarning($"ModelAnimation: cannot read '{source}': {e.Message}");
			return null;
		}

		var skeletonEntry = entries.FirstOrDefault(e => e.Name.Equals("skeleton", System.StringComparison.OrdinalIgnoreCase));
		if (skeletonEntry.Read == null)
			return null;
		HKX.Skeleton havokSkeleton;
		try
		{
			var skeletons = HKX.Read(skeletonEntry.Read()).ReadSkeletons();
			if (skeletons.Count == 0)
				return null;
			havokSkeleton = skeletons[0];
		}
		catch (System.Exception e)
		{
			GD.PushWarning($"ModelAnimation: cannot read the skeleton in '{source}': {e.Message}");
			return null;
		}
		string[] bonePaths = BindBones(havokSkeleton, skeleton);

		var library = new AnimationLibrary();
		var skipped = new List<string>();
		foreach (var entry in entries.Where(e => e.Name.StartsWith('a')).OrderBy(e => e.Name, System.StringComparer.Ordinal))
		{
			try
			{
				var hkx = HKX.Read(entry.Read());
				if (!hkx.ContentsVersion.StartsWith("Havok-5."))
				{
					skipped.Add(hkx.ContentsVersion);
					continue;
				}
				var clips = hkx.ReadAnimations(skipped);
				if (clips.Count > 0)
					library.AddAnimation(entry.Name, BuildAnimation(clips[0], havokSkeleton, bonePaths));
			}
			catch (System.Exception e)
			{
				GD.PushWarning($"ModelAnimation: cannot read '{entry.Name}' in '{source}': {e.Message}");
			}
		}
		if (skipped.Count > 0)
			GD.PushWarning($"ModelAnimation: '{source}' skips unsupported animations: "
				+ string.Join(", ", skipped.GroupBy(s => s).Select(g => $"{g.Key} x{g.Count()}")));
		return library.GetAnimationList().Count > 0 ? library : null;
	}

	// The clip InitAnimID names ("a00_" and four digits), or null.
	public static string InitialClip(AnimationLibrary library, int initAnimId) =>
		initAnimId >= 0 && library.HasAnimation($"a00_{initAnimId:D4}") ? $"a00_{initAnimId:D4}" : null;

	// Sets each bound bone to the clip's first frame. Placed objects rest in their initial clip's
	// first pose: 1,373 of the 1,561 resolvable initial clips are constant, the rest ambient
	// loops ending within 0.01 of their start.
	public static void ApplyFirstFrame(Skeleton3D skeleton, Animation animation)
	{
		for (int track = 0; track < animation.GetTrackCount(); track++)
		{
			if (animation.TrackGetKeyCount(track) == 0)
				continue;
			int bone = skeleton.FindBone(animation.TrackGetPath(track).GetConcatenatedSubNames());
			if (bone < 0)
				continue;
			switch (animation.TrackGetType(track))
			{
				case Animation.TrackType.Position3D:
					skeleton.SetBonePosePosition(bone, (Vector3)animation.TrackGetKeyValue(track, 0));
					break;
				case Animation.TrackType.Rotation3D:
					skeleton.SetBonePoseRotation(bone, (Quaternion)animation.TrackGetKeyValue(track, 0));
					break;
				case Animation.TrackType.Scale3D:
					skeleton.SetBonePoseScale(bone, (Vector3)animation.TrackGetKeyValue(track, 0));
					break;
			}
		}
	}

	// Whether every track holds its first key throughout.
	public static bool IsConstant(Animation animation)
	{
		for (int track = 0; track < animation.GetTrackCount(); track++)
		{
			var first = animation.TrackGetKeyValue(track, 0);
			for (int key = 1; key < animation.TrackGetKeyCount(track); key++)
			{
				var value = animation.TrackGetKeyValue(track, key);
				bool equal = value.VariantType == Variant.Type.Quaternion
					? ((Quaternion)value).IsEqualApprox((Quaternion)first)
					: ((Vector3)value).IsEqualApprox((Vector3)first);
				if (!equal)
					return false;
			}
		}
		return true;
	}

	// chr/<id>/<id>.flver -> chr/<id>/hkx; obj/<id>/sib/<id>.flver -> obj/<id>/hkx/<id>.anibnd.
	private static string FindSource(string flverPath)
	{
		string dir = System.IO.Path.GetDirectoryName(ProjectSettings.GlobalizePath(flverPath));
		string model = System.IO.Path.GetFileNameWithoutExtension(flverPath);
		string archive = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dir), "hkx", model + ".anibnd");
		if (System.IO.Path.GetFileName(dir) == "sib" && System.IO.File.Exists(archive))
			return archive;
		string folder = System.IO.Path.Combine(dir, "hkx");
		return System.IO.Directory.Exists(folder) ? folder : null;
	}

	private static List<Entry> ListEntries(string source)
	{
		if (System.IO.File.Exists(source))
			return BND3.Read(source).Files
				.Where(f => f.Name.EndsWith(".hkx", System.StringComparison.OrdinalIgnoreCase))
				.Select(f => new Entry(System.IO.Path.GetFileNameWithoutExtension(f.Name.Replace('\\', '/')), () => f.Bytes))
				.ToList();
		return System.IO.Directory.GetFiles(source, "*", System.IO.SearchOption.AllDirectories)
			.Where(f => System.IO.Path.GetExtension(f).Equals(".hkx", System.StringComparison.OrdinalIgnoreCase))
			.Select(f => new Entry(System.IO.Path.GetFileNameWithoutExtension(f), () => System.IO.File.ReadAllBytes(f)))
			.ToList();
	}

	// Havok bone -> Skeleton3D track path, or null for a bone the FLVER lacks. Names bind; when
	// the FLVER repeats a name (c6041's stub and real root), the node whose rest position is
	// nearest the Havok reference pose wins.
	private static string[] BindBones(HKX.Skeleton havok, FlverModelBuilder.FlverSkeleton flver)
	{
		var flverGlobals = new Transform3D[flver.Rests.Length];
		for (int i = 0; i < flver.Rests.Length; i++)
			flverGlobals[i] = flver.Parents[i] >= 0 ? flverGlobals[flver.Parents[i]] * flver.Rests[i] : flver.Rests[i];
		var havokGlobals = new Transform3D[havok.Bones.Count];
		var paths = new string[havok.Bones.Count];
		for (int i = 0; i < havok.Bones.Count; i++)
		{
			var bone = havok.Bones[i];
			var local = new Transform3D(new Basis(ToGodot(bone.Rotation)) * Basis.FromScale(ToGodotScale(bone.Scale)), ToGodot(bone.Translation));
			havokGlobals[i] = bone.ParentIndex >= 0 ? havokGlobals[bone.ParentIndex] * local : local;
			var candidates = Enumerable.Range(0, flver.SourceNames.Length).Where(n => flver.SourceNames[n] == bone.Name).ToList();
			if (candidates.Count == 0)
				continue;
			int best = candidates.OrderBy(n => flverGlobals[n].Origin.DistanceSquaredTo(havokGlobals[i].Origin)).First();
			paths[i] = $"Skeleton:{flver.Names[best]}";
		}
		return paths;
	}

	// Keys at the decoded frames. A bound bone without a track holds its Havok reference pose,
	// as Havok samples it; a FLVER node the Havok skeleton lacks stays at its rest. Extracted root
	// motion keys the Skeleton node itself (RootMotionTrack), relative to the clip's start.
	private static Animation BuildAnimation(HKX.Animation clip, HKX.Skeleton havok, string[] bonePaths)
	{
		var animation = new Animation { Length = clip.Duration, LoopMode = Animation.LoopModeEnum.None };
		int frameCount = clip.Frames.Count;
		double step = frameCount > 1 ? clip.Duration / (frameCount - 1) : 0;
		var trackOfBone = new Dictionary<int, int>();
		for (int t = 0; t < clip.TrackToBone.Count; t++)
			trackOfBone.TryAdd(clip.TrackToBone[t], t);

		if (clip.ExtractedMotion is { Count: > 0 } motion)
		{
			int position = animation.AddTrack(Animation.TrackType.Position3D);
			int rotation = animation.AddTrack(Animation.TrackType.Rotation3D);
			animation.TrackSetPath(position, RootMotionTrack);
			animation.TrackSetPath(rotation, RootMotionTrack);
			double motionStep = motion.Count > 1 ? clip.Duration / (motion.Count - 1) : 0;
			for (int i = 0; i < motion.Count; i++)
			{
				// W is the yaw about the up axis (+Y); the mirror negates it.
				animation.PositionTrackInsertKey(position, i * motionStep, new Vector3(-motion[i].X, motion[i].Y, motion[i].Z));
				animation.RotationTrackInsertKey(rotation, i * motionStep, new Quaternion(Vector3.Up, -motion[i].W));
			}
		}

		for (int b = 0; b < havok.Bones.Count; b++)
		{
			if (bonePaths[b] == null)
				continue;
			var bone = havok.Bones[b];
			var poses = trackOfBone.TryGetValue(b, out int t)
				? clip.Frames.Select(frame => frame[t]).ToList()
				: new List<HKX.QsTransform> { new() { Translation = bone.Translation, Rotation = bone.Rotation, Scale = bone.Scale } };

			int position = animation.AddTrack(Animation.TrackType.Position3D);
			int rotation = animation.AddTrack(Animation.TrackType.Rotation3D);
			int scale = animation.AddTrack(Animation.TrackType.Scale3D);
			foreach (int track in new[] { position, rotation, scale })
				animation.TrackSetPath(track, bonePaths[b]);
			for (int f = 0; f < poses.Count; f++)
			{
				animation.PositionTrackInsertKey(position, f * step, ToGodot(poses[f].Translation));
				animation.RotationTrackInsertKey(rotation, f * step, ToGodot(poses[f].Rotation));
				animation.ScaleTrackInsertKey(scale, f * step, ToGodotScale(poses[f].Scale));
			}
		}
		return animation;
	}

	// The FLVER mirror (X negated) applied to a local transform, S * M * S: translation X and the
	// rotation's Y and Z negate; scale is unchanged.
	private static Vector3 ToGodot(System.Numerics.Vector3 v) => new(-v.X, v.Y, v.Z);

	private static Vector3 ToGodotScale(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

	private static Quaternion ToGodot(System.Numerics.Quaternion q) => new Quaternion(q.X, -q.Y, -q.Z, q.W).Normalized();
}
