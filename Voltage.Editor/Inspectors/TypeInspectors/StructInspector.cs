using System.Collections.Generic;
using System.Reflection;
using ImGuiNET;
using Voltage;
using Voltage.Utils;
using Voltage.Editor.Utils;
using Voltage.Editor.Undo.Core;

namespace Voltage.Editor.Inspectors.TypeInspectors
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
                if (field.IsDefined(typeof(HideInInspectorAttribute)))
                    continue;

                if (!field.IsPublic && !field.IsDefined(typeof(SerializeAttribute)))
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
                if (prop.IsDefined(typeof(HideInInspectorAttribute)))
                    continue;

                if (!prop.CanRead)
                    continue;

                // Skip indexed properties (properties with parameters)
                var indexParams = prop.GetIndexParameters();
                if (indexParams != null && indexParams.Length > 0)
                    continue;

                bool hasPublicGetter = prop.GetMethod?.IsPublic ?? false;
                bool hasInspectableAttribute = prop.IsDefined(typeof(SerializeAttribute));
                
                // Rules for showing properties in structs:
                // 1. If property has [Inspectable] attribute, always show it
                // 2. If property has public getter, show it (will be read-only if setter is non-public)
                // 3. If getter is non-public, hide it (unless it has [Inspectable])
                
                if (!hasInspectableAttribute && !hasPublicGetter)
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
            VoltageEditorUtils.BeginBorderedGroup();

            _isHeaderOpen = Gui.CollapsingHeader($"{_name}");
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

                // Draw all field inspectors
                foreach (var inspector in _inspectors)
                {
                    inspector.Draw();
                }

                // End edit session for drag/slider operations
                if (_isEditingStruct && !anyFieldIsActive)
                {
                    _isEditingStruct = false;
                    var structValueAtEditEnd = GetValue();
                    
                    if (!Equals(_structValueAtEditStart, structValueAtEditEnd))
                    {
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

            VoltageEditorUtils.EndBorderedGroup(new System.Numerics.Vector2(4, 1), new System.Numerics.Vector2(4, 2));
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