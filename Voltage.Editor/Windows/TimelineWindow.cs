using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using Voltage.Cinematics;
using Voltage.Sprites;
using Voltage.Editor.Assets;
using Voltage.Editor.FilePickers;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.ProjectFile;
using Voltage.Utils.Tweens.Easing;
using Num = System.Numerics;
using Xna = Microsoft.Xna.Framework;

namespace Voltage.Editor.Windows
{
	/// <summary>
	/// Authoring UI for cinematic timelines. Follows the "edit through a director" model: it edits the
	/// <see cref="TimelineAsset"/> of the currently-selected entity's <see cref="TimelineDirector"/>, so the
	/// director's per-scene role bindings drive a live preview (scrub/play evaluate against real entities),
	/// while the data saves to the reusable <c>.vtimeline</c> asset.
	/// </summary>
	public class TimelineWindow
	{
		public bool IsOpen;

		private ImGuiManager _imgui;
		private TimelineDirector _director;      // the selected entity's director we're editing through
		private TimelineAsset _asset;          // working copy (also handed to the director for preview)
		private float _scrub;
		private bool _previewPlaying;
		private string _status;

		// Graphical timeline canvas.
		private float _pixelsPerSecond = 80f;

		// Record mode: while on, moving a bound entity in the scene auto-keyframes it at the playhead.
		private bool _recording;
		private readonly Dictionary<string, RecordSample> _recordBaseline = new();

		private struct RecordSample
		{
			public Xna.Vector2 Pos;
			public float Rot;
			public Xna.Vector2 Scale;
			public float Zoom;
			public bool HasCamera;
		}

		private enum LaneKind { Transform, Camera, Event, Spawn, Tint, Sprite, Activation, Audio, Property, Nested }

		/// <summary>
		/// One draggable element on a lane.
		/// </summary>
		private sealed class LaneItem
		{
			public float Time;
			public float Duration;
			public string Label;

			/// <summary>
			/// The underlying keyframe / clip / range instance.
			/// </summary>
			public object Key;

			/// <summary>Writes an edited (time, duration) back into the underlying keyframe / clip / range.</summary>
			public Action<float, float> Apply;

			public bool IsRange => Duration > 0f;
		}

		private sealed class Lane
		{
			public string Name;
			public LaneKind Kind;
			public readonly List<LaneItem> Items = new();
		}

		private enum DragMode { None, Move, ResizeStart, ResizeEnd }

		private DragMode _dragMode;
		private object _dragKey;
		private float _dragGrabOffset;

		private bool _needsNormalize;
		private static readonly Num.Vector4 Muted = new(0.6f, 0.6f, 0.6f, 1f);
		private static readonly Num.Vector4 Ok = new(0.3f, 1f, 0.4f, 1f);
		private static readonly Num.Vector4 Warn = new(1f, 0.8f, 0.2f, 1f);

		private static readonly string[] EaseNames = Enum.GetNames(typeof(EaseType));
		private static readonly string[] LoopNames = Enum.GetNames(typeof(SpriteAnimator.LoopMode));
		private static readonly string[] SkipNames = Enum.GetNames(typeof(SkipBehavior));

		public void Draw()
		{
			if (!IsOpen)
				return;

			_imgui ??= Core.GetGlobalManager<ImGuiManager>();

			ImGui.SetNextWindowSize(new Num.Vector2(820, 560), ImGuiCond.FirstUseEver);
			if (!Gui.Begin("Timeline ###TimelineWindow", ref IsOpen))
			{
				Gui.End();
				return;
			}

			SyncToSelectedDirector();

			if (_director == null)
			{
				ImGui.TextColored(Muted, "Select an entity with a Timeline Director to edit its timeline.");
				ImGui.Spacing();
				ImGui.TextWrapped("Drag a .vtimeline asset into the scene (or add a Timeline Director component) to get started.");
				Gui.End();
				return;
			}

			if (_asset == null)
			{
				DrawNoAssetState();
				Gui.End();
				return;
			}

			// Keep the director previewing against our working copy.
			_director.SetAsset(_asset);

			DrawTransport();

			if (_recording)
				HandleRecording();

			DrawTimelineCanvas();

			if (!string.IsNullOrEmpty(_status))
			{
				ImGui.TextColored(Ok, _status);
			}
			ImGui.Separator();

			if (ImGui.BeginTabBar("TimelineTabs"))
			{
				if (Gui.BeginTabItem("Roles"))      { DrawRoles(); ImGui.EndTabItem(); }
				if (Gui.BeginTabItem("Events"))     { DrawEvents(); ImGui.EndTabItem(); }
				if (Gui.BeginTabItem("Transform"))  { DrawTransformTracks(); ImGui.EndTabItem(); }
				if (Gui.BeginTabItem("Tint"))       { DrawTintTracks(); ImGui.EndTabItem(); }
				if (Gui.BeginTabItem("Sprite"))     { DrawSpriteTracks(); ImGui.EndTabItem(); }
				if (Gui.BeginTabItem("Active"))     { DrawActivationTracks(); ImGui.EndTabItem(); }
				if (Gui.BeginTabItem("Audio"))      { DrawAudioTracks(); ImGui.EndTabItem(); }
				if (Gui.BeginTabItem("Property"))   { DrawPropertyTracks(); ImGui.EndTabItem(); }
				if (Gui.BeginTabItem("Nested"))     { DrawNestedTracks(); ImGui.EndTabItem(); }
				if (Gui.BeginTabItem("Spawns"))     { DrawSpawns(); ImGui.EndTabItem(); }
				if (Gui.BeginTabItem("Markers"))    { DrawMarkers(); ImGui.EndTabItem(); }
				ImGui.EndTabBar();
			}

			if (_needsNormalize)
			{
				NormalizeAsset();
				_needsNormalize = false;
			}

			Gui.End();
		}

		#region Director / asset sync

		private void SyncToSelectedDirector()
		{
			var selected = _imgui?.SceneGraphWindow?.EntityPane?.SelectedEntities?.FirstOrDefault();
			var director = selected?.GetComponent<TimelineDirector>();

			if (ReferenceEquals(director, _director))
				return;

			// Selection changed — stop any preview on the old director and load the new one's asset.
			StopPreview();
			_director = director;
			_asset = _director != null ? LoadOrNull(_director) : null;
			CancelDrag();
			_scrub = 0f;
		}

