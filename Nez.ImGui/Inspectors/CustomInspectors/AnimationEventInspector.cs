using System;
using System.Linq;
using System.Collections.Generic;
using ImGuiNET;
using Nez.ImGuiTools.UndoActions;
using Nez.Sprites;
using Nez.Utils;
using Num = System.Numerics;

namespace Nez.ImGuiTools.Inspectors.CustomInspectors
{
    public class AnimationEventInspector
    {
        private SpriteAnimator _animator;
        private bool _shouldFocusWindow = false;
        private ImGuiManager _imGuiManager;

        private int _selectedAnimationIndex = 0;
        private int _selectedFrame = 0;
        private bool _isPlaying = false;
        private float _previewTimer = 0f;

        private List<AnimationEvent> _editableEvents = new();
        private string _saveStatusMessage = "";
        private float _saveStatusMessageTime = -1f;
        private const float SaveMessageDuration = 2f; // seconds

        private bool _showAddEventPopup = false;
        private int _newEventTypeIndex = 0; // 0 = AnimationEvent, 1 = LongAnimationEvent

        public AnimationEventInspector(SpriteAnimator animator)
        {
            SetAnimator(animator);
        }

        public void SetAnimator(SpriteAnimator animator)
        {
	        _animator = animator;

	        // Copy events for editing
	        _editableEvents = animator?.AnimationEvents != null
		        ? animator.AnimationEvents.Select(e =>
			        e is LongAnimationEvent le
				        ? new LongAnimationEvent(le.StartFrame, le.EndFrame, le.Name, le.Callback, le.AnimationName)
				        : new AnimationEvent(e.StartFrame, e.Name, e.Callback, e.AnimationName)
		        ).ToList()
		        : new List<AnimationEvent>();
        }

        public void SetWindowFocus()
        {
            _shouldFocusWindow = true;
        }

