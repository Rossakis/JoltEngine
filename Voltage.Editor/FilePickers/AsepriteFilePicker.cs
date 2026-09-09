using ImGuiNET;
using Voltage.Textures;
using Voltage.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.Persistence;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Utils;
using Num = System.Numerics;
using Voltage.Project;

namespace Voltage.Editor.FilePickers
{
    /// <summary>
    /// Reusable Aseprite file picker with layer and frame selection support.
    /// </summary>
    public class AsepriteFilePicker
    {
        public class AsepriteSelection
        {
            public string FilePath { get; set; }
            public List<string> LayerNames { get; set; }
            public List<int> FrameNumbers { get; set; }
            public List<Sprite> Sprites { get; set; }
            public bool ShowHiddenLayers;
            public bool IsLayerMergeOn;

			// When we create entities from this selection, assign layers in descending order between min and max
			public int MinRenderingLayer;
            public int MaxRenderingLayer;
        }

		private readonly object _owner;
        private readonly string _popupId;
        private readonly string _startingPath;
        private readonly bool _isAnimation;
        private readonly AssetFileBrowser _fileBrowser;

        private bool _awaitingFile;
        private bool _openOptionsPopup;
        private string _selectedFile;

        
        //  Layers and frames from the currently selected file
        private List<string> _availableLayers = new List<string>();
        private int _totalFrames = 0;
        private string _lastLoadedFile = null;
        private bool _isFileSelected;
        private List<int> _selectedFrames = new List<int>();
        private List<string> _selectedLayers = new List<string>();
        private bool _isOpen = false;

		// UI state
		private int _frameInputStart = 0;
        private int _frameInputEnd = 0;
        private string _layerSearchFilter = "";
        private PersistentBool _isLayerMergeOn;
        private PersistentBool _showHiddenLayers;
        private PersistentInt _minRenderingLayer;
		private PersistentInt _maxRenderingLayer;
		public string PopupId => _popupId;
        public bool IsOpen => _isOpen;

        /// <summary>
        /// Creates a new Aseprite file picker.
        /// </summary>
        /// <param name="owner">The owner object (used for FilePicker registration)</param>
        /// <param name="popupId">Unique ID for the popup window</param>
        /// <param name="startingPath">Starting directory path (defaults to Content folder)</param>
        public AsepriteFilePicker(object owner, string popupId, string startingPath = null, bool isAnimation = false)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _popupId = popupId ?? throw new ArgumentNullException(nameof(popupId));
            _startingPath = startingPath ?? Path.Combine(Environment.CurrentDirectory, "Content");
			_isLayerMergeOn = new($"{PopupId}_IsLayerMergeOn", false);
			_showHiddenLayers = new($"{PopupId}_ShowHiddenLayers", false);
			_minRenderingLayer = new($"{PopupId}_MinRenderingLayer", -89);
			_maxRenderingLayer = new($"{PopupId}_MaxRenderingLayer", 89);
			_isAnimation = isAnimation;
			_fileBrowser = new AssetFileBrowser($"{_popupId}-browser", new[] { ".aseprite" }, "Aseprite");
        }

        /// <summary>Opens the file browser; the layer/frame options popup follows once a file is chosen.</summary>
        public void Open()
        {
            Reset();
            _isOpen = true;
            _awaitingFile = true;
            _fileBrowser.Open("Select Aseprite File", _startingPath, _owner);
        }