		/// <summary>Rereads the working copy when an outside edit (the gateway) changed the file being shown.</summary>
		internal void ReloadFromDisk(string path)
		{
			var current = _director?.Timeline.ResolvePath();
			if (string.IsNullOrEmpty(current) || !string.Equals(Path.GetFullPath(current), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
				return;
			try
			{
				_asset = TimelineAssetIO.Load(path);
				_director.SetAsset(_asset);
				_status = $"Reloaded {Path.GetFileName(path)}.";
			}
			catch (Exception ex)
			{
				_status = $"Reload failed: {ex.Message}";
			}
		}

		private static TimelineAsset LoadOrNull(TimelineDirector director)
		{
			if (director.Asset != null)
				return director.Asset;

			var path = director.Timeline.ResolvePath();
			if (!string.IsNullOrEmpty(path) && File.Exists(path))
			{
				try { return TimelineAssetIO.Load(path); }
				catch (Exception ex) { Voltage.Debug.Warn($"[Timeline] load failed: {ex.Message}"); }
			}
			return null;
		}

		private void DrawNoAssetState()
		{
			ImGui.TextColored(Warn, "This director has no timeline asset loaded.");
			ImGui.Spacing();

			var path = _director.Timeline.ResolvePath();
			if (!string.IsNullOrEmpty(path))
				ImGui.TextColored(Muted, $"Expected: {path}");

			ImGui.Spacing();
			DrawFileButtons();
		}

		#endregion

		#region Transport (play / scrub / save)

		private void DrawTransport()
		{
			// Manually pump the director while previewing (in edit mode its Update() isn't called).
			if (_previewPlaying)
			{
				_director.Update();
				_scrub = _director.PlayheadTime;
				if (_director.State == DirectorState.Stopped)
					_previewPlaying = false;
			}

			if (Gui.Button(_previewPlaying ? "Pause" : "Play"))
			{
				if (_previewPlaying) { _director.Pause(); _previewPlaying = false; }
				else { SetRecording(false); if (_director.State != DirectorState.Paused) _director.Play(); else _director.Resume(); _previewPlaying = true; }
			}
			ImGui.SameLine();
			if (Gui.Button("Stop"))
				StopPreview();

			// Record toggle — red when armed.
			ImGui.SameLine();
			if (_recording)
			{
				ImGui.PushStyleColor(ImGuiCol.Button, new Num.Vector4(0.75f, 0.15f, 0.15f, 1f));
				ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Num.Vector4(0.9f, 0.25f, 0.25f, 1f));
			}
			if (Gui.Button(_recording ? "● Recording" : "● Record"))
				SetRecording(!_recording);
			if (_recording)
				ImGui.PopStyleColor(2);
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Record: move a bound entity in the scene (or zoom its camera) and a keyframe is\nwritten at the playhead. Scrub to a time first, then move the entity.");

			ImGui.SameLine();
			ImGui.TextColored(Muted, $"| {_scrub:0.00}s");

			// Zoom controls.
			ImGui.SameLine();
			if (Gui.SmallButton("-"))
				_pixelsPerSecond = Math.Max(12f, _pixelsPerSecond / 1.25f);
			ImGui.SameLine();
			if (Gui.SmallButton("+"))
				_pixelsPerSecond = Math.Min(600f, _pixelsPerSecond * 1.25f);
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Zoom (or scroll the wheel over the timeline).");

			ImGui.SameLine();
			ImGui.SetNextItemWidth(80);
			var dur = _asset.Duration;
			if (Gui.InputFloat("Length", ref dur, 0f, 0f, "%.2f"))
				_asset.Duration = Math.Max(0.1f, dur);

			ImGui.SameLine();
			var contentEnd = _asset.ContentEndTime();
			if (contentEnd > _asset.Duration + 0.001f)
			{
				ImGui.TextColored(Warn, $"content runs to {contentEnd:0.00}s");
				if (ImGui.IsItemHovered())
					ImGui.SetTooltip("Anything past Length never plays. Press Fit to extend the timeline.");
				ImGui.SameLine();
			}

			if (Gui.Button("Fit"))
				_asset.Duration = Math.Max(0.1f, contentEnd);
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Set Length to the end of the last keyframe, clip, event or marker.");

			ImGui.SameLine();
			if (Gui.Button("Save"))
				SaveIfPathKnown();
			ImGui.SameLine();
			if (Gui.Button("Load..."))
				LoadTimeline();
		}

		/// <summary>New / Load buttons — create a fresh .vtimeline file or open an existing one for this director.</summary>
		private void DrawFileButtons()
		{
			if (Gui.Button("New Timeline..."))
				NewTimeline();
			ImGui.SameLine();
			if (Gui.Button("Load Timeline..."))
				LoadTimeline();

			if (!NativeFileDialogs.IsAvailable)
			{
				ImGui.SameLine();
				ImGui.TextColored(Muted, "(native file dialogs unavailable on this platform)");
			}
		}

		private void NewTimeline()
		{
			var start = DefaultTimelineFolder();
			if (!NativeFileDialogs.TrySaveFile("New Timeline", Path.Combine(start, "NewTimeline" + TimelineAssetIO.FileExtension),
				    new[] { "*" + TimelineAssetIO.FileExtension }, "Voltage Timeline", out var path) || string.IsNullOrEmpty(path))
				return;

			if (!path.EndsWith(TimelineAssetIO.FileExtension, StringComparison.OrdinalIgnoreCase))
				path += TimelineAssetIO.FileExtension;

			try
			{
				_asset = TimelineAssetIO.CreateAndSave(path);
				CancelDrag();
				AssignTimelineToDirector(path);
				_scrub = 0f;
				_status = $"Created {Path.GetFileName(path)}.";
			}
			catch (Exception ex)
			{
				_status = $"Create failed: {ex.Message}";
			}
		}

		private void LoadTimeline()
		{
			var start = DefaultTimelineFolder();
			if (!NativeFileDialogs.TryOpenFile("Load Timeline", start,
				    new[] { "*" + TimelineAssetIO.FileExtension }, "Voltage Timeline", out var path) || string.IsNullOrEmpty(path))
				return;

			try
			{
				var loaded = TimelineAssetIO.Load(path);
				if (loaded == null)
				{
					_status = "Could not load that timeline.";
					return;
				}
				_asset = loaded;
				CancelDrag();
				AssignTimelineToDirector(path);
				_scrub = 0f;
				_status = $"Loaded {Path.GetFileName(path)}.";
			}
			catch (Exception ex)
			{
				_status = $"Load failed: {ex.Message}";
			}
		}

		/// <summary>Points the selected director's Timeline reference at <paramref name="path"/> (GUID + path) and previews it.</summary>
		private void AssignTimelineToDirector(string path)
		{
			var guid = AssetDatabase.Instance?.GetOrCreateGuid(path) ?? Guid.Empty;
			_director.Timeline = new Voltage.Serialization.AssetReference
			{
				AssetGuid = guid,
				AssetPath = path,
				AssetName = Path.GetFileNameWithoutExtension(path),
			};
			_director.SetAsset(_asset);
			AssetDatabase.Instance?.Refresh();   // catalog the new file so it appears in the browser
		}

		private static string DefaultTimelineFolder()
		{
			if (ProjectManager.Instance.HasActiveProject)
			{
				var dir = Path.Combine(ProjectManager.Instance.CurrentProject.DataFolder, "Timelines");
				try { Directory.CreateDirectory(dir); } catch { /* fall through to base dir */ }
				if (Directory.Exists(dir))
					return dir;
			}
			return AppContext.BaseDirectory;
		}

		private void StopPreview()
		{
			if (_director != null && _director.State != DirectorState.Stopped)
				_director.Cancel();   // abort preview and restore pre-preview entity state
			_previewPlaying = false;
			_scrub = 0f;
		}

		private void SaveIfPathKnown()
		{
			var path = _director.Timeline.ResolvePath();
			if (string.IsNullOrEmpty(path))
			{
				_status = "No .vtimeline file path — assign the director's Timeline asset first.";
				return;
			}
			try
			{
				_asset.InvalidateEventOrder();
				TimelineAssetIO.Save(_asset, path);
				TimelineNestedTrack.ClearCache();
				_status = $"Saved to {Path.GetFileName(path)}.";
			}
			catch (Exception ex)
			{
				_status = $"Save failed: {ex.Message}";
			}
		}

		#endregion

		#region Property tracks

		private void DrawPropertyTracks()
		{
			ImGui.TextColored(Muted,
				"Animate any component member marked [TimelineProperty]. Supported types: float, Vector2, Color.");
			ImGui.Spacing();

			var registered = TimelinePropertyRegistry.Registered().OrderBy(r => r.ComponentId)
				.ThenBy(r => r.Property).ToList();

			if (registered.Count == 0)
			{
				ImGui.TextColored(Warn,
					"No [TimelineProperty] members found. Mark a public float/Vector2/Color on a component\n" +
					"that has a [ComponentId], then recompile.");
			}

			var tracks = _asset.ParameterTracks.OfType<TimelinePropertyTrack>().ToList();
			for (var i = 0; i < tracks.Count; i++)
			{
				var track = tracks[i];
				ImGui.PushID("prop" + i);
				ImGui.Separator();

				ImGui.SetNextItemWidth(160);
				DrawRoleSelect("Target Role", ref track.TargetRole);
				ImGui.SameLine();
				if (Gui.SmallButton("Remove Track")) { _asset.ParameterTracks.Remove(track); ImGui.PopID(); break; }

				var current = string.IsNullOrEmpty(track.Property)
					? "(pick a property)"
					: $"{track.TargetComponentId}.{track.Property}";

				ImGui.SetNextItemWidth(280);
				if (Gui.BeginCombo("Property", current))
				{
					foreach (var entry in registered)
					{
						var label = $"{entry.ComponentId}.{entry.Property}  ({entry.Kind})";
						var selected = entry.ComponentId == track.TargetComponentId && entry.Property == track.Property;
						if (Gui.Selectable(label, selected))
						{
							track.TargetComponentId = entry.ComponentId;
							track.Property = entry.Property;
						}
					}
					ImGui.EndCombo();
				}

				if (TimelinePropertyRegistry.TryGetKind(track.TargetComponentId, track.Property, out var kind))
				{
					switch (kind)
					{
						case TimelinePropertyKind.Float: DrawFloatKeys("Value", track.FloatKeys); break;
						case TimelinePropertyKind.Vector2: DrawVector2Keys("Value", track.Vector2Keys); break;
						case TimelinePropertyKind.Color: DrawColorKeys("Value", track.ColorKeys); break;
					}
				}
				else if (!string.IsNullOrEmpty(track.Property))
				{
					ImGui.TextColored(Warn,
						$"'{track.TargetComponentId}.{track.Property}' is no longer registered — it was renamed or removed.");
				}

				ImGui.PopID();
			}

			ImGui.Separator();
			if (Gui.Button("Add Property Track"))
				_asset.ParameterTracks.Add(new TimelinePropertyTrack { TargetRole = _asset.Roles.FirstOrDefault()?.Name });
		}

		#endregion

		#region Nested timelines

		private void DrawNestedTracks()
		{
			ImGui.TextColored(Muted,
				"Play other timelines as reusable beats. Roles are shared with this timeline, so a nested\n" +
				"beat referencing \"Hero\" binds to this timeline's \"Hero\". Nested spawn clips are not run.");
			ImGui.Spacing();

			var tracks = _asset.ParameterTracks.OfType<TimelineNestedTrack>().ToList();
			for (var i = 0; i < tracks.Count; i++)
			{
				var track = tracks[i];
				ImGui.PushID("nest" + i);
				ImGui.Separator();

				if (Gui.SmallButton("Remove Track")) { _asset.ParameterTracks.Remove(track); ImGui.PopID(); break; }

				for (var c = 0; c < track.Clips.Count; c++)
				{
					var clip = track.Clips[c];
					ImGui.PushID(c);

					ImGui.SetNextItemWidth(70);
					MarkEditedTime(Gui.InputFloat("t", ref clip.Time, 0, 0, "%.2f"));
					ImGui.SameLine();

					var label = clip.Timeline.IsValid ? (clip.Timeline.AssetName ?? "timeline") : "None (.vtimeline)";
					Gui.Button($"{label}##nested", new Num.Vector2(170, 0));
					if (ImGui.BeginDragDropTarget())
					{
						var payload = ImGui.AcceptDragDropPayload(AssetBrowserWindow.DragDropPayloadId);
						bool accepted;
						unsafe { accepted = payload.NativePtr != null; }
						if (accepted && !AssetBrowserWindow.DraggedReference.IsEmpty)
						{
							var dr = AssetBrowserWindow.DraggedReference;
							AssetBrowserWindow.DraggedReference = Voltage.Editor.Assets.AssetReference.Empty;
							clip.Timeline = new Voltage.Serialization.AssetReference
							{
								AssetGuid = dr.Guid,
								AssetPath = dr.HintPath,
								AssetName = Path.GetFileNameWithoutExtension(dr.HintPath),
							};
							clip.Name ??= clip.Timeline.AssetName;
							TimelineNestedTrack.ClearCache();
						}
						ImGui.EndDragDropTarget();
					}

					ImGui.SameLine();
					ImGui.SetNextItemWidth(60);
					Gui.InputFloat("speed", ref clip.Speed, 0, 0, "%.2f");
					ImGui.SameLine();
					if (Gui.SmallButton("x")) { track.Clips.RemoveAt(c); ImGui.PopID(); break; }
					ImGui.PopID();
				}

				if (Gui.SmallButton("+ nested"))
					track.Clips.Add(new TimelineNestedClip { Time = _scrub });

				ImGui.PopID();
			}

			ImGui.Separator();
			if (Gui.Button("Add Nested Track"))
				_asset.ParameterTracks.Add(new TimelineNestedTrack());

			ImGui.SameLine();
			if (Gui.Button("Reload Nested"))
				TimelineNestedTrack.ClearCache();
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Nested timelines are cached. Press this after editing one to see the change.");
		}

		#endregion

		#region Markers

		private void DrawMarkers()
		{
			ImGui.TextColored(Muted,
				"Named points for navigation — jump the playhead, or seek to one at runtime with\n" +
				"director.SeekToMarker(\"name\"). Markers have no effect of their own; use an event's\n" +
				"Broadcast field to signal game code.");
			ImGui.Spacing();

			for (var i = 0; i < _asset.Markers.Count; i++)
			{
				var marker = _asset.Markers[i];
				ImGui.PushID("mk" + i);

				ImGui.SetNextItemWidth(70);
				MarkEditedTime(Gui.InputFloat("t", ref marker.Time, 0, 0, "%.2f"));
				ImGui.SameLine();

				var name = marker.Name ?? string.Empty;
				ImGui.SetNextItemWidth(180);
				if (Gui.InputText("name", ref name, 64)) marker.Name = name;

				ImGui.SameLine();
				if (Gui.SmallButton("go"))
				{
					_previewPlaying = false;
					_scrub = Math.Clamp(marker.Time, 0f, Math.Max(0.1f, _asset.Duration));
					_director.Evaluate(_scrub);
				}

				ImGui.SameLine();
				if (Gui.SmallButton("x")) { _asset.Markers.RemoveAt(i); ImGui.PopID(); break; }

				ImGui.PopID();
			}

			ImGui.Separator();
			if (Gui.Button("Add Marker"))
				_asset.Markers.Add(new TimelineMarker { Time = _scrub, Name = "beat" });
		}

		#endregion

		#region Graphical canvas

		/// <summary>
		/// A Unity-style timeline box: a per-second vertical grid you can zoom into, track lanes showing
		/// keyframes / events / spawn bars, and a draggable playhead. Click or drag in the time area to scrub.
		/// </summary>
		private void DrawTimelineCanvas()
		{
			var duration = Math.Max(0.1f, _asset.Duration);
			const float labelW = 150f;
			const float rulerH = 22f;
			const float trackH = 24f;

			var lanes = BuildLanes();
			var pps = _pixelsPerSecond;
			var contentW = labelW + duration * pps + 60f;
			var contentH = rulerH + Math.Max(1, lanes.Count) * trackH + 6f;
			var boxH = Math.Min(300f, contentH + 6f);

			var io = ImGui.GetIO();

			ImGui.BeginChild("##tlcanvas", new Num.Vector2(0, boxH), true, ImGuiWindowFlags.HorizontalScrollbar);

			var dl = ImGui.GetWindowDrawList();
			var origin = ImGui.GetCursorScreenPos();
			Gui.InvisibleButton("##tlcontent", new Num.Vector2(contentW, contentH));
			var hovered = ImGui.IsItemHovered();
			var mouse = ImGui.GetMousePos();

			var timeX = origin.X + labelW;
			var bottomY = origin.Y + contentH;

			var colGridSec = ImGui.GetColorU32(new Num.Vector4(1, 1, 1, 0.12f));
			var colGridMin = ImGui.GetColorU32(new Num.Vector4(1, 1, 1, 0.05f));
			var colText = ImGui.GetColorU32(new Num.Vector4(0.8f, 0.8f, 0.8f, 1f));
			var colRuler = ImGui.GetColorU32(new Num.Vector4(0.08f, 0.08f, 0.08f, 1f));
			var colGutter = ImGui.GetColorU32(new Num.Vector4(0.13f, 0.13f, 0.14f, 1f));
			var colLaneAlt = ImGui.GetColorU32(new Num.Vector4(1, 1, 1, 0.035f));
			var colPlay = ImGui.GetColorU32(new Num.Vector4(1f, 0.25f, 0.25f, 1f));
			var colKey = ImGui.GetColorU32(new Num.Vector4(0.4f, 0.8f, 1f, 1f));
			var colCam = ImGui.GetColorU32(new Num.Vector4(0.8f, 0.6f, 1f, 1f));
			var colEvent = ImGui.GetColorU32(new Num.Vector4(1f, 0.8f, 0.2f, 1f));
			var colSpawn = ImGui.GetColorU32(new Num.Vector4(0.5f, 1f, 0.5f, 0.45f));
			var colTint = ImGui.GetColorU32(new Num.Vector4(1f, 0.55f, 0.75f, 1f));
			var colAudio = ImGui.GetColorU32(new Num.Vector4(0.4f, 1f, 0.85f, 1f));
			var colSprite = ImGui.GetColorU32(new Num.Vector4(0.55f, 0.72f, 0.4f, 0.85f));
			var colActive = ImGui.GetColorU32(new Num.Vector4(0.45f, 0.6f, 0.85f, 0.85f));
			var colEdge = ImGui.GetColorU32(new Num.Vector4(1f, 1f, 1f, 0.45f));
			var colMarker = ImGui.GetColorU32(new Num.Vector4(1f, 0.55f, 0.15f, 0.9f));

			// Ruler background + per-second grid (with minor lines that appear as you zoom in).
			dl.AddRectFilled(new Num.Vector2(origin.X, origin.Y), new Num.Vector2(origin.X + contentW, origin.Y + rulerH), colRuler);
			var minorStep = pps >= 220f ? 0.1f : pps >= 90f ? 0.5f : 1f;
			for (var t = 0f; t <= duration + 0.0001f; t += minorStep)
			{
				var x = timeX + t * pps;
				var isSecond = Math.Abs(t - MathF.Round(t)) < 0.001f;
				dl.AddLine(new Num.Vector2(x, origin.Y + (isSecond ? 0f : rulerH)), new Num.Vector2(x, bottomY),
					isSecond ? colGridSec : colGridMin);
				if (isSecond)
					dl.AddText(new Num.Vector2(x + 3f, origin.Y + 4f), colText, ((int)MathF.Round(t)) + "s");
			}

			// Track lanes + name gutter.
			for (var i = 0; i < lanes.Count; i++)
			{
				var lane = lanes[i];
				var y = origin.Y + rulerH + i * trackH;
				var cy = y + trackH * 0.5f;

				if (i % 2 == 0)
					dl.AddRectFilled(new Num.Vector2(timeX, y), new Num.Vector2(origin.X + contentW, y + trackH), colLaneAlt);

				var laneColor = lane.Kind switch
				{
					LaneKind.Event => colEvent,
					LaneKind.Camera => colCam,
					LaneKind.Tint => colTint,
					LaneKind.Audio => colAudio,
					LaneKind.Spawn => colSpawn,
					LaneKind.Sprite => colSprite,
					LaneKind.Activation => colActive,
					_ => colKey,
				};

				for (var j = 0; j < lane.Items.Count; j++)
				{
					var item = lane.Items[j];
					var x0 = timeX + item.Time * pps;

					if (item.IsRange)
					{
						var x1 = timeX + (item.Time + item.Duration) * pps;
						var top = y + 3f;
						var bottom = y + trackH - 3f;

						dl.AddRectFilled(new Num.Vector2(x0, top), new Num.Vector2(x1, bottom), laneColor, 3f);
						dl.AddRectFilled(new Num.Vector2(x0, top), new Num.Vector2(x0 + EdgeGrabPx, bottom), colEdge, 2f);
						dl.AddRectFilled(new Num.Vector2(x1 - EdgeGrabPx, top), new Num.Vector2(x1, bottom), colEdge, 2f);

						if (!string.IsNullOrEmpty(item.Label) && x1 - x0 > 26f)
							dl.AddText(new Num.Vector2(x0 + EdgeGrabPx + 3f, y + 5f), colText, item.Label);

						if (hovered && !io.MouseDown[0] && mouse.Y >= top && mouse.Y <= bottom)
						{
							if (mouse.X >= x0 - EdgeGrabPx && mouse.X <= x0 + EdgeGrabPx)
								HoverEdge(DragMode.ResizeStart, item, mouse, timeX, pps);
							else if (mouse.X >= x1 - EdgeGrabPx && mouse.X <= x1 + EdgeGrabPx)
								HoverEdge(DragMode.ResizeEnd, item, mouse, timeX, pps);
							else if (mouse.X > x0 && mouse.X < x1)
								HoverEdge(DragMode.Move, item, mouse, timeX, pps);
						}
					}
					else
					{
						dl.AddQuadFilled(new Num.Vector2(x0, cy - 5f), new Num.Vector2(x0 + 5f, cy),
							new Num.Vector2(x0, cy + 5f), new Num.Vector2(x0 - 5f, cy), laneColor);

						if (hovered && !io.MouseDown[0] &&
							mouse.Y >= y && mouse.Y <= y + trackH &&
							Math.Abs(mouse.X - x0) <= 6f)
						{
							HoverEdge(DragMode.Move, item, mouse, timeX, pps);
						}
					}
				}

				dl.AddRectFilled(new Num.Vector2(origin.X, y), new Num.Vector2(timeX, y + trackH), colGutter);
				dl.AddText(new Num.Vector2(origin.X + 6f, y + 5f), colText, lane.Name);
			}

			// Gutter corner over the ruler.
			dl.AddRectFilled(new Num.Vector2(origin.X, origin.Y), new Num.Vector2(timeX, origin.Y + rulerH), colGutter);
			if (_recording)
				dl.AddText(new Num.Vector2(origin.X + 6f, origin.Y + 4f), colPlay, "REC");

			// Playhead.
			var px = timeX + _scrub * pps;
			dl.AddLine(new Num.Vector2(px, origin.Y), new Num.Vector2(px, bottomY), colPlay, 2f);
			dl.AddTriangleFilled(new Num.Vector2(px - 5f, origin.Y), new Num.Vector2(px + 5f, origin.Y),
				new Num.Vector2(px, origin.Y + 8f), colPlay);

			foreach (var marker in _asset.Markers)
			{
				if (marker == null)
					continue;
				var mx = timeX + marker.Time * pps;
				dl.AddLine(new Num.Vector2(mx, origin.Y + rulerH), new Num.Vector2(mx, bottomY), colMarker);
				dl.AddTriangleFilled(new Num.Vector2(mx - 4f, origin.Y + rulerH - 7f),
					new Num.Vector2(mx + 4f, origin.Y + rulerH - 7f), new Num.Vector2(mx, origin.Y + rulerH), colMarker);
				if (!string.IsNullOrEmpty(marker.Name))
					dl.AddText(new Num.Vector2(mx + 6f, origin.Y + rulerH - 15f), colMarker, marker.Name);
			}

			ApplyItemDrag(lanes, mouse, timeX, pps, duration, io);

			if (_dragMode == DragMode.None &&
				hovered && ImGui.IsMouseDown(ImGuiMouseButton.Left) && mouse.X >= timeX)
			{
				_previewPlaying = false;
				_scrub = Math.Clamp((mouse.X - timeX) / pps, 0f, duration);
				_director.Evaluate(_scrub);
				RefreshRecordBaseline();
			}

			// Zoom with the mouse wheel over the canvas.
			if (hovered && io.MouseWheel != 0f)
				_pixelsPerSecond = Math.Clamp(_pixelsPerSecond * (1f + io.MouseWheel * 0.12f), 12f, 600f);

			ImGui.EndChild();
		}

		/// <summary>
		/// Projects the asset into draggable lane items.
		/// </summary>
		private const float EdgeGrabPx = 5f;

		/// <summary>
		/// Arms a drag when the cursor is over an item, and shows the matching resize/move cursor.
		/// </summary>
		private void HoverEdge(DragMode mode, LaneItem target, Num.Vector2 mouse, float timeX, float pps)
		{
			ImGui.SetMouseCursor(mode == DragMode.Move ? ImGuiMouseCursor.ResizeAll : ImGuiMouseCursor.ResizeEW);

			if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left))
				return;

