using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using Voltage.Editor.Assets;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.FilePickers;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.Persistence;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Utils;
using Voltage.Tilesets;
using Num = System.Numerics;

namespace Voltage.Editor.Tools.Tilemap
{
	/// <summary>Authors a <c>.vtileset</c>: source image, grid, optional normal map. Previews the slicing live.</summary>
	public partial class TilesetEditorWindow
	{
		public bool IsOpen;

		private static readonly string[] ImageExtensions = { ".png", ".aseprite", ".ase" };

		private readonly FolderBrowser _folderBrowser = new("tileset-save-folder");
		private readonly AssetFileBrowser _imageBrowser =
			new("tileset-image-browser", ImageExtensions, "Images (png, aseprite)");
		// A tileset can store an exclusion list, so it can offer the "sync layers added later" switch.
		private readonly AsepriteLayerPopup _asepritePopup = new("tileset-aseprite-layers")
		{
			AllowSyncNewLayers = true,
		};
		private readonly AssetFileBrowser _tilesetBrowser =
			new("tileset-load-browser", new[] { TilesetAssetIO.FileExtension }, "Tileset (.vtileset)");

		/// <summary>Which slot the in-flight Browse / layer popup is filling.</summary>
		private bool _browsingNormalMap;

		private static readonly PersistentString _lastSaveFolder = new("Tileset_LastSaveFolder", string.Empty);

		private static string SaveFolder
		{
			get
			{
				var stored = _lastSaveFolder.Value;
				if (!string.IsNullOrEmpty(stored) && Directory.Exists(stored))
					return stored;

				return TilesetPaths.DefaultTilesetFolder() ?? string.Empty;
			}
			set => _lastSaveFolder.Value = value;
		}

		private TilesetAsset _asset;
		private string _path;
		private bool _dirty;

		private TilesetRuntime _preview;
		private IntPtr _previewTextureId;
		private IntPtr _previewNormalId;
		private bool _previewStale = true;

		private float _zoom = 2f;
		private bool _showNormalMap;
		private bool _solidEditMode;
		private int _selectedTile = -1;
		private bool _appendFramesOnClick;

		/// <summary>Opens an existing tileset for editing.</summary>
		public void Open(string absolutePath)
		{
			var asset = TilesetAssetIO.Load(absolutePath);
			if (asset == null)
			{
				EditorDebug.Log($"TilesetEditor: could not load '{absolutePath}'.", "Tileset");
				return;
			}

			_asset = asset;
			_path = absolutePath;
			_dirty = false;
			_previewStale = true;
			IsOpen = true;
		}

		public void NewTileset()
		{
			_asset = TilesetAssetIO.CreateDefault("New Tileset");
			_path = null;
			_dirty = true;
			_previewStale = true;
			IsOpen = true;
		}

		public void Draw()
		{
			if (!IsOpen)
				return;

			ImGui.SetNextWindowSize(new Num.Vector2(720, 620), ImGuiCond.FirstUseEver);

			var open = IsOpen;
			if (!Gui.Begin("Tileset Editor ###TilesetEditorWindow", ref open))
			{
				IsOpen = open;
				Gui.End();
				return;
			}

			IsOpen = open;

			DrawToolbar();
			ImGui.Separator();

			if (_asset == null)
			{
				ImGui.TextDisabled("No tileset open.");
				ImGui.Spacing();

				if (Gui.Button("New Tileset"))
					NewTileset();

				ImGui.SameLine();
				if (Gui.Button("Load..."))
					_tilesetBrowser.Open("Open a tileset", SaveFolder, this);

				if (ImGui.IsItemHovered())
					ImGui.SetTooltip("Open an existing .vtileset file.");

				ImGui.SameLine();
				ImGui.TextDisabled("or open a .vtileset from the Asset Browser.");

				Gui.End();
				PumpBrowsers();
				return;
			}

			DrawSettings();
			ImGui.Separator();
			DrawPreview();

			Gui.End();

			// Modals must be driven outside Begin/End.
			PumpBrowsers();
		}

		private void DrawToolbar()
		{
			if (Gui.Button("New"))
				NewTileset();

			ImGui.SameLine();

			if (Gui.Button("Save") && _asset != null)
				Save();

			ImGui.SameLine();

			var canSync = _asset != null && _asset.Texture.IsValid;
			ImGui.BeginDisabled(!canSync);
			if (Gui.Button("Sync Changes"))
				SyncSourceChanges();
			ImGui.EndDisabled();

			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				ImGui.SetTooltip(canSync
					? "Re-reads the source image from disk. Use after editing it externally."
					: "Set a source image first.");
			}

			ImGui.SameLine();
			ImGui.TextDisabled(_path == null
				? "(unsaved)"
				: $"{Path.GetFileName(_path)}{(_dirty ? " *" : "")}");
		}

