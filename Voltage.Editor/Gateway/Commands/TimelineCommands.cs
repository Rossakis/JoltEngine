using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Voltage.Cinematics;
using Voltage.Editor.Assets;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Undo.Core;
using Voltage.Gateway;
using Voltage.Serialization;
using Voltage.Sprites;
using static Voltage.Editor.Gateway.GatewayValues;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Timeline assets (tracks, clips, keys, markers), directors, and sprite animation playback. Edits write the .vtimeline straight away.</summary>
internal static class TimelineCommands
{
	private static readonly string[] ClipKinds = { "event", "spawn", "marker", "track" };

	public static void Register(GatewayCommandTable table)
	{
		table.Add("timeline.list", "Timeline assets of the project.", (_, _) =>
			RequireAssets().Items
				.Where(i => i.Extension.Equals(TimelineAssetIO.FileExtension, StringComparison.OrdinalIgnoreCase))
				.DistinctBy(i => i.AbsolutePath, StringComparer.OrdinalIgnoreCase)
				.Select(i => new { name = Path.GetFileNameWithoutExtension(i.FileName), path = i.AbsolutePath, guid = AssetDatabase.Instance.GetReference(i.AbsolutePath).Guid })
				.ToList()).ReadOnly();

		table.Add("timeline.create", "Write a new .vtimeline in the project.", (args, _) =>
		{
			var project = ProjectManager.Instance.CurrentProject ?? throw new GatewayException("no project loaded");
			var name = args.Require("name");
			if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
				throw new GatewayException($"invalid timeline name '{name}'");
			var folder = args.Has("folder")
				? GatewayPaths.RequireInside(Path.Combine(project.ProjectPath, args.Require("folder")), project.ProjectPath, "folder")
				: Path.Combine(project.DataFolder, "Timelines");
			Directory.CreateDirectory(folder);
			var path = Path.Combine(folder, name + TimelineAssetIO.FileExtension);
			if (File.Exists(path))
				throw new GatewayException($"timeline already exists: {path}");

			var asset = TimelineAssetIO.CreateDefault();
			asset.Duration = args.Float("duration", asset.Duration);
			TimelineAssetIO.Save(asset, path);
			AssetDatabase.Instance?.Refresh();
			return Summary(asset, path);
		}, P.Str("name", "File name without extension", required: true), P.Str("folder", "Project-relative folder; default Data/Timelines"), P.Float("duration", "Length in seconds", 5f));

		table.Add("timeline.info", "Roles, tracks, clips, markers and duration of a timeline asset.", (args, _) =>
		{
			var path = ResolveTimeline(args.Require("asset"));
			return Summary(TimelineAssetIO.Load(path), path);
		}, P.Str("asset", "Timeline GUID, path or file name", required: true)).ReadOnly();

		table.Add("timeline.set", "Set top-level fields of a timeline asset (Duration) or add and remove roles.", (args, _) =>
			Edit(args, asset =>
			{
				if (args.Has("duration"))
					asset.Duration = Math.Max(0.1f, args.Float("duration"));
				if (args.Has("addRole"))
				{
					var role = args.Require("addRole");
					if (asset.Roles.All(r => r.Name != role))
						asset.Roles.Add(new TimelineRole { Name = role, ExpectedComponentId = args.String("componentId") });
				}
				if (args.Has("removeRole"))
					asset.Roles.RemoveAll(r => r.Name == args.Require("removeRole"));
			}), P.Str("asset", "Timeline GUID, path or file name", required: true), P.Float("duration", "Length in seconds"), P.Str("addRole", "Role name to add"), P.Str("componentId", "Expected component id for the added role"), P.Str("removeRole", "Role name to remove"));

		table.Add("timeline.track.types", "Track type ids that timeline.track.add accepts.", (_, _) =>
			TimelineTrackRegistry.RegisteredIds.OrderBy(id => id, StringComparer.Ordinal).Select(id => new
			{
				id,
				type = TimelineTrackRegistry.TypeFor(id)?.Name,
				lists = ListFields(TimelineTrackRegistry.TypeFor(id)).Select(f => new { name = f.Name, item = ItemType(f).Name }).ToList()
			}).ToList()).ReadOnly();

		table.Add("timeline.track.add", "Add a parameter track by type id (see timeline.track.types) targeting a role; 'values' sets its public fields.", (args, _) =>
		{
			var typeId = args.Require("type");
			var type = TimelineTrackRegistry.TypeFor(typeId) ?? throw new GatewayException($"unknown track type '{typeId}'; see timeline.track.types");
			var index = -1;
			var result = Edit(args, asset =>
			{
				var track = (TimelineParameterTrack)Activator.CreateInstance(type);
				track.TargetRole = args.String("role", asset.Roles.FirstOrDefault()?.Name);
				ApplyValues(track, args);
				asset.ParameterTracks.Add(track);
				index = asset.ParameterTracks.Count - 1;
			});
			return new { index, timeline = result };
		}, P.Str("asset", "Timeline GUID, path or file name", required: true), P.Str("type", "Track type id", required: true), P.Str("role", "Target role; default the first role"), P.Obj("values", "Public fields to set on the track, by name or dotted path"));

		table.Add("timeline.track.set", "Set public fields of a track by index.", (args, _) =>
			Edit(args, asset => ApplyValues(Track(asset, args.Int("track", -1)), args)),
			P.Str("asset", "Timeline GUID, path or file name", required: true), P.Int("track", "Track index from timeline.info", required: true), P.Obj("values", "Fields to set", required: true));

		table.Add("timeline.track.remove", "Remove a track by index.", (args, _) =>
			Edit(args, asset => asset.ParameterTracks.RemoveAt(Index(args.Int("track", -1), asset.ParameterTracks.Count, "track"))),
			P.Str("asset", "Timeline GUID, path or file name", required: true), P.Int("track", "Track index", required: true)).Destructive();

		table.Add("timeline.clip.add", "Add an event, spawn clip, marker, or a clip on a track's list (kind=track with 'track' and optional 'list'); 'values' fills its fields.", (args, _) =>
		{
			var kind = args.String("kind", "event").ToLowerInvariant();
			if (!ClipKinds.Contains(kind))
				throw new GatewayException($"unknown kind '{kind}'; use {string.Join('|', ClipKinds)}");
			var index = -1;
			var result = Edit(args, asset =>
			{
				var list = ClipList(asset, args, kind, out var itemType);
				var clip = Activator.CreateInstance(itemType);
				SetIfPresent(clip, "Time", args, "time");
				SetIfPresent(clip, "Duration", args, "duration");
				SetIfPresent(clip, "Name", args, "name");
				ApplyValues(clip, args);
				list.Add(clip);
				index = list.Count - 1;
				asset.InvalidateEventOrder();
			});
			return new { index, timeline = result };
		}, P.Str("asset", "Timeline GUID, path or file name", required: true), P.Enum("kind", "What to add", ClipKinds, "event"), P.Int("track", "Track index for kind=track"), P.Str("list", "List field on the track for kind=track; default its only list"), P.Float("time", "Start time in seconds"), P.Float("duration", "Length in seconds"), P.Str("name", "Clip or marker name"), P.Obj("values", "Other fields, e.g. {\"BeginMethod\":\"Flash\",\"TargetRole\":\"Hero\"}"));

		table.Add("timeline.clip.set", "Set fields of an existing event, spawn clip, marker or track clip by index.", (args, _) =>
			Edit(args, asset =>
			{
				var kind = args.String("kind", "event").ToLowerInvariant();
				var list = ClipList(asset, args, kind, out Type _);
				var clip = list[Index(args.Int("index", -1), list.Count, "index")];
				SetIfPresent(clip, "Time", args, "time");
				SetIfPresent(clip, "Duration", args, "duration");
				SetIfPresent(clip, "Name", args, "name");
				ApplyValues(clip, args);
				asset.InvalidateEventOrder();
			}), P.Str("asset", "Timeline GUID, path or file name", required: true), P.Enum("kind", "Which list", ClipKinds, "event"), P.Int("index", "Position in that list", required: true), P.Int("track", "Track index for kind=track"), P.Str("list", "List field on the track for kind=track"), P.Float("time"), P.Float("duration"), P.Str("name"), P.Obj("values", "Other fields"));

		table.Add("timeline.clip.remove", "Remove an event, spawn clip, marker or track clip by index.", (args, _) =>
			Edit(args, asset =>
			{
				var kind = args.String("kind", "event").ToLowerInvariant();
				var list = ClipList(asset, args, kind, out Type _);
				list.RemoveAt(Index(args.Int("index", -1), list.Count, "index"));
				asset.InvalidateEventOrder();
			}), P.Str("asset", "Timeline GUID, path or file name", required: true), P.Enum("kind", "Which list", ClipKinds, "event"), P.Int("index", "Position in that list", required: true), P.Int("track", "Track index for kind=track"), P.Str("list", "List field on the track for kind=track")).Destructive();

		table.Add("timeline.key.add", "Add or replace a keyframe on a track channel (Position, Rotation, Scale, Zoom, Alpha, Tint, FloatKeys, Vector2Keys, ColorKeys); keys stay sorted by time.", (args, _) =>
			Edit(args, asset =>
			{
				var track = Track(asset, args.Int("track", -1));
				var channel = KeyList(track, args.Require("channel"), out var keyType);
				var time = args.Float("time");
				var key = Activator.CreateInstance(keyType);
				Set(key, "Time", JsonSerializer.SerializeToElement(time), false, null);
				Set(key, "Value", args.RequireProperty("value"), false, null);
				if (args.Has("ease"))
					Set(key, "Ease", args.RequireProperty("ease"), false, null);

				for (var i = channel.Count - 1; i >= 0; i--)
					if (Math.Abs(KeyTime(channel[i]) - time) < 0.0005f)
						channel.RemoveAt(i);
				var insert = 0;
				while (insert < channel.Count && KeyTime(channel[insert]) < time)
					insert++;
				channel.Insert(insert, key);
			}), P.Str("asset", "Timeline GUID, path or file name", required: true), P.Int("track", "Track index", required: true), P.Str("channel", "Keyframe list field on the track", required: true), P.Float("time", "Seconds", required: true), P.Any("value", "float, {x,y} or {r,g,b,a} to match the channel", required: true), P.Str("ease", "EaseType name; default Linear"));

		table.Add("timeline.key.remove", "Remove the keyframe at a time on a track channel.", (args, _) =>
			Edit(args, asset =>
			{
				var channel = KeyList(Track(asset, args.Int("track", -1)), args.Require("channel"), out Type _);
				var time = args.Float("time");
				var removed = 0;
				for (var i = channel.Count - 1; i >= 0; i--)
					if (Math.Abs(KeyTime(channel[i]) - time) < 0.0005f)
					{
						channel.RemoveAt(i);
						removed++;
					}
				if (removed == 0)
					throw new GatewayException($"no key at t={time}");
			}), P.Str("asset", "Timeline GUID, path or file name", required: true), P.Int("track", required: true), P.Str("channel", required: true), P.Float("time", required: true)).Destructive();

		table.Add("timeline.director", "Drive a TimelineDirector on an entity: play, pause, resume, stop, cancel, skip, seek (time or marker) or evaluate a time in edit mode.", (args, _) =>
		{
			var entity = ResolveEntity(args.Require("entity"));
			var director = entity.GetComponent<TimelineDirector>() ?? throw new GatewayException($"{entity.Name} has no TimelineDirector");
			var action = args.String("action", "play").ToLowerInvariant();
			switch (action)
			{
				case "play": director.Play(); break;
				case "pause": director.Pause(); break;
				case "resume": director.Resume(); break;
				case "stop": director.Stop(); break;
				case "cancel": director.Cancel(); break;
				case "skip": director.Skip(); break;
				case "seek":
					if (args.Has("marker"))
					{
						if (!director.SeekToMarker(args.Require("marker")))
							throw new GatewayException($"marker '{args.Require("marker")}' not found");
					}
					else
						director.Seek(args.Float("time"));
					break;
				case "evaluate": director.Evaluate(args.Float("time")); break;
				default: throw new GatewayException($"unknown action '{action}'");
			}
			return DirectorState(director);
		}, P.Str("entity", "Entity with a TimelineDirector", required: true), P.Enum("action", "What to do", new[] { "play", "pause", "resume", "stop", "cancel", "skip", "seek", "evaluate" }, "play"), P.Float("time", "Seconds for seek and evaluate"), P.Str("marker", "Marker name for seek"));

		table.Add("timeline.director.state", "Playhead, state and asset of a TimelineDirector.", (args, _) =>
		{
			var entity = ResolveEntity(args.Require("entity"));
			var director = entity.GetComponent<TimelineDirector>() ?? throw new GatewayException($"{entity.Name} has no TimelineDirector");
			return DirectorState(director);
		}, P.Str("entity", required: true)).ReadOnly();

		table.Add("animation.list", "Animations loaded on an entity's SpriteAnimator and what is playing.", (args, _) =>
		{
			var animator = Animator(args);
			return new
			{
				entity = animator.Entity?.Id,
				current = animator.CurrentAnimationName,
				state = animator.AnimationState.ToString(),
				loop = animator.CurrentLoopMode.ToString(),
				frame = animator.CurrentFrame,
				frameCount = animator.FrameCount,
				speed = animator.Speed,
				source = animator.TextureFilePath,
				animations = animator.Animations.Select(a => new { name = a.Key, frames = a.Value.Sprites?.Length ?? 0, fps = a.Value.FrameRates != null && a.Value.FrameRates.Length > 0 ? a.Value.FrameRates[0] : 0f }).ToList()
			};
		}, P.Str("entity", "Entity with a SpriteAnimator", required: true)).ReadOnly();

		table.Add("animation.play", "Play a named animation on an entity's SpriteAnimator.", (args, _) =>
		{
			var animator = Animator(args);
			var name = args.Require("name");
			var loop = SpriteAnimator.LoopMode.Loop;
			var loopText = args.String("loop");
			if (loopText != null)
			{
				if (bool.TryParse(loopText, out var loopFlag))
					loop = loopFlag ? SpriteAnimator.LoopMode.Loop : SpriteAnimator.LoopMode.Once;
				else if (!Enum.TryParse(loopText, true, out loop))
					throw new GatewayException($"unknown loop mode '{loopText}'; use true, false or one of {string.Join(", ", Enum.GetNames(typeof(SpriteAnimator.LoopMode)))}");
			}
			if (!animator.Animations.ContainsKey(name))
				throw new GatewayException($"no animation '{name}'; see animation.list");
			animator.Play(name, loop, args.Int("frame"));
			if (args.Has("speed"))
				animator.Speed = args.Float("speed");
			return new { playing = animator.CurrentAnimationName, state = animator.AnimationState.ToString(), loop = animator.CurrentLoopMode.ToString() };
		}, P.Str("entity", required: true), P.Str("name", "Animation name", required: true), P.Str("loop", "Loop mode name, or true (Loop) / false (Once)", "Loop"), P.Int("frame", "Start frame", 0), P.Float("speed", "Playback speed multiplier"));

		table.Add("animation.stop", "Stop, pause or resume the SpriteAnimator on an entity.", (args, _) =>
		{
			var animator = Animator(args);
			switch (args.String("action", "stop").ToLowerInvariant())
			{
				case "pause": animator.Pause(); break;
				case "resume": animator.UnPause(); break;
				default: animator.Stop(); break;
			}
			return new { current = animator.CurrentAnimationName, state = animator.AnimationState.ToString() };
		}, P.Str("entity", required: true), P.Enum("action", "stop, pause or resume", new[] { "stop", "pause", "resume" }, "stop"));

		table.Add("timeline.events", "[TimelineEvent] methods the compiled scripts registered, with their parameters where the component type is known.", (_, _) =>
			TimelineDispatch.RegisteredMethods().OrderBy(m => m.ComponentId, StringComparer.Ordinal).ThenBy(m => m.Method, StringComparer.Ordinal).Select(m => DescribeEvent(m.ComponentId, m.Method)).ToList()).ReadOnly();

		table.Add("timeline.properties", "[TimelineProperty] members a property track can animate.", (_, _) =>
			TimelinePropertyRegistry.Registered().OrderBy(p => p.ComponentId, StringComparer.Ordinal).ThenBy(p => p.Property, StringComparer.Ordinal)
				.Select(p => new { componentId = p.ComponentId, property = p.Property, kind = p.Kind.ToString() }).ToList()).ReadOnly();

		table.Add("timeline.eases", "EaseType names timeline.key.add accepts.", (_, _) => Enum.GetNames(typeof(Voltage.Utils.Tweens.Easing.EaseType)).ToList()).ReadOnly();

		table.Add("timeline.marker.list", "Markers of a timeline asset in time order.", (args, _) =>
		{
			var path = ResolveTimeline(args.Require("asset"));
			var asset = TimelineAssetIO.Load(path) ?? throw new GatewayException($"could not read {path}");
			return asset.Markers.Where(m => m != null).OrderBy(m => m.Time).Select(m => new { m.Name, m.Time }).ToList();
		}, P.Str("asset", "Timeline GUID, path or file name", required: true)).ReadOnly();

		table.Add("timeline.validate", "Problems a director would report at play; with entity, its bindings count too.", (args, _) =>
		{
			if (args.Has("entity"))
			{
				var director = Director(args);
				return new { entity = director.Entity?.Name, problems = director.Validate() };
			}
			var path = ResolveTimeline(args.Require("asset"));
			var asset = TimelineAssetIO.Load(path) ?? throw new GatewayException($"could not read {path}");
			return new { path, problems = ValidateAsset(asset) };
		}, P.Str("asset", "Timeline GUID, path or file name"), P.Str("entity", "Entity with a TimelineDirector; validates its asset with its bindings")).ReadOnly();

		table.Add("timeline.bind", "Bind a role on an entity's TimelineDirector to a target entity (undoable).", (args, _) =>
		{
			var director = Director(args);
			var role = args.Require("role");
			var target = args.Require("target");
			var index = director.Bindings.FindIndex(b => b?.Role == role);
			var description = $"Bind {role} on {director.Entity?.Name}";
			if (index < 0)
				Set(director, "Bindings[+]", JsonSerializer.SerializeToElement(new { Role = role, Entity = target }), true, description);
			else
				Set(director, $"Bindings[{index}].Entity", JsonSerializer.SerializeToElement(target), true, description);
			return DirectorState(director);
		}, P.Str("entity", "Entity with a TimelineDirector", required: true), P.Str("role", "Role name on the asset", required: true), P.Str("target", "Entity id, GUID or name to bind", required: true));

		table.Add("timeline.unbind", "Remove a role binding from an entity's TimelineDirector (undoable).", (args, _) =>
		{
			var director = Director(args);
			var role = args.Require("role");
			var index = director.Bindings.FindIndex(b => b?.Role == role);
			if (index < 0)
				throw new GatewayException($"no binding for role '{role}'");
			Set(director, $"Bindings[{index}]", default, true, $"Unbind {role} on {director.Entity?.Name}", remove: true);
			return DirectorState(director);
		}, P.Str("entity", "Entity with a TimelineDirector", required: true), P.Str("role", "Role name", required: true)).Destructive();
	}

