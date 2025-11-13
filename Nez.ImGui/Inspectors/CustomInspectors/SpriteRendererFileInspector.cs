using ImGuiNET;
using Microsoft.Xna.Framework;
using Nez.ImGuiTools.TypeInspectors;
using Nez.ImGuiTools.UndoActions;
using Nez.Sprites;
using Nez.Systems;
using Nez.Textures;
using Nez.Tiled;
using Nez.Utils.Extensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nez.Editor;
using Nez.Utils;
using Num = System.Numerics;
using Nez.ImGuiTools.FilePickers;

namespace Nez.ImGuiTools.TypeInspectors
{
    public class SpriteRendererFileInspector : AbstractTypeInspector
    {
        private static int _frameNumber = 0;
        private static string _layerName = "";
        private static string _imageLayerName = "";
        private string _errorMessage = "";

        // TMX picker state variables
        private static int tmxLayerIndex = 0;
        private List<string> _currentImageLayers = null;
        private string _lastSelectedTmxFile = null;

        private enum ImageLoadMode { MainImage, NormalMap }

        private (string popup, ImageLoadMode mode)? _pendingFilePickerPopup = null;
        private ImageLoadMode _nextImageLoadMode = ImageLoadMode.MainImage;

        private ImageLoadMode? _activeFilePickerMode = null;

        public SpriteRendererFileInspector() { }

        public override void Initialize()
        {
            base.Initialize();
            _name = "File Loader";
        }

        public override void DrawMutable()
        {
            var spriteRenderer = _target as SpriteRenderer;
            if (spriteRenderer == null)
                return;

            DrawImageSourceSelector(spriteRenderer);
        }

        private void DrawImageSourceSelector(SpriteRenderer spriteRenderer)
        {
            if (_pendingFilePickerPopup.HasValue)
            {
                ImGui.OpenPopup(_pendingFilePickerPopup.Value.popup);
                _activeFilePickerMode = _pendingFilePickerPopup.Value.mode;
                _pendingFilePickerPopup = null;
            }

            ImGui.TextColored(new Num.Vector4(0.8f, 0.9f, 1.0f, 1.0f), "Sprite Image Selection");

            if (spriteRenderer.Sprite?.Texture2D != null)
            {
                if (ImGui.CollapsingHeader("Image Info", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.Text($"Path: {spriteRenderer.Sprite.Texture2D.Name ?? "Unknown"}");

                    if (spriteRenderer.Data is SpriteRenderer.SpriteRendererComponentData data)
                    {
                        ImGui.Text($"Type: {data.FileType}");

                        switch (data.FileType)
                        {
                            case SpriteRenderer.SpriteRendererComponentData.ImageFileType.Aseprite:
                                if (data.AsepriteData.HasValue)
                                {
                                    var aseData = data.AsepriteData.Value;
                                    ImGui.Text($"Frame: {aseData.FrameNumber}");
                                    if (!string.IsNullOrEmpty(aseData.LayerName))
                                        ImGui.Text($"Layer: {aseData.LayerName}");
                                }
                                break;
                            case SpriteRenderer.SpriteRendererComponentData.ImageFileType.Tiled:
                                if (data.TiledData.HasValue)
                                {
                                    var tiledData = data.TiledData.Value;
                                    if (!string.IsNullOrEmpty(tiledData.ImageLayerName))
                                        ImGui.Text($"Tiled Layer: {tiledData.ImageLayerName}");
                                }
                                break;
                        }
                    }

                    float regionWidth = ImGui.GetContentRegionAvail().X;
                    float buttonLoadWidth = ImGui.CalcTextSize("Load Image").X + ImGui.GetStyle().FramePadding.X * 2.0f;
                    float buttonClearWidth = ImGui.CalcTextSize("Clear Sprite Image").X + ImGui.GetStyle().FramePadding.X * 2.0f;
                    float totalButtonWidth = buttonLoadWidth + ImGui.GetStyle().ItemSpacing.X + buttonClearWidth;
                    float cursorX = (regionWidth - totalButtonWidth) * 0.5f;
                    if (cursorX > 0)
                        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + cursorX);

                    bool loadImagePressed = ImGui.Button("Load Image", new Num.Vector2(buttonLoadWidth, 0));
                    ImGui.SameLine();
                    bool clearImagePressed = ImGui.Button("Clear Sprite Image", new Num.Vector2(buttonClearWidth, 0));

                    if (loadImagePressed)
                    {
                        _nextImageLoadMode = ImageLoadMode.MainImage;
                        ImGui.OpenPopup("image-type-popup");
                    }
                    if (clearImagePressed)
                    {
                        ClearSpriteWithUndo(spriteRenderer);
                        _errorMessage = "";
                    }

                    NezImGui.MediumVerticalSpace();
                }
            }
            else
            {
                ImGui.TextColored(new Num.Vector4(0.7f, 0.7f, 0.7f, 1.0f), "No image loaded");

                float regionWidth = ImGui.GetContentRegionAvail().X;
                float buttonLoadWidth = ImGui.CalcTextSize("Load Image").X + ImGui.GetStyle().FramePadding.X * 2.0f;
                float cursorX = (regionWidth - buttonLoadWidth) * 0.5f;
                if (cursorX > 0)
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + cursorX);

                if (ImGui.Button("Load Image", new Num.Vector2(buttonLoadWidth, 0)))
                {
                    _nextImageLoadMode = ImageLoadMode.MainImage;
                    ImGui.OpenPopup("image-type-popup");
                }
            }