        /// <summary>
        /// Draws the file picker popup. Returns the selected file info if a file was chosen, null otherwise.
        /// </summary>
        public AsepriteSelection Draw()
        {
            if (!_isOpen)
                return null;

            if (_awaitingFile)
            {
                PumpFileBrowser();
                return null;
            }

            if (_openOptionsPopup)
            {
                ImGui.OpenPopup(_popupId);
                _openOptionsPopup = false;
            }

            AsepriteSelection result = null;
            bool isOpen = _isOpen;

            if (Gui.BeginPopupModal(_popupId, ref isOpen))
            {
                ImGui.Text("Aseprite File Selection:");
                ImGuiSafe.TextColoredSafe(new Num.Vector4(0.7f, 1.0f, 0.7f, 1.0f), Path.GetFileName(_selectedFile));

                ImGui.SameLine();
                if (Gui.Button("Change"))
                {
                    _awaitingFile = true;
                    _fileBrowser.Open("Select Aseprite File", _startingPath, _owner);
                    ImGui.CloseCurrentPopup();
                }

                ImGui.Separator();
                ImGui.Spacing();

                bool layerMergeOn = _isLayerMergeOn.Value;
                if (Gui.Checkbox("Merge Layers", ref layerMergeOn))
                    _isLayerMergeOn.Value = layerMergeOn;

                if (ImGui.IsItemHovered())
                {
                    ImGuiSafe.SetTooltipSafe("If FALSE, then for each selected layer, a SpriteEntity will be created.\n " +
                                     "If TRUE, all visible layers will be merged into a single SpriteEntity.");
                }

                bool showHiddenLayers = _showHiddenLayers.Value;
                if (VoltageEditorUtils.EyeToggle("loadhiddenlayers", ref showHiddenLayers,
                        showHiddenLayers
                            ? "Layers hidden in Aseprite are listed - click to leave them out."
                            : "Layers hidden in Aseprite are left out - click to list them.",
                        "Hidden Layers"))
                {
                    _showHiddenLayers.Value = showHiddenLayers;
                    
                    // Reload metadata when checkbox changes to update available layers
                    if (_isFileSelected && !string.IsNullOrEmpty(_lastLoadedFile))
                    {
                        LoadAsepriteMetadata(_lastLoadedFile);
                    }
                }

				int minRenderingLayer = _minRenderingLayer.Value;
                if (Gui.InputInt("Merge Layers", ref minRenderingLayer))
	                _isLayerMergeOn.Value = layerMergeOn;

                if (ImGui.IsItemHovered())
                {
	                ImGuiSafe.SetTooltipSafe("If FALSE, then for each selected layer, a SpriteEntity will be created.\n " +
	                                 "If TRUE, all visible layers will be merged into a single SpriteEntity.");
                }

				if (_availableLayers.Count > 0)
                {
                    if (ImGui.BeginChild("selection-section", new Num.Vector2(800, 300), true))
                    {
                        DrawLayerSelection();
                        ImGui.Separator();

                        if (_isAnimation)
                            DrawFrameSelection();

                        ImGui.EndChild();
                    }
                }
                
                ImGui.Separator();

                bool shouldLoad = DrawActionButtons(ref _isOpen);

                if (shouldLoad)
                {
                    string contentRoot = ProjectManager.Instance.CurrentProject.ContentsFolder;
                    if (CrossPlatformPath.IsPathUnder(contentRoot, _selectedFile))
                    {
                        string relativePath = CrossPlatformPath.GetRelativePathForStorage(ProjectManager.Instance.CurrentProject.ProjectPath, _selectedFile);
                        
                        result = new AsepriteSelection
                        {
                            FilePath = relativePath,
                            LayerNames = _selectedLayers.Count > 0 ? new List<string>(_selectedLayers) : null,
                            FrameNumbers = _selectedFrames.Count > 0 ? new List<int>(_selectedFrames) : new List<int> { 0 },
                            Sprites = GenerateSprites(relativePath),
                            IsLayerMergeOn = _isLayerMergeOn.Value,
                            ShowHiddenLayers = _showHiddenLayers.Value,
							MinRenderingLayer = _minRenderingLayer.Value,
							MaxRenderingLayer = _maxRenderingLayer.Value
						};

                        ImGui.CloseCurrentPopup();
                        _isOpen = false;
                        Reset();
                    }
                    else
                    {
                        Debug.Error("File must be in Content folder!");
                    }
                }

	            Gui.EndPopup();
            }

            // "Change" closes the popup too, so that is not a dismissal.
            if ((!isOpen || !_isOpen) && !_awaitingFile)
            {
                _isOpen = false;
                Reset();
            }

            return result;
        }

        /// <summary>Runs the file-selection stage: advances to the options popup, or closes on cancel.</summary>
        private void PumpFileBrowser()
        {
            _fileBrowser.Draw("Select an .aseprite file");

            if (_fileBrowser.TryTakeResult(out var picked))
            {
                if (!string.IsNullOrEmpty(picked) && File.Exists(picked) &&
                    picked.EndsWith(".aseprite", StringComparison.OrdinalIgnoreCase))
                {
                    _selectedFile = picked;

                    if (picked != _lastLoadedFile)
                    {
                        LoadAsepriteMetadata(picked);
                        _lastLoadedFile = picked;
                    }

                    _awaitingFile = false;
                    _openOptionsPopup = true;
                }
                else
                {
                    Debug.Error("Please select a valid .aseprite file.");
                    _isOpen = false;
                    Reset();
                }
            }
            else if (!_fileBrowser.IsBrowsing)
            {
                _isOpen = false;
                Reset();
            }
        }