			_dragMode = mode;
			_dragKey = target.Key;
			_dragGrabOffset = (mouse.X - timeX) / pps - target.Time;
		}

		/// <summary>
		/// Applies the in-flight drag.
		/// </summary>
		private void ApplyItemDrag(List<Lane> lanes, Num.Vector2 mouse, float timeX, float pps, float duration, ImGuiIOPtr io)
		{
			if (_dragMode == DragMode.None)
				return;

			if (!io.MouseDown[0])
			{
				_dragMode = DragMode.None;
				_dragKey = null;
				_needsNormalize = true;   // re-sort once the drag settles, not during it
				return;
			}

			var item = FindItem(lanes, _dragKey);
			if (item == null)
			{
				_dragMode = DragMode.None;
				_dragKey = null;
				return;
			}
			var cursor = Math.Max(0f, (mouse.X - timeX) / pps);
			var snap = io.KeyAlt ? 0f : SnapStep(pps);

			const float minLength = 0.05f;
			var time = item.Time;
			var length = item.Duration;

			switch (_dragMode)
			{
				case DragMode.Move:
					time = Snap(cursor - _dragGrabOffset, snap);
					break;

				case DragMode.ResizeStart:
				{
					var end = item.Time + item.Duration;
					time = Math.Min(Snap(cursor, snap), end - minLength);
					length = end - time;
					break;
				}

				case DragMode.ResizeEnd:
					length = Math.Max(minLength, Snap(cursor, snap) - item.Time);
					break;
			}

			time = Math.Clamp(time, 0f, Math.Max(0f, duration));
			item.Time = time;
			item.Duration = length;
			item.Apply?.Invoke(time, length);

			_previewPlaying = false;
			_director?.Evaluate(_scrub);
		}

