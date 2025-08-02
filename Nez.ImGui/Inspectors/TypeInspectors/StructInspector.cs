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
        private bool _hasAnyFieldChanged; // Flag to track if any field changed this frame
        private object _structValueBeforeFrame; // Store struct value at frame start

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
                // Reset the change flag and capture struct at frame start
                _hasAnyFieldChanged = false;
                _structValueBeforeFrame = GetValue();
                
                // Draw all field inspectors with undo disabled
                foreach (var inspector in _inspectors)
                {
                    // Disable undo for individual fields - the struct will handle it
                    inspector.IsUndoDisabled = true;
                    inspector.Draw();
                    inspector.IsUndoDisabled = false;
                }
                
                // Check if any field signaled a change
                if (_hasAnyFieldChanged)
                {
                    var structValueAfterFrame = GetValue();
                    
                    // Create undo action for the entire struct change
                    if (!IsUndoDisabled)
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

            NezImGui.EndBorderedGroup(new System.Numerics.Vector2(4, 1), new System.Numerics.Vector2(4, 2));
            ImGui.Unindent();
        }

        /// <summary>
        /// Called by child field inspectors when they detect a change
        /// </summary>
        public void NotifyFieldChanged()
        {
            _hasAnyFieldChanged = true;
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