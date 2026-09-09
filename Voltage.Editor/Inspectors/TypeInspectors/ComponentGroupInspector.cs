using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using ImGuiNET;
using Voltage;
using Voltage.Editor.Undo.ComponentActions;
using Voltage.Utils;
using Voltage.Editor.Utils;
using Voltage.Editor.Undo.Core;
using Voltage.Persistence;

namespace Voltage.Editor.Inspectors.TypeInspectors
{
    /// <summary>
    /// Inspector for fields implementing <see cref="IComponentGroup"/>.
    /// Renders a collapsible bordered section identical to <see cref="StructInspector"/>,
    /// but operates on class references so no value-type copy-back is required.
    /// Undo/Redo is handled via a JSON snapshot of the group taken before/after edits.
    /// </summary>
    public class ComponentGroupInspector : AbstractTypeInspector
    {
        private List<AbstractTypeInspector> _inspectors = new();
        private bool _isHeaderOpen;

        // ImGui keys open state by widget id and a rebuilt inspector gets a new one, so remember it per object.
        private static readonly ConditionalWeakTable<object, Dictionary<string, bool>> _expandedGroups = new();

        private string GroupStateKey => _pathFromRoot.Count > 0 ? string.Join(".", _pathFromRoot) : _name;

        private bool ExpandedState
        {
            get
            {
                var owner = GetRootTarget();
                return owner != null
                       && _expandedGroups.GetOrCreateValue(owner).TryGetValue(GroupStateKey, out var open)
                       && open;
            }
            set
            {
                var owner = GetRootTarget();
                if (owner != null)
                    _expandedGroups.GetOrCreateValue(owner)[GroupStateKey] = value;
            }
        }

        // Edit session state for drag/slider operations
        private bool _isEditingGroup = false;
        private string _groupSnapshotAtEditStart;

        // Immediate change tracking (for checkbox, InputInt, etc.)
        private bool _hasImmediateFieldChanged = false;
        private string _groupSnapshotBeforeFrame;

        public override void Initialize()
        {
            base.Initialize();

            var groupInstance = GetValue();
            if (groupInstance == null)
                return;

            var fields = ReflectionUtils.GetFields(_valueType);
            foreach (var field in fields)
            {
                if (field.IsDefined(typeof(HideInInspectorAttribute)))
                    continue;

                if (!field.IsPublic && !field.IsDefined(typeof(SerializeAttribute)))
                    continue;

                var inspector = TypeInspectorUtils.GetInspectorForType(field.FieldType, groupInstance, field);
                if (inspector == null)
                    continue;

                inspector.SetClassGroupTarget(_target, this, field);
                inspector.Initialize();
                _inspectors.Add(inspector);
            }

            var properties = ReflectionUtils.GetProperties(_valueType);
            foreach (var prop in properties)
            {
                if (prop.IsDefined(typeof(HideInInspectorAttribute)))
                    continue;

                if (!prop.CanRead)
                    continue;

                var indexParams = prop.GetIndexParameters();
                if (indexParams != null && indexParams.Length > 0)
                    continue;

                bool hasPublicGetter = prop.GetMethod?.IsPublic ?? false;
                bool hasInspectableAttribute = prop.IsDefined(typeof(SerializeAttribute));

                if (!hasInspectableAttribute && !hasPublicGetter)
                    continue;

                var inspector = TypeInspectorUtils.GetInspectorForType(prop.PropertyType, groupInstance, prop);
                if (inspector == null)
                    continue;

                inspector.SetClassGroupTarget(_target, this, prop);
                inspector.Initialize();
                _inspectors.Add(inspector);
            }
        }