		/// <summary>Abandons an in-flight drag — its key refers to an object graph that is going away.</summary>
		private void CancelDrag()
		{
			_dragMode = DragMode.None;
			_dragKey = null;
			_needsNormalize = false;
		}

		private static LaneItem FindItem(List<Lane> lanes, object key)
		{
			if (key == null)
				return null;

			foreach (var lane in lanes)
			{
				foreach (var item in lane.Items)
				{
					if (ReferenceEquals(item.Key, key))
						return item;
				}
			}

			return null;
		}

		/// <summary>
		/// Restores the invariants the runtime depends on after an edit: keyframe lists must be sorted ascending (every sampler assumes it), and the director's cached event ordering only rebuilds on a count change, so a retimed event needs an explicit invalidation.
		/// </summary>
		private void NormalizeAsset()
		{
			if (_asset == null)
				return;

			foreach (var track in _asset.ParameterTracks)
			{
				switch (track)
				{
					case TimelineTransformTrack tt:
						tt.Position.Sort(CompareVector2Keys);
						tt.Rotation.Sort(CompareFloatKeys);
						tt.Scale.Sort(CompareVector2Keys);
						break;
					case TimelineCameraTrack ct:
						ct.Zoom.Sort(CompareFloatKeys);
						break;
					case TimelineTintTrack tint:
						tint.Alpha.Sort(CompareFloatKeys);
						tint.Tint.Sort((a, b) => a.Time.CompareTo(b.Time));
						break;
					case TimelineSpriteTrack sprite:
						sprite.Clips.Sort((a, b) => a.Time.CompareTo(b.Time));
						break;
					case TimelineActivationTrack activation:
						activation.Ranges.Sort((a, b) => a.Time.CompareTo(b.Time));
						break;
					case TimelineAudioTrack audioTrack:
						audioTrack.Clips.Sort((a, b) => a.Time.CompareTo(b.Time));
						break;
					case TimelineNestedTrack nestedTrack:
						nestedTrack.Clips.Sort((a, b) => a.Time.CompareTo(b.Time));
						break;
					case TimelinePropertyTrack prop:
						prop.FloatKeys.Sort(CompareFloatKeys);
						prop.Vector2Keys.Sort(CompareVector2Keys);
						prop.ColorKeys.Sort((a, b) => a.Time.CompareTo(b.Time));
						break;
				}
			}

			_asset.InvalidateEventOrder();
		}

