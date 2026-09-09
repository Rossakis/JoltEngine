using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using Voltage.Aseprite;
using Voltage.Sprites;
using Voltage.Textures;
using Voltage.Utils;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.FilePickers;
using Voltage.Editor.Inspectors.TypeInspectors;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Undo;
using Voltage.Editor.Undo.ComponentActions;
using Voltage.Editor.Utils;
using Num = System.Numerics;
using Voltage.Editor.Undo.Core;

namespace Voltage.Editor.Inspectors.CustomInspectors
{
    public class SpriteAnimatorFileInspector : AbstractTypeInspector
    {
        private static int _frameNumber = 0;
        private string _errorMessage = "";

        // Layer selection state
        private static List<string> _availableLayers = new();
        private static HashSet<int> _selectedLayerIndices = new();
        private static string _lastSelectedAsepriteFile = null;
        private static List<string> _availableTags = new();
        private static int _selectedTagIndex = -1;
        private ImGuiManager imGuiManager;
       
        public override void Initialize()
        {
            base.Initialize();
            _name = "Animation File Loader";
        }

        public static AnimationEventInspector AnimationEventInspectorInstance;

        public override void DrawMutable()
		{
			SpriteAnimator animator = _target as SpriteAnimator;
			
			if(imGuiManager == null)
				imGuiManager = Core.GetGlobalManager<ImGuiManager>();

			if (Gui.Button("Manage Animation Events", new Num.Vector2(-1, 0)))
			{
				imGuiManager.OpenAnimationEventInspector(animator);
			}

            if (animator == null)
                return;

            DrawAsepriteSourceSelector(animator);
        }

        private void DrawAsepriteSourceSelector(SpriteAnimator animator)
        {
            ImGui.TextColored(new Num.Vector4(0.8f, 0.9f, 1.0f, 1.0f), "Aseprite Animation Selection");

            if (!string.IsNullOrEmpty(animator.TextureFilePath))
            {
                ImGuiSafe.TextSafe($"Current File: {Path.GetFileName(animator.TextureFilePath)}");
            }
            else
            {
                ImGui.TextColored(new Num.Vector4(0.7f, 0.7f, 0.7f, 1.0f), "No animation file loaded");
            }

            ImGui.Spacing();

            if (!string.IsNullOrEmpty(_errorMessage))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(1.0f, 0.6f, 0.6f, 1.0f));
                ImGuiSafe.TextWrappedSafe($"WARNING: {_errorMessage}");
                ImGui.PopStyleColor();
                ImGui.Spacing();
            }

            if (Gui.Button("Load Aseprite Animation"))
            {
                _errorMessage = "";
                ImGui.OpenPopup("aseprite-anim-file-picker");
            }

            // Clear button with undo support
            if (!string.IsNullOrEmpty(animator.TextureFilePath))
            {
                ImGui.Spacing();
                if (Gui.Button("Clear Animation", new Num.Vector2(-1, 0)))
                {
                    ClearAnimationWithUndo(animator);
                    _errorMessage = "";
                }
            }

            if (!string.IsNullOrEmpty(animator.TextureFilePath))
            {
                ImGui.Spacing();
                ImGui.Separator();
                DrawDefaultAnimationSelector(animator);
            }