        private void LoadAsepriteMetadata(string filePath)
        {
            try
            {
                string relativePath = CrossPlatformPath.GetRelativePathForStorage(Environment.CurrentDirectory, filePath);
                var aseFile = Core.Content.LoadAsepriteFile(relativePath);
                
                _availableLayers.Clear();
                _totalFrames = aseFile.Frames.Count;
                
                if (aseFile.Layers != null)
                {
                    foreach (var layer in aseFile.Layers)
                    {
                        if (!string.IsNullOrEmpty(layer.Name))
                        {
                            if (_showHiddenLayers.Value || layer.IsVisible)
                            {
                                _availableLayers.Add(layer.Name);
                            }
                        }
                    }
                }
                
                _selectedLayers.Clear();
                _selectedFrames.Clear();
                _frameInputStart = 0;
                _frameInputEnd = Math.Max(0, _totalFrames - 1);
                _isFileSelected = true; 
            }
            catch (Exception ex)
            {
                EditorDebug.Log($"Error loading Aseprite file: {ex.Message}");
                _availableLayers.Clear();
                _totalFrames = 0;
                _isFileSelected = false; 
            }
        }

        private void DrawLayerSelection()
        {
            ImGui.TextColored(new Num.Vector4(0.8f, 0.9f, 1.0f, 1.0f), "Layer Selection:");
            ImGui.TextWrapped("Leave empty to merge all visible layers, or select specific layers:");
            
            ImGui.Spacing();
            Gui.InputText("Search Layers", ref _layerSearchFilter, 256);
            ImGui.Spacing();

            if (ImGui.BeginChild("layer-list", new Num.Vector2(-1, 120), true))
            {
                var filteredLayers = string.IsNullOrEmpty(_layerSearchFilter)
                    ? _availableLayers
                    : _availableLayers.Where(l => l.ToLower().Contains(_layerSearchFilter.ToLower())).ToList();

                foreach (var layer in filteredLayers)
                {
	                bool isSelected = _selectedLayers.Contains(layer);
                    if (Gui.Selectable(layer, isSelected))
                    {
                        if (isSelected)
                            _selectedLayers.Remove(layer);
                        else
                            _selectedLayers.Add(layer);
                    }
                }
                
                ImGui.EndChild();
            }

            if (_selectedLayers.Count > 0)
            {
                ImGuiSafe.TextColoredSafe(new Num.Vector4(0.7f, 1.0f, 0.7f, 1.0f),
                    $"Selected {_selectedLayers.Count} layer(s): {string.Join(", ", _selectedLayers)}");
            }
            else
            {
                ImGui.TextColored(new Num.Vector4(0.7f, 0.7f, 0.7f, 1.0f), "All visible layers will be merged");
            }
            
            ImGui.SameLine();
            if (Gui.Button("Clear Selection"))
            {
                _selectedLayers.Clear();
            }
        }

