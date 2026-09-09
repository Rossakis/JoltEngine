using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using Microsoft.Xna.Framework.Graphics;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.ImGuiCore;
using Num = System.Numerics;

namespace Voltage.Editor.FilePickers
{
	/// <summary>
	/// Picks how an Aseprite file becomes a tile image: a single flattened frame (choose layers + frame), or an
	/// ANIMATED tile built from a frame range (with a live play preview). The chosen layers apply to both.
	/// </summary>
	public sealed class AsepriteLayerPopup
	{
		private readonly string _popupId;

		private string _filePath;
		private readonly List<string> _layers = new();
		private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
		private int _frame;
		private int _frameCount = 1;

		private bool _animate;
		private int _animStart;
		private int _animEnd;

		private bool _syncNewLayers;

		private bool _openNextFrame;

		// Live preview: one texture per frame in the range, rebuilt when the range or layer set changes.
		private readonly List<Texture2D> _frameTextures = new();
		private readonly List<IntPtr> _frameIds = new();
		private float _previewDuration = 0.1f;
		private string _previewKey = "";

		public string FilePath => _filePath;

		/// <summary>Layers to composite. Empty means "all visible layers".</summary>
		public List<string> SelectedLayers { get; } = new();

		/// <summary>The unticked layers. Empty when everything is ticked.</summary>
		public List<string> ExcludedLayers { get; } = new();

		/// <summary>True when the caller should store <see cref="ExcludedLayers"/> rather than <see cref="SelectedLayers"/>.</summary>
		public bool SyncNewLayers { get; private set; }

		/// <summary>Offer the "sync layers added later" switch. Only for callers that can store an exclusion list.</summary>
		public bool AllowSyncNewLayers { get; init; }

		/// <summary>Zero-based frame index (single-frame mode).</summary>
		public int Frame { get; private set; }

		/// <summary>True when the user confirmed an animated tile.</summary>
		public bool Animate { get; private set; }
		public int AnimStart { get; private set; }
		public int AnimEnd { get; private set; }

		public AsepriteLayerPopup(string popupId) => _popupId = popupId;

		/// <summary>Reads the file's layers and frame count and arms the popup. False when the file cannot be read.</summary>
		/// <param name="savedLayers">The layers to tick, or - when <paramref name="syncsNewLayers"/> is set - the ones to leave unticked.</param>
		public bool Open(string absolutePath, IEnumerable<string> savedLayers = null, int frame = 0,
			bool syncsNewLayers = false)
		{
			_layers.Clear();
			_selected.Clear();

			try
			{
				// Opening the picker re-imports the file, so the cached parse must go.
				Core.Content?.EvictCachedAsset(absolutePath);
				Core.Scene?.Content?.EvictCachedAsset(absolutePath);

				var file = Core.Content.LoadAsepriteFile(absolutePath);
				if (file == null)
					return false;

				foreach (var layer in file.Layers)
				{
					if (!string.IsNullOrEmpty(layer.Name))
						_layers.Add(layer.Name);
				}

				_frameCount = Math.Max(1, file.Frames.Count);
			}
			catch (Exception e)
			{
				EditorDebug.Log($"Aseprite: could not read '{absolutePath}': {e.Message}", "Tileset");
				return false;
			}

			var saved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (savedLayers != null)
			{
				foreach (var layer in savedLayers)
					saved.Add(layer);
			}

			_syncNewLayers = AllowSyncNewLayers && (syncsNewLayers || saved.Count == 0);

			// An exclusion list ticks the complement, so a layer added since the last visit shows up already ticked.
			foreach (var layer in _layers)
			{
				if (_syncNewLayers ? !saved.Contains(layer) : saved.Contains(layer))
					_selected.Add(layer);
			}

			_filePath = absolutePath;
			_frame = Math.Clamp(frame, 0, _frameCount - 1);
			_animate = false;
			_animStart = 0;
			_animEnd = _frameCount - 1;
			FreePreviews();
			_openNextFrame = true;
			return true;
		}

