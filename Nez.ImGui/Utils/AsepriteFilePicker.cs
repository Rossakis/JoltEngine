using ImGuiNET;
using Nez.Editor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nez.Aseprite;
using Nez.Textures;
using Nez.Utils;
using Num = System.Numerics;

namespace Nez.ImGuiTools.Utils
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
        }

        private readonly object _owner;
        private readonly string _popupId;
        private readonly string _startingPath;
        
        private List<int> _selectedFrames = new List<int>();
        private List<string> _selectedLayers = new List<string>();
        private bool _isOpen = false;
        
        // Available layers and frames from the currently selected file
        private List<string> _availableLayers = new List<string>();
        private int _totalFrames = 0;
        private string _lastLoadedFile = null;

        // UI state
        private int _frameInputStart = 0;
        private int _frameInputEnd = 0;
        private string _layerSearchFilter = "";

        public string PopupId => _popupId;
        public bool IsOpen => _isOpen;

        /// <summary>
        /// Creates a new Aseprite file picker.
        /// </summary>
        /// <param name="owner">The owner object (used for FilePicker registration)</param>
        /// <param name="popupId">Unique ID for the popup window</param>
        /// <param name="startingPath">Starting directory path (defaults to Content folder)</param>
        public AsepriteFilePicker(object owner, string popupId, string startingPath = null)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _popupId = popupId ?? throw new ArgumentNullException(nameof(popupId));
            _startingPath = startingPath ?? Path.Combine(Environment.CurrentDirectory, "Content");
        }

        /// <summary>
        /// Opens the file picker popup.
        /// </summary>
        public void Open()
        {
            _isOpen = true;
        }

        /// <summary>
        /// Draws the file picker popup. Returns the selected file info if a file was chosen, null otherwise.
        /// </summary>
        public AsepriteSelection Draw()
        {
            AsepriteSelection result = null;
            bool isOpen = _isOpen;

			if (ImGui.BeginPopupModal(_popupId, ref isOpen, ImGuiWindowFlags.AlwaysAutoResize))
			{
			    var picker = FilePicker.GetFilePicker(_owner, _startingPath, ".aseprite");
			    picker.DontAllowTraverselBeyondRootFolder = true;
			
			    ImGui.Text("Aseprite File Selection:");
			    ImGui.Separator();
			
			    // File picker section
			    if (picker.Draw())
			    {
			        // Load metadata when a new file is selected
			        if (!string.IsNullOrEmpty(picker.SelectedFile) && 
			            picker.SelectedFile != _lastLoadedFile && 
			            picker.SelectedFile.EndsWith(".aseprite"))
			        {
			            LoadAsepriteMetadata(picker.SelectedFile);
			            _lastLoadedFile = picker.SelectedFile;
			        }
			        
			        ImGui.EndChild();
			    }
			
			    ImGui.Spacing();
			
			    // Layer and frame selection section (only show if file is loaded)
			    if (_availableLayers.Count > 0)
			    {
			        if (ImGui.BeginChild("selection-section", new Num.Vector2(800, 300), true))
			        {
			            DrawLayerSelection();
			            ImGui.Separator();
			            DrawFrameSelection();
			            ImGui.EndChild();
			        }
			    }
			    else if (!string.IsNullOrEmpty(picker.SelectedFile))
			    {
			        ImGui.TextColored(new Num.Vector4(1.0f, 0.6f, 0.0f, 1.0f), "Loading Aseprite metadata...");
			    }
			
			    ImGui.Separator();
			
			    // Action buttons
			    bool shouldLoad = DrawActionButtons(picker);
			
			    if (shouldLoad)
			    {
			        string contentRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Content"));
			        if (picker.SelectedFile.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase))
			        {
			            string relativePath = Path.GetRelativePath(Environment.CurrentDirectory, picker.SelectedFile).Replace('\\', '/');
			            
			            result = new AsepriteSelection
			            {
			                FilePath = relativePath,
			                LayerNames = _selectedLayers.Count > 0 ? new List<string>(_selectedLayers) : null,
			                FrameNumbers = _selectedFrames.Count > 0 ? new List<int>(_selectedFrames) : new List<int> { 0 },
			                Sprites = GenerateSprites(relativePath)
			            };
			
			            ImGui.CloseCurrentPopup();
			            FilePicker.RemoveFilePicker(picker);
			            _isOpen = false;
			            Reset();
			        }
			        else
			        {
			            NotificationSystem.ShowTimedNotification("File must be in Content folder!");
			        }
			    }
			
			    ImGui.EndPopup();
			}
			
			if (!isOpen)
			{
			    FilePicker.RemoveFilePicker(_owner);
			    _isOpen = false;
			    Reset();
			}

			return result;
        }

        private void LoadAsepriteMetadata(string filePath)
        {
            try
            {
                string relativePath = Path.GetRelativePath(Environment.CurrentDirectory, filePath).Replace('\\', '/');
                var aseFile = Core.Content.LoadAsepriteFile(relativePath);
                
                _availableLayers.Clear();
                _totalFrames = aseFile.Frames.Count;
                
                if (aseFile.Layers != null)
                {
                    foreach (var layer in aseFile.Layers)
                    {
                        if (!string.IsNullOrEmpty(layer.Name))
                        {
                            _availableLayers.Add(layer.Name);
                        }
                    }
                }
                
                _selectedLayers.Clear();
                _selectedFrames.Clear();
                _frameInputStart = 0;
                _frameInputEnd = Math.Max(0, _totalFrames - 1);
            }
            catch (Exception ex)
            {
                NotificationSystem.ShowTimedNotification($"Error loading Aseprite file: {ex.Message}");
                _availableLayers.Clear();
                _totalFrames = 0;
            }
        }

        private void DrawLayerSelection()
        {
            ImGui.TextColored(new Num.Vector4(0.8f, 0.9f, 1.0f, 1.0f), "Layer Selection:");
            ImGui.TextWrapped("Leave empty to merge all visible layers, or select specific layers:");
            
            ImGui.Spacing();
            ImGui.InputText("Search Layers", ref _layerSearchFilter, 256);
            ImGui.Spacing();

            if (ImGui.BeginChild("layer-list", new Num.Vector2(-1, 120), true))
            {
                var filteredLayers = string.IsNullOrEmpty(_layerSearchFilter)
                    ? _availableLayers
                    : _availableLayers.Where(l => l.ToLower().Contains(_layerSearchFilter.ToLower())).ToList();

                foreach (var layer in filteredLayers)
                {
                    bool isSelected = _selectedLayers.Contains(layer);
                    if (ImGui.Selectable(layer, isSelected))
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
                ImGui.TextColored(new Num.Vector4(0.7f, 1.0f, 0.7f, 1.0f), 
                    $"Selected {_selectedLayers.Count} layer(s): {string.Join(", ", _selectedLayers)}");
            }
            else
            {
                ImGui.TextColored(new Num.Vector4(0.7f, 0.7f, 0.7f, 1.0f), "All visible layers will be merged");
            }
            
            ImGui.SameLine();
            if (ImGui.Button("Clear Selection"))
            {
                _selectedLayers.Clear();
            }
        }

        private void DrawFrameSelection()
        {
            ImGui.TextColored(new Num.Vector4(0.8f, 0.9f, 1.0f, 1.0f), "Frame Selection:");
            ImGui.TextWrapped($"Total frames: {_totalFrames}. Select a range or individual frames:");
            
            ImGui.Spacing();
            
            ImGui.Text("Frame Range:");
            ImGui.DragInt("Start Frame", ref _frameInputStart, 1, 0, _totalFrames - 1);
            ImGui.DragInt("End Frame", ref _frameInputEnd, 1, 0, _totalFrames - 1);
            
            if (ImGui.Button("Add Frame Range"))
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
            if (ImGui.Button("Clear Frames"))
            {
                _selectedFrames.Clear();
            }
            
            ImGui.SameLine();
            if (ImGui.Button("Select All Frames"))
            {
                _selectedFrames.Clear();
                for (int i = 0; i < _totalFrames; i++)
                {
                    _selectedFrames.Add(i);
                }
            }

            ImGui.Spacing();
            
            if (_selectedFrames.Count > 0) // Display selected frames
            {
                ImGui.TextColored(new Num.Vector4(0.7f, 1.0f, 0.7f, 1.0f), 
                    $"Selected frames ({_selectedFrames.Count}): {string.Join(", ", _selectedFrames)}");
            }
            else
            {
                ImGui.TextColored(new Num.Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Frame 0 will be used by default");
            }
        }

        private List<Sprite> GenerateSprites(string relativePath)
        {
            var sprites = new List<Sprite>();
            
            try
            {
                var frames = _selectedFrames.Count > 0 ? _selectedFrames : new List<int> { 0 };
                
                foreach (var frameIndex in frames)
                {
                    Sprite sprite = null;
                    
                    if (_selectedLayers.Count > 0)
                    {
                        // Load frame with specific layer
                        // If multiple layers are selected, load the first one
                        sprite = AnimationUtils.LoadAsepriteFrameFromLayer(
                            relativePath, 
                            frameIndex, 
                            _selectedLayers[0]
                        );
                    }
                    else
                    {
                        // Load frame with all visible layers
                        sprite = AnimationUtils.LoadAsepriteFrame(
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
            catch (Exception ex)
            {
                NotificationSystem.ShowTimedNotification($"Error generating sprites: {ex.Message}");
            }
            
            //TODO: make sure each layers produces a separate Sprite
            Debug.Log($"{sprites.Count}");
            return sprites;
        }

        private bool DrawActionButtons(FilePicker picker)
        {
            bool shouldLoad = false;

            float buttonWidth = 100f;
            float totalWidth = ImGui.GetContentRegionAvail().X;
            float rightButtonStart = totalWidth - buttonWidth;

            if (ImGui.Button("Cancel", new Num.Vector2(buttonWidth, 0)))
            {
                Close();
            }

            ImGui.SameLine(rightButtonStart);

            bool canConfirm = !string.IsNullOrEmpty(picker.SelectedFile) && 
                             picker.SelectedFile.EndsWith(".aseprite") &&
                             _availableLayers.Count > 0;
            
            if (!canConfirm)
            {
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
            }

            if (ImGui.Button("Load", new Num.Vector2(buttonWidth, 0)) && canConfirm)
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
            _totalFrames = 0;
            _lastLoadedFile = null;
            _frameInputStart = 0;
            _frameInputEnd = 0;
            _layerSearchFilter = "";
        }

        /// <summary>
        /// Closes the file picker if it's open.
        /// </summary>
        public void Close()
        {
            if (_isOpen)
            {
                ImGui.CloseCurrentPopup();
                FilePicker.RemoveFilePicker(_owner);
                _isOpen = false;
                Reset();
                Debug.Log(IsOpen);
			}
		}
    }
}