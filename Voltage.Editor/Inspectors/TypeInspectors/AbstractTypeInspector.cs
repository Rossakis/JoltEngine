using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using ImGuiNET;
using Voltage;
using Voltage.Utils;
using Voltage.Utils.Extensions;
using Voltage.Editor.Utils;
using Voltage.Editor.Undo.Core;

namespace Voltage.Editor.Inspectors.TypeInspectors
{
	/// <summary>
	/// subclasses are used to inspect various built-in types. A bit of care has to be taken when we are dealing with any non-value types. Objects
	/// can be null and we don't want to inspect a null object. Having a null value for an inspected class when initialize is called means we
	/// cant create the AbstractTypeInspectors for the fields of the object since we need an object to wrap the getter/setter with.
	/// </summary>
	public abstract class AbstractTypeInspector
	{
		protected struct EditSession
		{
			public bool IsEditing;
			public object EditStartValue;
		}

		public string Name => _name;

		/// <summary>
		/// parent inspectors that also keep a list sub-inspectors can check this to ensure the object the sub-inspectors was inspecting
		/// is still around. Of course, child inspectors must be dilgent about setting it when the remove themselves!
		/// </summary>
		public bool IsTargetDestroyed => _isTargetDestroyed;

		// /// <summary>
		// /// When true, this inspector will not create undo actions
		// /// </summary>
		// public bool IsUndoDisabled { get; set; } = false;

		/// <summary>
		/// Public accessor for MemberInfo (needed by StructInspector)
		/// </summary>
		public MemberInfo MemberInfo => _memberInfo;

		protected int _scopeId = VoltageEditorUtils.GetScopeId();
		protected bool _wantsIndentWhenDrawn;

		protected bool _isTargetDestroyed;
		protected object _target;
		protected string _name;
		protected Type _valueType;
		protected Func<object, object> _getter;
		protected Action<object> _setter;
		protected MemberInfo _memberInfo;
		protected bool _isReadOnly;
		protected string _tooltip;

		// Undo/Redo support fields
		protected AbstractTypeInspector _parentInspector;
		protected List<string> _pathFromRoot = new List<string>(); // path of member names from root to this inspector
		private Dictionary<string, EditSession> _editSessions = new();

		/// <summary>
		/// used to prep the inspector
		/// </summary>
		public virtual void Initialize()
		{
			_tooltip = _memberInfo.GetAttribute<TooltipAttribute>()?.Tooltip;
			_editSessions.Clear();
		}

		/// <summary>
		/// used to draw the UI for the Inspector. Calls either drawMutable or drawReadOnly depending on the _isReadOnly bool
		/// </summary>
		public void Draw()
		{
			if (_wantsIndentWhenDrawn)
				ImGui.Indent();

			ImGui.PushID(_scopeId);
			if (_isReadOnly)
			{
				ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.5f);
				DrawReadOnly();
				ImGui.PopStyleVar();
			}
			else
			{
				DrawMutable();
			}

			ImGui.PopID();

			if (_wantsIndentWhenDrawn)
				ImGui.Unindent();
		}

		public abstract void DrawMutable();

		/// <summary>
		/// default implementation disables the next widget and calls through to drawMutable. If specialy drawing needs to
		/// be done (such as a multi-widget setup) this can be overridden.
		/// </summary>
		public virtual void DrawReadOnly()
		{
			VoltageEditorUtils.DisableNextWidget();
			DrawMutable();
		}

		/// <summary>
		/// if there is a tooltip and the item is hovered this will display it
		/// </summary>
		protected void HandleTooltip()
		{
			if (!string.IsNullOrEmpty(_tooltip) && ImGui.IsItemHovered())
			{
				ImGui.BeginTooltip();
				ImGuiSafe.TextSafe(_tooltip);
				ImGui.EndTooltip();
			}
		}

		#region Set target methods

		public void SetTarget(object target, FieldInfo field)
		{
			_target = target;
			_memberInfo = field;
			_name = field.Name;
			_valueType = field.FieldType;
			_isReadOnly = field.IsInitOnly;

			if (target == null)
				return;

			_getter = obj => { return field.GetValue(obj); };

			if (!_isReadOnly)
			{
				_setter = (val) => { field.SetValue(target, val); };
			}

			_parentInspector = null;
			_pathFromRoot = new List<string> { field.Name };
		}

		public void SetTarget(object target, PropertyInfo prop)
		{
			_memberInfo = prop;
			_target = target;
			_name = prop.Name;
			_valueType = prop.PropertyType;
			
			// Check getter and setter accessibility
			bool hasPublicGetter = prop.CanRead && (prop.GetMethod?.IsPublic ?? false);
			bool hasPublicSetter = prop.CanWrite && (prop.SetMethod?.IsPublic ?? false);
			
			// Property is read-only if:
			// 1. It can't write, OR
			// 2. The setter is not public (even if getter is public)
			_isReadOnly = !prop.CanWrite || !hasPublicSetter;

			if (target == null)
				return;

			// Always create getter if we can read
			if (prop.CanRead)
			{
				_getter = obj => { return prop.GetMethod.Invoke(obj, null); };
			}

			// Only create setter if property is writable AND has public setter
			if (prop.CanWrite && hasPublicSetter)
			{
				_setter = (val) => { prop.SetMethod.Invoke(target, new object[] { val }); };
			}

			_parentInspector = null;
			_pathFromRoot = new List<string> { prop.Name };
		}

