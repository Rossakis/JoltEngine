using System;
using System.Collections.Generic;
using ImGuiNET;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.Inspectors.Attributes;
using Voltage.Editor.Inspectors.TypeInspectors;
using Voltage.Editor.Undo.Core;
using Voltage.Editor.Undo.SceneComponentActions;
using Voltage.Editor.Utils;
using Voltage.Persistence;
using Num = System.Numerics;

namespace Voltage.Editor.Inspectors.ObjectInspectors;

/// <summary>
/// ImGui inspector panel for a single <see cref="SceneComponent"/> instance.
/// Mirrors the layout and interaction of <see cref="ComponentInspector"/> but
/// targets scene-scoped components rather than entity components.
/// </summary>
public class SceneComponentInspector
{
	private readonly SceneComponent _component;
	private readonly string _displayName;
	private readonly List<AbstractTypeInspector> _inspectors;
	private readonly List<AbstractTypeInspector> _readOnlyStructInspectors = new();
	private readonly List<AbstractTypeInspector> _regularInspectors = new();
	private readonly List<Action> _delegateMethods = new();
	private readonly int _scopeId = VoltageEditorUtils.GetScopeId();
	private bool _isReadOnlyStructsOpen;
	private ImGuiManager _imGuiManager;

	public SceneComponentInspector(SceneComponent component)
	{
		_component = component;

		_inspectors = TypeInspectorUtils.GetInspectableProperties(component);

		// The "Enabled" toggle is drawn manually below so strip it from auto list
		_inspectors.RemoveAll(i => i.Name == nameof(SceneComponent.Enabled));

		SeparateReadOnlyStructs();

		var typeName = _component.GetType().Name;
		var compName = string.IsNullOrEmpty(_component.Name) ? typeName : _component.Name;
		_displayName = compName == typeName ? typeName : $"{compName} ({typeName})";

		var methods = TypeInspectorUtils.GetAllMethodsWithAttribute<InspectorDelegateAttribute>(_component.GetType());
		foreach (var method in methods)
		{
			if (method.GetParameters().Length == 0)
				_delegateMethods.Add((Action)Delegate.CreateDelegate(typeof(Action), _component, method));
		}
	}

	private void SeparateReadOnlyStructs()
	{
		_regularInspectors.Clear();
		_readOnlyStructInspectors.Clear();

		foreach (var inspector in _inspectors)
		{
			if (inspector.GetType() == typeof(TypeInspectors.StructInspector))
			{
				var si = inspector as TypeInspectors.StructInspector;

				if (si.MemberInfo != null)
				{
					bool isReadOnly = false;

					if (si.MemberInfo is System.Reflection.FieldInfo fi)
						isReadOnly = fi.IsInitOnly;
					else if (si.MemberInfo is System.Reflection.PropertyInfo pi)
						isReadOnly = !(pi.CanWrite && (pi.SetMethod?.IsPublic ?? false));

					if (isReadOnly)
						_readOnlyStructInspectors.Add(inspector);
					else
						_regularInspectors.Add(inspector);
				}
				else
				{
					_regularInspectors.Add(inspector);
				}
			}
			else
			{
				_regularInspectors.Add(inspector);
			}
		}
	}

	public void Draw()
	{
		if (_imGuiManager == null)
			_imGuiManager = Core.GetGlobalManager<ImGuiManager>();

		ImGui.PushID(_scopeId);

		var isHeaderOpen = Gui.CollapsingHeader(_displayName);

		// Context menu: remove component
		if (Gui.BeginPopupContextItem())
		{
			if (Gui.Selectable("Remove Scene Component"))
			{
				EditorChangeTracker.PushUndo(
					new SceneComponentRemovedUndoAction(Core.Scene, _component,
						$"Remove SceneComponent {_component.GetType().Name}"),
					null,
					$"Remove SceneComponent {_component.GetType().Name}"
				);
				Core.Scene.RemoveSceneComponent(_component);
			}

			Gui.EndPopup();
		}

		if (isHeaderOpen)
		{
			// Enabled toggle
			bool oldEnabled = _component.Enabled;
			bool enabled = oldEnabled;
			if (Gui.Checkbox("Enabled##SC", ref enabled) && enabled != oldEnabled)
			{
				EditorChangeTracker.PushUndo(
					new SceneComponentEnabledChangeAction(_component, oldEnabled, enabled),
					null,
					$"{_displayName}.Enabled"
				);
				_component.SetEnabled(enabled);
			}

			VoltageEditorUtils.SmallVerticalSpace();

			for (var i = _regularInspectors.Count - 1; i >= 0; i--)
			{
				if (_regularInspectors[i].IsTargetDestroyed)
				{
					_regularInspectors.RemoveAt(i);
					continue;
				}
				_regularInspectors[i].Draw();
			}

			if (_readOnlyStructInspectors.Count > 0)
			{
				VoltageEditorUtils.SmallVerticalSpace();

				ImGui.PushStyleColor(ImGuiCol.Header, new Num.Vector4(0.3f, 0.3f, 0.4f, 0.6f));
				ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Num.Vector4(0.35f, 0.35f, 0.45f, 0.7f));
				ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Num.Vector4(0.4f, 0.4f, 0.5f, 0.8f));

				_isReadOnlyStructsOpen = Gui.CollapsingHeader(
					"Read Only",
					_isReadOnlyStructsOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None
				);

				ImGui.PopStyleColor(3);

				if (_isReadOnlyStructsOpen)
				{
					ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.7f);
					for (var i = _readOnlyStructInspectors.Count - 1; i >= 0; i--)
					{
						if (_readOnlyStructInspectors[i].IsTargetDestroyed)
						{
							_readOnlyStructInspectors.RemoveAt(i);
							continue;
						}
						_readOnlyStructInspectors[i].Draw();
					}
					ImGui.PopStyleVar();
				}
			}

			foreach (var action in _delegateMethods)
				action();
		}

		ImGui.PopID();
	}
}
