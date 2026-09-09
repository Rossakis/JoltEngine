using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using Voltage.Editor.Assets;
using Voltage.Editor.Gizmos;
using Voltage.Editor.Hotkeys;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.Persistence;
using Voltage.Editor.Undo.ComponentActions;
using Voltage.Editor.Undo.Core;
using Voltage.Editor.Utils;
using Voltage.Tilesets;
using EngineAssetReference = Voltage.Serialization.AssetReference;
using Num = System.Numerics;

namespace Voltage.Editor.Tools.Tilemap
{
	/// <summary>UI for tile painting: pick the layer, the tileset, the stamp and the active tool. Drives the shared <see cref="TilePaintTool"/>.</summary>
	public partial class TilePaletteWindow
	{
		public bool IsOpen;

		/// <summary>Window (or one of its children) has keyboard focus, so its shortcuts own Q/E/Z/X.</summary>
		public bool IsFocused { get; private set; }

		private static readonly string[] TilesetExtensions = { TilesetAssetIO.FileExtension };

		private TilePaintTool _tool;

		private TilesetRuntime _tileset;
		private string _tilesetPath;
		private IntPtr _atlasTextureId;

		// Cache generation the atlas was loaded at - a re-save under the same path moves it, which a path compare misses.
		private int _tilesetGeneration = -1;

		private float _zoom = 2f;

		private bool _openNewLayerPopup;
		private string _newLayerName = "Tilemap";

		private string _layerSearch = string.Empty;

		// Pruned every frame against the live scene, so a deleted layer cannot linger in the pick.
		private readonly HashSet<TilemapRenderer> _selectedLayers = new();
		private TilemapRenderer _layerRangeAnchor;
		private int _moveToOrder = 1;

		private readonly DragReorderStrip _layerReorder = new(LayerOrderPayloadId)
		{
			// Faster than the shared default: this scrolls the whole palette, so there is more travel to cover.
			ScrollSpeed = 900f,
			ScrollMargin = 52f,
		};

		private const string LayerOrderPayloadId = "TILEMAP_LAYER_ORDER";

		// Atlas selection, in tile coordinates. Several boxes can be active at once; the gaps between them
		// are preserved as holes in the stamp so the tiles land with the same spacing they were picked at.
		private readonly List<SelBox> _boxes = new();
		private bool _isSelecting;
		private Point2 _selectionAnchor;
		private Point2 _selectionEnd;
		private bool _dragAdditive;
		private bool _dragExtend;

		private bool _hasSelection => _boxes.Count > 0;