	private static TimelineDirector Director(GatewayArgs args)
	{
		var entity = ResolveEntity(args.Require("entity"));
		return entity.GetComponent<TimelineDirector>() ?? throw new GatewayException($"{entity.Name} has no TimelineDirector");
	}


	private static SpriteAnimator Animator(GatewayArgs args)
	{
		var entity = ResolveEntity(args.Require("entity"));
		return entity.GetComponent<SpriteAnimator>() ?? throw new GatewayException($"{entity.Name} has no SpriteAnimator");
	}

	/// <summary>Accepts a project asset key or an absolute .vtimeline path inside the project.</summary>
	private static string ResolveTimeline(string key)
	{
		if (Path.IsPathRooted(key) && File.Exists(key) && key.EndsWith(TimelineAssetIO.FileExtension, StringComparison.OrdinalIgnoreCase))
		{
			var project = ProjectManager.Instance.CurrentProject ?? throw new GatewayException("no project loaded");
			return GatewayPaths.RequireInside(key, project.ProjectPath, "asset");
		}
		var item = ResolveAsset(key);
		if (!item.Extension.Equals(TimelineAssetIO.FileExtension, StringComparison.OrdinalIgnoreCase))
			throw new GatewayException($"{item.FileName} is not a {TimelineAssetIO.FileExtension} asset");
		return item.AbsolutePath;
	}