		private static int CompareFloatKeys(FloatKeyframe a, FloatKeyframe b) => a.Time.CompareTo(b.Time);
		private static int CompareVector2Keys(Vector2Keyframe a, Vector2Keyframe b) => a.Time.CompareTo(b.Time);

		/// <summary>Grid step matching the visible ruler ticks, so dragging lands where the eye expects.</summary>
		/// <summary>
		/// Records that a time field was edited, so the asset is re-sorted once the edit settles.
		/// </summary>
		private bool MarkEditedTime(bool changed)
		{
			if (ImGui.IsItemDeactivatedAfterEdit())
				_needsNormalize = true;
			return changed;
		}

		private static float SnapStep(float pps) => pps >= 220f ? 0.05f : pps >= 90f ? 0.1f : 0.25f;

		private static float Snap(float value, float step) =>
			step <= 0f ? value : MathF.Round(value / step) * step;

		private List<Lane> BuildLanes()
		{
			var lanes = new List<Lane>();

			foreach (var track in _asset.ParameterTracks)
			{
				switch (track)
				{
					case TimelineTransformTrack tt:
					{
						var lane = new Lane { Name = $"{tt.TargetRole} - transform", Kind = LaneKind.Transform };
						AddFloatKeys(lane, tt.Rotation);
						AddVector2Keys(lane, tt.Position);
						AddVector2Keys(lane, tt.Scale);
						lanes.Add(lane);
						break;
					}

					case TimelineCameraTrack ct:
					{
						var lane = new Lane { Name = $"{ct.TargetRole} - zoom", Kind = LaneKind.Camera };
						AddFloatKeys(lane, ct.Zoom);
						lanes.Add(lane);
						break;
					}

					case TimelineTintTrack tint:
					{
						var lane = new Lane { Name = $"{tint.TargetRole} - tint", Kind = LaneKind.Tint };
						AddFloatKeys(lane, tint.Alpha);
						foreach (var k in tint.Tint)
						{
							var key = k;
							lane.Items.Add(new LaneItem { Time = key.Time, Key = key, Apply = (t, _) => key.Time = t });
						}
						lanes.Add(lane);
						break;
					}

					case TimelineSpriteTrack sprite:
					{
						var lane = new Lane { Name = $"{sprite.TargetRole} - sprite", Kind = LaneKind.Sprite };
						foreach (var c in sprite.Clips)
						{
							var clip = c;
							lane.Items.Add(new LaneItem
							{
								Time = clip.Time,
								Duration = clip.Duration,
								Label = clip.Animation,
								Key = clip,
								Apply = (t, d) => { clip.Time = t; clip.Duration = d; },
							});
						}
						lanes.Add(lane);
						break;
					}

					case TimelineActivationTrack activation:
					{
						var lane = new Lane { Name = $"{activation.TargetRole} - active", Kind = LaneKind.Activation };
						foreach (var r in activation.Ranges)
						{
							var range = r;
							lane.Items.Add(new LaneItem
							{
								Time = range.Time,
								Duration = range.Duration,
								Label = "visible",
								Key = range,
								Apply = (t, d) => { range.Time = t; range.Duration = d; },
							});
						}
						lanes.Add(lane);
						break;
					}

					case TimelinePropertyTrack prop:
					{
						var lane = new Lane
						{
							Name = $"{prop.TargetRole} - {prop.Property ?? "property"}",
							Kind = LaneKind.Property,
						};
						AddFloatKeys(lane, prop.FloatKeys);
						AddVector2Keys(lane, prop.Vector2Keys);
						foreach (var k in prop.ColorKeys)
						{
							var key = k;
							lane.Items.Add(new LaneItem { Time = key.Time, Key = key, Apply = (t, _) => key.Time = t });
						}
						lanes.Add(lane);
						break;
					}

					case TimelineNestedTrack nestedTrack:
					{
						var lane = new Lane { Name = "nested", Kind = LaneKind.Nested };
						foreach (var c in nestedTrack.Clips)
						{
							var clip = c;
							lane.Items.Add(new LaneItem
							{
								Time = clip.Time,
								Label = clip.Name ?? clip.Timeline.AssetName,
								Key = clip,
								Apply = (t, _) => clip.Time = t,
							});
						}
						lanes.Add(lane);
						break;
					}

					case TimelineAudioTrack audioTrack:
					{
						var lane = new Lane { Name = "audio", Kind = LaneKind.Audio };
						foreach (var c in audioTrack.Clips)
						{
							var clip = c;
							lane.Items.Add(new LaneItem
							{
								Time = clip.Time,
								Label = clip.Name,
								Key = clip,
								Apply = (t, _) => clip.Time = t,
							});
						}
						lanes.Add(lane);
						break;
					}
				}
			}

			if (_asset.Events.Count > 0)
			{
				var lane = new Lane { Name = "events", Kind = LaneKind.Event };
				foreach (var e in _asset.Events)
				{
					var evt = e;
					lane.Items.Add(new LaneItem
					{
						Time = evt.Time,
						Duration = evt.Duration,
						Label = evt.Name,
						Key = evt,
						Apply = (t, d) => { evt.Time = t; evt.Duration = d; _asset.InvalidateEventOrder(); },
					});
				}
				lanes.Add(lane);
			}

			foreach (var s in _asset.SpawnClips)
			{
				var spawn = s;
				var lane = new Lane { Name = $"spawn - {spawn.SpawnRole}", Kind = LaneKind.Spawn };
				lane.Items.Add(new LaneItem
				{
					Time = spawn.Time,
					Duration = spawn.Duration,
					Label = spawn.SpawnRole,
					Key = spawn,
					Apply = (t, d) => { spawn.Time = t; spawn.Duration = d; },
				});
				lanes.Add(lane);
			}

			return lanes;
		}

		private static void AddFloatKeys(Lane lane, List<FloatKeyframe> keys)
		{
			foreach (var k in keys)
			{
				var key = k;
				lane.Items.Add(new LaneItem { Time = key.Time, Key = key, Apply = (t, _) => key.Time = t });
			}
		}

		private static void AddVector2Keys(Lane lane, List<Vector2Keyframe> keys)
		{
			foreach (var k in keys)
			{
				var key = k;
				lane.Items.Add(new LaneItem { Time = key.Time, Key = key, Apply = (t, _) => key.Time = t });
			}
		}

		#endregion

		#region Recording

		private void SetRecording(bool on)
		{
			if (on == _recording)
				return;
			_recording = on;
			if (on)
			{
				_previewPlaying = false;
				_director.Evaluate(_scrub);   // pose entities at the current time, then capture as the baseline
				RefreshRecordBaseline();
				_status = "Recording - move a bound entity in the scene to keyframe it at the playhead.";
			}
			else
			{
				_recordBaseline.Clear();
			}
		}