		private readonly struct SelBox
		{
			public readonly int MinX;
			public readonly int MinY;
			public readonly int MaxX;
			public readonly int MaxY;

			public SelBox(Point2 a, Point2 b)
			{
				MinX = Math.Min(a.X, b.X);
				MinY = Math.Min(a.Y, b.Y);
				MaxX = Math.Max(a.X, b.X);
				MaxY = Math.Max(a.Y, b.Y);
			}

			private SelBox(int minX, int minY, int maxX, int maxY)
			{
				MinX = minX;
				MinY = minY;
				MaxX = maxX;
				MaxY = maxY;
			}

			public int Width => MaxX - MinX + 1;
			public int Height => MaxY - MinY + 1;
			public int Count => Width * Height;

			public bool Contains(Point2 c) => c.X >= MinX && c.X <= MaxX && c.Y >= MinY && c.Y <= MaxY;

			/// <summary>Touching counts as overlapping, so adjacent picks merge into one block.</summary>
			public bool Overlaps(SelBox o) => MinX <= o.MaxX && o.MinX <= MaxX && MinY <= o.MaxY && o.MinY <= MaxY;

			public SelBox Union(SelBox o) => new(
				Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY),
				Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));
		}

		private readonly struct Point2
		{
			public readonly int X;
			public readonly int Y;

			public Point2(int x, int y)
			{
				X = x;
				Y = y;
			}
		}

		public void Draw(TilePaintTool tool, TilesetEditorWindow tilesetEditor, GizmoSelectionManager cursor)
		{
			_tool = tool;

			if (!IsOpen)
			{
				IsFocused = false;
				return;
			}

			ImGui.SetNextWindowSize(new Num.Vector2(420, 640), ImGuiCond.FirstUseEver);

			// Focused windows only. IsFocused is last frame's answer, which is what we want: the click
			// that focuses the window comes before any wheel it should catch.
			var windowFlags = IsFocused ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoScrollWithMouse;

			// "###TilePaletteWindow" pins the ImGui ID independently of the label; it keys the docking entry in the
			// layout .ini. Without it the docked position is not restored.
			var open = IsOpen;
			if (!Gui.Begin("Tile Palette ###TilePaletteWindow", ref open, windowFlags))
			{
				IsOpen = open;
				IsFocused = false;
				Gui.End();
				return;
			}

			IsOpen = open;
			IsFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);

			tool.DropDeadTarget();

			EnsureTilesetLoaded(tool);

			DrawModeBanner(cursor);
			ImGui.Spacing();

			DrawIssues(tool);
			ImGui.Spacing();

			DrawTargetSelector(tool);
			ImGui.Spacing();
			ImGui.Separator();

			DrawTilesetSelector(tool, tilesetEditor);
			ImGui.Separator();

			DrawLayerOrder(tool);
			ImGui.Separator();

			DrawEditModeSwitch(tool);
			ImGui.Separator();

			DrawToolButtons(tool);
			DrawGridOptions(tool);
			ImGui.Separator();

			if (tool.EditMode == TileEditMode.Collision)
				DrawCollisionControls(tool);
			else
				DrawAtlas(tool);

			Gui.End();

			// Must be outside Begin/End to be modal.
			DrawNewLayerPopup(tool);
		}

		private void DrawNewLayerPopup(TilePaintTool tool)
		{
			if (_openNewLayerPopup)
			{
				ImGui.OpenPopup("new-tilemap-layer");
				_openNewLayerPopup = false;
			}

			var center = ImGui.GetMainViewport().GetCenter();
			ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));

			// Fixed size, and deliberately NOT AlwaysAutoResize: the fill-width input sizes to the window while the
			// window auto-sizes to its content, so the two chase each other smaller every frame.
			ImGui.SetNextWindowSize(new Num.Vector2(420, 190), ImGuiCond.Always);

			var open = true;
			if (!Gui.BeginPopupModal("new-tilemap-layer", ref open, ImGuiWindowFlags.NoResize))
				return;

			ImGui.TextUnformatted("New tilemap layer");
			ImGui.Separator();

			ImGui.TextUnformatted("Name:");
			ImGui.SetNextItemWidth(-1);

			var entered = Gui.InputText("##newlayername", ref _newLayerName, 128,
				ImGuiInputTextFlags.EnterReturnsTrue);

			var nameValid = !string.IsNullOrWhiteSpace(_newLayerName);

			ImGui.TextDisabled(_tileset?.Asset != null
				? $"Tileset: {_tileset.Asset.Name ?? "(unnamed)"}"
				: "No tileset selected - the layer will be created without one.");

			ImGui.Separator();

			ImGui.BeginDisabled(!nameValid);
			var confirm = Gui.Button("Create", new Num.Vector2(120, 0)) || (entered && nameValid);
			ImGui.EndDisabled();

			ImGui.SameLine();

			if (Gui.Button("Cancel", new Num.Vector2(120, 0)))
				ImGui.CloseCurrentPopup();

			if (confirm && nameValid)
			{
				var reference = _tilesetPath != null
					? TilemapSceneUtils.ReferenceFor(_tilesetPath)
					: default;

				var created = TilemapSceneUtils.CreateTilemapLayer(reference, null, _newLayerName.Trim());
				if (created != null)
				{
					tool.Target = created;
					SyncTilesetFromTarget(tool);
				}

				ImGui.CloseCurrentPopup();
			}

			Gui.EndPopup();
		}

		private void DrawModeBanner(GizmoSelectionManager cursor)
		{
			if (cursor.SelectionMode == CursorSelectionMode.TilePaint)
			{
				ImGui.TextColored(new Num.Vector4(0.4f, 0.9f, 0.5f, 1f), "Tile cursor active - paint in the game view.");
				return;
			}

			ImGui.TextColored(new Num.Vector4(0.9f, 0.75f, 0.35f, 1f), "Tile cursor is not active.");
			ImGui.SameLine();

			if (Gui.SmallButton("Activate (B)"))
				cursor.SelectionMode = CursorSelectionMode.TilePaint;
		}

		private void DrawIssues(TilePaintTool tool)
		{
			var issues = TileValidation.ValidateTileset(_tileset?.Asset, _tileset);

			// The "no tile selected" warning only makes sense while painting tiles; treat the collision brush
			// as always having a selection.
			var hasSelection = tool.EditMode == TileEditMode.Collision || tool.HasSelection;
			issues.AddRange(TileValidation.ValidatePainting(tool.Target, _tileset, hasSelection));

			TileValidation.Draw(issues);
		}

		private void DrawTargetSelector(TilePaintTool tool)
		{
			var maps = TilemapSceneUtils.FindTilemaps();

			var label = tool.Target?.Entity != null
				? tool.Target.Entity.Name
				: "(no layer selected)";

			if (Gui.BeginCombo("Layer", label))
			{
				foreach (var map in maps)
				{
					if (map.Entity == null)
						continue;

					var selected = ReferenceEquals(map, tool.Target);
					if (Gui.Selectable(map.Entity.Name, selected))
						SelectLayer(tool, map);

					if (selected)
						ImGui.SetItemDefaultFocus();
				}

				if (maps.Count == 0)
					ImGui.TextDisabled("No tilemap layers in this scene.");

				ImGui.EndCombo();
			}

			DrawLayerSearch(tool, maps);

			if (Gui.Button("New Layer"))
			{
				_newLayerName = string.IsNullOrEmpty(_tileset?.Asset?.Name)
					? "Tilemap"
					: $"{_tileset.Asset.Name} Layer";

				_openNewLayerPopup = true;
			}

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Creates an entity with a TilemapRenderer using the selected tileset.");

			if (tool.Target != null)
			{
				ImGui.SameLine();
				ImGui.TextDisabled($"{tool.Target.TileCount} tiles");
			}
		}

		/// <summary>Name filter over the scene's tilemap layers, closest match first.</summary>
		private void DrawLayerSearch(TilePaintTool tool, List<TilemapRenderer> maps)
		{
			var clearWidth = ImGui.GetFrameHeight();

			var inputWidth = Math.Max(60f,
				ImGui.GetContentRegionAvail().X - clearWidth - ImGui.GetStyle().ItemSpacing.X);

			ImGui.SetNextItemWidth(inputWidth);
			var entered = Gui.InputTextWithHint("##layersearch", "Search layers...", ref _layerSearch, 128,
				ImGuiInputTextFlags.EnterReturnsTrue);

			ImGui.SameLine();

			var filtering = !string.IsNullOrWhiteSpace(_layerSearch);

			ImGui.BeginDisabled(!filtering);
			if (Gui.Button("x##clearlayersearch", new Num.Vector2(clearWidth, 0)))
				_layerSearch = string.Empty;
			ImGui.EndDisabled();

			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
				ImGui.SetTooltip("Clear the search.");

			if (!filtering)
				return;

			var query = _layerSearch.Trim();
			var matches = RankLayers(maps, query);

			if (matches.Count == 0)
			{
				ImGui.TextDisabled($"No layer matches \"{query}\".");
				return;
			}

			if (entered)
			{
				SelectLayer(tool, matches[0]);
				return;
			}

			var rows = Math.Min(matches.Count, 6);
			var height = rows * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().FramePadding.Y * 2f;

			ImGui.BeginChild("layer-search-results", new Num.Vector2(inputWidth, height), true);

			for (var i = 0; i < matches.Count; i++)
			{
				var map = matches[i];

				ImGui.PushID(i);
				if (Gui.Selectable(map.Entity.Name, ReferenceEquals(map, tool.Target)))
					SelectLayer(tool, map);
				ImGui.PopID();
			}

			ImGui.EndChild();
		}

		/// <summary>Makes a layer the paint target. Clearing the search is what closes the result list behind it.</summary>
		private void SelectLayer(TilePaintTool tool, TilemapRenderer map)
		{
			tool.Target = map;
			_layerSearch = string.Empty;
			SyncTilesetFromTarget(tool);
		}

		private static List<TilemapRenderer> RankLayers(List<TilemapRenderer> maps, string query)
		{
			var scored = new List<(TilemapRenderer Map, int Score)>();

			foreach (var map in maps)
			{
				if (map.Entity == null)
					continue;

				var score = MatchScore(map.Entity.Name, query);
				if (score > 0)
					scored.Add((map, score));
			}

			scored.Sort((a, b) =>
			{
				var byScore = b.Score.CompareTo(a.Score);
				return byScore != 0
					? byScore
					: string.Compare(a.Map.Entity.Name, b.Map.Entity.Name, StringComparison.OrdinalIgnoreCase);
			});

			var result = new List<TilemapRenderer>(scored.Count);
			foreach (var entry in scored)
				result.Add(entry.Map);

			return result;
		}

		/// <summary>Match score, 0 for none. Exact beats prefix beats substring beats a scattered subsequence.</summary>
		private static int MatchScore(string name, string query)
		{
			if (string.IsNullOrEmpty(name))
				return 0;

			if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
				return 1000;

			if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
				return 800 - Math.Min(name.Length - query.Length, 99);

			var index = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
			if (index >= 0)
				return 600 - Math.Min(index, 99);

			// Letters in order but not adjacent, so "gt" finds "Ground Tiles". Spaces are skipped.
			var lowerName = name.ToLowerInvariant();
			var lowerQuery = query.ToLowerInvariant();

			var cursor = 0;
			var gaps = 0;

			foreach (var ch in lowerQuery)
			{
				if (ch == ' ')
					continue;

				var at = lowerName.IndexOf(ch, cursor);
				if (at < 0)
					return 0;

				gaps += at - cursor;
				cursor = at + 1;
			}

			return Math.Max(1, 400 - Math.Min(gaps, 399));
		}

		/// <summary>
		/// One switch for per-entity debug rendering across every tilemap layer. A mixed state reads as off, so the
		/// click that resolves it turns everything on.
		/// </summary>
		private static void DrawAllLayersDebugRender(List<TilemapRenderer> maps)
		{
			var on = 0;
			var total = 0;

			foreach (var map in maps)
			{
				if (map.Entity == null)
					continue;

				total++;
				if (map.Entity.DebugRenderEnabled)
					on++;
			}

			if (total == 0)
				return;

			var allOn = on == total;
			var value = allOn;

			if (Gui.Checkbox("Debug Render", ref value))
			{
				foreach (var map in maps)
					SetDebugRender(map, value);
			}

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Debug rendering for every tilemap layer at once - collider outlines and gizmos.");

			if (!allOn && on > 0)
			{
				ImGui.SameLine();
				ImGui.TextDisabled($"({on} of {total} on)");
			}
		}

		private static void SetLayerVisible(TilemapRenderer map, bool visible)
		{
			if (map?.Entity == null || map.Enabled == visible)
				return;

			if (visible && !map.Entity.Enabled)
				map.Entity.SetEnabled(true);

			map.SetEnabled(visible);
			EditorChangeTracker.MarkChanged(map.Entity, "Toggle tilemap layer visibility");
		}

		/// <summary>Moves one layer into another render layer as its own undo step. Depth is left alone.</summary>
		private static void SetRenderLayer(TilemapRenderer map, int renderLayer)
		{
			if (map?.Entity == null || map.RenderLayer == renderLayer)
				return;

			const string description = "Change tilemap render layer";

			var change = new List<TilemapLayerOrderUndoAction.LayerOrder>
			{
				new(map, map.RenderLayer, map.LayerDepth, renderLayer, map.LayerDepth)
			};

			map.RenderLayer = renderLayer;

			EditorChangeTracker.MarkChanged(map.Entity, description);
			EditorChangeTracker.PushUndo(new TilemapLayerOrderUndoAction(change, description));
		}

		private static void SetDebugRender(TilemapRenderer map, bool enabled)
		{
			if (map?.Entity == null || map.Entity.DebugRenderEnabled == enabled)
				return;

			map.Entity.DebugRenderEnabled = enabled;
			EditorChangeTracker.MarkChanged(map.Entity, "Toggle tilemap debug render");
		}

		private void DrawTilesetSelector(TilePaintTool tool, TilesetEditorWindow tilesetEditor)
		{
			var reference = tool.Target?.Tileset ?? (_tilesetPath != null
				? TilemapSceneUtils.ReferenceFor(_tilesetPath)
				: default);

			if (TileAssetUtils.DrawAssetSlot("Tileset", ref reference, TilesetExtensions))
				AssignTileset(tool, reference);

			if (Gui.Button("New Tileset..."))
				tilesetEditor.NewTileset();

			ImGui.SameLine();

			var canEdit = _tilesetPath != null;
			ImGui.BeginDisabled(!canEdit);
			if (Gui.Button("Edit Tileset") && canEdit)
				tilesetEditor.Open(_tilesetPath);
			ImGui.EndDisabled();

			ImGui.SameLine();

			var canSync = _tileset?.Asset != null && _tilesetPath != null;
			ImGui.BeginDisabled(!canSync);
			if (Gui.Button("Sync Changes") && canSync)
				SyncTilesetSource(tool);
			ImGui.EndDisabled();

			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				ImGui.SetTooltip(canSync
					? "Re-reads this tileset's source image from disk. Use after editing it externally."
					: "Assign a tileset first.");
			}

			if (_tileset == null)
			{
				ImGui.TextDisabled(tool.Target == null
					? "Pick a layer, or create one with a tileset."
					: "This layer has no tileset assigned.");
			}
		}

		/// <summary>
		/// Draw order across every tilemap layer, front-most first. Order 1 is the front. A LOWER RenderLayer draws IN
		/// FRONT (engine convention, inverted from intuition), and it is the same layer space sprites use.
		/// </summary>
		private void DrawLayerOrder(TilePaintTool tool)
		{
			var maps = SortedFrontToBack();

			if (!Gui.CollapsingHeader($"Layer order ({maps.Count})"))
				return;

			if (maps.Count == 0)
			{
				_selectedLayers.Clear();
				_layerRangeAnchor = null;
				ImGui.TextDisabled("No tilemap layers in this scene.");
				return;
			}

			PruneLayerSelection(maps);

			ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
			ImGui.TextWrapped(
				"Order 1 draws in front. Click a row to paint that layer, drag rows to reorder - Shift or Ctrl " +
				"click picks several, right-click for \"Move to\".");
			ImGui.PopStyleColor();

			if (_selectedLayers.Count > 1)
				ImGui.TextDisabled($"{_selectedLayers.Count} layers picked - they move as one block.");

			ImGui.Spacing();
			DrawAllLayersDebugRender(maps);
			ImGui.Spacing();

			RenderLayerOptions(out var layerNames, out var layerValues);

			_layerReorder.BeginFrame(_selectedLayers.Count,
				ImGui.GetFrameHeight() + ImGui.GetStyle().CellPadding.Y * 2f);

			if (ImGui.BeginTable("tilemap-layer-order", 6,
				    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit |
				    ImGuiTableFlags.Resizable))
			{
				ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed, 46f);
				ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 1f);
				ImGui.TableSetupColumn("Visible", ImGuiTableColumnFlags.WidthFixed, 52f);
				ImGui.TableSetupColumn("Debug", ImGuiTableColumnFlags.WidthFixed, 52f);

				// Stretch, not fixed: a fixed column takes its width out of the only stretching one.
				ImGui.TableSetupColumn("Render Layer", ImGuiTableColumnFlags.WidthStretch, 0.85f);
				ImGui.TableSetupColumn("Info", ImGuiTableColumnFlags.WidthFixed, 86f);
				ImGui.TableHeadersRow();

				for (var i = 0; i < maps.Count; i++)
				{
					var map = maps[i];
					if (map.Entity == null)
						continue;

					if (_layerReorder.TryGetGap(i, out var gapHeight) && _layerReorder.DrawStrip(i, gapHeight, true))
						DropPickedLayersAt(maps, i);

					ImGui.PushID(i);
					ImGui.TableNextRow();

					ImGui.TableSetColumnIndex(0);

					// The order number is the row hit box, spanning every column so a drag can start anywhere.
					ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Num.Vector2(0f, 0.5f));

					var picked = _selectedLayers.Contains(map);
					var rowClicked = Gui.Selectable($"{i + 1}##layerrow", picked,
						ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap,
						new Num.Vector2(0f, ImGui.GetFrameHeight()));

					ImGui.PopStyleVar();
					ImGui.SetItemAllowOverlap();

					// Read before the drag-drop calls: they push windows, which moves "last item" on.
					var rowRightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);

					if (rowClicked)
						ClickLayerRow(tool, maps, map);

					HandleLayerRowDragDrop(maps, map, i);

					if (rowRightClicked)
					{
						if (!_selectedLayers.Contains(map))
						{
							_selectedLayers.Clear();
							_selectedLayers.Add(map);
							_layerRangeAnchor = map;
						}

						_moveToOrder = FirstPickedOrder(maps);
						ImGui.OpenPopup("layer-order-menu");
					}

					DrawLayerOrderMenu(maps);

					ImGui.TableSetColumnIndex(1);

					ImGui.AlignTextToFramePadding();

					if (ReferenceEquals(map, tool.Target))
						ImGui.TextColored(new Num.Vector4(0.4f, 0.9f, 0.5f, 1f), map.Entity.Name);
					else
						ImGui.TextUnformatted(map.Entity.Name);

					ImGui.TableSetColumnIndex(2);

					var visible = map.Enabled;
					if (VoltageEditorUtils.EyeToggle("layervis", ref visible,
						    visible
							    ? "Layer is visible - click to hide it."
							    : map.Entity.Enabled
								    ? "Layer is hidden - click to show it."
								    : "Layer is hidden - its entity is disabled too, and showing it re-enables both."))
					{
						SetLayerVisible(map, visible);
					}

					ImGui.TableSetColumnIndex(3);

					// The column header names it, so the checkbox itself carries no label.
					var debug = map.Entity.DebugRenderEnabled;
					if (Gui.Checkbox("##dbg", ref debug))
						SetDebugRender(map, debug);

					if (ImGui.IsItemHovered())
						ImGui.SetTooltip("Debug rendering for this layer only - collider outlines and gizmos.");

					ImGui.TableSetColumnIndex(4);

					var layerIndex = Array.IndexOf(layerValues, map.RenderLayer);
					if (layerIndex < 0)
						layerIndex = 0;

					ImGui.SetNextItemWidth(-1);
					if (Gui.Combo("##renderlayer", ref layerIndex, layerNames, layerNames.Length))
						SetRenderLayer(map, layerValues[layerIndex]);

					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip(
							"A LOWER render layer draws in FRONT (engine convention).\n" +
							"This is the same layer space sprites and other renderables use, so it is how you put a\n" +
							"tilemap in front of or behind them.");
					}

					ImGui.TableSetColumnIndex(5);

					// Depth, not render layer: reorders fall back to depth when layers share a render layer.
					ImGui.AlignTextToFramePadding();
					ImGui.TextDisabled($"depth {map.LayerDepth:0.00}");

					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip(
							"Tie-breaker within one render layer: 0 = front, 1 = back.\n" +
							"Reordering sets this for you when two layers share a render layer.");
					}

					ImGui.PopID();
				}

				if (_layerReorder.TryGetGap(maps.Count, out var tailHeight) &&
				    _layerReorder.DrawStrip(maps.Count, tailHeight, true))
				{
					DropPickedLayersAt(maps, maps.Count);
				}

				ImGui.EndTable();
			}

			_layerReorder.AutoScroll();
		}

		/// <summary>The project's named render layers, matching the picker the Entity Inspector uses.</summary>
		private static void RenderLayerOptions(out string[] names, out int[] values)
		{
			var layers = Project.ProjectSettings.Instance.Rendering.RenderingLayers;

			var nameList = new List<string>();
			var valueList = new List<int>();

			foreach (var kvp in layers)
			{
				nameList.Add(kvp.Key);
				valueList.Add(kvp.Value);
			}

			names = nameList.ToArray();
			values = valueList.ToArray();
		}

		private static List<TilemapRenderer> SortedFrontToBack()
		{
			var maps = TilemapSceneUtils.FindTilemaps();

			maps.Sort((a, b) =>
			{
				var byLayer = a.RenderLayer.CompareTo(b.RenderLayer);
				if (byLayer != 0)
					return byLayer;

				var byDepth = a.LayerDepth.CompareTo(b.LayerDepth);
				if (byDepth != 0)
					return byDepth;

				// List.Sort is unstable and fresh layers tie on both keys, so a final tie-break is needed.
				return (a.Entity?.Id ?? 0).CompareTo(b.Entity?.Id ?? 0);
			});

			return maps;
		}

		/// <summary>Drops rows that left the scene, so a stale reference can never be dragged or moved.</summary>
		private void PruneLayerSelection(List<TilemapRenderer> maps)
		{
			if (_selectedLayers.Count > 0)
				_selectedLayers.RemoveWhere(map => map?.Entity == null || !maps.Contains(map));

			if (_layerRangeAnchor != null && !maps.Contains(_layerRangeAnchor))
				_layerRangeAnchor = null;
		}

		/// <summary>Plain click picks and paints; Ctrl toggles; Shift takes the run from the anchor.</summary>
		private void ClickLayerRow(TilePaintTool tool, List<TilemapRenderer> maps, TilemapRenderer map)
		{
			var io = ImGui.GetIO();

			if (io.KeyShift && _layerRangeAnchor != null)
			{
				SelectLayerRange(maps, _layerRangeAnchor, map);
				return;
			}

			if (io.KeyCtrl || io.KeySuper)
			{
				if (!_selectedLayers.Add(map))
					_selectedLayers.Remove(map);

				_layerRangeAnchor = map;
				return;
			}

			_selectedLayers.Clear();
			_selectedLayers.Add(map);
			_layerRangeAnchor = map;

			if (!ReferenceEquals(tool.Target, map))
			{
				tool.Target = map;
				SyncTilesetFromTarget(tool);
			}
		}

		private void SelectLayerRange(List<TilemapRenderer> maps, TilemapRenderer anchor, TilemapRenderer map)
		{
			var from = maps.IndexOf(anchor);
			var to = maps.IndexOf(map);

			if (from < 0 || to < 0)
				return;

			if (from > to)
				(from, to) = (to, from);

			_selectedLayers.Clear();
			for (var i = from; i <= to; i++)
				_selectedLayers.Add(maps[i]);
		}

		/// <summary>Order number (1-based) of the front-most picked row, 1 when nothing is picked.</summary>
		private int FirstPickedOrder(List<TilemapRenderer> maps)
		{
			for (var i = 0; i < maps.Count; i++)
			{
				if (_selectedLayers.Contains(maps[i]))
					return i + 1;
			}

			return 1;
		}

		/// <summary>Makes the row a drag source and drop target. A drop lands in the seam above or below, depending on which half the cursor is over.</summary>
		private void HandleLayerRowDragDrop(List<TilemapRenderer> maps, TilemapRenderer map, int index)
		{
			var io = ImGui.GetIO();

			// A modifier click never drags: ImGui starts one after ~3px, which a Ctrl-click wobbles past,
			// swallowing the click and collapsing the pick.
			var modifierHeld = io.KeyShift || io.KeyCtrl || io.KeySuper;

			if ((!modifierHeld || _layerReorder.IsDragging) &&
			    ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceNoHoldToOpenOthers))
			{
				if (!_selectedLayers.Contains(map))
				{
					_selectedLayers.Clear();
					_selectedLayers.Add(map);
					_layerRangeAnchor = map;
				}

				_layerReorder.SetPayload();

				ImGui.TextUnformatted(_selectedLayers.Count == 1
					? $"Move {map.Entity.Name}"
					: $"Move {_selectedLayers.Count} layers");

				ImGui.EndDragDropSource();
			}

			var zone = _layerReorder.HandleRow(index, false, out var dropped);

			if (dropped && zone != DragReorderStrip.RowZone.None)
				DropPickedLayersAt(maps, zone == DragReorderStrip.RowZone.Above ? index : index + 1);
		}

		/// <summary>Drops the picked layers into the seam above the slot. The order counts only the layers staying, or the block overshoots the gap.</summary>
		private void DropPickedLayersAt(List<TilemapRenderer> maps, int slot)
		{
			var insert = 0;
			for (var i = 0; i < slot && i < maps.Count; i++)
			{
				if (!_selectedLayers.Contains(maps[i]))
					insert++;
			}

			// The rows are about to renumber, so a kept gap would open in the middle of the new order.
			_layerReorder.Reset();

			MoveSelectedLayersTo(maps, insert + 1);
		}

		/// <summary>Right-click menu for the picked rows: send them to an order number, to the front or to the back.</summary>
		private void DrawLayerOrderMenu(List<TilemapRenderer> maps)
		{
			if (!Gui.BeginPopup("layer-order-menu"))
				return;

			var count = _selectedLayers.Count;

			ImGui.TextDisabled(count == 1 ? "1 layer picked" : $"{count} layers picked");
			ImGui.Separator();

			var lastStart = Math.Max(1, maps.Count - count + 1);

			ImGui.SetNextItemWidth(120f);
			var entered = Gui.InputInt("Move to order", ref _moveToOrder, 1, 1,
				ImGuiInputTextFlags.EnterReturnsTrue);

			_moveToOrder = Math.Clamp(_moveToOrder, 1, Math.Max(1, maps.Count));

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip($"1 draws in front, {lastStart} is as far back as this block still fits.");

			var confirm = Gui.Button("Move", new Num.Vector2(90f, 0)) || entered;

			if (ImGui.IsItemHovered())
			{
				var start = Math.Clamp(_moveToOrder, 1, lastStart);

				ImGui.SetTooltip(count == 1
					? $"Moves this layer to order {start}."
					: $"The front-most picked layer takes order {start} and the rest follow directly underneath it\n" +
					  $"in the order they are in now, so the pick ends up as orders {start}-{start + count - 1}.\n" +
					  "Every other layer renumbers around the block.");
			}

			ImGui.SameLine();
			var cancel = Gui.Button("Cancel", new Num.Vector2(90f, 0));

			if (confirm)
			{
				MoveSelectedLayersTo(maps, _moveToOrder);
				ImGui.CloseCurrentPopup();
			}
			else if (cancel)
			{
				ImGui.CloseCurrentPopup();
			}

			ImGui.Separator();

			if (Gui.Selectable("Select all layers", false, ImGuiSelectableFlags.DontClosePopups))
			{
				_selectedLayers.Clear();
				foreach (var other in maps)
					_selectedLayers.Add(other);
			}

			if (Gui.Selectable("Clear pick", false, ImGuiSelectableFlags.DontClosePopups))
			{
				_selectedLayers.Clear();
				_layerRangeAnchor = null;
			}

			Gui.EndPopup();
		}

		/// <summary>Reinserts the picked layers as one block starting at targetOrder (1 = front), keeping their relative order while the rest close the gap.</summary>
		private void MoveSelectedLayersTo(List<TilemapRenderer> sorted, int targetOrder)
		{
			if (_selectedLayers.Count == 0)
				return;

			var picked = new List<TilemapRenderer>();
			var rest = new List<TilemapRenderer>();

			foreach (var map in sorted)
				(_selectedLayers.Contains(map) ? picked : rest).Add(map);

			if (picked.Count == 0)
				return;

			var insert = Math.Clamp(targetOrder - 1, 0, rest.Count);

			var order = new List<TilemapRenderer>(sorted.Count);

			for (var i = 0; i < insert; i++)
				order.Add(rest[i]);

			order.AddRange(picked);

			for (var i = insert; i < rest.Count; i++)
				order.Add(rest[i]);

			// A wobbled click arrives as a drop on its own row. Nothing moved, so record nothing.
			var moved = false;
			for (var i = 0; i < order.Count && !moved; i++)
				moved = !ReferenceEquals(order[i], sorted[i]);

			if (!moved)
				return;

			ApplyLayerOrder(sorted, order, picked);
		}

		/// <summary>Writes the order back by handing out the render-layer values already in play, so the bands tilemaps occupy against sprites are untouched. moved lists only the layers the user actually moved, which are the ones named and marked unsaved.</summary>
		private static void ApplyLayerOrder(List<TilemapRenderer> sorted, List<TilemapRenderer> order,
			List<TilemapRenderer> moved)
		{
			if (order.Count != sorted.Count || order.Count == 0)
				return;

			var slotLayers = new int[sorted.Count];
			for (var i = 0; i < sorted.Count; i++)
				slotLayers[i] = sorted[i].RenderLayer;

			var wasLayer = new int[order.Count];
			var wasDepth = new float[order.Count];

			for (var i = 0; i < order.Count; i++)
			{
				wasLayer[i] = order[i].RenderLayer;
				wasDepth[i] = order[i].LayerDepth;
			}

			for (var i = 0; i < order.Count; i++)
				order[i].RenderLayer = slotLayers[i];

			SpreadDepthsWithinLayers(order);

			var changes = new List<TilemapLayerOrderUndoAction.LayerOrder>();

			for (var i = 0; i < order.Count; i++)
			{
				var map = order[i];

				if (map.RenderLayer == wasLayer[i] && Math.Abs(map.LayerDepth - wasDepth[i]) < 0.0001f)
					continue;

				changes.Add(new TilemapLayerOrderUndoAction.LayerOrder(map, wasLayer[i], wasDepth[i],
					map.RenderLayer, map.LayerDepth));
			}

			if (changes.Count == 0)
				return;

			// Only the moved layers are listed: everything passed over was renumbered too, and naming
			// all of them turns one move into a dozen identical lines in the close dialog.
			var marked = 0;

			if (moved != null)
			{
				foreach (var map in moved)
				{
					var from = sorted.IndexOf(map) + 1;
					var to = order.IndexOf(map) + 1;

					if (map?.Entity == null || from <= 0 || to <= 0)
						continue;

					EditorChangeTracker.MarkChanged(map.Entity, $"Reorder '{map.Entity.Name}': {from} -> {to}");
					marked++;
				}
			}

			if (marked == 0)
				EditorChangeTracker.MarkChanged(changes[0].Map.Entity, "Reorder tilemap layers");

			// One step for the whole move: undoing half would leave an order nobody asked for.
			EditorChangeTracker.PushUndo(
				new TilemapLayerOrderUndoAction(changes, DescribeReorder(sorted, order, moved)));
		}

		/// <summary>One line naming the move, capped so a large block stays readable. Shown in undo history and the unsaved-changes dialog.</summary>
		private static string DescribeReorder(List<TilemapRenderer> sorted, List<TilemapRenderer> order,
			List<TilemapRenderer> moved)
		{
			if (moved == null || moved.Count == 0)
				return "Reorder tilemap layers";

			const int maxNamed = 3;
			var parts = new List<string>();

			foreach (var map in moved)
			{
				if (parts.Count == maxNamed)
				{
					parts.Add($"+{moved.Count - maxNamed} more");
					break;
				}

				var from = sorted.IndexOf(map) + 1;
				var to = order.IndexOf(map) + 1;

				if (map?.Entity == null || from <= 0 || to <= 0)
					continue;

				parts.Add($"{map.Entity.Name} {from}->{to}");
			}

			return parts.Count == 0
				? "Reorder tilemap layers"
				: "Reorder tilemap layers: " + string.Join(", ", parts);
		}

		private static void SpreadDepthsWithinLayers(List<TilemapRenderer> sorted)
		{
			var i = 0;
			while (i < sorted.Count)
			{
				var layer = sorted[i].RenderLayer;

				var j = i;
				while (j < sorted.Count && sorted[j].RenderLayer == layer)
					j++;

				var count = j - i;
				if (count > 1)
				{
					for (var k = 0; k < count; k++)
						sorted[i + k].LayerDepth = k / (float)(count - 1) * 0.9f;
				}

				i = j;
			}
		}

		private void DrawToolButtons(TilePaintTool tool)
		{
			DrawToolButton(tool, TileTool.Brush, "Brush", "Stamp the selected tiles. Shift or right-drag erases.");
			ImGui.SameLine();
			DrawToolButton(tool, TileTool.Eraser, "Eraser", "Erase cells.");
			ImGui.SameLine();
			DrawToolButton(tool, TileTool.Rectangle, "Rect", "Drag a rectangle; the stamp tiles across it.");
			ImGui.SameLine();
			DrawToolButton(tool, TileTool.Fill, "Fill", "Flood fill the contiguous matching region.");
			ImGui.SameLine();
			DrawToolButton(tool, TileTool.Picker, "Pick", "Pick the tile under the cursor into the palette.");
			ImGui.SameLine();
			DrawToolButton(tool, TileTool.Select, "Select",
				"Marquee placed tiles in the game view, then drag to move them a whole number of cells. " +
				"Ctrl or Shift adds another box. Tiles and their collision move together.");

			if (tool.Tool == TileTool.Select)
				DrawSelectionControls(tool);

			Gui.Checkbox("Stack while dragging", ref tool.StackWhileDragging);

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(
					"Off (default): dragging steps the stamp by its own size, so tiles are laid AROUND each\n" +
					"other, edge to edge.\n\n" +
					"On: dragging follows the cursor cell by cell, so tiles are laid ON TOP of each other and\n" +
					"overlap. A tile with transparency stacks over what is beneath it; a fully opaque one\n" +
					"replaces it.\n\n" +
					"A single click is always free-form, whichever way this is set.");
			}

			DrawOrientationControls(tool);
			DrawBrushPreview(tool);
			DrawTerrainSelector(tool);
		}

		/// <summary>Actions for a live in-viewport tile selection: rotate the block, or drop the selection.</summary>
		private static void DrawSelectionControls(TilePaintTool tool)
		{
			if (!tool.HasTileSelection)
			{
				ImGui.TextDisabled("Drag in the game view to select placed tiles.");
				return;
			}

			ImGui.TextDisabled($"{tool.SelectedCellCount} cells selected");
			ImGui.SameLine();

			if (Gui.SmallButton("Rot -##seltiles"))
				tool.RotateSelection(-1);

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip($"Rotate the selected tiles 90° left ({EditorHotkeys.Hint(EditorHotkeys.TileRotateLeft)})");

			ImGui.SameLine();

			if (Gui.SmallButton("Rot +##seltiles"))
				tool.RotateSelection(1);

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip($"Rotate the selected tiles 90° right ({EditorHotkeys.Hint(EditorHotkeys.TileRotateRight)})");

			ImGui.SameLine();

			if (Gui.SmallButton("Delete##seltiles"))
				tool.DeleteSelection();

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Clear the selected tiles and their collision (Delete or Backspace).");

			ImGui.SameLine();

			if (Gui.SmallButton("Deselect##seltiles"))
				tool.ClearTileSelection();
		}

		/// <summary>
		/// Shows the WHOLE selected stamp with the brush's current rotation/flip applied - each tile is oriented
		/// about its own centre in the same grid arrangement a stroke actually paints (the block itself is not
		/// rotated, matching StampAt), so the preview never lies about the result.
		/// </summary>
		private void DrawBrushPreview(TilePaintTool tool)
		{
			if (_tileset?.Texture == null || _atlasTextureId == IntPtr.Zero || !tool.HasSelection)
				return;

			var cols = tool.SelectionWidth;
			var rows = tool.SelectionHeight;
			var texW = (float)_tileset.Texture.Width;
			var texH = (float)_tileset.Texture.Height;

			var flipX = tool.OrientationFlipX ? -1f : 1f;
			var flipY = tool.OrientationFlipY ? -1f : 1f;
			var angle = tool.OrientationRotation * (float)(Math.PI * 0.5);
			var cos = (float)Math.Cos(angle);
			var sin = (float)Math.Sin(angle);

			// Fit the whole colsxrows block into a fixed box, preserving its shape.
			const float box = 96f;
			var cellPx = box / Math.Max(cols, rows);
			var blockW = cols * cellPx;
			var blockH = rows * cellPx;

			var origin = ImGui.GetCursorScreenPos();
			var blockX = origin.X + (box - blockW) * 0.5f;
			var blockY = origin.Y + (box - blockH) * 0.5f;

			var drawList = ImGui.GetWindowDrawList();
			var boxMax = new Num.Vector2(origin.X + box, origin.Y + box);
			drawList.AddRectFilled(origin, boxMax, TileAtlasBackground.ColorU32);

			var half = cellPx * 0.5f;
			for (var sy = 0; sy < rows; sy++)
			{
				for (var sx = 0; sx < cols; sx++)
				{
					var tile = tool.Selection[sy * cols + sx];
					if (!_tileset.IsValidIndex(tile))
						continue;

					// Animated tiles play in the preview too, so a placed animation reads at a glance.
					var displayTile = _tileset.ResolveFrame(tile, (float)ImGui.GetTime());
					if (!_tileset.IsValidIndex(displayTile))
						displayTile = tile;

					var rect = _tileset.SourceRects[displayTile];
					var uvTL = new Num.Vector2(rect.X / texW, rect.Y / texH);
					var uvTR = new Num.Vector2((rect.X + rect.Width) / texW, rect.Y / texH);
					var uvBR = new Num.Vector2((rect.X + rect.Width) / texW, (rect.Y + rect.Height) / texH);
					var uvBL = new Num.Vector2(rect.X / texW, (rect.Y + rect.Height) / texH);

					var cx = blockX + sx * cellPx + half;
					var cy = blockY + sy * cellPx + half;

					// Flip in local space, then rotate about the tile's centre - the renderer's exact order.
					Num.Vector2 Corner(float lx, float ly)
					{
						lx *= half * flipX;
						ly *= half * flipY;
						return new Num.Vector2(cx + (lx * cos - ly * sin), cy + (lx * sin + ly * cos));
					}

					drawList.AddImageQuad(_atlasTextureId,
						Corner(-1, -1), Corner(1, -1), Corner(1, 1), Corner(-1, 1),
						uvTL, uvTR, uvBR, uvBL, 0xFFFFFFFF);
				}
			}

			drawList.AddRect(origin, boxMax, ImGui.GetColorU32(new Num.Vector4(0.5f, 0.5f, 0.5f, 1f)));

			ImGui.Dummy(new Num.Vector2(box, box));
			ImGui.SameLine();
			ImGui.TextDisabled($"brush {cols}x{rows}{(tool.CurrentOrientation == 0 ? "" : " (oriented)")}");
		}

		/// <summary>Autotile terrain picker. When set, the brush paints the terrain and auto-selects tiles by neighbour.</summary>
		private void DrawTerrainSelector(TilePaintTool tool)
		{
			var terrains = _tileset?.Asset?.Terrains;
			if (terrains == null || terrains.Count == 0)
			{
				tool.ActiveTerrain = -1;
				return;
			}

			var current = 0;
			var labels = new List<string> { "Off (paint tiles)" };
			for (var i = 0; i < terrains.Count; i++)
			{
				labels.Add(string.IsNullOrEmpty(terrains[i].Name) ? $"Terrain {terrains[i].Id}" : terrains[i].Name);
				if (terrains[i].Id == tool.ActiveTerrain)
					current = i + 1;
			}

			ImGui.SetNextItemWidth(200f);
			if (Gui.Combo("Autotile", ref current, labels.ToArray(), labels.Count))
				tool.ActiveTerrain = current == 0 ? -1 : terrains[current - 1].Id;

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Paint a terrain; the right edge/corner tile is chosen automatically from neighbours.\nDefine terrains and per-tile masks in the Tileset Editor.");
		}

		/// <summary>Orientation applied to newly painted tiles. Hotkeys Z/X rotate, V/C flip.</summary>
		private static void DrawOrientationControls(TilePaintTool tool)
		{
			ImGui.TextUnformatted("Orient");
			ImGui.SameLine();

			if (Gui.SmallButton("Rot -##orient")) tool.RotateBrush(-1);
			if (ImGui.IsItemHovered()) ImGui.SetTooltip("Rotate 90° left (Q, palette focused)");
			ImGui.SameLine();

			if (Gui.SmallButton("Rot +##orient")) tool.RotateBrush(1);
			if (ImGui.IsItemHovered()) ImGui.SetTooltip("Rotate 90° right (E, palette focused)");
			ImGui.SameLine();

			var flipX = tool.OrientationFlipX;
			if (ToggleSmall("Flip X##orient", flipX)) tool.ToggleFlipX();
			if (ImGui.IsItemHovered()) ImGui.SetTooltip("Flip horizontally (Z, palette focused)");
			ImGui.SameLine();

			var flipY = tool.OrientationFlipY;
			if (ToggleSmall("Flip Y##orient", flipY)) tool.ToggleFlipY();
			if (ImGui.IsItemHovered()) ImGui.SetTooltip("Flip vertically (X, palette focused)");
			ImGui.SameLine();

			ImGui.BeginDisabled(tool.CurrentOrientation == 0);
			if (Gui.SmallButton("Reset##orient")) tool.ResetOrientation();
			ImGui.EndDisabled();

			if (tool.CurrentOrientation != 0)
			{
				ImGui.SameLine();
				ImGui.TextDisabled($"({tool.OrientationRotation * 90}°{(flipX ? " X" : "")}{(flipY ? " Y" : "")})");
			}
		}

		private static bool ToggleSmall(string label, bool active)
		{
			if (active)
				ImGui.PushStyleColor(ImGuiCol.Button, new Num.Vector4(0.2f, 0.5f, 1f, 1f));

			var clicked = Gui.SmallButton(label);

			if (active)
				ImGui.PopStyleColor();

			return clicked;
		}

		private static void DrawToolButton(TilePaintTool tool, TileTool value, string label, string tooltip)
		{
			var active = tool.Tool == value;

			if (active)
				ImGui.PushStyleColor(ImGuiCol.Button, new Num.Vector4(0.2f, 0.5f, 1f, 1f));

			if (Gui.Button(label))
				tool.Tool = value;

			if (active)
				ImGui.PopStyleColor();

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip(tooltip);
		}

		private void DrawGridOptions(TilePaintTool tool)
		{
			VoltageEditorUtils.EyeToggle("showgrid", ref tool.ShowGrid,
				tool.ShowGrid ? "Grid is shown - click to hide it." : "Grid is hidden - click to show it.",
				"Grid");
			ImGui.SameLine();
			Gui.Checkbox("Always", ref tool.AlwaysShowGrid);

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Keep drawing the grid even when the tile cursor is not active.");

			var color = new Num.Vector4(
				tool.GridColor.R / 255f, tool.GridColor.G / 255f,
				tool.GridColor.B / 255f, tool.GridColor.A / 255f);

			if (Gui.ColorEdit4("Grid color", ref color,
				    ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview))
			{
				tool.GridColor = new Microsoft.Xna.Framework.Color(color.X, color.Y, color.Z, color.W);
			}

			ImGui.SetNextItemWidth(140f);
			Gui.SliderFloat("Grid thickness", ref tool.GridThickness, 0.25f, 4f, "%.2f px");

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Line width in screen pixels - stays constant as you zoom.");

			ImGui.SetNextItemWidth(140f);
			Gui.SliderFloat("Cursor thickness", ref tool.HighlightThickness, 0.5f, 6f, "%.2f px");
		}

		// The tile-grid overlay is skipped past this many tiles per axis (see TilePaletteDrawing.DrawSliceGrid);
		// beyond it the atlas still shows but without visible tile boundaries.
		private const int GridDisplayLimit = 256;

		/// <summary>Yellow warning (with the shared warning icon) when the tileset is too large to display its grid.</summary>
		private void DrawAtlasWarnings()
		{
			if (_tileset.Columns <= GridDisplayLimit && _tileset.Rows <= GridDisplayLimit)
				return;

			var warning = new Num.Vector4(1f, 0.85f, 0.25f, 1f);

			if (ImguiImageLoader.WarningIconId != IntPtr.Zero)
			{
				var iconSize = ImGui.GetFontSize() + 2f;
				ImGui.Image(ImguiImageLoader.WarningIconId, new Num.Vector2(iconSize, iconSize),
					Num.Vector2.Zero, Num.Vector2.One, warning);
				ImGui.SameLine();
			}

			ImGui.PushStyleColor(ImGuiCol.Text, warning);
			ImGui.TextWrapped(
				$"Large tileset ({_tileset.Columns}x{_tileset.Rows} tiles): the tile grid is hidden beyond " +
				$"{GridDisplayLimit} per axis. Zoom in to line up tile edges, or split the tileset.");
			ImGui.PopStyleColor();
		}

		private void DrawAtlas(TilePaintTool tool)
		{
			if (_tileset?.Texture == null || _atlasTextureId == IntPtr.Zero)
				return;

			DrawAtlasWarnings();

			if (_tileset.Asset?.TextureIsAsepriteAnimation == true)
			{
				ImGui.TextDisabled("Animated tileset: the strip below is the animation frames. Paint tile 0 (top-left) — it plays automatically.");
			}

			// Logarithmic + a sub-1x floor so an oversized atlas can be zoomed OUT to fit, not only scrolled.
			ImGui.SetNextItemWidth(160f);
			Gui.SliderFloat("Zoom", ref _zoom, 0.1f, 8f, "%.2fx", ImGuiSliderFlags.Logarithmic);

			ImGui.SameLine();

			// "Fit" scales the whole tileset into the space the atlas child is about to occupy - the answer for a
			// tileset too big to browse at 1x. GetContentRegionAvail here is the remaining window, ~= the child.
			if (Gui.Button("Fit"))
			{
				var avail = ImGui.GetContentRegionAvail();
				var fitX = avail.X / _tileset.Texture.Width;
				var fitY = Math.Max(80f, avail.Y - 4f) / _tileset.Texture.Height;
				_zoom = Math.Clamp(Math.Min(fitX, fitY), 0.02f, 8f);
			}

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Zoom the whole tileset to fit the panel.");

			ImGui.SameLine();
			if (Gui.Button("1:1"))
				_zoom = 1f;

			TileAtlasBackground.DrawPicker();

			if (_hasSelection)
			{
				var tiles = 0;
				foreach (var box in _boxes)
					tiles += box.Count;

				var summary = _boxes.Count == 1
					? $"Selection: {_boxes[0].Width} x {_boxes[0].Height} tile{(tiles == 1 ? "" : "s")}"
					: $"Selection: {tiles} tiles in {_boxes.Count} groups (gaps are kept)";

				ImGui.TextDisabled(summary);

				ImGui.SameLine();
				if (Gui.SmallButton("Clear##sel"))
				{
					_boxes.Clear();
					tool.SetSelection(Array.Empty<int>(), 0, 0);
				}
			}
			else
			{
				ImGui.TextDisabled("Click or drag to pick tiles. Ctrl adds a group, Shift grows the last one.");
				ImGui.TextDisabled("Middle-drag pans, Ctrl+wheel zooms.");
			}

			// Both scrollbars, so a large atlas at any zoom pans in every direction. NoScrollWithMouse because
			// the wheel is ours: Ctrl zooms, Shift pans sideways, plain scrolls.
			ImGui.PushStyleColor(ImGuiCol.ChildBg, TileAtlasBackground.Color);
			ImGui.BeginChild("tile_atlas", new Num.Vector2(0, 0), true,
				ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.AlwaysVerticalScrollbar |
				ImGuiWindowFlags.NoScrollWithMouse);

			HandleAtlasWheel();
			HandleAtlasPan();

			var origin = ImGui.GetCursorScreenPos();
			var size = new Num.Vector2(_tileset.Texture.Width * _zoom, _tileset.Texture.Height * _zoom);

			ImGui.Image(_atlasTextureId, size);

			var drawList = ImGui.GetWindowDrawList();
			TilePaletteDrawing.DrawSliceGrid(drawList, origin, _tileset, _zoom);

			HandleAtlasSelection(tool, origin);
			DrawSelectionOverlay(drawList, origin);

			ImGui.EndChild();
			ImGui.PopStyleColor();
		}

		private const float MinAtlasZoom = 0.02f;
		private const float MaxAtlasZoom = 8f;

		private bool _isPanning;
		private Num.Vector2 _panStartMouse;
		private Num.Vector2 _panStartScroll;

		/// <summary>Wheel over the atlas: Ctrl zooms at the cursor, Shift pans sideways, plain scrolls. Ignored while unfocused.</summary>
		private void HandleAtlasWheel()
		{
			var io = ImGui.GetIO();
			if (!IsFocused || !ImGui.IsWindowHovered())
				return;

			var wheel = io.MouseWheel;

			if (io.KeyCtrl && Math.Abs(wheel) > 0.001f)
			{
				ZoomAtCursor(wheel);
				return;
			}

			var step = ImGui.GetFontSize() * 3f;

			if (Math.Abs(wheel) > 0.001f)
			{
				if (io.KeyShift)
					ImGui.SetScrollX(ImGui.GetScrollX() - wheel * step);
				else
					ImGui.SetScrollY(ImGui.GetScrollY() - wheel * step);
			}

			if (Math.Abs(io.MouseWheelH) > 0.001f)
				ImGui.SetScrollX(ImGui.GetScrollX() - io.MouseWheelH * step);
		}

		/// <summary>Middle-drag pans the tile box, mirroring the editor camera's middle-mouse pan.</summary>
		private void HandleAtlasPan()
		{
			if (_isPanning)
			{
				if (ImGui.IsMouseDown(ImGuiMouseButton.Middle))
				{
					var delta = ImGui.GetIO().MousePos - _panStartMouse;
					ImGui.SetScrollX(_panStartScroll.X - delta.X);
					ImGui.SetScrollY(_panStartScroll.Y - delta.Y);
					ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
				}
				else
				{
					_isPanning = false;
				}

				return;
			}

			if (!ImGui.IsWindowHovered() || !ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
				return;

			_isPanning = true;
			_panStartMouse = ImGui.GetIO().MousePos;
			_panStartScroll = new Num.Vector2(ImGui.GetScrollX(), ImGui.GetScrollY());
		}

		/// <summary>Zooms about the cursor, scrolling so the texel under it stays put.</summary>
		private void ZoomAtCursor(float wheel)
		{
			var previous = _zoom;
			var next = Math.Clamp(previous * (float)Math.Pow(1.2f, wheel), MinAtlasZoom, MaxAtlasZoom);
			if (Math.Abs(next - previous) < 0.0001f)
				return;

			// Called before the image is drawn, so the cursor still sits at the scrolled content origin.
			var texel = (ImGui.GetIO().MousePos - ImGui.GetCursorScreenPos()) / previous;
			_zoom = next;

			ImGui.SetScrollX(ImGui.GetScrollX() + texel.X * (next - previous));
			ImGui.SetScrollY(ImGui.GetScrollY() + texel.Y * (next - previous));
		}

		/// <summary>Pans the atlas when a drag-selection reaches the panel edges.</summary>
		private static void AutoScrollAtEdges()
		{
			const float margin = 28f;
			const float pixelsPerSecond = 420f;

			var mouse = ImGui.GetIO().MousePos;
			var min = ImGui.GetWindowPos();
			var max = min + ImGui.GetWindowSize();

			static float Axis(float pos, float lo, float hi)
			{
				if (pos < lo + margin)
					return -(1f - Math.Clamp((pos - lo) / margin, 0f, 1f));

				if (pos > hi - margin)
					return 1f - Math.Clamp((hi - pos) / margin, 0f, 1f);

				return 0f;
			}

			var delta = ImGui.GetIO().DeltaTime * pixelsPerSecond;
			var x = Axis(mouse.X, min.X, max.X);
			var y = Axis(mouse.Y, min.Y, max.Y);

			if (x != 0f)
				ImGui.SetScrollX(ImGui.GetScrollX() + x * delta);

			if (y != 0f)
				ImGui.SetScrollY(ImGui.GetScrollY() + y * delta);
		}

		private void HandleAtlasSelection(TilePaintTool tool, Num.Vector2 origin)
		{
			if (!ImGui.IsItemHovered() && !_isSelecting)
				return;

			var mouse = ImGui.GetIO().MousePos;
			var cell = CellAt(mouse, origin);

			if (!IsInsideAtlas(cell))
			{
				if (!_isSelecting)
					return;
			}

			if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && IsInsideAtlas(cell))
			{
				var io = ImGui.GetIO();

				_isSelecting = true;
				_selectionAnchor = cell;
				_selectionEnd = cell;
				_dragAdditive = io.KeyCtrl || io.KeySuper;
				_dragExtend = io.KeyShift;

				// A plain pick replaces everything; Ctrl adds a box and Shift grows the last one.
				if (!_dragAdditive && !_dragExtend)
					_boxes.Clear();
			}
			else if (_isSelecting && ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				_selectionEnd = Clamp(cell);
				AutoScrollAtEdges();
			}

			if (_isSelecting && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
			{
				_isSelecting = false;
				CommitDraggedBox();
				CommitSelection(tool);
			}
		}

		/// <summary>Folds the box just dragged into the box list, honouring the Ctrl / Shift modifier it started with.</summary>
		private void CommitDraggedBox()
		{
			var box = new SelBox(_selectionAnchor, _selectionEnd);

			if (_dragExtend && _boxes.Count > 0)
			{
				_boxes[_boxes.Count - 1] = _boxes[_boxes.Count - 1].Union(box);
				MergeOverlapping();
				return;
			}

			// Ctrl-clicking a single cell that is already picked removes the box it belongs to.
			if (_dragAdditive && box.Count == 1)
			{
				var index = _boxes.FindIndex(b => b.Contains(_selectionAnchor));
				if (index >= 0)
				{
					_boxes.RemoveAt(index);
					return;
				}
			}

			_boxes.Add(box);
			MergeOverlapping();
		}

		/// <summary>Overlapping or touching boxes collapse into one, so a picked region is always a single block.</summary>
		private void MergeOverlapping()
		{
			var merged = true;

			while (merged)
			{
				merged = false;

				for (var i = 0; i < _boxes.Count && !merged; i++)
				{
					for (var j = i + 1; j < _boxes.Count; j++)
					{
						if (!_boxes[i].Overlaps(_boxes[j]))
							continue;

						_boxes[i] = _boxes[i].Union(_boxes[j]);
						_boxes.RemoveAt(j);
						merged = true;
						break;
					}
				}
			}
		}

		/// <summary>
		/// Flattens every box into one stamp spanning their combined bounds. Cells no box covers stay
		/// <see cref="TilePaintTool.NoTile"/>, which is what keeps the gaps between separate picks intact when painted.
		/// </summary>
		private void CommitSelection(TilePaintTool tool)
		{
			if (_boxes.Count == 0)
			{
				tool.SetSelection(Array.Empty<int>(), 0, 0);
				return;
			}

			int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
			foreach (var box in _boxes)
			{
				minX = Math.Min(minX, box.MinX);
				minY = Math.Min(minY, box.MinY);
				maxX = Math.Max(maxX, box.MaxX);
				maxY = Math.Max(maxY, box.MaxY);
			}

			var width = maxX - minX + 1;
			var height = maxY - minY + 1;

			var tiles = new int[width * height];
			for (var i = 0; i < tiles.Length; i++)
				tiles[i] = TilePaintTool.NoTile;

			foreach (var box in _boxes)
			{
				for (var y = box.MinY; y <= box.MaxY; y++)
				{
					for (var x = box.MinX; x <= box.MaxX; x++)
						tiles[(y - minY) * width + (x - minX)] = y * _tileset.Columns + x;
				}
			}

			tool.SetSelection(tiles, width, height);

			if (tool.Tool is TileTool.Picker or TileTool.Eraser)
				tool.Tool = TileTool.Brush;
		}

		private void DrawSelectionOverlay(ImDrawListPtr drawList, Num.Vector2 origin)
		{
			foreach (var box in _boxes)
				DrawBoxOverlay(drawList, origin, box, 0.25f, 1f);

			// The in-flight drag is drawn brighter so it reads as separate from what is already picked.
			if (_isSelecting)
				DrawBoxOverlay(drawList, origin, new SelBox(_selectionAnchor, _selectionEnd), 0.35f, 1f);
		}

		private void DrawBoxOverlay(ImDrawListPtr drawList, Num.Vector2 origin, SelBox box, float fillAlpha, float lineAlpha)
		{
			var tw = _tileset.TileWidth;
			var th = _tileset.TileHeight;
			var spacing = _tileset.Asset.Spacing;
			var margin = _tileset.Asset.Margin;

			var topLeft = new Num.Vector2(
				origin.X + (margin + box.MinX * (tw + spacing)) * _zoom,
				origin.Y + (margin + box.MinY * (th + spacing)) * _zoom);

			var bottomRight = new Num.Vector2(
				origin.X + (margin + box.MaxX * (tw + spacing) + tw) * _zoom,
				origin.Y + (margin + box.MaxY * (th + spacing) + th) * _zoom);

			drawList.AddRectFilled(topLeft, bottomRight,
				ImGui.GetColorU32(new Num.Vector4(0.2f, 0.6f, 1f, fillAlpha)));
			drawList.AddRect(topLeft, bottomRight,
				ImGui.GetColorU32(new Num.Vector4(0.3f, 0.8f, 1f, lineAlpha)), 0f, 0, 2f);
		}

		private Point2 CellAt(Num.Vector2 mouse, Num.Vector2 origin)
		{
			var tw = _tileset.TileWidth + _tileset.Asset.Spacing;
			var th = _tileset.TileHeight + _tileset.Asset.Spacing;
			var margin = _tileset.Asset.Margin;

			var localX = (mouse.X - origin.X) / _zoom - margin;
			var localY = (mouse.Y - origin.Y) / _zoom - margin;

			return new Point2(
				(int)Math.Floor(localX / Math.Max(1, tw)),
				(int)Math.Floor(localY / Math.Max(1, th)));
		}

		private bool IsInsideAtlas(Point2 cell) =>
			cell.X >= 0 && cell.Y >= 0 && cell.X < _tileset.Columns && cell.Y < _tileset.Rows;

		private Point2 Clamp(Point2 cell) => new(
			Math.Clamp(cell.X, 0, Math.Max(0, _tileset.Columns - 1)),
			Math.Clamp(cell.Y, 0, Math.Max(0, _tileset.Rows - 1)));

		#region Tileset binding

		private void EnsureTilesetLoaded(TilePaintTool tool)
		{
			// With no layer targeted the palette holds a tileset staged for the next New Layer - leave it be.
			if (tool.Target == null)
				return;

			var reference = tool.Target.Tileset;

			if (!reference.IsValid)
			{
				if (_tileset != null)
					SyncTilesetFromTarget(tool);

				return;
			}

			var path = reference.ResolvePath();
			var pathChanged = path != null && !string.Equals(path, _tilesetPath, StringComparison.OrdinalIgnoreCase);

			if (pathChanged || _tilesetGeneration != TilesetRuntime.Generation)
				SyncTilesetFromTarget(tool);
		}

		private void AssignTileset(TilePaintTool tool, EngineAssetReference reference)
		{
			if (tool.Target != null)
			{
				tool.Target.Tileset = reference;
				tool.Target.ApplyDeferredMaterial();
				EditorChangeTracker.MarkChanged(tool.Target.Entity, "Assign tileset");
			}

			_boxes.Clear();
			tool.SetSelection(Array.Empty<int>(), 0, 0);
			LoadTileset(reference);
		}

		/// <summary>
		/// Re-reads the tileset's source image from disk. Syncing disposes the cached runtime we are holding, so the
		/// atlas binding is rebuilt from scratch; the picked tiles survive when the grid still has the same shape.
		/// </summary>
		private void SyncTilesetSource(TilePaintTool tool)
		{
			var asset = _tileset?.Asset;
			if (asset == null)
				return;

			var reference = tool.StagedTileset;
			var columns = _tileset.Columns;
			var rows = _tileset.Rows;
			var boxes = new List<SelBox>(_boxes);

			if (!TilesetSourceSync.Sync(asset, _tilesetPath))
				return;

			LoadTileset(reference);

			// A same-shaped grid means the picked tiles still index the same cells, so keep the brush as it was.
			if (_tileset == null || _tileset.Columns != columns || _tileset.Rows != rows || boxes.Count == 0)
				return;

			_boxes.Clear();
			_boxes.AddRange(boxes);
			CommitSelection(tool);
		}

		private void SyncTilesetFromTarget(TilePaintTool tool)
		{
			_boxes.Clear();
			tool.SetSelection(Array.Empty<int>(), 0, 0);
			LoadTileset(tool.Target?.Tileset ?? default);
		}

		private void LoadTileset(EngineAssetReference reference)
		{
			ReleaseAtlas();

			_tileset = null;
			_tilesetPath = null;
			_tilesetGeneration = TilesetRuntime.Generation;

			// Stage on the tool even before a layer exists: it is what the first stroke binds its new layer to.
			_tool.StagedTileset = reference;

			if (!reference.IsValid)
				return;

			_tilesetPath = reference.ResolvePath();
			_tileset = TilesetRuntime.Get(reference);

			if (_tileset?.Texture == null)
				return;

			_tool.FallbackTileWidth = _tileset.TileWidth;
			_tool.FallbackTileHeight = _tileset.TileHeight;

			var manager = Core.GetGlobalManager<ImGuiManager>();
			if (manager != null)
				_atlasTextureId = manager.BindTexture(_tileset.Texture);

			if (_tileset.TileCount > 0)
			{
				_boxes.Clear();
				_boxes.Add(new SelBox(new Point2(0, 0), new Point2(0, 0)));
				_tool.SetSingleSelection(0);
			}
		}

		private void ReleaseAtlas()
		{
			if (_atlasTextureId == IntPtr.Zero)
				return;

			Core.GetGlobalManager<ImGuiManager>()?.UnbindTexture(_atlasTextureId);
			_atlasTextureId = IntPtr.Zero;
		}

		#endregion
	}

	/// <summary>Shared draw-list helper: overlays the tile grid on an atlas image.</summary>
	public static class TilePaletteDrawing
	{
		public static void DrawSliceGrid(ImDrawListPtr drawList, Num.Vector2 origin, TilesetRuntime tileset, float zoom)
		{
			if (tileset?.Texture == null || tileset.Columns <= 0 || tileset.Rows <= 0)
				return;

			// A dense grid at low zoom is just noise - and thousands of draw-list lines.
			if (tileset.Columns > 256 || tileset.Rows > 256)
				return;

			var color = ImGui.GetColorU32(new Num.Vector4(1f, 1f, 1f, 0.25f));

			var tw = tileset.TileWidth;
			var th = tileset.TileHeight;
			var spacing = tileset.Asset.Spacing;
			var margin = tileset.Asset.Margin;

			for (var col = 0; col <= tileset.Columns; col++)
			{
				var x = origin.X + (margin + col * (tw + spacing)) * zoom;
				var yTop = origin.Y + margin * zoom;
				var yBottom = origin.Y + (margin + tileset.Rows * (th + spacing) - spacing) * zoom;

				drawList.AddLine(new Num.Vector2(x, yTop), new Num.Vector2(x, yBottom), color);
			}

			for (var row = 0; row <= tileset.Rows; row++)
			{
				var y = origin.Y + (margin + row * (th + spacing)) * zoom;
				var xLeft = origin.X + margin * zoom;
				var xRight = origin.X + (margin + tileset.Columns * (tw + spacing) - spacing) * zoom;

				drawList.AddLine(new Num.Vector2(xLeft, y), new Num.Vector2(xRight, y), color);
			}
		}
	}
}