            if (spriteRenderer.NormalMap != null)
            {
                if (ImGui.CollapsingHeader("Normal Map Info", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.Text($"Path: {spriteRenderer.NormalMap.Texture2D.Name ?? "Unknown"}");
                    ImGui.Spacing();

                    float regionWidth = ImGui.GetContentRegionAvail().X;
                    float buttonLoadWidth = ImGui.CalcTextSize("Load Normal Map").X + ImGui.GetStyle().FramePadding.X * 2.0f;
                    float buttonClearWidth = ImGui.CalcTextSize("Clear Normal Map").X + ImGui.GetStyle().FramePadding.X * 2.0f;
                    float totalButtonWidth = buttonLoadWidth + ImGui.GetStyle().ItemSpacing.X + buttonClearWidth;
                    float cursorX = (regionWidth - totalButtonWidth) * 0.5f;
                    if (cursorX > 0)
                        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + cursorX);

                    bool loadNormalPressed = ImGui.Button("Load Normal Map", new Num.Vector2(buttonLoadWidth, 0));
                    ImGui.SameLine();
                    bool clearNormalPressed = ImGui.Button("Clear Normal Map", new Num.Vector2(buttonClearWidth, 0));

                    if (loadNormalPressed)
                    {
                        _nextImageLoadMode = ImageLoadMode.NormalMap;
                        ImGui.OpenPopup("image-type-popup");
                    }
                    if (clearNormalPressed)
                    {
	                    ClearNormalWithUndo(spriteRenderer);
	                    _errorMessage = "";
                    }

                    NezImGui.MediumVerticalSpace();
                }
            }
            else
            {
                ImGui.TextColored(new Num.Vector4(0.7f, 0.9f, 0.7f, 1.0f), "No normal map loaded");

                float regionWidth = ImGui.GetContentRegionAvail().X;
                float buttonLoadWidth = ImGui.CalcTextSize("Load Normal Map").X + ImGui.GetStyle().FramePadding.X * 2.0f;
                float cursorX = (regionWidth - buttonLoadWidth) * 0.5f;
                if (cursorX > 0)
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + cursorX);

                if (ImGui.Button("Load Normal Map", new Num.Vector2(buttonLoadWidth, 0)))
                {
                    _nextImageLoadMode = ImageLoadMode.NormalMap;
                    ImGui.OpenPopup("image-type-popup");
                }
            }

            ImGui.Spacing();

