using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Nez.ImGuiTools.TypeInspectors;
using Nez.ImGuiTools.UndoActions;
using Nez.Sprites;
using Nez.Systems;
using Nez.Tiled;
using Nez.Utils.Extensions;
using Nez.Textures;
using Num = System.Numerics;

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

        public SpriteRendererFileInspector()
        {
        }

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

            // ONLY draw the file loading UI - no standard properties
            DrawImageSourceSelector(spriteRenderer);
        }

        private void DrawImageSourceSelector(SpriteRenderer spriteRenderer)
        {
            ImGui.TextColored(new Num.Vector4(0.8f, 0.9f, 1.0f, 1.0f), "Sprite Image Selection");

            // Show current texture info if available
            if (spriteRenderer.Sprite?.Texture2D != null)
            {
                ImGui.Text($"Current: {spriteRenderer.Sprite.Texture2D.Name ?? "Unknown"}");
                ImGui.Text($"Size: {spriteRenderer.Sprite.Texture2D.Width}x{spriteRenderer.Sprite.Texture2D.Height}");

                // Show file type if available from component data
                if (spriteRenderer.Data is SpriteRenderer.SpriteRendererComponentData data)
                {
                    ImGui.Text($"Type: {data.FileType}");

                    // Show additional info based on file type
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
                                    ImGui.Text($"Image Layer: {tiledData.ImageLayerName}");
                            }
                            break;
                    }
                }
            }
            else
            {
                ImGui.TextColored(new Num.Vector4(0.7f, 0.7f, 0.7f, 1.0f), "No image loaded");
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

            // File selection buttons - REMOVED JPG SUPPORT
            if (ImGui.Button("Load PNG"))
            {
                _errorMessage = "";
                ImGui.OpenPopup("png-file-picker");
            }

            ImGui.SameLine();
            if (ImGui.Button("Load Aseprite"))
            {
                _errorMessage = "";
                ImGui.OpenPopup("aseprite-file-picker");
            }

            ImGui.SameLine();
            if (ImGui.Button("Load TMX"))
            {
                _errorMessage = "";
                ImGui.OpenPopup("tmx-file-picker");
            }

            // Clear button with undo support
            if (spriteRenderer.Sprite != null)
            {
                ImGui.Spacing();
                if (ImGui.Button("Clear Sprite", new Num.Vector2(-1, 0)))
                {
                    ClearSpriteWithUndo(spriteRenderer);
                    _errorMessage = "";
                }
            }

            // File picker popups - UPDATED TO PNG ONLY
            DrawFilePickerPopup("png-file-picker", ".png", (sr, path) => LoadPngFileFromPicker(sr, path));
            DrawAsepriteFilePickerPopup(spriteRenderer);
            DrawTmxFilePickerPopup(spriteRenderer);
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
                TiledData = null
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

        private void DrawFilePickerPopup(string popupId, string extensions, Action<SpriteRenderer, string> loadAction)
        {
            bool isOpen = true;
            if (ImGui.BeginPopupModal(popupId, ref isOpen, ImGuiWindowFlags.AlwaysAutoResize))
            {
                var picker = FilePicker.GetFilePicker(this, Path.Combine(Environment.CurrentDirectory, "Content"), extensions);
                picker.DontAllowTraverselBeyondRootFolder = true;

                FilePicker.DrawFilePickerContent(picker);

                ImGui.Separator();
                
                // Handle the return value from DrawCustomButtons
                if (DrawCustomButtons(picker, loadAction, "Open"))
                {
                    string contentRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Content"));
                    if (picker.SelectedFile.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        string relativePath = Path.GetRelativePath(Environment.CurrentDirectory, picker.SelectedFile).Replace('\\', '/');
                        loadAction(GetSpriteRenderer(), relativePath);
                        ImGui.CloseCurrentPopup();
                        FilePicker.RemoveFilePicker(this);
                    }
                    else
                    {
                        _errorMessage = "File must be in Content folder!";
                    }
                }

                ImGui.EndPopup();
            }
        }

        private void DrawAsepriteFilePickerPopup(SpriteRenderer spriteRenderer)
        {
            bool isOpen = true;
            if (ImGui.BeginPopupModal("aseprite-file-picker", ref isOpen, ImGuiWindowFlags.AlwaysAutoResize))
            {
                var picker = FilePicker.GetFilePicker(this, Path.Combine(Environment.CurrentDirectory, "Content"), ".ase|.aseprite");
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
                if (DrawCustomButtons(picker, (sr, path) => LoadAsepriteFileFromPicker(sr, path, _frameNumber, string.IsNullOrEmpty(_layerName) ? null : _layerName), "Load"))
                {
                    string contentRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Content"));
                    if (picker.SelectedFile.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        string relativePath = Path.GetRelativePath(Environment.CurrentDirectory, picker.SelectedFile).Replace('\\', '/');
                        LoadAsepriteFileFromPicker(spriteRenderer, relativePath, _frameNumber, string.IsNullOrEmpty(_layerName) ? null : _layerName);
                        ImGui.CloseCurrentPopup();
                        FilePicker.RemoveFilePicker(this);
                    }
                    else
                    {
                        _errorMessage = "File must be in Content folder!";
                    }
                }

                ImGui.EndPopup();
            }
        }

        private void DrawTmxFilePickerPopup(SpriteRenderer spriteRenderer)
        {
            bool isOpen = true;
            if (ImGui.BeginPopupModal("tmx-file-picker", ref isOpen, ImGuiWindowFlags.AlwaysAutoResize))
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
                    FilePicker.RemoveFilePicker(this);
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
                        LoadTmxFileFromPicker(spriteRenderer, relativePath, string.IsNullOrEmpty(_imageLayerName) ? null : _imageLayerName);
                        ImGui.CloseCurrentPopup();
                        FilePicker.RemoveFilePicker(this);

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
        }

        private bool DrawCustomButtons(FilePicker picker, Action<SpriteRenderer, string> loadAction, string confirmText)
        {
            bool shouldLoad = false;

            // Calculate button widths
            float buttonWidth = 100f;
            float totalWidth = ImGui.GetContentRegionAvail().X;
            float rightButtonStart = totalWidth - buttonWidth;

            // Cancel button on the left
            if (ImGui.Button("Cancel", new Num.Vector2(buttonWidth, 0)))
            {
                ImGui.CloseCurrentPopup();
                FilePicker.RemoveFilePicker(this);
            }

            // Confirm button on the right
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

        // Helper method to replicate FilePicker's GetFileSystemEntries functionality
        private List<string> GetFileSystemEntries(FilePicker picker, string fullName)
        {
            var files = new List<string>();
            var dirs = new List<string>();

            foreach (var fse in Directory.GetFileSystemEntries(fullName))
            {
                if (Directory.Exists(fse) && (!picker.HideHiddenFolders || !Path.GetFileName(fse).StartsWith(".")))
                {
                    dirs.Add(fse);
                }
                else if (!picker.OnlyAllowFolders)
                {
                    if (picker.AllowedExtensions != null)
                    {
                        var ext = Path.GetExtension(fse);
                        if (picker.AllowedExtensions.Contains(ext))
                            files.Add(fse);
                    }
                    else
                    {
                        files.Add(fse);
                    }
                }
            }

            dirs.Sort();
            files.Sort();

            var ret = new List<string>(dirs);
            ret.AddRange(files);

            return ret;
        }
        
        private void LoadPngFileFromPicker(SpriteRenderer spriteRenderer, string relativePath)
        {
            try
            {
                var contentManager = spriteRenderer.Entity?.Scene?.Content ?? Core.Content;
                if (contentManager != null)
                {
                    // Store the old state for undo
                    var oldSprite = spriteRenderer.Sprite;
                    var oldData = spriteRenderer.Data != null ? 
                        new SpriteRenderer.SpriteRendererComponentData(spriteRenderer) : 
                        new SpriteRenderer.SpriteRendererComponentData();

                    // Load the new PNG
                    spriteRenderer.LoadPngFile(relativePath);
                    
                    // Store the new state
                    var newSprite = spriteRenderer.Sprite;
                    var newData = new SpriteRenderer.SpriteRendererComponentData(spriteRenderer);

                    // Push undo action
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
                    _errorMessage = ""; // Clear error on success
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

        private void LoadAsepriteFileFromPicker(SpriteRenderer spriteRenderer, string relativePath, int frameNumber, string layerName)
        {
            try
            {
                var contentManager = spriteRenderer.Entity?.Scene?.Content ?? Core.Content;
                if (contentManager != null)
                {
                    // Store the old state for undo
                    var oldSprite = spriteRenderer.Sprite;
                    var oldData = spriteRenderer.Data != null ? 
                        new SpriteRenderer.SpriteRendererComponentData(spriteRenderer) : 
                        new SpriteRenderer.SpriteRendererComponentData();

                    // Load the new Aseprite file
                    spriteRenderer.LoadAsepriteFile(relativePath, layerName, frameNumber);
                    
                    // Store the new state
                    var newSprite = spriteRenderer.Sprite;
                    var newData = new SpriteRenderer.SpriteRendererComponentData(spriteRenderer);

                    // Push undo action
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
                    _errorMessage = ""; // Clear error on success
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

        private void LoadTmxFileFromPicker(SpriteRenderer spriteRenderer, string relativePath, string imageLayerName)
        {
            try
            {
                var contentManager = spriteRenderer.Entity?.Scene?.Content ?? Core.Content;
                if (contentManager != null)
                {
                    // Store the old state for undo
                    var oldSprite = spriteRenderer.Sprite;
                    var oldData = spriteRenderer.Data != null ? 
                        new SpriteRenderer.SpriteRendererComponentData(spriteRenderer) : 
                        new SpriteRenderer.SpriteRendererComponentData();

                    // Load the new TMX file
                    spriteRenderer.LoadTmxFile(relativePath, imageLayerName);
                    
                    // Store the new state
                    var newSprite = spriteRenderer.Sprite;
                    var newData = new SpriteRenderer.SpriteRendererComponentData(spriteRenderer);

                    // Push undo action
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
                    _errorMessage = ""; // Clear error on success
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

        /// <summary>
        /// Scans a TMX file and returns the available image layer names
        /// </summary>
        /// <param name="filePath">Full path to the TMX file</param>
        /// <returns>List of image layer names, or null if file couldn't be read</returns>
        private List<string> GetImageLayersFromTmxFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath) || !filePath.EndsWith(".tmx"))
                return null;

            try
            {
                // Convert absolute path to relative path for NezContentManager
                string relativePath = Path.GetRelativePath(Environment.CurrentDirectory, filePath).Replace('\\', '/');
                
                // Use the relative path with NezContentManager
                var tiledMap = Core.Content.LoadTiledMap(relativePath);

                // Get all image layer names from the main map
                var imageLayerNames = tiledMap.ImageLayers
                    .Where(layer => !string.IsNullOrEmpty(layer.Name))
                    .Select(layer => layer.Name)
                    .ToList();

                // Also check for image layers inside groups (nested layers)
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


        /// <summary>
        /// Recursively adds image layer names from groups and nested groups
        /// </summary>
        private void AddImageLayersFromGroup(TmxGroup group, List<string> imageLayerNames)
        {
	        // Add image layers from this group
	        foreach (var imageLayer in group.ImageLayers)
	        {
		        if (!string.IsNullOrEmpty(imageLayer.Name))
		        {
			        // Optionally prefix with group name for clarity
			        var layerName = !string.IsNullOrEmpty(group.Name)
				        ? $"{group.Name}/{imageLayer.Name}"
				        : imageLayer.Name;
			        imageLayerNames.Add(layerName);
		        }
	        }

	        // Recursively check nested groups
	        foreach (var nestedGroup in group.Groups)
	        {
		        AddImageLayersFromGroup(nestedGroup, imageLayerNames);
	        }
        }

        private SpriteRenderer GetSpriteRenderer()
        {
	        return _target as SpriteRenderer;
        }
	}
}