	/// <summary>Load, mutate, save; the file edit lands in the undo history and every director or window showing the file rereads it.</summary>
	private static object Edit(GatewayArgs args, Action<TimelineAsset> mutate)
	{
		var path = ResolveTimeline(args.Require("asset"));
		var before = File.ReadAllText(path);
		var asset = TimelineAssetIO.Load(path) ?? throw new GatewayException($"could not read {path}");
		mutate(asset);
		TimelineAssetIO.Save(asset, path);
		var after = File.ReadAllText(path);
		if (after != before)
			EditorChangeTracker.PushUndo(new TimelineFileUndoAction(path, before, after), null, null);
		Refresh(path);
		return Summary(asset, path);
	}

	/// <summary>Directors bound to the file and an open Timeline window keep a loaded copy; hand them the new one.</summary>
	private static void Refresh(string path)
	{
		TimelineNestedTrack.ClearCache();
		EditorGatewayDispatcher.Current?.ImGuiManager.TimelineWindow.ReloadFromDisk(path);
		var scene = Core.Scene;
		if (scene == null)
			return;
		foreach (var entity in scene.Entities)
			foreach (var director in entity.GetComponents<TimelineDirector>())
			{
				if (director.Asset == null)
					continue;
				var bound = director.Timeline.ResolvePath();
				if (!string.IsNullOrEmpty(bound) && string.Equals(Path.GetFullPath(bound), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
					director.SetAsset(TimelineAssetIO.Load(path));
			}
	}

	/// <summary>Swaps the whole file between its before and after text.</summary>
	private sealed class TimelineFileUndoAction : EditorChangeTracker.IEditorAction
	{
		private readonly string _path, _before, _after;

		public TimelineFileUndoAction(string path, string before, string after)
		{
			_path = path;
			_before = before;
			_after = after;
		}

		public string Description => $"Edit {Path.GetFileName(_path)}";

		public void Undo() => Write(_before, _after);

		public void Redo() => Write(_after, _before);

		private void Write(string text, string expected)
		{
			// Another editor of the file since this step gets overwritten; say so rather than fail the undo.
			if (File.Exists(_path) && File.ReadAllText(_path) != expected)
				Debug.Warn($"[Timeline] {Path.GetFileName(_path)} changed since this edit; undo replaces those changes");
			File.WriteAllText(_path, text, new System.Text.UTF8Encoding(false));
			Refresh(_path);
		}
	}

	private static object DescribeEvent(string componentId, string method)
	{
		if (!ComponentIdRegistry.TryGetType(componentId, out var type))
			return new { componentId, method, component = (string)null, displayName = (string)null, parameters = (List<object>)null };
		var info = type.GetMethod(method, BindingFlags.Public | BindingFlags.Instance);
		return new
		{
			componentId,
			method,
			component = type.FullName,
			displayName = info?.GetCustomAttribute<TimelineEventAttribute>()?.DisplayName,
			parameters = info?.GetParameters().Select(p => (object)new { name = p.Name, type = p.ParameterType.Name }).ToList()
		};
	}

	/// <summary>Asset-only checks that need no director: roles, prefabs, registered event methods and properties, track roles and length.</summary>
	private static List<string> ValidateAsset(TimelineAsset asset)
	{
		var problems = new List<string>();
		var roles = asset.Roles.Where(r => r != null && !string.IsNullOrEmpty(r.Name)).ToDictionary(r => r.Name, r => r.ExpectedComponentId);
		foreach (var role in roles.Keys)
			if (asset.SpawnClips.All(s => s?.SpawnRole != role))
				problems.Add($"role '{role}' needs a director binding or a spawn clip.");
		foreach (var spawn in asset.SpawnClips)
			if (spawn != null && !spawn.Prefab.IsValid)
				problems.Add($"spawn '{spawn.SpawnRole}' has no prefab assigned.");
		foreach (var e in asset.Events)
		{
			if (e == null)
				continue;
			if (!string.IsNullOrEmpty(e.TargetRole) && !roles.ContainsKey(e.TargetRole))
				problems.Add($"event '{e.Name}' targets unknown role '{e.TargetRole}'.");
			else if (!string.IsNullOrEmpty(e.TargetRole) && roles[e.TargetRole] is { Length: > 0 } id)
				foreach (var m in new[] { e.BeginMethod, e.EndMethod })
					if (!string.IsNullOrEmpty(m) && !TimelineDispatch.IsRegistered(id, m))
						problems.Add($"event '{e.Name}' calls {id}.{m}, which no [TimelineEvent] registered; see timeline.events.");
		}
		foreach (var track in asset.ParameterTracks)
		{
			if (track == null)
				continue;
			if (!string.IsNullOrEmpty(track.TargetRole) && !roles.ContainsKey(track.TargetRole))
				problems.Add($"{TimelineTrackRegistry.IdFor(track.GetType())} track targets unknown role '{track.TargetRole}'.");
			if (track is TimelinePropertyTrack p && !string.IsNullOrEmpty(p.TargetComponentId) && !string.IsNullOrEmpty(p.Property) && !TimelinePropertyRegistry.TryGetKind(p.TargetComponentId, p.Property, out _))
				problems.Add($"property track {p.TargetComponentId}.{p.Property} has no [TimelineProperty] registration; see timeline.properties.");
		}
		var contentEnd = asset.ContentEndTime();
		if (contentEnd > asset.Duration + 0.001f)
			problems.Add($"content runs to {contentEnd:0.00}s but Length is {asset.Duration:0.00}s.");
		return problems;
	}

	private static TimelineParameterTrack Track(TimelineAsset asset, int index) => asset.ParameterTracks[Index(index, asset.ParameterTracks.Count, "track")];

	private static int Index(int index, int count, string what)
	{
		if (index < 0 || index >= count)
			throw new GatewayException($"{what} {index} is out of range (0..{count - 1})");
		return index;
	}

	private static IEnumerable<FieldInfo> ListFields(Type type) =>
		type == null ? Array.Empty<FieldInfo>() : type.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(List<>));

	private static Type ItemType(FieldInfo list) => list.FieldType.GetGenericArguments()[0];

	private static bool IsKeyframe(Type t) => t == typeof(FloatKeyframe) || t == typeof(Vector2Keyframe) || t == typeof(ColorKeyframe);

	private static IList ClipList(TimelineAsset asset, GatewayArgs args, string kind, out Type itemType)
	{
		switch (kind)
		{
			case "event": itemType = typeof(TimelineEventClip); return asset.Events;
			case "spawn": itemType = typeof(TimelineSpawnClip); return asset.SpawnClips;
			case "marker": itemType = typeof(TimelineMarker); return asset.Markers;
		}

		var track = Track(asset, args.Int("track", -1));
		var lists = ListFields(track.GetType()).Where(f => !IsKeyframe(ItemType(f))).ToList();
		var name = args.String("list");
		var field = name != null
			? lists.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new GatewayException($"{track.GetType().Name} has no clip list '{name}'")
			: lists.Count == 1 ? lists[0] : throw new GatewayException($"{track.GetType().Name} has {lists.Count} clip lists; pass 'list'");
		itemType = ItemType(field);
		return (IList)field.GetValue(track);
	}

	private static IList KeyList(TimelineParameterTrack track, string channel, out Type keyType)
	{
		var field = ListFields(track.GetType()).FirstOrDefault(f => IsKeyframe(ItemType(f)) && f.Name.Equals(channel, StringComparison.OrdinalIgnoreCase))
			?? throw new GatewayException($"{track.GetType().Name} has no keyframe channel '{channel}'");
		keyType = ItemType(field);
		return (IList)field.GetValue(track);
	}

	private static float KeyTime(object key) => (float)key.GetType().GetField("Time").GetValue(key);

	private static void SetIfPresent(object target, string member, GatewayArgs args, string param)
	{
		if (!args.TryGet(param, out var value))
			return;
		if (target.GetType().GetField(member) == null)
			throw new GatewayException($"{target.GetType().Name} has no '{member}'");
		Set(target, member, value, false, null);
	}

	private static void ApplyValues(object target, GatewayArgs args)
	{
		if (!args.TryGet("values", out var values))
			return;
		if (values.ValueKind != JsonValueKind.Object)
			throw new GatewayException("'values' must be an object");
		foreach (var property in values.EnumerateObject())
			Set(target, property.Name, property.Value, false, null);
	}

	private static object DirectorState(TimelineDirector director) => new
	{
		entity = director.Entity?.Id,
		state = director.State.ToString(),
		time = director.PlayheadTime,
		duration = director.Duration,
		timeline = director.Timeline.IsValid ? director.Timeline.AssetPath : null,
		bindings = director.Bindings.Select(b => new { b.Role, entity = Describe(b.Entity) }).ToList()
	};

	private static object Summary(TimelineAsset asset, string path) => new
	{
		path,
		guid = AssetDatabase.Instance?.GetReference(path).Guid,
		duration = asset.Duration,
		contentEnd = asset.ContentEndTime(),
		roles = asset.Roles.Select(r => new { r.Name, r.ExpectedComponentId }).ToList(),
		tracks = asset.ParameterTracks.Select((t, i) => new
		{
			index = i,
			type = TimelineTrackRegistry.IdFor(t.GetType()),
			role = t.TargetRole,
			values = Snapshot(t),
			channels = ListFields(t.GetType()).Where(f => IsKeyframe(ItemType(f))).ToDictionary(f => f.Name, f => ((IList)f.GetValue(t)).Cast<object>().Select(k => Snapshot(k)).ToList()),
			clips = ListFields(t.GetType()).Where(f => !IsKeyframe(ItemType(f))).ToDictionary(f => f.Name, f => ((IList)f.GetValue(t)).Cast<object>().Select(c => Snapshot(c)).ToList())
		}).ToList(),
		events = asset.Events.Select(e => new { e.Name, e.Time, e.Duration, e.TargetRole, e.BeginMethod, e.EndMethod, e.BroadcastMessage, skip = e.OnSkip.ToString() }).ToList(),
		spawnClips = asset.SpawnClips.Select(s => new { s.SpawnRole, s.Time, s.Duration, s.KeepAfterTimeline, prefab = Describe(s.Prefab) }).ToList(),
		markers = asset.Markers.Select(m => new { m.Name, m.Time }).ToList()
	};
}