		/// <summary>Captures the current transform (and camera zoom) of every bound role as the record baseline.</summary>
		private void RefreshRecordBaseline()
		{
			if (!_recording)
				return;

			_recordBaseline.Clear();
			foreach (var role in _asset.Roles)
			{
				var e = RoleEntity(role.Name);
				if (e == null)
					continue;
				var cam = e.GetComponent<Camera>();
				_recordBaseline[role.Name] = new RecordSample
				{
					Pos = e.Position, Rot = e.Rotation, Scale = e.Scale,
					Zoom = cam?.RawZoom ?? 0f, HasCamera = cam != null,
				};
			}
		}

		/// <summary>
		/// Each frame while recording: if a bound entity moved (or its camera zoomed) since the baseline,
		/// write/update a keyframe at the playhead. This is the "move it in the scene -> it's recorded" flow.
		/// </summary>
		private void HandleRecording()
		{
			var dirty = false;

			foreach (var role in _asset.Roles)
			{
				var e = RoleEntity(role.Name);
				if (e == null)
					continue;

				if (!_recordBaseline.TryGetValue(role.Name, out var b))
				{
					RefreshRecordBaseline();
					continue;
				}

				var cam = e.GetComponent<Camera>();
				var moved = !NearV(e.Position, b.Pos) || !Near(e.Rotation, b.Rot) || !NearV(e.Scale, b.Scale);
				var zoomed = b.HasCamera && cam != null && !Near(cam.RawZoom, b.Zoom);

				if (moved)
					UpsertTransformKey(role.Name, _scrub, e.Position, e.Rotation, e.Scale);
				if (zoomed)
					UpsertCameraKey(role.Name, _scrub, cam.RawZoom);

				if (moved || zoomed)
				{
					_recordBaseline[role.Name] = new RecordSample
					{
						Pos = e.Position, Rot = e.Rotation, Scale = e.Scale,
						Zoom = cam?.RawZoom ?? 0f, HasCamera = cam != null,
					};
					dirty = true;
				}
			}

			if (dirty)
				_asset.InvalidateEventOrder();
		}

		private Entity RoleEntity(string role)
		{
			if (string.IsNullOrEmpty(role))
				return null;
			var binding = _director.Bindings.FirstOrDefault(b => b.Role == role);
			if (binding == null || !binding.Entity.IsValid)
				return null;

			var scene = Core.Scene;
			if (scene == null)
				return null;

			var id = binding.Entity.GetPersistentId();
			var e = id != Guid.Empty ? scene.FindEntityByPersistentId(id) : null;
			if (e == null && !string.IsNullOrEmpty(binding.Entity.EntityName))
				e = scene.FindEntity(binding.Entity.EntityName);
			return e;
		}

		private void UpsertTransformKey(string role, float time, Xna.Vector2 pos, float rot, Xna.Vector2 scale)
		{
			var track = _asset.ParameterTracks.OfType<TimelineTransformTrack>().FirstOrDefault(t => t.TargetRole == role);
			if (track == null)
			{
				track = new TimelineTransformTrack { TargetRole = role };
				_asset.ParameterTracks.Add(track);
			}
			UpsertVector2(track.Position, time, pos);
			UpsertFloat(track.Rotation, time, rot);
			UpsertVector2(track.Scale, time, scale);
		}

		private void UpsertCameraKey(string role, float time, float zoom)
		{
			var track = _asset.ParameterTracks.OfType<TimelineCameraTrack>().FirstOrDefault(t => t.TargetRole == role);
			if (track == null)
			{
				track = new TimelineCameraTrack { TargetRole = role };
				_asset.ParameterTracks.Add(track);
			}
			UpsertFloat(track.Zoom, time, zoom);
		}

		private static void UpsertVector2(List<Vector2Keyframe> keys, float time, Xna.Vector2 value)
		{
			var existing = keys.FirstOrDefault(k => Math.Abs(k.Time - time) < 0.001f);
			if (existing != null) { existing.Value = value; return; }
			keys.Add(new Vector2Keyframe { Time = time, Value = value });
			keys.Sort((a, b) => a.Time.CompareTo(b.Time));
		}

		private static void UpsertFloat(List<FloatKeyframe> keys, float time, float value)
		{
			var existing = keys.FirstOrDefault(k => Math.Abs(k.Time - time) < 0.001f);
			if (existing != null) { existing.Value = value; return; }
			keys.Add(new FloatKeyframe { Time = time, Value = value });
			keys.Sort((a, b) => a.Time.CompareTo(b.Time));
		}

		private static bool Near(float a, float b) => Math.Abs(a - b) < 0.001f;
		private static bool NearV(Xna.Vector2 a, Xna.Vector2 b) => Near(a.X, b.X) && Near(a.Y, b.Y);

		#endregion

		#region Roles

		private void DrawRoles()
		{
			ImGui.TextColored(Muted, "Abstract actor slots this timeline drives. Bind each to a scene entity on the director.");
			ImGui.Spacing();

			for (var i = 0; i < _asset.Roles.Count; i++)
			{
				var role = _asset.Roles[i];
				ImGui.PushID(i);

				var name = role.Name ?? "";
				ImGui.SetNextItemWidth(200);
				if (Gui.InputText("Role", ref name, 64))
					role.Name = name;

				ImGui.SameLine();
				DrawRoleBindingCombo(role.Name);

				ImGui.SameLine();
				if (Gui.SmallButton("X"))
				{
					_asset.Roles.RemoveAt(i);
					ImGui.PopID();
					break;
				}
				ImGui.PopID();
			}

			if (Gui.Button("Add Role"))
				_asset.Roles.Add(new TimelineRole { Name = "Role" + _asset.Roles.Count });
		}

		private void DrawRoleBindingCombo(string role)
		{
			if (string.IsNullOrEmpty(role))
				return;

			var binding = _director.Bindings.FirstOrDefault(b => b.Role == role);
			var current = binding?.Entity.EntityName ?? "(unbound)";

			ImGui.SetNextItemWidth(220);
			if (Gui.BeginCombo($"##bind_{role}", current))
			{
				var scene = Core.Scene;
				if (scene != null)
				{
					foreach (var e in scene.Entities.OrderBy(e => e.Name))
					{
						if (Gui.Selectable(e.Name, current == e.Name))
							BindRole(role, e);
					}
				}
				ImGui.EndCombo();
			}
		}

		private void BindRole(string role, Entity entity)
		{
			var binding = _director.Bindings.FirstOrDefault(b => b.Role == role);
			if (binding == null)
			{
				binding = new RoleBinding { Role = role };
				_director.Bindings.Add(binding);
			}
			binding.Entity = Voltage.Serialization.EntityReference.From(entity);
		}

		#endregion

		#region Events

		private void DrawEvents()
		{
			ImGui.TextColored(Muted, "Fire component methods (AOT-safe) or broadcast signals at a time. Ranged clips fire a begin + end method.");
			ImGui.Spacing();

			var componentIds = TimelineDispatch.RegisteredMethods().Select(m => m.ComponentId).Distinct().OrderBy(x => x).ToArray();

			for (var i = 0; i < _asset.Events.Count; i++)
			{
				var e = _asset.Events[i];
				ImGui.PushID("evt" + i);
				ImGui.Separator();

				var name = e.Name ?? "";
				ImGui.SetNextItemWidth(160);
				if (Gui.InputText("Name", ref name, 64)) e.Name = name;

				ImGui.SameLine();
				ImGui.SetNextItemWidth(80);
				if (Gui.InputFloat("Time", ref e.Time, 0, 0, "%.2f")) _asset.InvalidateEventOrder();
				ImGui.SameLine();
				ImGui.SetNextItemWidth(80);
				Gui.InputFloat("Dur", ref e.Duration, 0, 0, "%.2f");

				// Target role
				ImGui.SetNextItemWidth(140);
				DrawRoleSelect("Role##evt", ref e.TargetRole);

				ImGui.SameLine();
				ImGui.SetNextItemWidth(150);
				var comp = e.TargetComponentId ?? "";
				if (Gui.BeginCombo("Component", comp))
				{
					foreach (var id in componentIds)
						if (Gui.Selectable(id, comp == id)) e.TargetComponentId = id;
					ImGui.EndCombo();
				}

				// Method dropdowns (begin/end) filtered by chosen component
				var methods = TimelineDispatch.RegisteredMethods()
					.Where(m => m.ComponentId == e.TargetComponentId).Select(m => m.Method).OrderBy(x => x).ToArray();
				ImGui.SameLine();
				ImGui.SetNextItemWidth(140);
				DrawMethodCombo("Begin", ref e.BeginMethod, methods);
				if (e.Duration > 0f)
				{
					ImGui.SameLine();
					ImGui.SetNextItemWidth(140);
					DrawMethodCombo("End", ref e.EndMethod, methods);
				}

				var bc = e.BroadcastMessage ?? "";
				ImGui.SetNextItemWidth(160);
				if (Gui.InputText("Broadcast", ref bc, 64)) e.BroadcastMessage = string.IsNullOrWhiteSpace(bc) ? null : bc;

				ImGui.SameLine();
				var skip = (int)e.OnSkip;
				ImGui.SetNextItemWidth(150);
				if (Gui.Combo("On Skip", ref skip, SkipNames, SkipNames.Length)) e.OnSkip = (SkipBehavior)skip;
				if (ImGui.IsItemHovered())
					ImGui.SetTooltip("Skip = dropped when the player skips.\nFireImmediately = still runs (give item, spawn boss).");

				ImGui.SameLine();
				if (Gui.SmallButton("Remove")) { _asset.Events.RemoveAt(i); _asset.InvalidateEventOrder(); ImGui.PopID(); break; }

				ImGui.PopID();
			}

			ImGui.Separator();
			if (Gui.Button("Add Event"))
			{
				_asset.Events.Add(new TimelineEventClip { Name = "event", Time = _scrub });
				_asset.InvalidateEventOrder();
			}
		}