		/// <summary>
		/// this version will first fetch the struct before getting/setting values on it when invoking the getter/setter
		/// </summary>
		/// <returns>The struct target.</returns>
		/// <param name="target">Target.</param>
		/// <param name="structName">Struct name.</param>
		/// <param name="field">Field.</param>
		public void SetStructTarget(object target, AbstractTypeInspector parentInspector, FieldInfo field)
		{
			_target = target;
			_memberInfo = field;
			_name = field.Name;
			_valueType = field.FieldType;
			_isReadOnly = field.IsInitOnly || parentInspector._isReadOnly;

			_getter = obj =>
			{
				var structValue = parentInspector.GetValue();
				return field.GetValue(structValue);
			};

			if (!_isReadOnly)
			{
				_setter = val =>
				{
					var structValue = parentInspector.GetValue();
					field.SetValue(structValue, val);
					parentInspector.SetValue(structValue);
					
					// Only notify for immediate changes (not during edit sessions)
					// Edit sessions are handled by the StructInspector's IsFieldCurrentlyActive() detection
					if (parentInspector is StructInspector structInspector)
					{
						// Check if this is an immediate change (not part of an edit session)
						bool isImmediateChange = !IsCurrentlyInEditSession();
						
						if (isImmediateChange)
						{
							structInspector.NotifyFieldChanged();
						}
					}
				};
			}

			_parentInspector = parentInspector;
			_pathFromRoot = new List<string>(parentInspector._pathFromRoot) { field.Name };
		}

		public void SetStructTarget(object target, AbstractTypeInspector parentInspector, PropertyInfo prop)
		{
			_target = target;
			_memberInfo = prop;
			_name = prop.Name;
			_valueType = prop.PropertyType;
	
			// Check getter and setter accessibility
			bool hasPublicGetter = prop.CanRead && (prop.GetMethod?.IsPublic ?? false);
			bool hasPublicSetter = prop.CanWrite && (prop.SetMethod?.IsPublic ?? false);
	
			// Property is read-only if:
			// 1. It can't write, OR
			// 2. The setter is not public, OR
			// 3. Parent is read-only
			_isReadOnly = !prop.CanWrite || !hasPublicSetter || parentInspector._isReadOnly;

			// Always create getter if we can read
			if (prop.CanRead)
			{
				_getter = obj =>
				{
					var structValue = parentInspector.GetValue();
					return ReflectionUtils.GetPropertyGetter(prop).Invoke(structValue, null);
				};
			}

			// Only create setter if property is writable, has public setter, AND parent is not read-only
			if (prop.CanWrite && hasPublicSetter && !parentInspector._isReadOnly)
			{
				_setter = (val) =>
				{
					var structValue = parentInspector.GetValue();
					prop.SetValue(structValue, val);
					parentInspector.SetValue(structValue);
				};
			}

			_parentInspector = parentInspector;
			_pathFromRoot = new List<string>(parentInspector._pathFromRoot) { prop.Name };
		}

		/// <summary>
		/// Sets up this inspector as a child of a <see cref="ComponentGroupInspector"/> (class reference, not value type).
		/// Unlike <see cref="SetStructTarget"/>, no copy-back is needed — fields are mutated directly on the group instance.
		/// </summary>
		public void SetClassGroupTarget(object target, AbstractTypeInspector parentInspector, FieldInfo field)
		{
			_target = target;
			_memberInfo = field;
			_name = field.Name;
			_valueType = field.FieldType;
			_isReadOnly = field.IsInitOnly || parentInspector._isReadOnly;

			// Group is a class reference — fetch it once; no copy-back needed.
			_getter = obj =>
			{
				var groupInstance = parentInspector.GetValue();
				return groupInstance != null ? field.GetValue(groupInstance) : null;
			};

			if (!_isReadOnly)
			{
				_setter = val =>
				{
					var groupInstance = parentInspector.GetValue();
					if (groupInstance == null) return;
					field.SetValue(groupInstance, val);

					// Notify parent for immediate (non-drag) undo tracking
					if (parentInspector is ComponentGroupInspector groupInspector && !IsCurrentlyInEditSession())
						groupInspector.NotifyFieldChanged();
				};
			}

			_parentInspector = parentInspector;
			_pathFromRoot = new List<string>(parentInspector._pathFromRoot) { field.Name };
		}

