using System.Collections.Generic;
using System.Reflection;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Nez.ImGuiTools.UndoActions;
using Nez.Utils;

namespace Nez.ImGuiTools.TypeInspectors
{
    public class StructInspector : AbstractTypeInspector
    {
        List<AbstractTypeInspector> _inspectors = new List<AbstractTypeInspector>();
        bool _isHeaderOpen;
        
        // Edit session state for struct-level undo (for drag/slider operations)
        private bool _isEditingStruct = false;
        private object _structValueAtEditStart;
        
        // Immediate change tracking (for boolean, InputInt, etc.)
        private bool _hasImmediateFieldChanged = false;
        private object _structValueBeforeFrame;

        public override void Initialize()
        {
            base.Initialize();

            // figure out which fields and properties are useful to add to the inspector
            var fields = ReflectionUtils.GetFields(_valueType);
            foreach (var field in fields)
            {
                if (!field.IsPublic && !field.IsDefined(typeof(InspectableAttribute)))
                    continue;

                var inspector = TypeInspectorUtils.GetInspectorForType(field.FieldType, _target, field);
                if (inspector != null)
                {
                    inspector.SetStructTarget(_target, this, field);
                    inspector.Initialize();
                    _inspectors.Add(inspector);
                }
            }

            var properties = ReflectionUtils.GetProperties(_valueType);
            foreach (var prop in properties)
            {
                if (!prop.CanRead || !prop.CanWrite)
                    continue;

                var isPropertyUndefinedOrPublic = !prop.CanWrite || (prop.CanWrite && prop.SetMethod.IsPublic);
                if ((!prop.GetMethod.IsPublic || !isPropertyUndefinedOrPublic) &&
                    !prop.IsDefined(typeof(InspectableAttribute)))
                    continue;

                var inspector = TypeInspectorUtils.GetInspectorForType(prop.PropertyType, _target, prop);
                if (inspector != null)
                {
                    inspector.SetStructTarget(_target, this, prop);
                    inspector.Initialize();
                    _inspectors.Add(inspector);
                }
            }
        }

        public override void DrawMutable()
        {
            ImGui.Indent();
            NezImGui.BeginBorderedGroup();

            _isHeaderOpen = ImGui.CollapsingHeader($"{_name}");
            if (_isHeaderOpen)
            {
                // Reset immediate change flag and capture struct at frame start
                _hasImmediateFieldChanged = false;
                _structValueBeforeFrame = GetValue();
                
                // Check if any field is currently active (for edit session detection)
                bool anyFieldIsActive = false;
                foreach (var inspector in _inspectors)
                {
                    if (inspector.IsFieldCurrentlyActive())
                    {
                        anyFieldIsActive = true;
                        break;
                    }
                }

                // Start edit session for drag/slider operations
                if (anyFieldIsActive && !_isEditingStruct)
                {
                    _isEditingStruct = true;
                    _structValueAtEditStart = GetValue();
                }

                // Draw all field inspectors - don't disable undo anymore
                foreach (var inspector in _inspectors)
                {
                    inspector.Draw(); // Remove undo disabling
                }

                // End edit session for drag/slider operations
                if (_isEditingStruct && !anyFieldIsActive)
                {
                    _isEditingStruct = false;
                    var structValueAtEditEnd = GetValue();
                    
                    if (!Equals(_structValueAtEditStart, structValueAtEditEnd))
                    {
                        // Always create undo action for struct changes
                        EditorChangeTracker.PushUndo(
                            new PathUndoAction(
                                GetRootTarget(),
                                new List<string>(_pathFromRoot),
                                _structValueAtEditStart,
                                structValueAtEditEnd,
                                $"{GetFullPathDescription()} (struct modified)"
                            ),
                            GetRootTarget(),
                            $"{GetFullPathDescription()} (struct modified)"
                        );
                    }
                }

                // Handle immediate changes (boolean, InputInt, etc.) - only when not in edit session
                if (_hasImmediateFieldChanged && !_isEditingStruct)
                {
                    var structValueAfterFrame = GetValue();
                    
                    if (!Equals(_structValueBeforeFrame, structValueAfterFrame))
                    {
                        // Always create undo action for immediate changes
                        EditorChangeTracker.PushUndo(
                            new PathUndoAction(
                                GetRootTarget(),
                                new List<string>(_pathFromRoot),
                                _structValueBeforeFrame,
                                structValueAfterFrame,
                                $"{GetFullPathDescription()} (struct modified)"
                            ),
                            GetRootTarget(),
                            $"{GetFullPathDescription()} (struct modified)"
                        );
                    }
                }
            }

            NezImGui.EndBorderedGroup(new System.Numerics.Vector2(4, 1), new System.Numerics.Vector2(4, 2));
            ImGui.Unindent();
        }

        /// <summary>
        /// Called by child field inspectors when they have immediate changes (boolean, InputInt, etc.)
        /// </summary>
        public void NotifyFieldChanged()
        {
            _hasImmediateFieldChanged = true;
        }

        /// <summary>
        /// we need to override here so that we can keep the header enabled so that it can be opened
        /// </summary>
        public override void DrawReadOnly()
        {
            DrawMutable();
        }
    }
}