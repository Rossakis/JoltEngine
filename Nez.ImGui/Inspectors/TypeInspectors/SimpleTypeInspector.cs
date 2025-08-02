using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Nez.ImGuiTools.UndoActions;
using Nez.Utils;
using Nez.Utils.Extensions;
using Num = System.Numerics;

namespace Nez.ImGuiTools.TypeInspectors
{
    /// <summary>
    /// handles inspecting a slew of different basic types
    /// </summary>
    public class SimpleTypeInspector : AbstractTypeInspector
    {
        public static Type[] KSupportedTypes =
        {
            typeof(bool), typeof(Color), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float),
            typeof(string), typeof(Vector2), typeof(Vector3)
        };

        RangeAttribute _rangeAttribute;
        Action _inspectMethodAction;
        bool _isUnsignedInt;

        public override void Initialize()
        {
            base.Initialize();
            _rangeAttribute = _memberInfo.GetAttribute<RangeAttribute>();

            // the inspect method name matters! We use reflection to fetch it.
            var valueTypeName = _valueType.Name.ToString();
            var inspectorMethodName = "Inspect" + valueTypeName[0].ToString().ToUpper() + valueTypeName.Substring(1);
            var inspectMethodInfo = ReflectionUtils.GetMethodInfo(this, inspectorMethodName);
            _inspectMethodAction = ReflectionUtils.CreateDelegate<Action>(this, inspectMethodInfo);

            // fix up the Range.minValue if we have an unsigned value to avoid overflow when converting
            _isUnsignedInt = _valueType == typeof(uint) || _valueType == typeof(ulong);
            if (_isUnsignedInt && _rangeAttribute == null)
                _rangeAttribute = new RangeAttribute(0);
            else if (_isUnsignedInt && _rangeAttribute != null && _rangeAttribute.MinValue < 0)
                _rangeAttribute.MinValue = 0;
        }

        public override void DrawMutable()
        {
            _inspectMethodAction();
            HandleTooltip();
        }

        void InspectBoolean()
        {
            var value = GetValue<bool>();
            if (ImGui.Checkbox(_name, ref value))
                SetValueWithUndo(value, _name);
        }

        void InspectColor()
        {
            var value = GetValue<Color>().ToNumerics();
            if (ImGui.ColorEdit4(_name, ref value))
                SetValueWithUndo(value.ToXNAColor(), _name);
        }

        void InspectString()
        {
            var value = GetValue<string>() ?? string.Empty;
            if (ImGui.InputText(_name, ref value, 100))
                SetValueWithUndo(value, _name);
        }

        /// <summary>
        /// simplifies int, uint, long and ulong handling. They all get converted to Int32 so there is some precision loss.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        bool InspectAnyInt(ref int value)
        {
            bool changed = false;
            string fieldKey = _name;

            if (_rangeAttribute != null)
            {
                if (_rangeAttribute.UseDragVersion)
                {
                    // DragInt: batch undo/redo
                    changed = ImGui.DragInt(_name, ref value, 1, (int)_rangeAttribute.MinValue, (int)_rangeAttribute.MaxValue);
                    HandleEditSession(fieldKey, value, changed);
                }
                else
                {
                    // SliderInt: batch undo/redo
                    changed = ImGui.SliderInt(_name, ref value, (int)_rangeAttribute.MinValue, (int)_rangeAttribute.MaxValue);
                    HandleEditSession(fieldKey, value, changed);
                }
            }
            else
            {
                // InputInt: immediate undo/redo
                changed = ImGui.InputInt(_name, ref value);
                if (changed)
                    SetValueWithUndo(value, _name);
            }

            return changed;
        }

        void InspectInt32()
        {
            var value = GetValue<int>();
            InspectAnyInt(ref value);
        }

        void InspectUInt32()
        {
            var value = Convert.ToInt32(GetValue());
            if (_rangeAttribute != null)
            {
                bool changed;
                string fieldKey = _name;
                if (_rangeAttribute.UseDragVersion)
                {
                    changed = ImGui.DragInt(_name, ref value, 1, (int)_rangeAttribute.MinValue, (int)_rangeAttribute.MaxValue);
                    HandleEditSession(fieldKey, value, changed, v => Convert.ToUInt32(v));
                }
                else
                {
                    changed = ImGui.SliderInt(_name, ref value, (int)_rangeAttribute.MinValue, (int)_rangeAttribute.MaxValue);
                    HandleEditSession(fieldKey, value, changed, v => Convert.ToUInt32(v));
                }
            }
            else
            {
                if (ImGui.InputInt(_name, ref value))
                    SetValueWithUndo(Convert.ToUInt32(value), _name);
            }
        }