        public override void DrawMutable()
        {
            ImGui.Indent();
            VoltageEditorUtils.BeginBorderedGroup();

            // Resolve display label: prefer [ComponentGroup(label:)] on the field, fall back to field name
            string label = _name;
            var groupAttr = _memberInfo?.GetCustomAttribute<ComponentGroupAttribute>();
            if (groupAttr?.Label != null)
                label = groupAttr.Label;

            // Our store is the source of truth; CollapsingHeader still returns the post-click value.
            ImGui.SetNextItemOpen(ExpandedState, ImGuiCond.Always);
            _isHeaderOpen = Gui.CollapsingHeader(label);
            ExpandedState = _isHeaderOpen;
            if (_isHeaderOpen)
            {
                var groupInstance = GetValue();
                if (groupInstance == null)
                {
                    ImGui.TextDisabled("(null group — not initialized)");
                }
                else
                {
                    // Capture snapshot at frame start for immediate-change detection
                    _hasImmediateFieldChanged = false;
                    _groupSnapshotBeforeFrame = SnapshotGroup(groupInstance);

                    // Detect whether any child drag/slider is active
                    bool anyChildActive = false;
                    foreach (var inspector in _inspectors)
                    {
                        if (inspector.IsFieldCurrentlyActive())
                        {
                            anyChildActive = true;
                            break;
                        }
                    }

                    // Begin drag/slider edit session
                    if (anyChildActive && !_isEditingGroup)
                    {
                        _isEditingGroup = true;
                        _groupSnapshotAtEditStart = SnapshotGroup(groupInstance);
                    }

                    foreach (var inspector in _inspectors)
                        inspector.Draw();

                    // End drag/slider edit session — push undo if value changed
                    if (_isEditingGroup && !anyChildActive)
                    {
                        _isEditingGroup = false;
                        var snapshotEnd = SnapshotGroup(groupInstance);

                        if (_groupSnapshotAtEditStart != snapshotEnd)
                        {
                            PushGroupUndo(_groupSnapshotAtEditStart, snapshotEnd, $"{GetFullPathDescription()} (group modified)");
                        }
                    }

                    // Handle immediate changes (checkbox, InputText, etc.)
                    if (_hasImmediateFieldChanged && !_isEditingGroup)
                    {
                        var snapshotAfter = SnapshotGroup(groupInstance);

                        if (_groupSnapshotBeforeFrame != snapshotAfter)
                        {
                            PushGroupUndo(_groupSnapshotBeforeFrame, snapshotAfter, $"{GetFullPathDescription()} (group modified)");
                        }
                    }
                }
            }

            VoltageEditorUtils.EndBorderedGroup(new System.Numerics.Vector2(4, 1), new System.Numerics.Vector2(4, 2));
            ImGui.Unindent();
        }

        /// <summary>
        /// Called by child field inspectors when they have immediate (non-drag) changes.
        /// </summary>
        public void NotifyFieldChanged()
        {
            _hasImmediateFieldChanged = true;
        }

        public override void DrawReadOnly()
        {
            DrawMutable();
        }

        // ── Undo helpers ──────────────────────────────────────────────────────────

        private string SnapshotGroup(object groupInstance)
        {
            try
            {
                return Json.ToJson(groupInstance, new JsonSettings
                {
                    PrettyPrint = false,
                    TypeNameHandling = TypeNameHandling.Auto,
                    PreserveReferencesHandling = false
                });
            }
            catch
            {
                return string.Empty;
            }
        }

        private void PushGroupUndo(string snapshotBefore, string snapshotAfter, string description)
        {
            var groupType = _valueType;
            EditorChangeTracker.PushUndo(
                new ComponentGroupUndoAction(
                    GetRootTarget(),
                    new List<string>(_pathFromRoot),
                    snapshotBefore,
                    snapshotAfter,
                    groupType,
                    description
                ),
                GetRootTarget(),
                description
            );
            EditorChangeTracker.MarkChanged(GetRootTarget(), description);
        }
    }

    /// <summary>
    /// Proxy class needed by <see cref="TypeInspectorUtils"/> — the naming convention
    /// <c>TypeInspectors_*</c> is used throughout the codebase for registration.
    /// </summary>
    public class TypeInspectors_ComponentGroupInspector : ComponentGroupInspector { }
}