		/// <summary>
		/// Sets up this inspector as a child of a <see cref="ComponentGroupInspector"/> (class reference, not value type).
		/// Property variant — no copy-back needed.
		/// </summary>
		public void SetClassGroupTarget(object target, AbstractTypeInspector parentInspector, PropertyInfo prop)
		{
			_target = target;
			_memberInfo = prop;
			_name = prop.Name;
			_valueType = prop.PropertyType;

			bool hasPublicGetter = prop.CanRead && (prop.GetMethod?.IsPublic ?? false);
			bool hasPublicSetter = prop.CanWrite && (prop.SetMethod?.IsPublic ?? false);

			_isReadOnly = !prop.CanWrite || !hasPublicSetter || parentInspector._isReadOnly;

			if (prop.CanRead)
			{
				_getter = obj =>
				{
					var groupInstance = parentInspector.GetValue();
					return groupInstance != null ? ReflectionUtils.GetPropertyGetter(prop).Invoke(groupInstance, null) : null;
				};
			}

			if (prop.CanWrite && hasPublicSetter && !parentInspector._isReadOnly)
			{
				_setter = val =>
				{
					var groupInstance = parentInspector.GetValue();
					if (groupInstance == null) return;
					prop.SetValue(groupInstance, val);
				};
			}

			_parentInspector = parentInspector;
			_pathFromRoot = new List<string>(parentInspector._pathFromRoot) { prop.Name };
		}

		public void SetTarget(object target, MethodInfo method)
		{
			_memberInfo = method;
			_target = target;
			_name = method.Name;
			_parentInspector = null;
			_pathFromRoot = new List<string> { method.Name };
		}

		#endregion

		#region Get/set values

		public T GetValue<T>()
		{
		    return (T)_getter(_target);
		}

		public object GetValue()
		{
		    return _getter(_target);
		}


		/// <summary>Bumped on every inspector write, so a window can tell a real edit from merely clicking a header.</summary>
		public static int ValueWriteCount { get; private set; }

		// Per object as well as globally, so an edit on a scene entity cannot dirty the Data Asset window.
		private static readonly ConditionalWeakTable<object, StrongBox<int>> _writeCounts = new();

		/// <summary>How many inspector writes have landed on this object. Compare across frames to spot edits.</summary>
		public static int WriteCountFor(object target) =>
			target != null && _writeCounts.TryGetValue(target, out var box) ? box.Value : 0;

		/// <summary>For writes that bypass the inspectors, such as undo/redo applying a value by reflection.</summary>
		public static void NoteExternalValueWrite(object target = null) => NoteWrite(target);

		/// <summary>For edits that mutate the value in place rather than assigning it, e.g. list elements.</summary>
		protected void NoteValueWritten() => NoteWrite(GetRootTarget());

		private static void NoteWrite(object target)
		{
			ValueWriteCount++;
			if (target != null)
				_writeCounts.GetOrCreateValue(target).Value++;
		}

		protected void SetValue(object value)
		{
		    _setter.Invoke(value);
		    NoteWrite(GetRootTarget());
		}

		public FieldInfo GetFieldInfo()
		{
			return _memberInfo as FieldInfo;
		}

		public PropertyInfo GetPropertyInfo()
		{
			return _memberInfo as PropertyInfo;
		}


		#endregion

		#region Undo/Redo Support
		protected EditSession GetEditSession(string fieldName)
		{
			if (!_editSessions.TryGetValue(fieldName, out var session))
				session = new EditSession();
			return session;
		}

		protected void SetEditSession(string fieldName, EditSession session)
		{
			_editSessions[fieldName] = session;
		}

		/// <summary>
		/// Sets the value and pushes an undo action if the value changed.
		/// </summary>
		protected void SetValueWithUndo(object newValue, string description = null)
		{
			var oldValue = GetValue();
			if (!Equals(oldValue, newValue))
			{
				EditorChangeTracker.PushUndo(
					new PathUndoAction(
						GetRootTarget(),
						new List<string>(_pathFromRoot),
						oldValue,
						newValue,
						description ?? GetFullPathDescription()
					),
					GetRootTarget(),
					description ?? GetFullPathDescription()
				);

				SetValue(newValue);
			}
		}

		/// <summary>
		/// Traverses up to the root target (the top-most inspector's _target).
		/// </summary>
		protected object GetRootTarget()
		{
			AbstractTypeInspector current = this;
			while (current._parentInspector != null)
				current = current._parentInspector;
			return current._target;
		}

		#endregion

		protected string GetFullPathDescription()
		{
			// Try to get the entity name if the root is an Entity
			var root = GetRootTarget();
			string entityName = root is Entity entity ? entity.Name : root?.ToString() ?? "UnknownEntity";
			return $"{entityName}.{string.Join(".", _pathFromRoot)}";
		}

		/// <summary>
		/// Returns true if this field is currently being actively edited (mouse is down on a drag/slider)
		/// </summary>
		public bool IsFieldCurrentlyActive()
		{
			// Check if any of our edit sessions are currently active
			foreach (var session in _editSessions.Values)
			{
				if (session.IsEditing)
					return true;
			}
			return false;
		}

		/// <summary>
		/// Returns true if this inspector is currently in an edit session
		/// </summary>
		private bool IsCurrentlyInEditSession()
		{
			foreach (var session in _editSessions.Values)
			{
				if (session.IsEditing)
					return true;
			}
			return false;
		}
	}
}