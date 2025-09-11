using System;
using System.Linq;
using ImGuiNET;
using Nez.ImGuiTools.UndoActions;
using Num = System.Numerics;


namespace Nez.ImGuiTools.ObjectInspectors;

public class TransformInspector
{
    private Transform _transform;

    // Edit session state for each property
    private bool _isEditingLocalPosition;
    private Microsoft.Xna.Framework.Vector2 _localPositionEditStartValue;

    private bool _isEditingLocalRotation;
    private float _localRotationEditStartValue;

    private bool _isEditingLocalScale;
    private Microsoft.Xna.Framework.Vector2 _localScaleEditStartValue;
    private bool _showSetParentPopup = false;
    private string _parentSearch = "";
    private int _parentListSelectedIndex = -1;

	public TransformInspector(Transform transform)
    {
        _transform = transform;
    }

    public void Draw()
    {
        if (ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.LabelText("Children", _transform.ChildCount.ToString());

            if (_transform.Parent == null)
            {
                ImGui.LabelText("Parent", "none");
            }
            else
            {
                if (NezImGui.LabelButton("Parent", _transform.Parent.Entity.Name))
                    Core.GetGlobalManager<ImGuiManager>().OpenSeparateEntityInspector(_transform.Parent.Entity);

                if (ImGui.Button("Detach From Parent"))
                    _transform.Parent = null;
            }

            if (ImGui.Button("Set Parent"))
            {
                _showSetParentPopup = true;
                _parentSearch = "";
                _parentListSelectedIndex = -1;
                ImGui.OpenPopup("SetParentPopup");
            }

            if (_showSetParentPopup)
            {
                ImGui.SetNextWindowSize(new Num.Vector2(350, 400), ImGuiCond.Appearing);
                if (ImGui.BeginPopupModal("SetParentPopup", ref _showSetParentPopup, ImGuiWindowFlags.AlwaysAutoResize))
                {
                    ImGui.Text("Select Parent Entity");
                    ImGui.Separator();

                    // Search box
                    ImGui.InputText("Search", ref _parentSearch, 64);

                    // Get all entities except the current one
                    var allEntities = _transform.Entity.Scene.Entities
                        .Where(e => e != _transform.Entity)
                        .ToList();

                    // Filter by search
                    var filtered = string.IsNullOrWhiteSpace(_parentSearch)
                        ? allEntities
                        : allEntities.Where(e => e.Name != null && e.Name.Contains(_parentSearch, StringComparison.OrdinalIgnoreCase)).ToList();

                    // List selectable entities
                    ImGui.BeginChild("ParentList", new Num.Vector2(320, 300), true);
                    for (int i = 0; i < filtered.Count; i++)
                    {
                        bool selected = i == _parentListSelectedIndex;
                        if (ImGui.Selectable(filtered[i].Name, selected))
                        {
                            _parentListSelectedIndex = i;
                        }
                    }
                    ImGui.EndChild();

                    // Confirm/Cancel buttons
                    if (ImGui.Button("Set") && _parentListSelectedIndex >= 0 && _parentListSelectedIndex < filtered.Count)
                    {
                        var selectedParent = filtered[_parentListSelectedIndex];
                        _transform.SetParent(selectedParent.Transform);
                        _showSetParentPopup = false;
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel"))
                    {
                        _showSetParentPopup = false;
                        ImGui.CloseCurrentPopup();
                    }

                    ImGui.EndPopup();
                }
            }

            NezImGui.SmallVerticalSpace();

            // Local Position 
            var pos = _transform.LocalPosition.ToNumerics();
            bool posChanged = ImGui.DragFloat2("Local Position", ref pos);

            if (ImGui.IsItemActive() && !_isEditingLocalPosition)
            {
                _isEditingLocalPosition = true;
                _localPositionEditStartValue = _transform.LocalPosition;
            }

            if (posChanged)
                _transform.SetLocalPosition(pos.ToXNA());

            if (_isEditingLocalPosition && ImGui.IsItemDeactivatedAfterEdit())
            {
                _isEditingLocalPosition = false;
                var endValue = _transform.LocalPosition;
                if (_localPositionEditStartValue != endValue)
                {
                    EditorChangeTracker.PushUndo(
                        new GenericValueChangeAction(
                            _transform,
                            (obj, val) => ((Transform)obj).SetLocalPosition((Microsoft.Xna.Framework.Vector2)val),
                            _localPositionEditStartValue,
                            endValue,
                            $"{_transform.Entity?.Name ?? "Entity"}.Transform.LocalPosition"
                        ),
                        _transform.Entity,
                        $"{_transform.Entity?.Name ?? "Entity"}.Transform.LocalPosition"
                    );
                }
            }

            // Local Rotation Degrees 
            var rot = _transform.LocalRotationDegrees;
            bool rotChanged = ImGui.DragFloat("Local Rotation", ref rot, 1, -360, 360);

            if (ImGui.IsItemActive() && !_isEditingLocalRotation)
            {
                _isEditingLocalRotation = true;
                _localRotationEditStartValue = _transform.LocalRotationDegrees;
            }

            if (rotChanged)
                _transform.SetLocalRotationDegrees(rot);

            if (_isEditingLocalRotation && ImGui.IsItemDeactivatedAfterEdit())
            {
                _isEditingLocalRotation = false;
                var endValue = _transform.LocalRotationDegrees;
                if (_localRotationEditStartValue != endValue)
                {
                    EditorChangeTracker.PushUndo(
                        new GenericValueChangeAction(
                            _transform,
                            (obj, val) => ((Transform)obj).SetLocalRotationDegrees((float)val),
                            _localRotationEditStartValue,
                            endValue,
                            $"{_transform.Entity?.Name ?? "Entity"}.Transform.LocalRotationDegrees"
                        ),
                        _transform.Entity,
                        $"{_transform.Entity?.Name ?? "Entity"}.Transform.LocalRotationDegrees"
                    );
                }
            }

            // Local Scale 
            var scale = _transform.LocalScale.ToNumerics();
            bool scaleChanged = ImGui.DragFloat2("Local Scale", ref scale, 0.05f);

            if (ImGui.IsItemActive() && !_isEditingLocalScale)
            {
                _isEditingLocalScale = true;
                _localScaleEditStartValue = _transform.LocalScale;
            }

            if (scaleChanged)
                _transform.SetLocalScale(scale.ToXNA());

            if (_isEditingLocalScale && ImGui.IsItemDeactivatedAfterEdit())
            {
                _isEditingLocalScale = false;
                var endValue = _transform.LocalScale;
                if (_localScaleEditStartValue != endValue)
                {
                    EditorChangeTracker.PushUndo(
                        new GenericValueChangeAction(
                            _transform,
                            (obj, val) => ((Transform)obj).SetLocalScale((Microsoft.Xna.Framework.Vector2)val),
                            _localScaleEditStartValue,
                            endValue,
                            $"{_transform.Entity?.Name ?? "Entity"}.Transform.LocalScale"
                        ),
                        _transform.Entity,
                        $"{_transform.Entity?.Name ?? "Entity"}.Transform.LocalScale"
                    );
                }
            }

            // Global Scale not tracked for undo 
            scale = _transform.Scale.ToNumerics();
            if (ImGui.DragFloat2("Scale", ref scale, 0.05f))
                _transform.SetScale(scale.ToXNA());
        }
    }
}