		/// <summary>Draws the popup. Returns true on the single frame the user confirms.</summary>
		public bool Draw()
		{
			// Build/refresh the per-frame preview textures before the layout pass so the modal only draws them.
			EnsurePreview();

			if (_openNextFrame)
			{
				ImGui.OpenPopup(_popupId);
				_openNextFrame = false;
			}

			var center = ImGui.GetMainViewport().GetCenter();
			ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
			ImGui.SetNextWindowSize(new Num.Vector2(440, 560), ImGuiCond.Appearing);

			var open = true;
			if (!Gui.BeginPopupModal(_popupId, ref open, ImGuiWindowFlags.NoResize))
				return false;

			ImGui.TextUnformatted(Path.GetFileName(_filePath ?? string.Empty));
			ImGui.Separator();

			Gui.Checkbox("Create as animated tile", ref _animate);
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Packs a frame range into an animated tile instead of flattening one frame.");

			if (_animate)
				DrawAnimationRange();
			else
				DrawSingleFrame();

			ImGui.Spacing();
			DrawLayers();

			ImGui.Separator();

			var confirmed = false;
			if (Gui.Button("Use this", new Num.Vector2(120, 0)))
			{
				CaptureLayers();

				Animate = _animate;
				if (_animate)
				{
					AnimStart = Math.Min(_animStart, _animEnd);
					AnimEnd = Math.Max(_animStart, _animEnd);
				}
				else
				{
					Frame = _frame;
				}

				confirmed = true;
				FreePreviews();
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();

			if (Gui.Button("Cancel", new Num.Vector2(120, 0)))
			{
				FreePreviews();
				ImGui.CloseCurrentPopup();
			}

			Gui.EndPopup();

			// Dismissed by the X or a click outside.
			if (!open)
				FreePreviews();

			return confirmed;
		}

		private void DrawSingleFrame()
		{
			ImGui.TextUnformatted("Frame");
			ImGui.SetNextItemWidth(-1);

			var frame = _frame;
			if (Gui.SliderInt("##frame", ref frame, 0, _frameCount - 1, $"%d / {_frameCount - 1}"))
				_frame = Math.Clamp(frame, 0, _frameCount - 1);
		}

		private void DrawAnimationRange()
		{
			var start = _animStart;
			var end = _animEnd;

			ImGui.TextUnformatted("Start / end frame");
			ImGui.SetNextItemWidth(-1);
			if (Gui.SliderInt("##animstart", ref start, 0, _frameCount - 1, $"start %d / {_frameCount - 1}"))
				_animStart = Math.Clamp(start, 0, _frameCount - 1);

			ImGui.SetNextItemWidth(-1);
			if (Gui.SliderInt("##animend", ref end, 0, _frameCount - 1, $"end %d / {_frameCount - 1}"))
				_animEnd = Math.Clamp(end, 0, _frameCount - 1);

			var lo = Math.Min(_animStart, _animEnd);
			var hi = Math.Max(_animStart, _animEnd);
			ImGui.TextDisabled($"{hi - lo + 1} frames");

			DrawPlayPreview(lo);
		}

		/// <summary>Rebuilds the per-frame preview textures when the frame range or layer set changes.</summary>
		private void EnsurePreview()
		{
			if (!_animate)
			{
				if (_frameIds.Count > 0)
					FreePreviews();
				return;
			}

			var lo = Math.Min(_animStart, _animEnd);
			var hi = Math.Max(_animStart, _animEnd);
			var key = $"{lo}:{hi}:{string.Join(',', OrderedSelectedLayers())}";
			if (key != _previewKey)
				RebuildPreview(lo, hi, key);
		}

		private void DrawPlayPreview(int lo)
		{
			const float size = 96f;
			var origin = ImGui.GetCursorScreenPos();
			var drawList = ImGui.GetWindowDrawList();
			var boxMax = new Num.Vector2(origin.X + size, origin.Y + size);
			drawList.AddRectFilled(origin, boxMax, ImGui.GetColorU32(new Num.Vector4(0.15f, 0.15f, 0.15f, 1f)));

			if (_frameIds.Count > 0)
			{
				var idx = (int)(ImGui.GetTime() / _previewDuration) % _frameIds.Count;

				ImGui.Image(_frameIds[idx], new Num.Vector2(size, size));
				drawList.AddRect(origin, boxMax, ImGui.GetColorU32(new Num.Vector4(0.5f, 0.5f, 0.5f, 1f)));

				ImGui.SameLine();
				ImGui.TextDisabled($"playing (frame {lo + idx})");
			}
			else
			{
				ImGui.Dummy(new Num.Vector2(size, size));
				drawList.AddRect(origin, boxMax, ImGui.GetColorU32(new Num.Vector4(0.5f, 0.5f, 0.5f, 1f)));
			}
		}

		private IEnumerable<string> OrderedSelectedLayers()
		{
			foreach (var layer in _layers)
			{
				if (_selected.Contains(layer))
					yield return layer;
			}
		}

		private void DrawLayers()
		{
			ImGui.TextUnformatted("Layers");

			if (AllowSyncNewLayers)
			{
				Gui.Checkbox("Sync layers added later", ref _syncNewLayers);

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip(
						"On: the layers you UNTICK are remembered as the ones to leave out, so any layer added to\n" +
						"the file after today is composited automatically and Sync Changes always picks it up.\n\n" +
						"Off: the layers you TICK are remembered as the whole list. It is a snapshot of the file as\n" +
						"it is now, so a layer added later is silently left out until you come back here.");
				}
			}

			var all = _selected.Count == 0 || _selected.Count >= _layers.Count;

			if (all)
				ImGui.TextDisabled("All visible layers.");
			else
				ImGui.TextDisabled($"{_selected.Count} of {_layers.Count} layers composited together.");

			// With every layer in, both modes store the same thing, so the switch changes nothing.
			ImGui.TextDisabled(_syncNewLayers || all
				? "Layers added to the file later are included automatically."
				: "Layers added to the file later are NOT included until you tick them here.");

			ImGui.BeginChild("layers", new Num.Vector2(0, 150), true);

			// Layer names repeat across groups, so the name alone is not a unique ImGui ID.
			for (var i = 0; i < _layers.Count; i++)
			{
				var layer = _layers[i];
				var ticked = _selected.Contains(layer);
				if (Gui.Checkbox($"{layer}##layer{i}", ref ticked))
				{
					if (ticked)
						_selected.Add(layer);
					else
						_selected.Remove(layer);

					// The preview depends on the layer set, so force a rebuild.
					_previewKey = "";
				}
			}

			if (_layers.Count == 0)
				ImGui.TextDisabled("This file reports no named layers.");

			ImGui.EndChild();

			if (Gui.Button("Select all"))
			{
				foreach (var layer in _layers)
					_selected.Add(layer);
				_previewKey = "";
			}

			ImGui.SameLine();

			if (Gui.Button("Clear"))
			{
				_selected.Clear();
				_previewKey = "";
			}
		}

