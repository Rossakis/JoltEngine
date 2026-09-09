using ImGuiNET;
using Voltage.Utils;
using System;
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
    /// Reusable TMX (Tiled Map) file picker.
    /// </summary>
    public class TmxFilePicker
    {
        public enum ImageLoadMode
        {
            None = 0,
            SeparateLayers = 1,
            BakedLayers = 2
        }

        public class TmxSelection
        {
	        public string FilePath; 
	        public bool LoadColliders; 
	        public ImageLoadMode ImageMode;
            public int LayerToRenderTo;
        }

        private readonly object _owner;
        private readonly string _popupId;
        private readonly string _startingPath;
        private readonly AssetFileBrowser _fileBrowser;
        private bool _isOpen = false;
        private bool _awaitingFile = false;
        private bool _openOptionsPopup = false;
        private string _selectedFile;

        // UI state - persistent options
        private PersistentBool _loadColliders;
        private PersistentInt _imageLoadMode;
        private PersistentInt _layerToRenderTo;

        public string PopupId => _popupId;
        public bool IsOpen => _isOpen;

        /// <summary>
        /// Creates a new TMX file picker.
        /// </summary>
        /// <param name="owner">The owner object (used for FilePicker registration)</param>
        /// <param name="popupId">Unique ID for the popup window</param>
        /// <param name="startingPath">Starting directory path (defaults to Content folder)</param>
        public TmxFilePicker(object owner, string popupId, string startingPath = null)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _popupId = popupId ?? throw new ArgumentNullException(nameof(popupId));
            _startingPath = startingPath ?? Path.Combine(Environment.CurrentDirectory, "Content");
            _loadColliders = new PersistentBool($"{PopupId}_LoadColliders", true);
            _imageLoadMode = new PersistentInt($"{PopupId}_ImageLoadMode", (int)ImageLoadMode.SeparateLayers);
            _layerToRenderTo = new PersistentInt($"{PopupId}_LayerToRenderTo", 0);
            _fileBrowser = new AssetFileBrowser($"{_popupId}-browser", new[] { ".tmx" }, "Tiled maps");
        }

        /// <summary>Opens the file browser; the load-options popup follows once a file is chosen.</summary>
        public void Open()
        {
            Reset();
            _isOpen = true;
            _awaitingFile = true;
            _fileBrowser.Open("Select TMX File", _startingPath, _owner);
        }

        /// <summary>
        /// Draws the file picker popup. Returns the selected file info if a file was chosen, null otherwise.
        /// </summary>
        public TmxSelection Draw()
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

            TmxSelection result = null;
            bool isOpen = _isOpen;

            if (Gui.BeginPopupModal(_popupId, ref isOpen, ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.Text("TMX File Selection:");
                ImGuiSafe.TextColoredSafe(new Num.Vector4(0.7f, 1.0f, 0.7f, 1.0f), Path.GetFileName(_selectedFile));

                ImGui.SameLine();
                if (Gui.Button("Change"))
                {
                    _awaitingFile = true;
                    _fileBrowser.Open("Select TMX File", _startingPath, _owner);
                    ImGui.CloseCurrentPopup();
                }

                {
                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Spacing();

                    ImGui.TextColored(new Num.Vector4(0.8f, 0.9f, 1.0f, 1.0f), "Load Options:");

                    bool loadColliders = _loadColliders.Value;
                    if (Gui.Checkbox("Load Colliders", ref loadColliders))
                        _loadColliders.Value = loadColliders;

                    if (ImGui.IsItemHovered())
                    {
                        ImGuiSafe.SetTooltipSafe("If TRUE, collider object layers from the TMX file will be loaded.\n" +
                                       "This includes both 'Colliders' and 'Ledges' object groups.");
                    }

                    VoltageEditorUtils.MediumVerticalSpace();

                    ImGui.TextWrapped("Select Image Mode:");
                    if (ImGui.IsItemHovered())
                    {
	                    ImGuiSafe.SetTooltipSafe(
		                    "Choose how image layers from the TMX file should be loaded:\n\n" +
		                    " None: No image layers will be loaded\n" +
		                    " Separate Layers: Each image layer becomes its own SpriteEntity\n" +
		                    " Baked Layers: All image layers are merged into a single texture"
	                    );
                    }

                    int currentMode = _imageLoadMode.Value;
                    
                    if (Gui.RadioButton("None", ref currentMode, (int)ImageLoadMode.None))
                        _imageLoadMode.Value = currentMode;
                    
                    if (Gui.RadioButton("Load Separate Image Layers", ref currentMode, (int)ImageLoadMode.SeparateLayers))
                        _imageLoadMode.Value = currentMode;
                    
                    if (Gui.RadioButton("Bake Image Layers", ref currentMode, (int)ImageLoadMode.BakedLayers))
                        _imageLoadMode.Value = currentMode;

                    var renderingLayers = ProjectSettings.Instance.Rendering.RenderingLayers;
                    var layerNames = renderingLayers.Keys.ToList();
                    var layerValues = renderingLayers.Values.ToList();

                    int minSelectedIndex = layerValues.IndexOf(_layerToRenderTo.Value);
                    if (minSelectedIndex < 0) minSelectedIndex = 0;
                    if (Gui.Combo("Layer To Render At:", ref minSelectedIndex, string.Join('\0', layerNames) + '\0'))
                    {
	                    _layerToRenderTo.Value = layerValues[minSelectedIndex];
                    }
				}

                ImGui.Separator();
                bool shouldLoad = DrawActionButtons();

                if (shouldLoad)
                {
                    string contentRoot = ProjectManager.Instance.CurrentProject.ContentsFolder;
                    if (CrossPlatformPath.IsPathUnder(contentRoot, _selectedFile))
                    {
                        string relativePath = CrossPlatformPath.GetRelativePathForStorage(ProjectManager.Instance.CurrentProject.ProjectPath, _selectedFile);

                        result = new TmxSelection
                        {
                            FilePath = relativePath,
                            LoadColliders = _loadColliders.Value,
                            ImageMode = (ImageLoadMode)_imageLoadMode.Value,
							LayerToRenderTo = _layerToRenderTo.Value
                        };

                        ImGui.CloseCurrentPopup();
                        _isOpen = false;
                        Reset();
                    }
                    else
                    {
                        Debug.Error("Selected TMX file is not inside Content folder!");
                    }
                }

                Gui.EndPopup();
            }

            // "Change" closes the popup too, so that is not a dismissal.
            if (!isOpen && !_awaitingFile)
            {
                _isOpen = false;
                Reset();
            }

            return result;
        }

        /// <summary>Runs the file-selection stage: advances to the options popup, or closes on cancel.</summary>
        private void PumpFileBrowser()
        {
            _fileBrowser.Draw("Select a .tmx file");

            if (_fileBrowser.TryTakeResult(out var picked))
            {
                if (!string.IsNullOrEmpty(picked) && File.Exists(picked) && !Directory.Exists(picked) &&
                    picked.EndsWith(".tmx", StringComparison.OrdinalIgnoreCase))
                {
                    _selectedFile = picked;
                    _awaitingFile = false;
                    _openOptionsPopup = true;
                }
                else
                {
                    Debug.Error("Please select a valid .tmx file.");
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

        private bool DrawActionButtons()
        {
            bool shouldLoad = false;

            float buttonWidth = 100f;
            float totalWidth = ImGui.GetContentRegionAvail().X;
            float rightButtonStart = totalWidth - buttonWidth;

            if (Gui.Button("Cancel", new Num.Vector2(buttonWidth, 0)))
            {
                Close();
            }

            ImGui.SameLine(rightButtonStart);

            bool canConfirm = !string.IsNullOrEmpty(_selectedFile) && File.Exists(_selectedFile);

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
        /// Resets selection state.
        /// </summary>
        public void Reset()
        {
            _awaitingFile = false;
            _openOptionsPopup = false;
            _selectedFile = null;
        }

        /// <summary>
        /// Closes the file picker if it's open.
        /// </summary>
        public void Close()
        {
            if (_isOpen)
            {
                ImGui.CloseCurrentPopup();
                _isOpen = false;
                Reset();
            }
        }
    }
}