        void InspectInt64()
        {
            var value = Convert.ToInt32(GetValue());
            if (_rangeAttribute != null)
            {
                bool changed;
                string fieldKey = _name;
                if (_rangeAttribute.UseDragVersion)
                {
                    changed = ImGui.DragInt(_name, ref value, 1, (int)_rangeAttribute.MinValue, (int)_rangeAttribute.MaxValue);
                    HandleEditSession(fieldKey, value, changed, v => Convert.ToInt64(v));
                }
                else
                {
                    changed = ImGui.SliderInt(_name, ref value, (int)_rangeAttribute.MinValue, (int)_rangeAttribute.MaxValue);
                    HandleEditSession(fieldKey, value, changed, v => Convert.ToInt64(v));
                }
            }
            else
            {
                if (ImGui.InputInt(_name, ref value))
                    SetValueWithUndo(Convert.ToInt64(value), _name);
            }
        }

        unsafe void InspectUInt64()
        {
            var value = Convert.ToInt32(GetValue());
            if (_rangeAttribute != null)
            {
                bool changed;
                string fieldKey = _name;
                if (_rangeAttribute.UseDragVersion)
                {
                    changed = ImGui.DragInt(_name, ref value, 1, (int)_rangeAttribute.MinValue, (int)_rangeAttribute.MaxValue);
                    HandleEditSession(fieldKey, value, changed, v => Convert.ToUInt64(v));
                }
                else
                {
                    changed = ImGui.SliderInt(_name, ref value, (int)_rangeAttribute.MinValue, (int)_rangeAttribute.MaxValue);
                    HandleEditSession(fieldKey, value, changed, v => Convert.ToUInt64(v));
                }
            }
            else
            {
                if (ImGui.InputInt(_name, ref value))
                    SetValueWithUndo(Convert.ToUInt64(value), _name);
            }
        }

        void InspectSingle()
        {
            var value = GetValue<float>();
            bool changed = false;
            string fieldKey = _name;

            if (_rangeAttribute != null)
            {
                if (_rangeAttribute.UseDragVersion)
                {
                    changed = ImGui.DragFloat(_name, ref value, 1, _rangeAttribute.MinValue, _rangeAttribute.MaxValue);
                    HandleEditSession(fieldKey, value, changed);
                }
                else
                {
                    changed = ImGui.SliderFloat(_name, ref value, _rangeAttribute.MinValue, _rangeAttribute.MaxValue);
                    HandleEditSession(fieldKey, value, changed);
                }
            }
            else
            {
                changed = ImGui.DragFloat(_name, ref value);
                HandleEditSession(fieldKey, value, changed);
            }
        }

        void InspectVector2()
        {
            var value = GetValue<Vector2>().ToNumerics();
            string fieldKey = _name;
            bool changed = ImGui.DragFloat2(_name, ref value);
            HandleEditSession(fieldKey, value.ToXNA(), changed);
        }

        void InspectVector3()
        {
            var value = GetValue<Vector3>().ToNumerics();
            string fieldKey = _name;
            bool changed = ImGui.DragFloat3(_name, ref value);
            HandleEditSession(fieldKey, value.ToXNA(), changed);
        }

		void HandleEditSession<T>(string fieldKey, T value, bool changed, Func<T, object> convert = null)
		{
			var session = GetEditSession(fieldKey);

			// Start of edit session
			if (ImGui.IsItemActive() && !session.IsEditing)
			{
				session.IsEditing = true;
				session.EditStartValue = GetValue();
			}

			// Apply value live (for drags/sliders)
			if (changed)
			{
				SetValue(convert != null ? convert(value) : value);
			}

			// End of edit session: push undo if value changed
			if (session.IsEditing && ImGui.IsItemDeactivatedAfterEdit())
			{
				session.IsEditing = false;
				var endValue = GetValue();
				if (!Equals(session.EditStartValue, endValue))
				{
					if (_parentInspector is not StructInspector)
					{
						// For non-struct fields, create undo action directly
						EditorChangeTracker.PushUndo(
							new PathUndoAction(
								GetRootTarget(),
								new List<string>(_pathFromRoot),
								session.EditStartValue,
								endValue,
								GetFullPathDescription()
							),
							GetRootTarget(),
							GetFullPathDescription()
						);
						EditorChangeTracker.MarkChanged(GetRootTarget(), GetFullPathDescription());
					}
				}
			}

			SetEditSession(fieldKey, session);
		}
	}
}