        private void DrawFrameSelection()
        {
            ImGui.TextColored(new Num.Vector4(0.8f, 0.9f, 1.0f, 1.0f), "Frame Selection:");
            ImGuiSafe.TextWrappedSafe($"Total frames: {_totalFrames}. Select a range or individual frames:");
            
            ImGui.Spacing();
            
            ImGui.Text("Frame Range:");
            Gui.DragInt("Start Frame", ref _frameInputStart, 1, 0, _totalFrames - 1);
            Gui.DragInt("End Frame", ref _frameInputEnd, 1, 0, _totalFrames - 1);
            
            if (Gui.Button("Add Frame Range"))
            {
                int start = Math.Max(0, Math.Min(_frameInputStart, _frameInputEnd));
                int end = Math.Min(_totalFrames - 1, Math.Max(_frameInputStart, _frameInputEnd));
                
                for (int i = start; i <= end; i++)
                {
                    if (!_selectedFrames.Contains(i))
                    {
                        _selectedFrames.Add(i);
                    }
                }
                _selectedFrames.Sort();
            }
            
            ImGui.SameLine();
            if (Gui.Button("Clear Frames"))
            {
                _selectedFrames.Clear();
            }
            
            ImGui.SameLine();
            if (Gui.Button("Select All Frames"))
            {
                _selectedFrames.Clear();
                for (int i = 0; i < _totalFrames; i++)
                {
                    _selectedFrames.Add(i);
                }
            }

            ImGui.Spacing();
            
            if (_selectedFrames.Count > 0) 
            {
                ImGuiSafe.TextColoredSafe(new Num.Vector4(0.7f, 1.0f, 0.7f, 1.0f),
                    $"Selected frames ({_selectedFrames.Count}): {string.Join(", ", _selectedFrames)}");
            }
            else
            {
                ImGui.TextColored(new Num.Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Frame 0 will be used by default");
            }

			var renderingLayers = ProjectSettings.Instance.Rendering.RenderingLayers;
			var layerNames = renderingLayers.Keys.ToList();
			var layerValues = renderingLayers.Values.ToList();

			int minSelectedIndex = layerValues.IndexOf(_minRenderingLayer.Value);
			if (minSelectedIndex < 0) minSelectedIndex = 0;
			if (Gui.Combo("Min Rendering Layer", ref minSelectedIndex, string.Join('\0', layerNames) + '\0'))
			{
				_minRenderingLayer.Value = layerValues[minSelectedIndex];
			}

			int maxSelectedIndex = layerValues.IndexOf(_maxRenderingLayer.Value);
			if (maxSelectedIndex < 0) maxSelectedIndex = 0;
			if (Gui.Combo("Max Rendering Layer", ref maxSelectedIndex, string.Join('\0', layerNames) + '\0'))
			{
				_maxRenderingLayer.Value = layerValues[maxSelectedIndex];
			}
		}

        private List<Sprite> GenerateSprites(string relativePath)
        {
            var sprites = new List<Sprite>();
            
            try
            {
	            if (_isAnimation)
	            {
		            var frames = _selectedFrames.Count > 0 ? _selectedFrames : new List<int> { 0 };

		            foreach (var frameIndex in frames)
		            {
			            Sprite sprite = null;

			            if (_selectedLayers.Count > 0)
			            {
				            // Load frame with specific layer
				            // If multiple layers are selected, load the first one
				            sprite = AsepriteUtils.LoadAsepriteFrameFromLayer(
					            relativePath,
					            frameIndex,
					            _selectedLayers[0]
				            );
			            }
			            else
			            {
				            // Load frame with all visible layers
				            sprite = AsepriteUtils.LoadAsepriteFrame(
					            relativePath,
					            frameIndex
				            );
			            }

			            if (sprite != null)
			            {
				            sprites.Add(sprite);
			            }
		            }
				}
	            else
	            {
		            Sprite sprite = null;

		            if (_selectedLayers.Count > 0)
		            {
			            sprite = AsepriteUtils.LoadAsepriteFrameFromLayer(
				            relativePath,
				            0,
				            _selectedLayers[0]
			            );
		            }
		            else
		            {
			            sprite = AsepriteUtils.LoadAsepriteFrame(
				            relativePath,
				            0
			            );
		            }

		            if (sprite != null)
		            {
			            sprites.Add(sprite);
		            }
				}
                
            }
            catch (Exception ex)
            {
                EditorDebug.Error($"Error generating sprites: {ex.Message}");
            }
            
            return sprites;
        }

        private bool DrawActionButtons(ref bool openPopup)
        {
            bool shouldLoad = false;

            float buttonWidth = 100f;
            float totalWidth = ImGui.GetContentRegionAvail().X;
            float rightButtonStart = totalWidth - buttonWidth;

            if (Gui.Button("Cancel", new Num.Vector2(buttonWidth, 0)))
            {
                ImGui.CloseCurrentPopup();
                openPopup = false;
            }

            ImGui.SameLine(rightButtonStart);

            bool canConfirm = !string.IsNullOrEmpty(_selectedFile) && _availableLayers.Count > 0;
            
            if (!canConfirm)
            {
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
            }

            if (Gui.Button("Load", new Num.Vector2(buttonWidth, 0)) && canConfirm)
            {
                shouldLoad = true;
            }

            if (!canConfirm)
            {
                ImGui.PopStyleVar();
            }

            return shouldLoad;
        }

        /// <summary>
        /// Resets all selections to defaults.
        /// </summary>
        public void Reset()
        {
            _selectedFrames.Clear();
            _selectedLayers.Clear();
            _availableLayers.Clear();
            _isFileSelected = false;
			_totalFrames = 0;
            _lastLoadedFile = null;
            _frameInputStart = 0;
            _frameInputEnd = 0;
            _layerSearchFilter = "";
            _awaitingFile = false;
            _openOptionsPopup = false;
            _selectedFile = null;
        }
    }
}