            // Error message section
            if (!string.IsNullOrEmpty(_errorMessage))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(1.0f, 0.6f, 0.6f, 1.0f));
                ImGui.TextWrapped($"WARNING: {_errorMessage}");
                ImGui.PopStyleColor();
                ImGui.Spacing();
            }

            if (ImGui.BeginPopup("image-type-popup"))
            {
                if (ImGui.Selectable("PNG"))
                {
                    _pendingFilePickerPopup = ("file-picker-png", _nextImageLoadMode);
                    ImGui.CloseCurrentPopup();
                }
                if (ImGui.Selectable("Aseprite"))
                {
                    _pendingFilePickerPopup = ("file-picker-aseprite", _nextImageLoadMode);
                    ImGui.CloseCurrentPopup();
                }
                if (ImGui.Selectable("TMX"))
                {
                    _pendingFilePickerPopup = ("file-picker-tmx", _nextImageLoadMode);
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }

            // File picker popups (shared for both image and normal map)
            DrawFilePickerPopup((sr, path) => LoadFileFromPicker(sr, path, _nextImageLoadMode, SpriteRenderer.SpriteRendererComponentData.ImageFileType.Png));
            DrawAsepriteFilePickerPopup(spriteRenderer, _nextImageLoadMode);
            DrawTmxFilePickerPopup(spriteRenderer, _nextImageLoadMode);
        }

        /// <summary>
        /// Clears the sprite with full undo/redo support
        /// </summary>
        private void ClearSpriteWithUndo(SpriteRenderer spriteRenderer)
        {
            // Store the old state for undo
            var oldSprite = spriteRenderer.Sprite;
            var oldData = spriteRenderer.Data != null ?
                new SpriteRenderer.SpriteRendererComponentData(spriteRenderer) :
                new SpriteRenderer.SpriteRendererComponentData();

            // Clear the sprite
            spriteRenderer.SetSprite(null);

            // Create new empty data
            var newData = new SpriteRenderer.SpriteRendererComponentData
            {
                TextureFilePath = "",
                Color = spriteRenderer.Color,
                LocalOffset = spriteRenderer.LocalOffset,
                Origin = spriteRenderer.Origin,
                LayerDepth = spriteRenderer.LayerDepth,
                RenderLayer = spriteRenderer.RenderLayer,
                Enabled = spriteRenderer.Enabled,
                SpriteEffects = spriteRenderer.SpriteEffects,
                FileType = SpriteRenderer.SpriteRendererComponentData.ImageFileType.None,
                AsepriteData = null,
                TiledData = null,
				NormalMapFilePath = oldData.NormalMapFilePath,
				NormalMapFileType = oldData.NormalMapFileType
            };

            spriteRenderer.Data = newData;

            // Push undo action
            EditorChangeTracker.PushUndo(
                new SpriteLoadUndoAction(
                    spriteRenderer,
                    oldSprite,
                    oldData,
                    null, // New sprite is null
                    newData,
                    $"Clear Sprite: {spriteRenderer.Entity?.Name ?? "Unknown Entity"}"
                ),
                spriteRenderer.Entity,
                $"Clear Sprite: {spriteRenderer.Entity?.Name ?? "Unknown Entity"}"
            );

            Debug.Log($"Cleared sprite from {spriteRenderer.Entity?.Name}");
        }

        private void ClearNormalWithUndo(SpriteRenderer spriteRenderer)
        {
	        var oldData = spriteRenderer.Data != null
		        ? new SpriteRenderer.SpriteRendererComponentData(spriteRenderer)
		        : new SpriteRenderer.SpriteRendererComponentData();

	        spriteRenderer.SetNormalMap(null);

	        var newData = new SpriteRenderer.SpriteRendererComponentData
	        {
		        TextureFilePath = oldData.TextureFilePath,
		        Color = spriteRenderer.Color,
		        LocalOffset = spriteRenderer.LocalOffset,
		        Origin = spriteRenderer.Origin,
		        LayerDepth = spriteRenderer.LayerDepth,
		        RenderLayer = spriteRenderer.RenderLayer,
		        Enabled = spriteRenderer.Enabled,
		        SpriteEffects = spriteRenderer.SpriteEffects,
		        FileType = oldData.FileType,
		        AsepriteData = oldData.AsepriteData,
		        TiledData = oldData.TiledData,

		        // Only change the normal map
				NormalMapFilePath = null,
				NormalMapFileType = SpriteRenderer.SpriteRendererComponentData.ImageFileType.None
			};

	        spriteRenderer.Data = newData;

			// Push undo action
			EditorChangeTracker.PushUndo(
		        new SpriteLoadUndoAction(
			        spriteRenderer,
			        spriteRenderer.Sprite,
			        oldData,
			        spriteRenderer.Sprite,
			        newData,
			        $"Clear Normal Map: {spriteRenderer.Entity?.Name ?? "Unknown Entity"}"
		        ),
		        spriteRenderer.Entity,
		        $"Clear Normal Map: {spriteRenderer.Entity?.Name ?? "Unknown Entity"}"
	        );

	        Debug.Log($"Cleared normal map from {spriteRenderer.Entity?.Name}");
        }

		private void DrawFilePickerPopup(Action<SpriteRenderer, string> loadAction)
        {
            bool isOpen = true;
            if (ImGui.BeginPopupModal("file-picker-png", ref isOpen, ImGuiWindowFlags.AlwaysAutoResize))
            {
                var picker = FilePicker.GetFilePicker(this, Path.Combine(Environment.CurrentDirectory, "Content"), ".png");
                picker.DontAllowTraverselBeyondRootFolder = true;

                FilePicker.DrawFilePickerContent(picker);

                ImGui.Separator();

                if (DrawCustomButtons(picker, loadAction, "Open"))
                {
                    string contentRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Content"));
                    if (picker.SelectedFile.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        string relativePath = Path.GetRelativePath(Environment.CurrentDirectory, picker.SelectedFile).Replace('\\', '/');
                        loadAction(GetSpriteRenderer(), relativePath);
                        ImGui.CloseCurrentPopup();
                        FilePicker.RemoveFilePicker(picker);
                    }
                    else
                    {
                        _errorMessage = "File must be in Content folder!";
                    }
                }

                ImGui.EndPopup();
            }

            if (!isOpen)
            {
	            FilePicker.RemoveFilePicker(this);
				_activeFilePickerMode = null;
            }
        }

        private void DrawAsepriteFilePickerPopup(SpriteRenderer spriteRenderer, ImageLoadMode mode)
        {
            bool isOpen = true;
            if (ImGui.BeginPopupModal("file-picker-aseprite", ref isOpen, ImGuiWindowFlags.AlwaysAutoResize))
            {
                var picker = FilePicker.GetFilePicker(this, Path.Combine(Environment.CurrentDirectory, "Content"), ".aseprite");
                picker.DontAllowTraverselBeyondRootFolder = true;

                ImGui.Text("Aseprite Options:");
                ImGui.Separator();

                ImGui.DragInt("Frame Number", ref _frameNumber, 1, 0, 999);
                _frameNumber = Math.Max(0, _frameNumber);

                ImGui.InputText("Layer Name (optional)", ref _layerName, 256);
                ImGui.TextColored(new Num.Vector4(0.7f, 0.7f, 0.7f, 1), "Leave empty for all visible layers");

                ImGui.Separator();

                FilePicker.DrawFilePickerContent(picker);

                ImGui.Separator();
                if (DrawCustomButtons(picker, (sr, path) => LoadAsepriteFileFromPicker(sr, path, _frameNumber, string.IsNullOrEmpty(_layerName) ? null : _layerName, mode), "Load"))
                {
                    string contentRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Content"));
                    if (picker.SelectedFile.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        string relativePath = Path.GetRelativePath(Environment.CurrentDirectory, picker.SelectedFile).Replace('\\', '/');
                        LoadAsepriteFileFromPicker(spriteRenderer, relativePath, _frameNumber, string.IsNullOrEmpty(_layerName) ? null : _layerName, mode);
                        ImGui.CloseCurrentPopup();
                        FilePicker.RemoveFilePicker(picker);
                    }
                    else
                    {
                        _errorMessage = "File must be in Content folder!";
                    }
                }

                ImGui.EndPopup();
            }

            if (!isOpen)
            {
	            FilePicker.RemoveFilePicker(this);
				_activeFilePickerMode = null;
            }
		}

        private void DrawTmxFilePickerPopup(SpriteRenderer spriteRenderer, ImageLoadMode mode)
        {
            bool isOpen = true;
            if (ImGui.BeginPopupModal("file-picker-tmx", ref isOpen, ImGuiWindowFlags.AlwaysAutoResize))
            {
                var picker = FilePicker.GetFilePicker(this, Path.Combine(Environment.CurrentDirectory, "Content"), ".tmx");
                picker.DontAllowTraverselBeyondRootFolder = true;

                ImGui.Text("TMX Options:");
                ImGui.Separator();

                if (ImGui.BeginChild("tmx-content", new Num.Vector2(800, 400), true))
                {
                    if (ImGui.BeginChild("file-picker", new Num.Vector2(480, 0), true))
                    {
                        FilePicker.DrawFilePickerContent(picker);
                        ImGui.EndChild();
                    }

                    ImGui.SameLine();

                    if (ImGui.BeginChild("layer-selection", new Num.Vector2(300, 0), true))
                    {
                        ImGui.Text("Image Layers:");
                        ImGui.Separator();

                        var selectedFile = picker.SelectedFile;

                        if (selectedFile != _lastSelectedTmxFile)
                        {
                            _lastSelectedTmxFile = selectedFile;
                            _currentImageLayers = GetImageLayersFromTmxFile(selectedFile);
                            tmxLayerIndex = 0;
                            _imageLayerName = "";
                        }

                        if (_currentImageLayers != null && _currentImageLayers.Count > 0)
                        {
                            var layerOptions = new List<string> { "(First Image Layer)" };
                            layerOptions.AddRange(_currentImageLayers);

                            ImGui.PushItemWidth(-1);

                            if (ImGui.BeginListBox("##layer-listbox", new Num.Vector2(-1, -1)))
                            {
                                for (int i = 0; i < layerOptions.Count; i++)
                                {
                                    bool isSelected = (i == 0 && string.IsNullOrEmpty(_imageLayerName)) ||
                                                      (i > 0 && _imageLayerName == _currentImageLayers[i - 1]);

                                    if (ImGui.Selectable(layerOptions[i], isSelected))
                                    {
                                        tmxLayerIndex = i;
                                        _imageLayerName = i == 0 ? "" : _currentImageLayers[i - 1];
                                    }
                                }
                                ImGui.EndListBox();
                            }

                            ImGui.PopItemWidth();

                            ImGui.Separator();
                            if (tmxLayerIndex == 0)
                            {
                                ImGui.TextColored(new Num.Vector4(0.7f, 0.7f, 0.7f, 1), "Will use the first image layer found");
                            }
                            else
                            {
                                ImGui.TextColored(new Num.Vector4(0.7f, 1.0f, 0.7f, 1), $"Selected: {_imageLayerName}");
                            }
                        }
                        else if (!string.IsNullOrEmpty(selectedFile))
                        {
                            if (_currentImageLayers != null && _currentImageLayers.Count == 0)
                            {
                                ImGui.TextColored(new Num.Vector4(1.0f, 0.6f, 0.0f, 1), "WARNING: No image layers found");
                            }
                            else
                            {
                                ImGui.TextColored(new Num.Vector4(0.7f, 0.7f, 0.7f, 1), "Loading layers...");
                            }
                        }
                        else
                        {
                            ImGui.TextColored(new Num.Vector4(0.7f, 0.7f, 0.7f, 1), "Select a TMX file to see available image layers");
                        }

                        ImGui.EndChild();
                    }

                    ImGui.EndChild();
                }

                ImGui.Separator();

                bool fileSelected = !string.IsNullOrEmpty(picker.SelectedFile) && picker.SelectedFile.EndsWith(".tmx");

                float buttonWidth = 100f;
                float spacing = ImGui.GetStyle().ItemSpacing.X;
                float totalButtonWidth = (buttonWidth * 2) + spacing;
                float availableWidth = ImGui.GetContentRegionAvail().X;
                float buttonStartX = availableWidth - totalButtonWidth;

                ImGui.SetCursorPosX(buttonStartX);

                if (ImGui.Button("Cancel", new Num.Vector2(buttonWidth, 0)))
                {
                    ImGui.CloseCurrentPopup();
                    FilePicker.RemoveFilePicker(picker);
                    tmxLayerIndex = 0;
                    _imageLayerName = "";
                    _currentImageLayers = null;
                    _lastSelectedTmxFile = null;
                }

                ImGui.SameLine();

                if (!fileSelected)
                {
                    ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
                }

                if (ImGui.Button("Load TMX", new Num.Vector2(buttonWidth, 0)) && fileSelected)
                {
                    string contentRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Content"));
                    if (picker.SelectedFile.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        string relativePath = Path.GetRelativePath(Environment.CurrentDirectory, picker.SelectedFile).Replace('\\', '/');
                        LoadTmxFileFromPicker(spriteRenderer, relativePath, string.IsNullOrEmpty(_imageLayerName) ? null : _imageLayerName, mode);
                        ImGui.CloseCurrentPopup();
                        FilePicker.RemoveFilePicker(picker);

                        tmxLayerIndex = 0;
                        _imageLayerName = "";
                        _currentImageLayers = null;
                        _lastSelectedTmxFile = null;
                    }
                    else
                    {
                        _errorMessage = "File must be in Content folder!";
                    }
                }

                if (!fileSelected)
                {
                    ImGui.PopStyleVar();
                }

                ImGui.EndPopup();
            }

            if (!isOpen)
            {
	            FilePicker.RemoveFilePicker(this);
				_activeFilePickerMode = null;
            }
		}

        private bool DrawCustomButtons(FilePicker picker, Action<SpriteRenderer, string> loadAction, string confirmText)
        {
            bool shouldLoad = false;

            float buttonWidth = 100f;
            float totalWidth = ImGui.GetContentRegionAvail().X;
            float rightButtonStart = totalWidth - buttonWidth;

            if (ImGui.Button("Cancel", new Num.Vector2(buttonWidth, 0)))
            {
                ImGui.CloseCurrentPopup();
                FilePicker.RemoveFilePicker(this);
            }

            ImGui.SameLine(rightButtonStart);

            bool canConfirm = !string.IsNullOrEmpty(picker.SelectedFile);
            if (!canConfirm)
            {
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
            }

            if (ImGui.Button(confirmText, new Num.Vector2(buttonWidth, 0)) && canConfirm)
            {
                shouldLoad = true;
            }

            if (!canConfirm)
            {
                ImGui.PopStyleVar();
            }

            return shouldLoad;
        }

        private List<string> GetImageLayersFromTmxFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath) || !filePath.EndsWith(".tmx"))
                return null;

            try
            {
                string relativePath = Path.GetRelativePath(Environment.CurrentDirectory, filePath).Replace('\\', '/');
                var tiledMap = Core.Content.LoadTiledMap(relativePath);

                var imageLayerNames = tiledMap.ImageLayers
                    .Where(layer => !string.IsNullOrEmpty(layer.Name))
                    .Select(layer => layer.Name)
                    .ToList();

                foreach (var group in tiledMap.Groups)
                {
                    AddImageLayersFromGroup(group, imageLayerNames);
                }

                Debug.Log($"Found {imageLayerNames.Count} image layers in TMX file: {filePath}");
                return imageLayerNames;
            }
            catch (Exception e)
            {
                Debug.Error($"Error reading TMX file for image layers: {e.Message}");
                return null;
            }
        }

        private void AddImageLayersFromGroup(TmxGroup group, List<string> imageLayerNames)
        {
            foreach (var imageLayer in group.ImageLayers)
            {
                if (!string.IsNullOrEmpty(imageLayer.Name))
                {
                    var layerName = !string.IsNullOrEmpty(group.Name)
                        ? $"{group.Name}/{imageLayer.Name}"
                        : imageLayer.Name;
                    imageLayerNames.Add(layerName);
                }
            }

            foreach (var nestedGroup in group.Groups)
            {
                AddImageLayersFromGroup(nestedGroup, imageLayerNames);
            }
        }

        private SpriteRenderer GetSpriteRenderer()
        {
            return _target as SpriteRenderer;
        }

        private void LoadFileFromPicker(SpriteRenderer spriteRenderer, string relativePath, ImageLoadMode mode, SpriteRenderer.SpriteRendererComponentData.ImageFileType fileType)
        {
            var data = spriteRenderer.Data as SpriteRenderer.SpriteRendererComponentData;
            if (data == null)
                throw new Exception("SpriteRendererData is null");

            if (mode == ImageLoadMode.MainImage)
            {
                try
                {
                    var contentManager = spriteRenderer.Entity?.Scene?.Content ?? Core.Content;
                    if (contentManager != null)
                    {
                        var oldSprite = spriteRenderer.Sprite;
                        var oldData = spriteRenderer.Data != null ?
                            new SpriteRenderer.SpriteRendererComponentData(spriteRenderer) :
                            new SpriteRenderer.SpriteRendererComponentData();

                        spriteRenderer.LoadPngFile(relativePath);

                        var newSprite = spriteRenderer.Sprite;
                        var newData = new SpriteRenderer.SpriteRendererComponentData(spriteRenderer);

                        EditorChangeTracker.PushUndo(
                            new SpriteLoadUndoAction(
                                spriteRenderer,
                                oldSprite,
                                oldData,
                                newSprite,
                                newData,
                                $"Load PNG: {Path.GetFileName(relativePath)}"
                            ),
                            spriteRenderer.Entity,
                            $"Load PNG: {Path.GetFileName(relativePath)}"
                        );

                        Debug.Log($"Loaded PNG from editor: {relativePath}");
                        _errorMessage = "";
                    }
                    else
                    {
                        _errorMessage = "Content manager not available";
                    }
                }
                catch (Exception ex)
                {
                    _errorMessage = $"Failed to load PNG: {ex.Message}";
                    Debug.Error($"Error loading PNG from editor: {ex.Message}");
                }
            }
            else
            {
                // Use SetNormalMap instead of LoadNormalMap, with error and undo support
                var contentManager = spriteRenderer.Entity?.Scene?.Content ?? Core.Content;
                Sprite normalMapSprite = null;
                string errorMsg = null;
                try
                {
                    switch (fileType)
                    {
                        case SpriteRenderer.SpriteRendererComponentData.ImageFileType.Png:
                            var texture = contentManager.LoadTexture(relativePath);
                            if (texture != null)
                                normalMapSprite = new Sprite(texture);
                            else
                                errorMsg = "Failed to load PNG normal map texture.";
                            break;
                        case SpriteRenderer.SpriteRendererComponentData.ImageFileType.Aseprite:
                            var aseFile = contentManager.LoadAsepriteFile(relativePath);
                            if (aseFile != null)
                                normalMapSprite = aseFile.Frames.Count > 0 ? aseFile.Frames[0].ToSprite() : null;
                            else
                                errorMsg = "Failed to load Aseprite normal map file.";
                            break;
                        case SpriteRenderer.SpriteRendererComponentData.ImageFileType.Tiled:
                            var tiledMap = contentManager.LoadTiledMap(relativePath);
                            if (tiledMap.ImageLayers.Count > 0 && tiledMap.ImageLayers[0].Image.Texture != null)
                                normalMapSprite = new Sprite(tiledMap.ImageLayers[0].Image.Texture);
                            else
                                errorMsg = "Failed to load Tiled normal map image layer.";
                            break;
                        default:
                            var ext = Path.GetExtension(relativePath).ToLower();
                            if (ext == ".png")
                            {
                                var tex = contentManager.LoadTexture(relativePath);
                                if (tex != null)
                                    normalMapSprite = new Sprite(tex);
                                else
                                    errorMsg = "Failed to load PNG normal map texture.";
                            }
                            else
                            {
                                errorMsg = "Unknown normal map file type.";
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    errorMsg = $"Error loading normal map: {ex.Message}";
                }

                var oldData = spriteRenderer.Data != null
                    ? new SpriteRenderer.SpriteRendererComponentData(spriteRenderer)
                    : new SpriteRenderer.SpriteRendererComponentData();

                spriteRenderer.LoadNormalMap(relativePath, fileType);
                spriteRenderer.SetNormalMap(normalMapSprite);

                var newData = new SpriteRenderer.SpriteRendererComponentData(spriteRenderer);

                EditorChangeTracker.PushUndo(
                    new SpriteLoadUndoAction(
                        spriteRenderer,
                        spriteRenderer.Sprite,
                        oldData,
                        spriteRenderer.Sprite,
                        newData,
                        $"Load Normal Map: {Path.GetFileName(relativePath)}"
                    ),
                    spriteRenderer.Entity,
                    $"Load Normal Map: {Path.GetFileName(relativePath)}"
                );

                if (!string.IsNullOrEmpty(errorMsg) || normalMapSprite == null)
                {
                    _errorMessage = errorMsg ?? "Failed to load normal map.";
                    NotificationSystem.ShowTimedNotification(_errorMessage);
                }
                else
                {
                    _errorMessage = "";
                    NotificationSystem.ShowTimedNotification($"Normal map loaded: {relativePath}");
                }
            }
        }

        private void LoadAsepriteFileFromPicker(SpriteRenderer spriteRenderer, string relativePath, int frameNumber, string layerName, ImageLoadMode mode)
        {
            var data = spriteRenderer.Data as SpriteRenderer.SpriteRendererComponentData;
            if (data == null)
                throw new Exception("SpriteRendererData is null");

            if (mode == ImageLoadMode.MainImage)
            {
                try
                {
                    var contentManager = spriteRenderer.Entity?.Scene?.Content ?? Core.Content;
                    if (contentManager != null)
                    {
                        var oldSprite = spriteRenderer.Sprite;
                        var oldData = spriteRenderer.Data != null ?
                            new SpriteRenderer.SpriteRendererComponentData(spriteRenderer) :
                            new SpriteRenderer.SpriteRendererComponentData();

                        spriteRenderer.LoadAsepriteFile(relativePath, layerName, frameNumber);

                        var newSprite = spriteRenderer.Sprite;
                        var newData = new SpriteRenderer.SpriteRendererComponentData(spriteRenderer);

                        EditorChangeTracker.PushUndo(
                            new SpriteLoadUndoAction(
                                spriteRenderer,
                                oldSprite,
                                oldData,
                                newSprite,
                                newData,
                                $"Load Aseprite: {Path.GetFileName(relativePath)} (frame {frameNumber}, layer: {layerName ?? "all"})"
                            ),
                            spriteRenderer.Entity,
                            $"Load Aseprite: {Path.GetFileName(relativePath)}"
                        );

                        Debug.Log($"Loaded Aseprite from editor: {relativePath} (frame {frameNumber}, layer: {layerName ?? "all"})");
                        _errorMessage = "";
                    }
                    else
                    {
                        _errorMessage = "Content manager not available";
                    }
                }
                catch (Exception ex)
                {
                    _errorMessage = $"Failed to load Aseprite: {ex.Message}";
                    Debug.Error($"Error loading Aseprite from editor: {ex.Message}");
                }
            }
            else
            {
                // SetNormalMap for Aseprite normal maps, with error and undo support
                var contentManager = spriteRenderer.Entity?.Scene?.Content ?? Core.Content;
                Sprite normalMapSprite = null;
                string errorMsg = null;
                try
                {
                    var aseFile = contentManager.LoadAsepriteFile(relativePath);
                    if (aseFile != null)
                    {
                        if (!string.IsNullOrEmpty(layerName))
                        {
                            normalMapSprite = AsepriteUtils.LoadAsepriteFrameFromLayer(relativePath, frameNumber, layerName);
                        }
                        else
                        {
                            normalMapSprite = aseFile.Frames.Count > frameNumber ? aseFile.Frames[frameNumber].ToSprite() : null;
                        }
                        if (normalMapSprite == null)
                            errorMsg = "Failed to load Aseprite normal map frame/layer.";
                    }
                    else
                    {
                        errorMsg = "Failed to load Aseprite normal map file.";
                    }
                }
                catch (Exception ex)
                {
                    errorMsg = $"Error loading Aseprite normal map: {ex.Message}";
                }

                var oldNormalMap = spriteRenderer.NormalMap;
                var oldData = spriteRenderer.Data != null
                    ? new SpriteRenderer.SpriteRendererComponentData(spriteRenderer)
                    : new SpriteRenderer.SpriteRendererComponentData();

                if (spriteRenderer.Data is SpriteRenderer.SpriteRendererComponentData)
                {
                    data.NormalMapFilePath = relativePath;
                    data.NormalMapFileType = SpriteRenderer.SpriteRendererComponentData.ImageFileType.Aseprite;
                }

                spriteRenderer.SetNormalMap(normalMapSprite);

				var newData = new SpriteRenderer.SpriteRendererComponentData(spriteRenderer);

                EditorChangeTracker.PushUndo(
                    new SpriteLoadUndoAction(
                        spriteRenderer,
                        spriteRenderer.Sprite,
                        oldData,
                        spriteRenderer.Sprite,
                        newData,
                        $"Load Normal Map (Aseprite): {Path.GetFileName(relativePath)} (frame {frameNumber}, layer: {layerName ?? "all"})"
                    ),
                    spriteRenderer.Entity,
                    $"Load Normal Map (Aseprite): {Path.GetFileName(relativePath)}"
                );

                if (!string.IsNullOrEmpty(errorMsg) || normalMapSprite == null)
                {
                    _errorMessage = errorMsg ?? "Failed to load normal map.";
                    Debug.Error(_errorMessage);
                }
                else
                {
                    _errorMessage = "";
                    Debug.Log($"Normal map (Aseprite) loaded: {relativePath} (frame {frameNumber}, layer: {layerName ?? "all"})");
                }
            }
        }

        private void LoadTmxFileFromPicker(SpriteRenderer spriteRenderer, String relativePath, String imageLayerName, ImageLoadMode mode)
        {
            var data = spriteRenderer.Data as SpriteRenderer.SpriteRendererComponentData;
            if (data == null)
                throw new Exception("SpriteRendererData is null");

            if (mode == ImageLoadMode.MainImage)
            {
                try
                {
                    var contentManager = spriteRenderer.Entity?.Scene?.Content ?? Core.Content;
                    if (contentManager != null)
                    {
                        var oldSprite = spriteRenderer.Sprite;
                        var oldData = spriteRenderer.Data != null ?
                            new SpriteRenderer.SpriteRendererComponentData(spriteRenderer) :
                            new SpriteRenderer.SpriteRendererComponentData();

                        spriteRenderer.LoadTmxFile(relativePath, imageLayerName);

                        var newSprite = spriteRenderer.Sprite;
                        var newData = new SpriteRenderer.SpriteRendererComponentData(spriteRenderer);

                        EditorChangeTracker.PushUndo(
                            new SpriteLoadUndoAction(
                                spriteRenderer,
                                oldSprite,
                                oldData,
                                newSprite,
                                newData,
                                $"Load TMX: {Path.GetFileName(relativePath)} (layer: {imageLayerName ?? "first"})"
                            ),
                            spriteRenderer.Entity,
                            $"Load TMX: {Path.GetFileName(relativePath)}"
                        );

                        Debug.Log($"Loaded TMX from editor: {relativePath} (layer: {imageLayerName ?? "first"})");
                        _errorMessage = "";
                    }
                    else
                    {
                        _errorMessage = "Content manager not available";
                    }
                }
                catch (Exception ex)
                {
                    _errorMessage = $"Failed to load TMX: {ex.Message}";
                    Debug.Error($"Error loading TMX from editor: {ex.Message}");
                }
            }
            else
            {
                // SetNormalMap for TMX normal maps, with error and undo support
                var contentManager = spriteRenderer.Entity?.Scene?.Content ?? Core.Content;
                Sprite normalMapSprite = null;
                string errorMsg = null;
                try
                {
                    var tiledMap = contentManager.LoadTiledMap(relativePath);
                    if (tiledMap.ImageLayers.Count > 0)
                    {
                        if (!string.IsNullOrEmpty(imageLayerName))
                        {
                            var imageLayer = tiledMap.ImageLayers.FirstOrDefault(l => l.Name == imageLayerName);
                            if (imageLayer != null && imageLayer.Image.Texture != null)
                                normalMapSprite = new Sprite(imageLayer.Image.Texture);
                            else
                                errorMsg = "Failed to find specified image layer for normal map.";
                        }
                        else if (tiledMap.ImageLayers[0].Image.Texture != null)
                        {
                            normalMapSprite = new Sprite(tiledMap.ImageLayers[0].Image.Texture);
                        }
                        else
                        {
                            errorMsg = "No valid image layer found for normal map.";
                        }
                    }
                    else
                    {
                        errorMsg = "No image layers found in TMX file for normal map.";
                    }
                }
                catch (Exception ex)
                {
                    errorMsg = $"Error loading TMX normal map: {ex.Message}";
                }

                var oldData = spriteRenderer.Data != null
                    ? new SpriteRenderer.SpriteRendererComponentData(spriteRenderer)
                    : new SpriteRenderer.SpriteRendererComponentData();

                spriteRenderer.SetNormalMap(normalMapSprite);

                var newNormalMap = spriteRenderer.NormalMap;
                var newData = new SpriteRenderer.SpriteRendererComponentData(spriteRenderer);

                EditorChangeTracker.PushUndo(
                    new SpriteLoadUndoAction(
                        spriteRenderer,
                        spriteRenderer.Sprite,
                        oldData,
                        spriteRenderer.Sprite,
                        newData,
                        $"Load Normal Map (TMX): {Path.GetFileName(relativePath)} (layer: {imageLayerName ?? "first"})"
                    ),
                    spriteRenderer.Entity,
                    $"Load Normal Map (TMX): {Path.GetFileName(relativePath)}"
                );

                if (!string.IsNullOrEmpty(errorMsg) || normalMapSprite == null)
                {
                    _errorMessage = errorMsg ?? "Failed to load normal map.";
                    Debug.Error(_errorMessage);
                }
                else
                {
                    _errorMessage = "";
                    Debug.Log($"Normal map (TMX) loaded: {relativePath} (layer: {imageLayerName ?? "first"})");
                }
            }
        }
    }
}