		private void CaptureLayers()
		{
			SelectedLayers.Clear();
			ExcludedLayers.Clear();
			SyncNewLayers = AllowSyncNewLayers && _syncNewLayers;

			// File order, not tick order: that is how Aseprite composites, so the result matches the artist's view.
			foreach (var layer in _layers)
			{
				if (_selected.Contains(layer))
					SelectedLayers.Add(layer);
				else
					ExcludedLayers.Add(layer);
			}

			// Ticking nothing means "all visible layers", not "no layers", so it leaves nothing out.
			if (_selected.Count == 0)
				ExcludedLayers.Clear();

			// Everything ticked saves as nothing ticked: same pixels today, and it keeps following the file.
			if (_layers.Count > 0 && SelectedLayers.Count == _layers.Count)
				SelectedLayers.Clear();
		}

		private void RebuildPreview(int lo, int hi, string key)
		{
			FreePreviews();
			_previewKey = key;

			var manager = Core.GetGlobalManager<ImGuiManager>();
			if (manager == null)
				return;

			try
			{
				var file = Core.Content.LoadAsepriteFile(_filePath);
				if (file == null)
					return;

				var layers = new List<string>(OrderedSelectedLayers()).ToArray();

				// The first frame's duration drives playback speed (Aseprite stores per-frame durations in ms).
				if (file.Frames.Count > lo)
					_previewDuration = Math.Max(0.01f, file.Frames[lo].Duration / 1000f);

				// One texture per frame via the proven GetTextureFromLayers path (frameNumber is 1-based).
				for (var f = lo; f <= hi; f++)
				{
					var tex = file.GetTextureFromLayers(f + 1, true, false, layers);
					if (tex == null)
						continue;

					_frameTextures.Add(tex);
					_frameIds.Add(manager.BindTexture(tex));
				}
			}
			catch (Exception e)
			{
				EditorDebug.Log($"Aseprite: preview build failed: {e.Message}", "Tileset");
			}
		}

		private void FreePreviews()
		{
			var manager = Core.GetGlobalManager<ImGuiManager>();
			foreach (var id in _frameIds)
			{
				if (id != IntPtr.Zero)
					manager?.UnbindTexture(id);
			}
			foreach (var tex in _frameTextures)
				tex?.Dispose();

			_frameIds.Clear();
			_frameTextures.Clear();
			_previewKey = "";
		}
	}
}