            DrawAsepriteAnimationFilePickerPopup(animator);
        }

        // Cache of animation names for the currently-loaded file, so we don't reparse the Aseprite every frame.
        private string _defaultAnimCacheFile = null;
        private List<string> _defaultAnimNames = new();

        /// <summary>
        /// Lets the user choose which animation plays on scene load. The choice is stored in
        /// <see cref="SpriteAnimator.LoadedTag"/>, which is serialized and replayed by OnStart via LoadAllAnimations.
        /// </summary>
        private void DrawDefaultAnimationSelector(SpriteAnimator animator)
        {
            ImGui.TextColored(new Num.Vector4(0.8f, 0.9f, 1.0f, 1.0f), "Default Animation (played on scene load)");

            RefreshDefaultAnimNames(animator);

            if (_defaultAnimNames.Count == 0)
            {
                ImGui.TextColored(new Num.Vector4(0.7f, 0.7f, 0.7f, 1f), "No animations found in this file.");
                return;
            }

            int current = _defaultAnimNames.IndexOf(animator.LoadedTag);
            if (current < 0) current = 0;

            ImGui.SetNextItemWidth(-1);
            if (Gui.BeginCombo("##DefaultAnim", _defaultAnimNames[current]))
            {
                for (int i = 0; i < _defaultAnimNames.Count; i++)
                {
                    bool selected = i == current;
                    if (Gui.Selectable(_defaultAnimNames[i], selected) && _defaultAnimNames[i] != animator.LoadedTag)
                        SetDefaultAnimationWithUndo(animator, _defaultAnimNames[i]);

                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("This animation is serialized and plays automatically the next time the scene loads.");
        }

        private void RefreshDefaultAnimNames(SpriteAnimator animator)
        {
            if (_defaultAnimCacheFile == animator.TextureFilePath && _defaultAnimNames.Count > 0)
                return;

            _defaultAnimCacheFile = animator.TextureFilePath;
            _defaultAnimNames.Clear();

            // Prefer names already loaded on the animator; fall back to the file's tags.
            if (animator.Animations is { Count: > 0 })
            {
                foreach (var key in animator.Animations.Keys)
                    if (!_defaultAnimNames.Contains(key))
                        _defaultAnimNames.Add(key);
                return;
            }

            try
            {
                var file = animator.Entity?.Scene?.Content?.LoadAsepriteFile(animator.TextureFilePath)
                           ?? Core.Content.LoadAsepriteFile(animator.TextureFilePath);
                if (file?.Tags != null)
                    foreach (var tag in file.Tags)
                        if (!_defaultAnimNames.Contains(tag.Name))
                            _defaultAnimNames.Add(tag.Name);
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to read animations: {ex.Message}";
            }
        }

        private void SetDefaultAnimationWithUndo(SpriteAnimator animator, string animationName)
        {
            var oldFilePath = animator.TextureFilePath;
            var oldData = animator.Data != null
                ? new SpriteAnimator.SpriteAnimatorComponentData(animator)
                : new SpriteAnimator.SpriteAnimatorComponentData();

            animator.LoadedTag = animationName;

            // Load every animation so the editor can preview the newly-selected default immediately.
            try
            {
                animator.LoadAllAnimations();
                if (animator.Animations != null && animator.Animations.ContainsKey(animationName))
                    animator.Play(animationName);
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to play '{animationName}': {ex.Message}";
            }

            var newData = new SpriteAnimator.SpriteAnimatorComponentData(animator);

            EditorChangeTracker.PushUndo(
                new SpriteAnimatorLoadUndoAction(
                    animator,
                    oldFilePath,
                    oldData,
                    animator.TextureFilePath,
                    newData,
                    $"Set Default Animation: {animationName}"
                ),
                animator.Entity,
                $"Set Default Animation: {animationName}"
            );
        }

        private void DrawAsepriteAnimationFilePickerPopup(SpriteAnimator animator)
        {
            bool isOpen = true;
            if (Gui.BeginPopupModal("aseprite-anim-file-picker", ref isOpen, ImGuiWindowFlags.AlwaysAutoResize))
            {
                var picker = FilePicker.GetFilePicker(this, ProjectManager.Instance.CurrentProject.ContentsFolder, ".ase|.aseprite");
                picker.DontAllowTraverselBeyondRootFolder = true;

                if (string.IsNullOrEmpty(picker.SelectedFile) || !File.Exists(picker.SelectedFile))
                {
                    ImGui.Text("Select an Aseprite file:");
                    ImGui.Separator();
                    FilePicker.DrawFilePickerContent(picker);

                    ImGui.Separator();
                    float buttonWidth = 100f;

                    if (Gui.Button("Cancel", new Num.Vector2(buttonWidth, 0)))
                    {
                        ImGui.CloseCurrentPopup();
                        FilePicker.RemoveFilePicker(this);
                    }
                }
                else
                {
                    ImGui.Text("Aseprite Animation Options:");
                    ImGui.Separator();

                    ImGuiSafe.TextSafe($"Selected File: {Path.GetFileName(picker.SelectedFile)}");

                    // Tag selection box
                    DrawTagSelectionBox(picker);

                    Gui.DragInt("Frame Number", ref _frameNumber, 1, 0, 999);
                    _frameNumber = Math.Max(0, _frameNumber);

                    ImGui.TextColored(new Num.Vector4(0.7f, 0.7f, 0.7f, 1), "Select an animation tag and layers below.");

                    ImGui.Separator();

                    DrawLayerSelectionBox(picker);

                    ImGui.Separator();

                    float buttonWidth = 100f;
                    float totalWidth = ImGui.GetContentRegionAvail().X;
                    float rightButtonStart = totalWidth - buttonWidth;

                    if (Gui.Button("Back", new Num.Vector2(buttonWidth, 0)))
                    {
                        // Clear selected file and tags/layers, return to file picker
                        picker.SelectedFile = null;
                        _availableTags.Clear();
                        _selectedTagIndex = -1;
                        _availableLayers.Clear();
                        _selectedLayerIndices.Clear();
						_lastSelectedAsepriteFile = null;
                        _errorMessage = "";
						return; // Early return to redraw the file picker UI
                    }

                    ImGui.SameLine();
                    if (Gui.Button("Cancel", new Num.Vector2(buttonWidth, 0)))
                    {
                        ImGui.CloseCurrentPopup();
                        FilePicker.RemoveFilePicker(this);

                        _availableLayers.Clear();
                        _selectedLayerIndices.Clear();
					}

					ImGui.SameLine(rightButtonStart);

                    bool canConfirm = _selectedTagIndex >= 0 && _selectedTagIndex < _availableTags.Count;
                    if (!canConfirm)
                    {
                        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
                    }

                    if (Gui.Button("Load", new Num.Vector2(buttonWidth, 0)) && canConfirm)
                    {
                        string contentRoot = ProjectManager.Instance.CurrentProject.ContentsFolder;
                        if (CrossPlatformPath.IsPathUnder(contentRoot, picker.SelectedFile))
                        {
                            // Use project root so path is portable across machines and OSes
                            string relativePath = CrossPlatformPath.GetRelativePathForStorage(ProjectManager.Instance.CurrentProject.ProjectPath, picker.SelectedFile);
                            string selectedTagName = _availableTags[_selectedTagIndex];
                            LoadAsepriteAnimationFromPicker(animator, relativePath, selectedTagName, _frameNumber);
                            ImGui.CloseCurrentPopup();
                            FilePicker.RemoveFilePicker(this);
                            _availableLayers.Clear();
                            _selectedLayerIndices.Clear();
						}
						else
                        {
                            _errorMessage = "File must be in Content folder!";
                        }
                    }

                    if (!canConfirm)
                    {
                        ImGui.PopStyleVar();
                    }
                }

                Gui.EndPopup();
            }
        }

        private void DrawLayerSelectionBox(FilePicker picker)
        {
            if (!string.IsNullOrEmpty(picker.SelectedFile) && File.Exists(picker.SelectedFile))
            {
                _lastSelectedAsepriteFile = picker.SelectedFile;

                try
                {
                    string relativePath = CrossPlatformPath.GetRelativePathForStorage(ProjectManager.Instance.CurrentProject.ProjectPath, picker.SelectedFile);
                    var asepriteFile = Core.Content.LoadAsepriteFile(relativePath);
                    if (asepriteFile != null && asepriteFile.Layers != null)
                    {
                        foreach (var layer in asepriteFile.Layers)
                        {
							if(!_availableLayers.Contains(layer.Name))
								_availableLayers.Add(layer.Name);
                        }
                    }
                    else
                    {
                        _errorMessage = "No layers found in file.";
                    }
                }
                catch (Exception ex)
                {
                    _errorMessage = $"Failed to read layers: {ex.Message}";
                }
            }

            ImGui.Text("Select Layers (multi-select):");

            // "Select All" button
            if (_availableLayers.Count > 0)
            {
                if (Gui.Button("Select All", new Num.Vector2(220, 0)))
                {
                    _selectedLayerIndices.Clear();
                    for (int i = 0; i < _availableLayers.Count; i++)
                        _selectedLayerIndices.Add(i);
                }

                ImGui.BeginChild("layer-listbox", new Num.Vector2(400, 150), true);

                for (int i = 0; i < _availableLayers.Count; i++)
                {
                    bool selected = _selectedLayerIndices.Contains(i);
                    if (Gui.Selectable(_availableLayers[i], selected, ImGuiSelectableFlags.AllowDoubleClick | ImGuiSelectableFlags.DontClosePopups))
                    {
                        // Toggle selection on click
                        if (selected)
                            _selectedLayerIndices.Remove(i);
                        else
                            _selectedLayerIndices.Add(i);
                    }
                }

                ImGui.EndChild();
            }
            else
            {
                ImGui.TextColored(new Num.Vector4(0.7f, 0.7f, 0.7f, 1), "No layers found or file not selected.");
            }
        }

        private void DrawTagSelectionBox(FilePicker picker)
        {
            // Only update available tags if the selected file changes
            if (picker.SelectedFile != _lastSelectedAsepriteFile)
            {
                _lastSelectedAsepriteFile = picker.SelectedFile;
                _availableTags.Clear();
                _selectedTagIndex = -1;

                if (!string.IsNullOrEmpty(picker.SelectedFile) && File.Exists(picker.SelectedFile))
                {
                    try
                    {
                        string relativePath = CrossPlatformPath.GetRelativePathForStorage(Environment.CurrentDirectory, picker.SelectedFile);
                        var asepriteFile = Core.Content.LoadAsepriteFile(relativePath);
                        if (asepriteFile != null && asepriteFile.Tags != null)
                        {
                            foreach (var tag in asepriteFile.Tags)
                            {
                                _availableTags.Add(tag.Name);
                            }
                            if (_availableTags.Count > 0)
                                _selectedTagIndex = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        _errorMessage = $"Failed to read tags: {ex.Message}";
                    }
                }
            }

            ImGui.Text("Select Animation Tag:");
            if (_availableTags.Count > 0)
            {
                ImGui.BeginChild("tag-listbox", new Num.Vector2(400, 80), true);
                for (int i = 0; i < _availableTags.Count; i++)
                {
                    bool selected = _selectedTagIndex == i;
                    if (Gui.Selectable(_availableTags[i], selected, ImGuiSelectableFlags.DontClosePopups))
                    {
                        _selectedTagIndex = i;
                    }
                }
                ImGui.EndChild();
            }
            else
            {
                ImGui.TextColored(new Num.Vector4(0.7f, 0.7f, 0.7f, 1), "No tags found in file.");
            }
        }

        private void LoadAsepriteAnimationFromPicker(SpriteAnimator animator, string relativePath, string animationTagName, int frameNumber)
        {
            try
            {
                var contentManager = animator.Entity?.Scene?.Content ?? Core.Content;
                if (contentManager != null)
                {
                    // Store the old state for undo
                    var oldFilePath = animator.TextureFilePath;
                    var oldData = animator.Data != null ?
                        new SpriteAnimator.SpriteAnimatorComponentData(animator) :
                        new SpriteAnimator.SpriteAnimatorComponentData();

                    // Prepare selected layers
                    var selectedLayers = new List<string>();
                    foreach (var idx in _selectedLayerIndices)
                    {
                        if (idx >= 0 && idx < _availableLayers.Count)
                            selectedLayers.Add(_availableLayers[idx]);
                    }

                    // Assign selected layers and tag to the animator
                    animator.LoadedLayers = selectedLayers;
                    animator.LoadedTag = animationTagName;

                    // Load the new Aseprite animation using AsepriteUtils
                    if (selectedLayers.Count > 0)
                    {
                        AsepriteUtils.LoadAsepriteAnimationWithLayers(
                            animator.Entity,
                            relativePath,
                            animationTagName,
                            null,
                            selectedLayers.ToArray()
                        );
                    }
                    else
                    {
                        AsepriteUtils.LoadAsepriteAnimationWithLayers(
                            animator.Entity,
                            relativePath,
                            animationTagName,
                            null
                        );
                    }

                    animator.TextureFilePath = relativePath;

                    // Store the new state
                    var newFilePath = animator.TextureFilePath;
                    var newData = new SpriteAnimator.SpriteAnimatorComponentData(animator);

                    // Push undo action
                    EditorChangeTracker.PushUndo(
                        new SpriteAnimatorLoadUndoAction(
                            animator,
                            oldFilePath,
                            oldData,
                            newFilePath,
                            newData,
                            $"Load Aseprite Animation: {Path.GetRelativePath(Environment.CurrentDirectory, relativePath)} (tag: {animationTagName}, layers: {(selectedLayers.Count > 0 ? string.Join(", ", selectedLayers) : "all")}, frame: {frameNumber})"
                        ),
                        animator.Entity,
                        $"Load Aseprite Animation: {Path.GetRelativePath(Environment.CurrentDirectory, relativePath)}"
                    );

                    _errorMessage = ""; // Clear error on success

				}
				else
                {
                    _errorMessage = "Content manager not available";
                }
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to load Aseprite animation: {ex.Message}";
            }
		}

		private void ClearAnimationWithUndo(SpriteAnimator animator)
        {
            var oldFilePath = animator.TextureFilePath;
            var oldData = animator.Data != null ?
                new SpriteAnimator.SpriteAnimatorComponentData(animator) :
                new SpriteAnimator.SpriteAnimatorComponentData();

            animator.TextureFilePath = "";
            animator.Stop();

            var newData = new SpriteAnimator.SpriteAnimatorComponentData(animator);

            EditorChangeTracker.PushUndo(
                new SpriteAnimatorLoadUndoAction(
                    animator,
                    oldFilePath,
                    oldData,
                    "",
                    newData,
                    $"Clear Animation: {animator.Entity?.Name ?? "Unknown Entity"}"
                ),
                animator.Entity,
                $"Clear Animation: {animator.Entity?.Name ?? "Unknown Entity"}"
            );
        }
    }
}