		private void DrawMethodCombo(string label, ref string value, string[] methods)
		{
			var cur = value ?? "(none)";
			if (Gui.BeginCombo(label, cur))
			{
				if (Gui.Selectable("(none)", value == null)) value = null;
				foreach (var m in methods)
					if (Gui.Selectable(m, value == m)) value = m;
				ImGui.EndCombo();
			}
		}

		private void DrawRoleSelect(string label, ref string role)
		{
			var cur = role ?? "(none)";
			if (Gui.BeginCombo(label, cur))
			{
				if (Gui.Selectable("(none)", role == null)) role = null;
				foreach (var r in _asset.Roles)
					if (Gui.Selectable(r.Name, role == r.Name)) role = r.Name;
				ImGui.EndCombo();
			}
		}

		#endregion

		#region Transform tracks

		private void DrawTransformTracks()
		{
			ImGui.TextColored(Muted, "Keyframe an entity's transform over time. Scrub to preview.");
			ImGui.Spacing();

			var transformTracks = _asset.ParameterTracks.OfType<TimelineTransformTrack>().ToList();
			for (var i = 0; i < transformTracks.Count; i++)
			{
				var track = transformTracks[i];
				ImGui.PushID("tt" + i);
				ImGui.Separator();

				ImGui.SetNextItemWidth(160);
				DrawRoleSelect("Target Role", ref track.TargetRole);
				ImGui.SameLine();
				if (Gui.SmallButton("Remove Track")) { _asset.ParameterTracks.Remove(track); ImGui.PopID(); break; }

				DrawVector2Keys("Position", track.Position);
				DrawFloatKeys("Rotation (rad)", track.Rotation);
				DrawVector2Keys("Scale", track.Scale);

				ImGui.PopID();
			}

			ImGui.Separator();
			if (Gui.Button("Add Transform Track"))
				_asset.ParameterTracks.Add(new TimelineTransformTrack { TargetRole = _asset.Roles.FirstOrDefault()?.Name });
		}

		private void DrawVector2Keys(string label, List<Vector2Keyframe> keys)
		{
			if (!Gui.TreeNode($"{label} ({keys.Count} keys)"))
				return;

			for (var i = 0; i < keys.Count; i++)
			{
				var k = keys[i];
				ImGui.PushID(label + i);
				ImGui.SetNextItemWidth(70);
				MarkEditedTime(Gui.InputFloat("t", ref k.Time, 0, 0, "%.2f"));
				ImGui.SameLine();
				var v = new Num.Vector2(k.Value.X, k.Value.Y);
				ImGui.SetNextItemWidth(140);
				if (Gui.InputFloat2("val", ref v)) k.Value = new Microsoft.Xna.Framework.Vector2(v.X, v.Y);
				ImGui.SameLine();
				var ease = (int)k.Ease;
				ImGui.SetNextItemWidth(130);
				if (Gui.Combo("ease", ref ease, EaseNames, EaseNames.Length)) k.Ease = (EaseType)ease;
				ImGui.SameLine();
				if (Gui.SmallButton("x")) { keys.RemoveAt(i); ImGui.PopID(); break; }
				ImGui.PopID();
			}
			if (Gui.SmallButton($"+ key##{label}"))
			{
				var last = keys.Count > 0 ? keys[keys.Count - 1].Value : default;
				keys.Add(new Vector2Keyframe { Time = _scrub, Value = last });
				keys.Sort((a, b) => a.Time.CompareTo(b.Time));
			}
			ImGui.TreePop();
		}

		private void DrawFloatKeys(string label, List<FloatKeyframe> keys)
		{
			if (!Gui.TreeNode($"{label} ({keys.Count} keys)"))
				return;

			for (var i = 0; i < keys.Count; i++)
			{
				var k = keys[i];
				ImGui.PushID(label + i);
				ImGui.SetNextItemWidth(70);
				MarkEditedTime(Gui.InputFloat("t", ref k.Time, 0, 0, "%.2f"));
				ImGui.SameLine();
				ImGui.SetNextItemWidth(90);
				Gui.InputFloat("val", ref k.Value, 0, 0, "%.3f");
				ImGui.SameLine();
				var ease = (int)k.Ease;
				ImGui.SetNextItemWidth(130);
				if (Gui.Combo("ease", ref ease, EaseNames, EaseNames.Length)) k.Ease = (EaseType)ease;
				ImGui.SameLine();
				if (Gui.SmallButton("x")) { keys.RemoveAt(i); ImGui.PopID(); break; }
				ImGui.PopID();
			}
			if (Gui.SmallButton($"+ key##{label}"))
			{
				var last = keys.Count > 0 ? keys[keys.Count - 1].Value : 0f;
				keys.Add(new FloatKeyframe { Time = _scrub, Value = last });
				keys.Sort((a, b) => a.Time.CompareTo(b.Time));
			}
			ImGui.TreePop();
		}

		#endregion

		#region Tint / sprite / activation / audio tracks

		private void DrawTintTracks()
		{
			ImGui.TextColored(Muted, "Fade or tint every renderable on an actor. Alpha and RGB are independent.");
			ImGui.Spacing();

			var tracks = _asset.ParameterTracks.OfType<TimelineTintTrack>().ToList();
			for (var i = 0; i < tracks.Count; i++)
			{
				var track = tracks[i];
				ImGui.PushID("tint" + i);
				ImGui.Separator();

				ImGui.SetNextItemWidth(160);
				DrawRoleSelect("Target Role", ref track.TargetRole);
				ImGui.SameLine();
				if (Gui.SmallButton("Remove Track")) { _asset.ParameterTracks.Remove(track); ImGui.PopID(); break; }

				DrawFloatKeys("Alpha (0-1)", track.Alpha);
				DrawColorKeys("Tint", track.Tint);

				ImGui.PopID();
			}

			ImGui.Separator();
			if (Gui.Button("Add Tint Track"))
				_asset.ParameterTracks.Add(new TimelineTintTrack { TargetRole = _asset.Roles.FirstOrDefault()?.Name });
		}

		private void DrawColorKeys(string label, List<ColorKeyframe> keys)
		{
			if (!Gui.TreeNode($"{label} ({keys.Count} keys)"))
				return;

			for (var i = 0; i < keys.Count; i++)
			{
				var k = keys[i];
				ImGui.PushID(label + i);
				ImGui.SetNextItemWidth(70);
				MarkEditedTime(Gui.InputFloat("t", ref k.Time, 0, 0, "%.2f"));
				ImGui.SameLine();

				var rgb = new Num.Vector3(k.Value.R / 255f, k.Value.G / 255f, k.Value.B / 255f);
				ImGui.SetNextItemWidth(160);
				if (Gui.ColorEdit3("rgb", ref rgb, ImGuiColorEditFlags.NoInputs))
					k.Value = new Xna.Color(rgb.X, rgb.Y, rgb.Z);

				ImGui.SameLine();
				var ease = (int)k.Ease;
				ImGui.SetNextItemWidth(130);
				if (Gui.Combo("ease", ref ease, EaseNames, EaseNames.Length)) k.Ease = (EaseType)ease;
				ImGui.SameLine();
				if (Gui.SmallButton("x")) { keys.RemoveAt(i); ImGui.PopID(); break; }
				ImGui.PopID();
			}

			if (Gui.SmallButton($"+ key##{label}"))
			{
				keys.Add(new ColorKeyframe { Time = _scrub, Value = Xna.Color.White });
				keys.Sort((a, b) => a.Time.CompareTo(b.Time));
			}
			ImGui.TreePop();
		}