        public void Draw()
        {
            if (_imGuiManager == null)
                _imGuiManager = Core.GetGlobalManager<ImGuiManager>();

            float left = _imGuiManager.SceneGraphWindow.SceneGraphWidth;
            float right = Screen.Width - _imGuiManager.InspectorTabWidth;
            float width = right - left;
            float top = _imGuiManager.SceneGraphWindow.SceneGraphPosY + _imGuiManager.GameWindowHeight;
            float height = Screen.Height - top;

            ImGui.SetNextWindowPos(new Num.Vector2(left, top), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.Always);

            bool open = true;
            if (ImGui.Begin("Animation Event Inspector", ref open, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse))
            {
                if (_shouldFocusWindow)
                {
                    ImGui.SetWindowFocus();
                    _shouldFocusWindow = false;
                }

                if (_animator == null)
                {
                    ImGui.TextColored(new Num.Vector4(1, 1, 0, 1), "Select an AnimatedSprite to Manage its events");
                    ImGui.End();
                    // Handle window close when no animator is selected
                    if (!open)
                    {
                        var imGuiManager = Core.GetGlobalManager<ImGuiManager>();
                        imGuiManager.ShowAnimationEventInspector = false;
                        imGuiManager.UnregisterDrawCommand(Draw);
                        SpriteAnimatorFileInspector.AnimationEventInspectorInstance = null;
                    }
                    return;
                }

                // Animation selection
                var animationNames = _animator.Animations.Keys.ToArray();
                string[] animationDisplayNames = new string[animationNames.Length];
                for (int i = 0; i < animationNames.Length; i++)
                {
                    int eventCount = _editableEvents.Count(e => e.AnimationName == animationNames[i]);
                    animationDisplayNames[i] = eventCount > 0
                        ? $"{animationNames[i]} ({eventCount})"
                        : animationNames[i];
                }
                if (_selectedAnimationIndex >= animationNames.Length)
                    _selectedAnimationIndex = 0;

                if (ImGui.Combo("Animation", ref _selectedAnimationIndex, animationDisplayNames, animationDisplayNames.Length))
                {
                    _animator.Play(animationNames[_selectedAnimationIndex]);
                    _selectedFrame = 0;
                    _previewTimer = 0f;
                }

                ImGui.Separator();

                // Play/Stop toggle
                if (ImGui.Button(_isPlaying ? "Stop" : "Play"))
                {
                    _isPlaying = !_isPlaying;
                }

                // Frame slider
                int frameCount = _animator.Animations[animationNames[_selectedAnimationIndex]].Sprites.Length;
                if (_selectedFrame >= frameCount)
                    _selectedFrame = 0;

                if (ImGui.SliderInt("Frame", ref _selectedFrame, 0, frameCount - 1))
                {
                    _animator.SetFrame(_selectedFrame);
                    _previewTimer = 0f;
                    _isPlaying = false;
                }

                // Animation preview logic
                if (_isPlaying && frameCount > 0)
                {
                    float frameRate = _animator.Animations[animationNames[_selectedAnimationIndex]].FrameRates[_selectedFrame];
                    _previewTimer += Time.DeltaTime;
                    if (_previewTimer >= 1f / frameRate)
                    {
                        _selectedFrame = (_selectedFrame + 1) % frameCount;
                        _animator.SetFrame(_selectedFrame);
                        _previewTimer = 0f;
                    }
                }

                ImGui.Separator();

                // Only show events for the current animation
                string selectedAnimationName = animationNames[_selectedAnimationIndex];
                var eventsForCurrentAnimation = _editableEvents
                    .Where(e => e.AnimationName == selectedAnimationName)
                    .ToList();

                ImGui.Text("Events:");
                if (eventsForCurrentAnimation.Count == 0)
                {
                    ImGui.TextColored(new Num.Vector4(1, 1, 0, 1), "No events have been added to this animation");
                }
                else if (ImGui.BeginTable("EventsTable", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
                {
                    ImGui.TableSetupColumn("Frame");
                    ImGui.TableSetupColumn("Name");
                    ImGui.TableSetupColumn("Animation");
                    ImGui.TableSetupColumn("Select Animation");
                    ImGui.TableSetupColumn("Actions");
                    ImGui.TableHeadersRow();

                    for (int i = 0; i < eventsForCurrentAnimation.Count; i++)
                    {
                        var evt = eventsForCurrentAnimation[i];
                        int evtIndex = _editableEvents.IndexOf(evt); // For correct editing/deleting

                        ImGui.TableNextRow();

                        // Frame column
                        ImGui.TableSetColumnIndex(0);
                        // Clamp startFrame
                        evt.StartFrame = Math.Clamp(evt.StartFrame, 0, frameCount - 1);
                        ImGui.Text("startFrame");
                        ImGui.InputInt($"##startFrame_{evtIndex}", ref evt.StartFrame);

                        if (evt is LongAnimationEvent longEvt)
                        {
                            // Clamp endFrame
                            longEvt.EndFrame = Math.Clamp(longEvt.EndFrame, 0, frameCount - 1);
                            ImGui.Text("endFrame");
                            ImGui.InputInt($"##endFrame_{evtIndex}", ref longEvt.EndFrame);
                        }

                        // Name column
                        ImGui.TableSetColumnIndex(1);
                        string name = evt.Name ?? "";
                        if (ImGui.InputText($"##name_{evtIndex}", ref name, 32))
                        {
                            // Ensure uniqueness only within the same animation
                            if (_editableEvents.Any(e => e != evt && e.Name == name && e.AnimationName == evt.AnimationName))
                            {
                                int suffix = 1;
                                string baseName = name;
                                while (_editableEvents.Any(e => e != evt && e.Name == name && e.AnimationName == evt.AnimationName))
                                {
                                    name = $"{baseName}_{suffix++}";
                                }
                            }
                            evt.Name = name;
                        }

                        // Animation column (shows current assignment or "Not Selected")
                        ImGui.TableSetColumnIndex(2);
                        ImGui.Text(evt.AnimationName ?? "Not Selected");

                        // Animation selection listbox
                        ImGui.TableSetColumnIndex(3);
                        int selectedAnimIdx = -1;
                        if (!string.IsNullOrEmpty(evt.AnimationName))
                            selectedAnimIdx = Array.IndexOf(animationNames, evt.AnimationName);

                        string listboxLabel = $"##anim_select_{evtIndex}";
                        if (ImGui.BeginCombo(listboxLabel, selectedAnimIdx >= 0 ? animationNames[selectedAnimIdx] : "Not Selected"))
                        {
                            // "Not Selected" option
                            if (ImGui.Selectable("Not Selected", selectedAnimIdx == -1))
                            {
                                evt.AnimationName = null;
                            }
                            for (int animIdx = 0; animIdx < animationNames.Length; animIdx++)
                            {
                                bool isSelected = selectedAnimIdx == animIdx;
                                if (ImGui.Selectable(animationNames[animIdx], isSelected))
                                {
                                    evt.AnimationName = animationNames[animIdx];
                                }
                                if (isSelected)
                                    ImGui.SetItemDefaultFocus();
                            }
                            ImGui.EndCombo();
                        }

                        // Actions column
                        ImGui.TableSetColumnIndex(4);
                        if (ImGui.Button($"Delete##{evtIndex}"))
                        {
                            _editableEvents.RemoveAt(evtIndex);
                            break;
                        }
                    }
                    ImGui.EndTable();
                }

                // Add new event
                if (ImGui.Button("Add Event"))
                {
                    _showAddEventPopup = true;
                    _newEventTypeIndex = 0;
                }

                // Add Event Popup
                if (_showAddEventPopup)
                {
                    ImGui.OpenPopup("AddEventPopup");
                    if (ImGui.BeginPopupModal("AddEventPopup", ref _showAddEventPopup, ImGuiWindowFlags.AlwaysAutoResize))
                    {
                        ImGui.Text("Select Event Type:");
                        string[] eventTypes = { "AnimationEvent", "LongAnimationEvent" };
                        ImGui.Combo("Type", ref _newEventTypeIndex, eventTypes, eventTypes.Length);

                        if (ImGui.Button("Create"))
                        {
                            var oldEvents = new List<AnimationEvent>(_editableEvents);
                            var animationName = animationNames[_selectedAnimationIndex];
                            if (_newEventTypeIndex == 0)
                            {
                                _editableEvents.Add(new AnimationEvent
                                {
                                    StartFrame = 0,
                                    Name = "",
                                    AnimationName = animationName 
                                });
                            }
                            else
                            {
                                _editableEvents.Add(new LongAnimationEvent
                                {
                                    StartFrame = 0,
                                    EndFrame = 0,
                                    Name = "",
                                    AnimationName = animationName 
                                });
                            }
                            EditorChangeTracker.PushUndo(
                                new GenericValueChangeAction(
                                    _animator,
                                    typeof(SpriteAnimator).GetProperty(nameof(SpriteAnimator.AnimationEvents)),
                                    oldEvents,
                                    new List<AnimationEvent>(_editableEvents),
                                    $"{_animator.Entity?.Name ?? "Animator"}.AnimationEvents"
                                ),
                                _animator,
                                $"{_animator.Entity?.Name ?? "Animator"}.AnimationEvents"
                            );
                            ImGui.CloseCurrentPopup();
                            _showAddEventPopup = false;
                        }
                        ImGui.SameLine();
                        if (ImGui.Button("Cancel"))
                        {
                            ImGui.CloseCurrentPopup();
                            _showAddEventPopup = false;
                        }
                        ImGui.EndPopup();
                    }
                }

                ImGui.Separator();

                // Save button
                float buttonWidth = ImGui.GetWindowWidth() * 0.5f; 
                float buttonHeight = ImGui.GetFontSize() * 2.2f;  
                float cursorX = (ImGui.GetWindowWidth() - buttonWidth) * 0.5f;
                ImGui.SetCursorPosX(cursorX);

                if (ImGui.Button("Save", new Num.Vector2(buttonWidth, buttonHeight)))
                {
                    try
                    {
                        var animationName = animationNames[_selectedAnimationIndex];
                        // Assign current animation to events with no selection
                        foreach (var evt in _editableEvents)
                        {
                            if (string.IsNullOrEmpty(evt.AnimationName))
                                evt.AnimationName = animationName;
                        }
                        _animator.AnimationEvents = _editableEvents
                            .Select(e =>
                            {
                                if (e is LongAnimationEvent le)
                                    return new LongAnimationEvent(le.StartFrame, le.EndFrame, le.Name, le.Callback, le.AnimationName);
                                else
                                    return new AnimationEvent(e.StartFrame, e.Name, e.Callback, e.AnimationName);
                            })
                            .ToList();
                        _saveStatusMessage = "Events saved successfully!";
                        _saveStatusMessageTime = Time.TotalTime; // Start timer

                        // Save the entire scene after saving animation events
                        _imGuiManager.InvokeSaveSceneChanges();
                    }
                    catch (Exception ex)
                    {
                        _saveStatusMessage = $"Save failed: {ex.Message}";
                        _saveStatusMessageTime = Time.TotalTime;
                    }
                }

                // Show status message only for a few seconds
                if (!string.IsNullOrEmpty(_saveStatusMessage) && _saveStatusMessageTime >= 0)
                {
                    if (Time.TotalTime - _saveStatusMessageTime < SaveMessageDuration)
                    {
                        ImGui.TextColored(new Num.Vector4(0.2f, 1.0f, 0.2f, 1.0f), _saveStatusMessage);
                    }
                    else
                    {
                        _saveStatusMessage = "";
                        _saveStatusMessageTime = -1f;
                    }
                }
            }
            ImGui.End();

            // Handle window close when animator is selected
            if (!open)
            {
                var imGuiManager = Core.GetGlobalManager<ImGuiManager>();
                imGuiManager.ShowAnimationEventInspector = false;
                imGuiManager.UnregisterDrawCommand(Draw);
                SpriteAnimatorFileInspector.AnimationEventInspectorInstance = null;
            }
        }
    }
}