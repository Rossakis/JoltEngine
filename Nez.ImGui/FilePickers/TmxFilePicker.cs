using ImGuiNET;
using Nez.Editor;
using Nez.ImGuiTools.Persistence;
using Nez.ImGuiTools.Utils;
using Nez.Utils;
using System;
using System.IO;
using Num = System.Numerics;

namespace Nez.ImGuiTools.FilePickers
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
            public string FilePath { get; set; }
            public bool LoadColliders { get; set; }
            public ImageLoadMode ImageMode { get; set; }
        }

        private readonly object _owner;
        private readonly string _popupId;
        private readonly string _startingPath;
        private bool _isOpen = false;
        private bool _isFileSelected = false;

        // UI state - persistent options
        private PersistentBool _loadColliders;
        private PersistentInt _imageLoadMode;

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
        public TmxSelection Draw()
        {
            TmxSelection result = null;
            bool isOpen = _isOpen;

            if (ImGui.BeginPopupModal(_popupId, ref isOpen, ImGuiWindowFlags.AlwaysAutoResize))
            {
                var picker = FilePicker.GetFilePicker(_owner, _startingPath, ".tmx");
                picker.DontAllowTraverselBeyondRootFolder = true;

                ImGui.Text("TMX File Selection:");
                ImGui.Separator();

                if (picker.Draw())
                {
                    var file = picker.SelectedFile;

                    if (!string.IsNullOrEmpty(file) && 
                        File.Exists(file) && 
                        !Directory.Exists(file) && 
                        file.EndsWith(".tmx", StringComparison.OrdinalIgnoreCase))
                    {
                        _isFileSelected = true;
                    }
                }

                if (_isFileSelected)
                {
                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Spacing();

                    ImGui.TextColored(new Num.Vector4(0.8f, 0.9f, 1.0f, 1.0f), "Load Options:");
                    
                    bool loadColliders = _loadColliders.Value;
                    if (ImGui.Checkbox("Load Colliders", ref loadColliders))
                        _loadColliders.Value = loadColliders;

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("If TRUE, collider object layers from the TMX file will be loaded.\n" +
                                       "This includes both 'Colliders' and 'Ledges' object groups.");
                    }

                    NezImGui.MediumVerticalSpace();

                    ImGui.TextWrapped("Select Image Mode:");
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(
                            "Choose how image layers from the TMX file should be loaded:\n\n" +
                            "• None: No image layers will be loaded\n" +
                            "• Separate Layers: Each image layer becomes its own SpriteEntity\n" +
                            "• Baked Layers: All image layers are merged into a single texture"
                        );
                    }

                    int currentMode = _imageLoadMode.Value;
                    
                    if (ImGui.RadioButton("None", ref currentMode, (int)ImageLoadMode.None))
                        _imageLoadMode.Value = currentMode;
                    
                    if (ImGui.RadioButton("Load Separate Image Layers", ref currentMode, (int)ImageLoadMode.SeparateLayers))
                        _imageLoadMode.Value = currentMode;
                    
                    if (ImGui.RadioButton("Bake Image Layers", ref currentMode, (int)ImageLoadMode.BakedLayers))
                        _imageLoadMode.Value = currentMode;
                }

                ImGui.Separator();
                bool shouldLoad = DrawActionButtons(picker);

                if (shouldLoad)
                {
                    string contentRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Content"));
                    if (picker.SelectedFile.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        string relativePath = "Content" + picker.SelectedFile.Substring(contentRoot.Length).Replace('\\', '/');

                        result = new TmxSelection
                        {
                            FilePath = relativePath,
                            LoadColliders = _loadColliders.Value,
                            ImageMode = (ImageLoadMode)_imageLoadMode.Value
                        };

                        ImGui.CloseCurrentPopup();
                        FilePicker.RemoveFilePicker(_owner);
                        _isOpen = false;
                        Reset();
                    }
                    else
                    {
                        NotificationSystem.ShowTimedNotification("Selected file is not inside Content folder!");
                    }
                }

                ImGui.EndPopup();
            }

            // Handle popup closed via X button or ESC
            if (!isOpen)
            {
                FilePicker.RemoveFilePicker(_owner);
                _isOpen = false;
                Reset();
            }

            return result;
        }

        private bool DrawActionButtons(FilePicker picker)
        {
            bool shouldLoad = false;

            float buttonWidth = 100f;
            float totalWidth = ImGui.GetContentRegionAvail().X;
            float rightButtonStart = totalWidth - buttonWidth;

            if (_isFileSelected)
            {
                if (ImGui.Button("Cancel", new Num.Vector2(buttonWidth, 0)))
                {
                    Close();
                }
            }

            ImGui.SameLine(rightButtonStart);

            bool canConfirm = !string.IsNullOrEmpty(picker.SelectedFile) &&
                             picker.SelectedFile.EndsWith(".tmx", StringComparison.OrdinalIgnoreCase) &&
                             File.Exists(picker.SelectedFile) &&
                             _isFileSelected;

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
        /// Resets selection state.
        /// </summary>
        public void Reset()
        {
            _isFileSelected = false;
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
            }
        }
    }
}