		private void DrawSpriteTracks()
		{
			ImGui.TextColored(Muted,
				"Choose which SpriteAnimator animation plays over a span. Scrubbing selects the clip but does\n" +
				"not seek inside it.");
			ImGui.Spacing();

			var tracks = _asset.ParameterTracks.OfType<TimelineSpriteTrack>().ToList();
			for (var i = 0; i < tracks.Count; i++)
			{
				var track = tracks[i];
				ImGui.PushID("sprite" + i);
				ImGui.Separator();

				ImGui.SetNextItemWidth(160);
				DrawRoleSelect("Target Role", ref track.TargetRole);
				ImGui.SameLine();
				if (Gui.SmallButton("Remove Track")) { _asset.ParameterTracks.Remove(track); ImGui.PopID(); break; }

				for (var c = 0; c < track.Clips.Count; c++)
				{
					var clip = track.Clips[c];
					ImGui.PushID(c);
					ImGui.SetNextItemWidth(70);
					MarkEditedTime(Gui.InputFloat("t", ref clip.Time, 0, 0, "%.2f"));
					ImGui.SameLine();
					ImGui.SetNextItemWidth(70);
					Gui.InputFloat("len", ref clip.Duration, 0, 0, "%.2f");
					ImGui.SameLine();
					var name = clip.Animation ?? string.Empty;
					ImGui.SetNextItemWidth(130);
					if (Gui.InputText("anim", ref name, 64)) clip.Animation = name;
					ImGui.SameLine();
					var loop = (int)clip.Loop;
					ImGui.SetNextItemWidth(110);
					if (Gui.Combo("loop", ref loop, LoopNames, LoopNames.Length))
						clip.Loop = (SpriteAnimator.LoopMode)loop;
					ImGui.SameLine();
					if (Gui.SmallButton("x")) { track.Clips.RemoveAt(c); ImGui.PopID(); break; }
					ImGui.PopID();
				}

				if (Gui.SmallButton("+ clip"))
					track.Clips.Add(new TimelineSpriteClip { Time = _scrub, Duration = 1f });

				ImGui.PopID();
			}

			ImGui.Separator();
			if (Gui.Button("Add Sprite Track"))
				_asset.ParameterTracks.Add(new TimelineSpriteTrack { TargetRole = _asset.Roles.FirstOrDefault()?.Name });
		}

		private void DrawActivationTracks()
		{
			ImGui.TextColored(Muted, "Show an actor only during these spans. Scrubs correctly in both directions.");
			ImGui.Spacing();

			var tracks = _asset.ParameterTracks.OfType<TimelineActivationTrack>().ToList();
			for (var i = 0; i < tracks.Count; i++)
			{
				var track = tracks[i];
				ImGui.PushID("act" + i);
				ImGui.Separator();

				ImGui.SetNextItemWidth(160);
				DrawRoleSelect("Target Role", ref track.TargetRole);
				ImGui.SameLine();
				if (Gui.SmallButton("Remove Track")) { _asset.ParameterTracks.Remove(track); ImGui.PopID(); break; }

				var outside = track.ActiveOutsideRanges;
				if (Gui.Checkbox("Active outside ranges (invert)", ref outside))
					track.ActiveOutsideRanges = outside;

				for (var r = 0; r < track.Ranges.Count; r++)
				{
					var range = track.Ranges[r];
					ImGui.PushID(r);
					ImGui.SetNextItemWidth(70);
					MarkEditedTime(Gui.InputFloat("t", ref range.Time, 0, 0, "%.2f"));
					ImGui.SameLine();
					ImGui.SetNextItemWidth(70);
					Gui.InputFloat("len", ref range.Duration, 0, 0, "%.2f");
					ImGui.SameLine();
					if (Gui.SmallButton("x")) { track.Ranges.RemoveAt(r); ImGui.PopID(); break; }
					ImGui.PopID();
				}

				if (Gui.SmallButton("+ range"))
					track.Ranges.Add(new TimelineActiveRange { Time = _scrub, Duration = 1f });

				ImGui.PopID();
			}

			ImGui.Separator();
			if (Gui.Button("Add Activation Track"))
				_asset.ParameterTracks.Add(new TimelineActivationTrack { TargetRole = _asset.Roles.FirstOrDefault()?.Name });
		}

		private void DrawAudioTracks()
		{
			ImGui.TextColored(Muted,
				"One-shots fired as the playhead passes them. Scrubbing does not retrigger them.");
			ImGui.Spacing();

			var tracks = _asset.ParameterTracks.OfType<TimelineAudioTrack>().ToList();
			for (var i = 0; i < tracks.Count; i++)
			{
				var track = tracks[i];
				ImGui.PushID("audio" + i);
				ImGui.Separator();

				if (Gui.SmallButton("Remove Track")) { _asset.ParameterTracks.Remove(track); ImGui.PopID(); break; }

				for (var c = 0; c < track.Clips.Count; c++)
				{
					var clip = track.Clips[c];
					ImGui.PushID(c);
					ImGui.SetNextItemWidth(70);
					MarkEditedTime(Gui.InputFloat("t", ref clip.Time, 0, 0, "%.2f"));
					ImGui.SameLine();

					var label = clip.Clip.IsValid ? (clip.Clip.AssetName ?? "clip") : "None (audio)";
					ImGui.SetNextItemWidth(150);
					Gui.Button($"{label}##clip", new Num.Vector2(150, 0));
					if (ImGui.BeginDragDropTarget())
					{
						var payload = ImGui.AcceptDragDropPayload(AssetBrowserWindow.DragDropPayloadId);
						bool accepted;
						unsafe { accepted = payload.NativePtr != null; }
						if (accepted && !AssetBrowserWindow.DraggedReference.IsEmpty)
						{
							var dr = AssetBrowserWindow.DraggedReference;
							AssetBrowserWindow.DraggedReference = Voltage.Editor.Assets.AssetReference.Empty;
							clip.Clip = new Voltage.Serialization.AssetReference
							{
								AssetGuid = dr.Guid,
								AssetPath = dr.HintPath,
								AssetName = Path.GetFileNameWithoutExtension(dr.HintPath),
							};
							if (string.IsNullOrEmpty(clip.Name))
								clip.Name = clip.Clip.AssetName;
						}
						ImGui.EndDragDropTarget();
					}

					ImGui.SameLine();
					ImGui.SetNextItemWidth(70);
					Gui.InputFloat("vol", ref clip.Volume, 0, 0, "%.2f");
					ImGui.SameLine();
					var bus = clip.Bus ?? "SFX";
					ImGui.SetNextItemWidth(80);
					if (Gui.InputText("bus", ref bus, 32)) clip.Bus = bus;
					ImGui.SameLine();
					if (Gui.SmallButton("x")) { track.Clips.RemoveAt(c); ImGui.PopID(); break; }
					ImGui.PopID();
				}

				if (Gui.SmallButton("+ sound"))
					track.Clips.Add(new TimelineAudioClip { Time = _scrub });

				ImGui.PopID();
			}

			ImGui.Separator();
			if (Gui.Button("Add Audio Track"))
				_asset.ParameterTracks.Add(new TimelineAudioTrack());
		}

		#endregion

		#region Spawns

		private void DrawSpawns()
		{
			ImGui.TextColored(Muted, "Spawn a prefab for a time range, bound to a role other tracks can target.");
			ImGui.Spacing();

			for (var i = 0; i < _asset.SpawnClips.Count; i++)
			{
				var s = _asset.SpawnClips[i];
				ImGui.PushID("sp" + i);
				ImGui.Separator();

				var role = s.SpawnRole ?? "";
				ImGui.SetNextItemWidth(140);
				if (Gui.InputText("Spawn Role", ref role, 64)) s.SpawnRole = role;

				ImGui.SameLine();
				ImGui.SetNextItemWidth(80);
				Gui.InputFloat("Time", ref s.Time, 0, 0, "%.2f");
				ImGui.SameLine();
				ImGui.SetNextItemWidth(80);
				Gui.InputFloat("Dur", ref s.Duration, 0, 0, "%.2f");

				ImGui.SameLine();
				Gui.Checkbox("Keep after", ref s.KeepAfterTimeline);

				ImGui.TextColored(Muted, $"Prefab: {(s.Prefab.IsValid ? s.Prefab.PrefabName : "(assign in inspector — drag a prefab)")}");

				ImGui.SameLine();
				if (Gui.SmallButton("Remove")) { _asset.SpawnClips.RemoveAt(i); ImGui.PopID(); break; }
				ImGui.PopID();
			}

			ImGui.Separator();
			if (Gui.Button("Add Spawn"))
				_asset.SpawnClips.Add(new TimelineSpawnClip { SpawnRole = "Spawn" + _asset.SpawnClips.Count, Time = _scrub, Duration = 1f });
		}

		#endregion
	}
}