		/// <summary>Save location for a new tileset. Defaults to the last folder used, persisted across sessions.</summary>
		private void DrawSaveFolder()
		{
			if (_path != null)
				return;

			ImGui.TextUnformatted("Save to");
			ImGui.SameLine(LabelWidth);

			var folder = SaveFolder;
			ImGui.SetNextItemWidth(FieldWidth(1));
			Gui.InputText("##savefolder", ref folder, 512, ImGuiInputTextFlags.ReadOnly);

			if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(folder))
				ImGui.SetTooltip(folder);

			ImGui.SameLine();
			if (Gui.Button("Browse...##savefolder", new Num.Vector2(ButtonWidth, 0)))
				_folderBrowser.Open("Select the folder to save the tileset in", SaveFolder, this);

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Remembered for the next tileset you create.");
		}

		private void DrawSettings()
		{
			var changed = false;

			var name = _asset.Name ?? string.Empty;
			if (Gui.InputText("Name", ref name, 128))
			{
				_asset.Name = name;
				changed = true;
			}

			DrawSaveFolder();

			ImGui.Spacing();
			ImGui.TextUnformatted("Source image");

			changed |= DrawImageSlot("Image", isNormalMap: false);

			ImGui.Spacing();
			ImGui.TextUnformatted("Normal map (optional - enables deferred lighting for these tiles)");

			changed |= DrawImageSlot("Normal map", isNormalMap: true);

			ImGui.TextDisabled("The normal map must be the same size as the source image - it is sampled with the same UVs.");

			ImGui.Spacing();
			ImGui.TextUnformatted("Grid");

			var tileWidth = _asset.TileWidth;
			if (Gui.InputInt("Tile width", ref tileWidth))
			{
				_asset.TileWidth = Math.Max(1, tileWidth);
				changed = true;
			}

			var tileHeight = _asset.TileHeight;
			if (Gui.InputInt("Tile height", ref tileHeight))
			{
				_asset.TileHeight = Math.Max(1, tileHeight);
				changed = true;
			}

			var spacing = _asset.Spacing;
			if (Gui.InputInt("Spacing", ref spacing))
			{
				_asset.Spacing = Math.Max(0, spacing);
				changed = true;
			}

			var margin = _asset.Margin;
			if (Gui.InputInt("Margin", ref margin))
			{
				_asset.Margin = Math.Max(0, margin);
				changed = true;
			}

			if (changed)
			{
				_dirty = true;
				_previewStale = true;
			}
		}

		/// <summary>One image slot. A .png is taken as-is; an .aseprite opens the layer/frame popup.</summary>
		private bool DrawImageSlot(string label, bool isNormalMap)
		{
			var reference = isNormalMap ? _asset.NormalMap : _asset.Texture;
			var source = isNormalMap ? _asset.NormalMapSource : _asset.TextureSource;
			var layers = isNormalMap ? _asset.NormalMapLayers : _asset.TextureLayers;
			var syncsNewLayers = isNormalMap ? _asset.NormalMapSyncsNewLayers : _asset.TextureSyncsNewLayers;
			var frame = isNormalMap ? _asset.NormalMapFrame : _asset.TextureFrame;

			ImGui.TextUnformatted(label);
			ImGui.SameLine(LabelWidth);

			var display = reference.IsValid
				? reference.AssetName ?? Path.GetFileName(reference.AssetPath)
				: "(None)";

			var buttons = reference.IsValid ? 2 : 1;
			ImGui.SetNextItemWidth(FieldWidth(buttons));
			Gui.InputText($"##slot{label}", ref display, 256, ImGuiInputTextFlags.ReadOnly);

			if (ImGui.IsItemHovered() && reference.IsValid)
				ImGui.SetTooltip(reference.AssetPath ?? display);

			ImGui.SameLine();
			if (Gui.Button($"Browse...##{label}", new Num.Vector2(ButtonWidth, 0)))
			{
				_browsingNormalMap = isNormalMap;
				_imageBrowser.Open($"Select the {label.ToLowerInvariant()}", ImageBrowseStart(reference), this);
			}

			var changed = false;

			if (reference.IsValid)
			{
				ImGui.SameLine();
				if (Gui.Button($"Clear##{label}", new Num.Vector2(ButtonWidth, 0)))
				{
					SetImage(isNormalMap, default, TilesetImageSource.Png, null, 0);
					changed = true;
				}
			}

			// Own row: SameLine here would draw this over the top of the text field above.
			if (reference.IsValid && source == TilesetImageSource.Aseprite)
			{
				string summary;
				if (layers == null || layers.Count == 0)
					summary = $"all visible layers, frame {frame}";
				else if (syncsNewLayers)
					summary = $"all visible layers except {string.Join(", ", layers)} - frame {frame}";
				else
					summary = $"{string.Join(", ", layers)} - frame {frame} (layers added later are not included)";

				ImGui.SetCursorPosX(ImGui.GetCursorPosX() + LabelWidth);

				if (Gui.SmallButton($"Layers...##{label}"))
				{
					var path = reference.ResolvePath();
					if (path != null && _asepritePopup.Open(path, layers, frame, syncsNewLayers))
						_browsingNormalMap = isNormalMap;
				}

				ImGui.SameLine();
				ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + FieldWidth(1));
				ImGui.TextDisabled(summary);
				ImGui.PopTextWrapPos();
			}

			return changed;
		}

		// Field width is what's left after the label and buttons, so a long path shrinks the field
		// instead of pushing the buttons out of the window.
		private const float LabelWidth = 160f;
		private const float ButtonWidth = 90f;

		private static float FieldWidth(int buttonCount)
		{
			var spacing = ImGui.GetStyle().ItemSpacing.X;
			var reserved = buttonCount * (ButtonWidth + spacing);
			return Math.Max(80f, ImGui.GetContentRegionAvail().X - reserved);
		}

		private static string ImageBrowseStart(Voltage.Serialization.AssetReference reference)
		{
			var current = reference.ResolvePath();
			if (!string.IsNullOrEmpty(current))
				return Path.GetDirectoryName(current);

			return ProjectManager.Instance?.CurrentProject?.ContentsFolder;
		}

		private void SetImage(bool isNormalMap, Voltage.Serialization.AssetReference reference,
			TilesetImageSource source, List<string> layers, int frame, bool syncsNewLayers = false)
		{
			if (isNormalMap)
			{
				_asset.NormalMap = reference;
				_asset.NormalMapSource = source;
				_asset.NormalMapLayers = layers ?? new List<string>();
				_asset.NormalMapSyncsNewLayers = syncsNewLayers;
				_asset.NormalMapFrame = frame;
			}
			else
			{
				_asset.Texture = reference;
				_asset.TextureSource = source;
				_asset.TextureLayers = layers ?? new List<string>();
				_asset.TextureSyncsNewLayers = syncsNewLayers;
				_asset.TextureFrame = frame;

				// A plain image replaces any prior animation-strip source.
				_asset.TextureIsAsepriteAnimation = false;
			}

			_dirty = true;
			_previewStale = true;
		}

		/// <summary>Feeds browse/popup results back into the asset. Called every frame from Draw.</summary>
		private void PumpBrowsers()
		{
			_folderBrowser.Draw("Select the folder to save the tileset in");
			_imageBrowser.Draw("Select an image");
			_tilesetBrowser.Draw("Open a tileset");

			if (_tilesetBrowser.TryTakeResult(out var tilesetFile) && !string.IsNullOrEmpty(tilesetFile))
				Open(tilesetFile);

			DrawColliderPopup();

			if (_folderBrowser.TryTakeResult(out var folder) && !string.IsNullOrEmpty(folder))
				SaveFolder = folder;

			if (_imageBrowser.TryTakeResult(out var file) && !string.IsNullOrEmpty(file))
			{
				// Browsing is a re-import, so the cached parse has to go first.
				TilesetSourceSync.EvictSourceCache(file);

				var ext = Path.GetExtension(file).ToLowerInvariant();

				if (ext is ".aseprite" or ".ase")
				{
					// Defer: the asset is only set once the layer/frame choice is confirmed.
					if (!_asepritePopup.Open(file))
						EditorDebug.Log($"Tileset: could not read the Aseprite file '{file}'.", "Tileset");
				}
				else
				{
					SetImage(_browsingNormalMap, ReferenceFor(file), TilesetImageSource.Png, null, 0);
				}
			}

			if (_asepritePopup.Draw())
			{
				var syncsNewLayers = _asepritePopup.SyncNewLayers;
				var layers = new List<string>(syncsNewLayers
					? _asepritePopup.ExcludedLayers
					: _asepritePopup.SelectedLayers);

				if (_asepritePopup.Animate && !_browsingNormalMap)
					SetAsepriteAnimation(ReferenceFor(_asepritePopup.FilePath), layers, syncsNewLayers,
						_asepritePopup.AnimStart, _asepritePopup.AnimEnd);
				else
					SetImage(_browsingNormalMap, ReferenceFor(_asepritePopup.FilePath),
						TilesetImageSource.Aseprite, layers, _asepritePopup.Frame, syncsNewLayers);
			}
		}

		/// <summary>
		/// Configures the whole tileset as an Aseprite animation: the source becomes a strip of frames
		/// [start..end], the tile size is set to the frame size, and tile 0 is set to cycle through them.
		/// </summary>
		private void SetAsepriteAnimation(Voltage.Serialization.AssetReference reference, List<string> layers,
			bool syncsNewLayers, int start, int end)
		{
			_asset.Texture = reference;
			_asset.TextureSource = TilesetImageSource.Aseprite;
			_asset.TextureLayers = layers ?? new List<string>();
			_asset.TextureSyncsNewLayers = syncsNewLayers;
			_asset.TextureIsAsepriteAnimation = true;
			_asset.TextureAnimStart = start;
			_asset.TextureAnimEnd = end;

			var path = reference.ResolvePath();
			var count = Math.Max(1, end - start + 1);
			var frameDuration = 0.12f;

			if (!string.IsNullOrEmpty(path))
			{
				try
				{
					var file = Core.Content.LoadAsepriteFile(path);
					if (file != null && file.Frames.Count > start)
					{
						_asset.TileWidth = file.Frames[start].Width;
						_asset.TileHeight = file.Frames[start].Height;
						frameDuration = Math.Max(0.01f, file.Frames[start].Duration / 1000f);
					}
				}
				catch (Exception ex)
				{
					EditorDebug.Log($"Tileset: could not read Aseprite frame size: {ex.Message}", "Tileset");
				}
			}

			// The strip is one row of `count` tiles; tile 0 animates across all of them.
			_asset.Tiles.Clear();
			var info = _asset.GetOrCreateTileInfo(0);
			info.AnimationFrames.Clear();
			for (var i = 0; i < count; i++)
				info.AnimationFrames.Add(i);
			info.AnimationFrameDuration = frameDuration;

			_selectedTile = 0;
			_dirty = true;
			_previewStale = true;
		}

		/// <summary>Re-reads the source images from disk without a re-import, so external edits show up.</summary>
		private void SyncSourceChanges()
		{
			var oldColumns = _asset?.Columns ?? 0;
			var oldRows = _asset?.Rows ?? 0;

			// This window owns unsaved edits to _asset, so the sync must not write the file behind the user's back.
			if (!TilesetSourceSync.Sync(_asset, _path, saveLayerChanges: false, out var layersChanged))
				return;

			if (layersChanged)
				_dirty = true;

			_previewStale = true;
			RebuildPreview();

			// A resized image reslices into a different grid, which shifts every tile index already painted in maps.
			if (_preview != null && (_preview.Columns != oldColumns || _preview.Rows != oldRows))
			{
				_dirty = true;
				EditorDebug.Log(
					$"Tileset: the source image changed size - the grid is now {_preview.Columns}x{_preview.Rows} " +
					$"(was {oldColumns}x{oldRows}). Tile indices in existing tilemaps have shifted. Save to keep it.",
					"Tileset");
			}
		}

		/// <summary>Registers the file with the AssetDatabase (minting its .meta GUID if new) and references it.</summary>
		private static Voltage.Serialization.AssetReference ReferenceFor(string absolutePath)
		{
			var db = AssetDatabase.Instance;
			if (db == null || string.IsNullOrEmpty(absolutePath))
				return default;

			return TileAssetUtils.ToEngineReference(db.GetReference(absolutePath));
		}

		private void DrawPreview()
		{
			if (_previewStale)
				RebuildPreview();

			TileValidation.Draw(TileValidation.ValidateTileset(_asset, _preview));

			if (_preview?.Texture == null)
			{
				ImGui.TextDisabled("Assign a source image to see the sliced preview.");
				return;
			}

			ImGui.TextUnformatted($"{_preview.Columns} x {_preview.Rows} = {_preview.TileCount} tiles " +
			                      $"({_preview.Texture.Width}x{_preview.Texture.Height} px)");

			ImGui.SetNextItemWidth(160f);
			Gui.SliderFloat("Zoom", ref _zoom, 0.1f, 8f, "%.2fx", ImGuiSliderFlags.Logarithmic);

			ImGui.SameLine();
			if (Gui.Button("Fit"))
			{
				var avail = ImGui.GetContentRegionAvail();
				var fitX = avail.X / _preview.Texture.Width;
				var fitY = Math.Max(80f, avail.Y - 4f) / _preview.Texture.Height;
				_zoom = Math.Clamp(Math.Min(fitX, fitY), 0.02f, 8f);
			}

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Zoom the whole tileset to fit the panel.");

			ImGui.SameLine();
			if (Gui.Button("1:1"))
				_zoom = 1f;

			TileAtlasBackground.DrawPicker();

			if (_preview.HasNormalMap)
			{
				VoltageEditorUtils.EyeToggle("shownormalmap", ref _showNormalMap,
					_showNormalMap
						? "Showing the normal map - click for the colour map."
						: "Showing the colour map - click for the normal map.",
					"Normal map");
				ImGui.SameLine();
			}
			else
			{
				_showNormalMap = false;
			}

			Gui.Checkbox("Edit solid tiles", ref _solidEditMode);
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Click tiles to flag them Solid (or hold Ctrl). 'Solid from flagged tiles' in the Tile Palette reads this.");

			ImGui.SameLine();
			ImGui.TextDisabled("| Click a tile to edit its collision shape, terrain and animation.");

			var textureId = _showNormalMap ? _previewNormalId : _previewTextureId;
			if (textureId == IntPtr.Zero)
				return;

			// Atlas takes the space left after the panel, but never below 20% of it (or an absolute floor). Fixed
			// heights, so on a small window the pair overflows into the window's own scrollbar instead of squeezing
			// the atlas to a sliver.
			var hasSelection = _selectedTile >= 0 && _selectedTile < _preview.TileCount;
			var availY = ImGui.GetContentRegionAvail().Y;
			var atlasHeight = hasSelection
				? Math.Max(Math.Max(availY - SelectedPanelHeight, availY * 0.2f), MinAtlasHeight)
				: Math.Max(availY, MinAtlasHeight);

			ImGui.PushStyleColor(ImGuiCol.ChildBg, TileAtlasBackground.Color);
			ImGui.BeginChild("tileset_preview", new Num.Vector2(0, atlasHeight), true,
				ImGuiWindowFlags.HorizontalScrollbar);

			var origin = ImGui.GetCursorScreenPos();
			var size = new Num.Vector2(_preview.Texture.Width * _zoom, _preview.Texture.Height * _zoom);
			ImGui.Image(textureId, size);

			TilePaletteDrawing.DrawSliceGrid(ImGui.GetWindowDrawList(), origin, _preview, _zoom);
			DrawTileOverlaysAndPicking(origin);
			DrawCollisionOverlay(origin);

			ImGui.EndChild();
			ImGui.PopStyleColor();

			if (hasSelection)
			{
				// Fixed height with its own scrollbar so a long frame list never clips.
				ImGui.BeginChild("tileset_tilepanel", new Num.Vector2(0, SelectedPanelHeight), true);
				DrawSelectedTilePanel();
				ImGui.EndChild();
			}
		}

		private const float SelectedPanelHeight = 300f;
		private const float MinAtlasHeight = 140f;

		private void DrawSelectedTilePanel()
		{
			if (_selectedTile < 0 || _selectedTile >= _preview.TileCount)
				return;

			ImGui.Separator();
			ImGui.TextUnformatted($"Tile {_selectedTile}");

			DrawCollisionShapePanel();
			DrawTerrainPanel();
			DrawAnimationPanel();
		}

		/// <summary>Assigns the selected tile to an autotile terrain and edits its 8-neighbour signature via a 3x3 grid.</summary>
		private void DrawTerrainPanel()
		{
			ImGui.TextUnformatted("Autotile terrain");

			var terrains = _asset.Terrains;

			// Manage terrains.
			if (Gui.SmallButton("+ New terrain"))
			{
				var id = 0;
				foreach (var t in terrains)
					id = Math.Max(id, t.Id + 1);
				terrains.Add(new Voltage.Tilesets.TilesetTerrain { Id = id, Name = $"Terrain {id}" });
				_dirty = true;
			}

			var info = _asset.GetOrCreateTileInfo(_selectedTile);

			// Terrain assignment combo.
			var labels = new List<string> { "None" };
			var index = 0;
			for (var i = 0; i < terrains.Count; i++)
			{
				labels.Add(string.IsNullOrEmpty(terrains[i].Name) ? $"Terrain {terrains[i].Id}" : terrains[i].Name);
				if (terrains[i].Id == info.TerrainId)
					index = i + 1;
			}

			ImGui.SetNextItemWidth(160f);
			if (Gui.Combo("Belongs to", ref index, labels.ToArray(), labels.Count))
			{
				info.TerrainId = index == 0 ? -1 : terrains[index - 1].Id;
				_dirty = true;
			}

			if (index > 0)
			{
				var terrain = terrains[index - 1];
				var name = terrain.Name ?? string.Empty;
				ImGui.SetNextItemWidth(160f);
				if (Gui.InputText("Name", ref name, 64))
				{
					terrain.Name = name;
					_dirty = true;
				}

				DrawTerrainMaskGrid(info);
			}
		}

		// 3x3 grid: the centre is this tile; the 8 surrounding checkboxes are the neighbours that must be the
		// same terrain for this tile to be chosen. Bit order matches TilesetTileInfo.TerrainMask.
		private void DrawTerrainMaskGrid(Voltage.Tilesets.TilesetTileInfo info)
		{
			ImGui.TextDisabled("Tick the neighbours that are the SAME terrain for this tile:");

			// (bitIndex or -1 for centre) laid out row-major NW..SE.
			int[] layout = { 7, 0, 1, 6, -1, 2, 5, 4, 3 };

			for (var row = 0; row < 3; row++)
			{
				for (var col = 0; col < 3; col++)
				{
					if (col > 0)
						ImGui.SameLine();

					var bit = layout[row * 3 + col];
					ImGui.PushID(row * 3 + col);

					if (bit < 0)
					{
						ImGui.BeginDisabled();
						var self = true;
						Gui.Checkbox("##self", ref self);
						ImGui.EndDisabled();
					}
					else
					{
						var set = (info.TerrainMask & (1 << bit)) != 0;
						if (Gui.Checkbox("##n", ref set))
						{
							if (set) info.TerrainMask |= (byte)(1 << bit);
							else info.TerrainMask &= (byte)~(1 << bit);
							_dirty = true;
						}
					}

					ImGui.PopID();
				}
			}
		}

		private static readonly string[] CollisionShapeNames =
		{
			"Box", "None", "Slope floor rises right", "Slope floor rises left", "Slope ceiling right", "Slope ceiling left",
			"Circle", "Custom",
		};

		/// <summary>Per-tile collision shape (box / none / slope / circle / custom) and the one-way flag.</summary>
		private void DrawCollisionShapePanel()
		{
			var info = _asset.GetOrCreateTileInfo(_selectedTile);

			var shape = (int)info.CollisionShape;
			ImGui.SetNextItemWidth(200f);
			if (Gui.Combo("Collision shape", ref shape, CollisionShapeNames, CollisionShapeNames.Length))
			{
				info.CollisionShape = (Voltage.Tilesets.TileCollisionShape)shape;
				_dirty = true;
			}

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("How a solid cell of this tile collides. Slopes/circle/custom emit their own collider; boxes greedy-merge.");

			if (info.CollisionShape == Voltage.Tilesets.TileCollisionShape.Custom)
				DrawCustomColliderPicker(info);

			var oneWay = info.OneWay;
			if (Gui.Checkbox("One-way platform", ref oneWay))
			{
				info.OneWay = oneWay;
				_dirty = true;
			}

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Blocks only from above; passed through from below/sides. Behaviour is new - verify in play.");
		}

		/// <summary>Picks / creates / edits the named custom collider a Custom-shaped tile uses (shared across the tileset).</summary>
		private void DrawCustomColliderPicker(Voltage.Tilesets.TilesetTileInfo info)
		{
			var colliders = _asset.CustomColliders;

			var labels = new List<string> { "(none)" };
			var current = 0;
			for (var i = 0; i < colliders.Count; i++)
			{
				labels.Add(string.IsNullOrEmpty(colliders[i].Name) ? $"Collider {i}" : colliders[i].Name);
				if (colliders[i].Name == info.CustomColliderName)
					current = i + 1;
			}

			ImGui.SetNextItemWidth(200f);
			if (Gui.Combo("Custom collider", ref current, labels.ToArray(), labels.Count))
			{
				info.CustomColliderName = current == 0 ? null : colliders[current - 1].Name;
				_dirty = true;
			}

			if (Gui.Button("New..."))
				OpenColliderEditor(info, null);

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Draw a new custom collision polygon on this tile. It is saved on the tileset and reusable by other tiles.");

			var canEdit = _asset.GetCustomCollider(info.CustomColliderName) != null;
			ImGui.SameLine();
			ImGui.BeginDisabled(!canEdit);
			if (Gui.Button("Edit...") && canEdit)
				OpenColliderEditor(info, info.CustomColliderName);
			ImGui.EndDisabled();

			if (colliders.Count == 0)
				ImGui.TextDisabled("No custom colliders yet - use \"New...\" to draw one.");
		}

		/// <summary>Authors the selected tile's animation: a sequence of frame tile-indices + per-frame duration.</summary>
		private void DrawAnimationPanel()
		{
			ImGui.TextUnformatted("Animation");

			var info = _asset.GetTileInfo(_selectedTile);
			var frameCount = info?.AnimationFrames.Count ?? 0;

			if (frameCount == 0)
			{
				ImGui.TextDisabled("Static tile. Add frames to animate it.");
				if (Gui.Button("Add first frame (self)"))
				{
					_asset.GetOrCreateTileInfo(_selectedTile).AnimationFrames.Add(_selectedTile);
					_dirty = true;
				}

				return;
			}

			var duration = info.AnimationFrameDuration;
			ImGui.SetNextItemWidth(120f);
			if (Gui.InputFloat("Frame seconds", ref duration, 0.01f, 0.1f, "%.3f"))
			{
				info.AnimationFrameDuration = Math.Max(0.001f, duration);
				_dirty = true;
			}

			ImGui.TextDisabled($"{frameCount} frames -> {info.AnimationFrameDuration * frameCount:0.00}s loop");

			DrawAnimationSwatch(info.AnimationFrames, info.AnimationFrameDuration);

			var remove = -1;
			for (var i = 0; i < info.AnimationFrames.Count; i++)
			{
				ImGui.PushID(i);

				var frame = info.AnimationFrames[i];
				ImGui.SetNextItemWidth(80f);
				if (Gui.InputInt("##frame", ref frame))
				{
					info.AnimationFrames[i] = Math.Clamp(frame, 0, _preview.TileCount - 1);
					_dirty = true;
				}

				ImGui.SameLine();
				if (Gui.SmallButton("x"))
					remove = i;

				ImGui.SameLine();
				ImGui.TextDisabled($"frame {i}");

				ImGui.PopID();
			}

			if (remove >= 0)
			{
				info.AnimationFrames.RemoveAt(remove);
				_dirty = true;
			}

			if (Gui.Button("Add frame"))
			{
				info.AnimationFrames.Add(_selectedTile);
				_dirty = true;
			}

			ImGui.SameLine();
			Gui.Checkbox("Pick frames by clicking", ref _appendFramesOnClick);

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("While on, clicking a tile in the preview appends it as the next frame.");
		}

		/// <summary>A live swatch cycling through the animation frames at the configured rate.</summary>
		private void DrawAnimationSwatch(System.Collections.Generic.List<int> frames, float frameDuration)
		{
			if (frames == null || frames.Count == 0 || _previewTextureId == IntPtr.Zero)
				return;

			var duration = Math.Max(0.001f, frameDuration);
			var frameIndex = (int)(ImGui.GetTime() / duration) % frames.Count;
			var tile = frames[frameIndex];
			if (!_preview.IsValidIndex(tile))
				return;

			DrawTileSwatch(tile, 64f);
			ImGui.SameLine();
			ImGui.TextDisabled($"playing (frame {frameIndex})");
		}

		/// <summary>Draws one tile of the atlas at the given on-screen size, with a backdrop + border.</summary>
		private void DrawTileSwatch(int tile, float size)
		{
			var rect = _preview.SourceRects[tile];
			var texW = (float)_preview.Texture.Width;
			var texH = (float)_preview.Texture.Height;
			var uv0 = new Num.Vector2(rect.X / texW, rect.Y / texH);
			var uv1 = new Num.Vector2((rect.X + rect.Width) / texW, (rect.Y + rect.Height) / texH);

			var origin = ImGui.GetCursorScreenPos();
			var boxMax = new Num.Vector2(origin.X + size, origin.Y + size);
			var drawList = ImGui.GetWindowDrawList();
			drawList.AddRectFilled(origin, boxMax, ImGui.GetColorU32(new Num.Vector4(0.15f, 0.15f, 0.15f, 1f)));

			ImGui.Image(_previewTextureId, new Num.Vector2(size, size), uv0, uv1);
			drawList.AddRect(origin, boxMax, ImGui.GetColorU32(new Num.Vector4(0.5f, 0.5f, 0.5f, 1f)));
		}

		/// <summary>Tints solid tiles red and animated tiles blue, outlines the selected tile, and routes a click
		/// to toggling Solid, selecting a tile, or appending an animation frame depending on the active mode.</summary>
		private void DrawTileOverlaysAndPicking(Num.Vector2 origin)
		{
			var drawList = ImGui.GetWindowDrawList();
			var tw = _preview.TileWidth;
			var th = _preview.TileHeight;
			var spacing = _preview.Asset.Spacing;
			var margin = _preview.Asset.Margin;

			Num.Vector2 TileMin(int index)
			{
				var col = index % _preview.Columns;
				var row = index / _preview.Columns;
				return new Num.Vector2(
					origin.X + (margin + col * (tw + spacing)) * _zoom,
					origin.Y + (margin + row * (th + spacing)) * _zoom);
			}

			var solidTint = ImGui.GetColorU32(new Num.Vector4(1f, 0.3f, 0.3f, 0.35f));
			var animTint = ImGui.GetColorU32(new Num.Vector4(0.3f, 0.6f, 1f, 0.35f));

			for (var i = 0; i < _preview.TileCount; i++)
			{
				var info = _preview.Asset.GetTileInfo(i);
				if (info == null)
					continue;

				var min = TileMin(i);
				var max = new Num.Vector2(min.X + tw * _zoom, min.Y + th * _zoom);

				if (info.Solid)
					drawList.AddRectFilled(min, max, solidTint);
				if (info.IsAnimated)
					drawList.AddRectFilled(min, max, animTint);
			}

			if (_selectedTile >= 0 && _selectedTile < _preview.TileCount)
			{
				var min = TileMin(_selectedTile);
				var max = new Num.Vector2(min.X + tw * _zoom, min.Y + th * _zoom);
				drawList.AddRect(min, max, ImGui.GetColorU32(new Num.Vector4(1f, 1f, 0.2f, 1f)), 0f, 0, 2f);
			}

			if (!ImGui.IsItemHovered() || !ImGui.IsMouseClicked(ImGuiMouseButton.Left))
				return;

			var index = TileIndexAt(origin, ImGui.GetIO().MousePos);
			if (index < 0)
				return;

			if (_solidEditMode || ImGui.GetIO().KeyCtrl)
			{
				var info = _asset.GetOrCreateTileInfo(index);
				info.Solid = !info.Solid;
				_dirty = true;
			}
			else if (_appendFramesOnClick && _selectedTile >= 0)
			{
				_asset.GetOrCreateTileInfo(_selectedTile).AnimationFrames.Add(index);
				_dirty = true;
			}
			else
			{
				_selectedTile = index;
			}
		}

		private int TileIndexAt(Num.Vector2 origin, Num.Vector2 mouse)
		{
			var tw = _preview.TileWidth + _preview.Asset.Spacing;
			var th = _preview.TileHeight + _preview.Asset.Spacing;
			var margin = _preview.Asset.Margin;

			var cx = (int)Math.Floor(((mouse.X - origin.X) / _zoom - margin) / Math.Max(1, tw));
			var cy = (int)Math.Floor(((mouse.Y - origin.Y) / _zoom - margin) / Math.Max(1, th));

			if (cx < 0 || cy < 0 || cx >= _preview.Columns || cy >= _preview.Rows)
				return -1;

			return cy * _preview.Columns + cx;
		}

		private void RebuildPreview()
		{
			_previewStale = false;

			ReleasePreviewTextures();
			_preview?.Dispose(); // frees an owned Aseprite texture; no-op for content-loaded ones
			_preview = null;

			if (_asset == null || !_asset.Texture.IsValid)
				return;

			_preview = TilesetRuntime.Build(_asset);
			if (_preview?.Texture == null)
				return;

			// Auto-select the first tile so the selected-tile panel shows without a manual click (Aseprite tilesets
			// already select tile 0 on creation; this gives normal grid tilesets parity).
			if (_preview.TileCount > 0 && (_selectedTile < 0 || _selectedTile >= _preview.TileCount))
				_selectedTile = 0;

			var manager = Core.GetGlobalManager<ImGuiManager>();
			if (manager == null)
				return;

			_previewTextureId = manager.BindTexture(_preview.Texture);

			if (_preview.NormalMap != null)
				_previewNormalId = manager.BindTexture(_preview.NormalMap);
		}

		private void ReleasePreviewTextures()
		{
			var manager = Core.GetGlobalManager<ImGuiManager>();
			if (manager == null)
			{
				_previewTextureId = IntPtr.Zero;
				_previewNormalId = IntPtr.Zero;
				return;
			}

			if (_previewTextureId != IntPtr.Zero)
			{
				manager.UnbindTexture(_previewTextureId);
				_previewTextureId = IntPtr.Zero;
			}

			if (_previewNormalId != IntPtr.Zero)
			{
				manager.UnbindTexture(_previewNormalId);
				_previewNormalId = IntPtr.Zero;
			}
		}

		private static TilesetImageSource SourceFor(string path)
		{
			var ext = Path.GetExtension(path)?.ToLowerInvariant();
			return ext is ".aseprite" or ".ase" ? TilesetImageSource.Aseprite : TilesetImageSource.Png;
		}

		private void Save()
		{
			if (_path == null)
			{
				var folder = SaveFolder;
				if (string.IsNullOrEmpty(folder))
				{
					EditorDebug.Log("TilesetEditor: no project open - cannot save.", "Tileset");
					return;
				}

				Directory.CreateDirectory(folder);

				var baseName = SanitizeFileName(_asset.Name) ?? "Tileset";
				_path = TilesetPaths.UniquePath(folder, baseName, TilesetAssetIO.FileExtension);
				SaveFolder = folder;
			}
			else
			{
				RenameOnDiskIfNeeded();
			}

			try
			{
				TilesetAssetIO.Save(_asset, _path);
				_dirty = false;

				// Live maps re-composite from the source images, so those must come off the cache too.
				TilesetSourceSync.EvictSourceCache(_asset.Texture.ResolvePath());
				TilesetSourceSync.EvictSourceCache(_asset.NormalMap.ResolvePath());

				// Drop the cached runtime and re-resolve every live map so the change shows up immediately.
				TilesetRuntime.Invalidate(_path);
				TilemapSceneUtils.ReloadTilesetsInScene();

				AssetDatabase.Instance?.Refresh();
				EditorDebug.Log($"Saved tileset '{Path.GetFileName(_path)}'.", "Tileset");
			}
			catch (Exception ex)
			{
				EditorDebug.Log($"TilesetEditor: failed to save '{_path}': {ex.Message}", "Tileset");
			}
		}

		/// <summary>Renames the file to match the asset Name. The .meta travels with it, so the GUID survives.</summary>
		private void RenameOnDiskIfNeeded()
		{
			var desired = SanitizeFileName(_asset.Name);
			if (desired == null)
				return;

			if (string.Equals(Path.GetFileNameWithoutExtension(_path), desired, StringComparison.Ordinal))
				return;

			var folder = Path.GetDirectoryName(_path);
			if (string.IsNullOrEmpty(folder))
				return;

			var target = Path.Combine(folder, desired + TilesetAssetIO.FileExtension);

			// A case-only rename targets the same file, so only a different one is a clash.
			if (File.Exists(target) && !string.Equals(target, _path, StringComparison.OrdinalIgnoreCase))
			{
				EditorDebug.Log(
					$"TilesetEditor: '{Path.GetFileName(target)}' already exists - keeping the current file name.",
					"Tileset");
				return;
			}

			var oldPath = _path;

			try
			{
				File.Move(oldPath, target);

				var oldMeta = oldPath + ".meta";
				if (File.Exists(oldMeta))
					File.Move(oldMeta, target + ".meta");
			}
			catch (Exception ex)
			{
				EditorDebug.Log($"TilesetEditor: could not rename to '{desired}': {ex.Message}", "Tileset");
				return;
			}

			TilesetRuntime.Invalidate(oldPath);
			_path = target;

			EditorDebug.Log($"Renamed tileset to '{Path.GetFileName(target)}'.", "Tileset");
		}

		/// <summary>Turns a display name into a usable file name, or null when there is nothing to use.</summary>
		private static string SanitizeFileName(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				return null;

			var cleaned = name.Trim();
			foreach (var c in Path.GetInvalidFileNameChars())
				cleaned = cleaned.Replace(c, '_');

			return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
		}
	}
}
