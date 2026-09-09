using System;
using System.Runtime.InteropServices;
using ImGuiNET;
using Num = System.Numerics;


namespace Voltage.Editor.Utils
{
	public static class VoltageEditorUtils
	{
		static int _idScope;

		/// <summary>
		/// gets a unique id that can be used with ImGui.PushId() to avoid conflicts with type inspectors
		/// </summary>
		/// <returns></returns>
		public static int GetScopeId() => _idScope++;

		public static void SmallVerticalSpace() => ImGui.Dummy(new Num.Vector2(0, 5));

		public static void MediumVerticalSpace() => ImGui.Dummy(new Num.Vector2(0, 10));
		public static void BigVerticalSpace() => ImGui.Dummy(new Num.Vector2(0, 15));
		public static void VeryBigVerticalSpace() => ImGui.Dummy(new Num.Vector2(0, 20));

		/// <summary>
		/// adds a DrawList command to draw a border around the group
		/// </summary>
		public static void BeginBorderedGroup()
		{
			ImGui.BeginGroup();
		}

		public static void EndBorderedGroup() => EndBorderedGroup(new Num.Vector2(3, 2), new Num.Vector2(0, 3));

		public static void EndBorderedGroup(Num.Vector2 minPadding, Num.Vector2 maxPadding = default(Num.Vector2))
		{
			ImGui.EndGroup();

			// attempt to size the border around the content to frame it
			var color = ImGui.GetStyle().Colors[(int) ImGuiCol.Border];

			var min = ImGui.GetItemRectMin();
			var max = ImGui.GetItemRectMax();
			max.X = min.X + ImGui.GetContentRegionAvail().X;
			ImGui.GetWindowDrawList().AddRect(min - minPadding, max + maxPadding, ImGui.ColorConvertFloat4ToU32(color));

			// this fits just the content, not the full width
			//ImGui.GetWindowDrawList().AddRect( ImGui.GetItemRectMin() - padding, ImGui.GetItemRectMax() + padding, packedColor );
		}

		/// <summary>
		/// aligns a button and label in the same way LabelText and regular widgets are lined up
		/// </summary>
		/// <param name="label"></param>
		/// <param name="buttonText"></param>
		/// <returns></returns>
		public static bool LabelButton(string label, string buttonText)
		{
			ImGui.AlignTextToFramePadding();

			var wasClicked = Gui.Button(buttonText);
			ImGui.SameLine(0,
				ImGui.GetWindowWidth() * 0.65f - ImGui.GetItemRectSize().X + ImGui.GetStyle().ItemInnerSpacing.X);
			ImGuiSafe.TextSafe(label);

			return wasClicked;
		}

		/// <summary>
		/// most widgets heights are calculated using this formula. Some let you specifiy a height though.
		/// </summary>
		/// <returns></returns>
		public static float GetDefaultWidgetHeight() => ImGui.GetFontSize() + ImGui.GetStyle().FramePadding.Y * 2f;

		/// <summary>
		/// draws an invisible button that will cover the next widget rect
		/// </summary>
		/// <param name="widgetCustomHeight"></param>
		public static void DisableNextWidget(float widgetCustomHeight = 0)
		{
			var origCursorPos = ImGui.GetCursorPos();
			var widgetSize = new Num.Vector2(ImGui.GetContentRegionAvail().X,
                widgetCustomHeight > 0 ? widgetCustomHeight : GetDefaultWidgetHeight());
			Gui.InvisibleButton("##disabled", widgetSize);
			ImGui.SetCursorPos(origCursorPos);
		}

		/// <summary>
		/// draws a button with the width as a percentage of the window contnet region centered.
		/// </summary>
		/// <param name="percentWidth"></param>
		/// <returns></returns>
		public static bool CenteredButton(string label, float percentWidth, float xIndent = 0)
		{
			var buttonWidth = ImGui.GetContentRegionAvail().X * percentWidth;
			ImGui.SetCursorPosX(xIndent + (ImGui.GetContentRegionAvail().X - buttonWidth) / 2f);
			return Gui.Button(label, new System.Numerics.Vector2(buttonWidth, GetDefaultWidgetHeight()));
		}

		/// <summary>
		/// The editor's one control for "is this shown?". Flips <paramref name="visible"/> and returns true on
		/// the frame it is clicked. Pass a label to draw text after the icon.
		/// </summary>
		public static bool EyeToggle(string id, ref bool visible, string tooltip = null, string label = null,
		                             float size = 0f)
		{
			var icon = visible ? ImguiImageLoader.EyeOn : ImguiImageLoader.EyeOff;

			// Fall back to a checkbox if the icons never got bound, so the control is never missing.
			if (icon == IntPtr.Zero)
			{
				var toggled = Gui.Checkbox(string.IsNullOrEmpty(label) ? $"##{id}" : $"{label}##{id}", ref visible);
				if (tooltip != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
					ImGui.SetTooltip(tooltip);

				return toggled;
			}

			if (size <= 0f)
				size = ImGui.GetFontSize() + 2f;

			// Hidden dims as well as changing the art, so the two states differ in shape and in weight.
			var tint = visible
				? Num.Vector4.One
				: new Num.Vector4(1f, 1f, 1f, 0.45f);

			ImGui.PushStyleColor(ImGuiCol.Button, new Num.Vector4(0f, 0f, 0f, 0f));
			ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Num.Vector2(2f, 2f));

			var clicked = Gui.ImageButton($"##eye_{id}", icon, new Num.Vector2(size, size),
				Num.Vector2.Zero, Num.Vector2.One, new Num.Vector4(0f, 0f, 0f, 0f), tint);

			ImGui.PopStyleVar();
			ImGui.PopStyleColor();

			if (tooltip != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
				ImGui.SetTooltip(tooltip);

			if (clicked)
				visible = !visible;

			if (!string.IsNullOrEmpty(label))
			{
				ImGui.SameLine();
				ImGui.AlignTextToFramePadding();
				ImGuiSafe.TextSafe(label);
			}

			return clicked;
		}

		/// <summary>
		/// Colour swatch button with a hairline outline, so it stays readable even when the editor theme is the
		/// same colour as the swatch shows. <paramref name="highlight"/> makes that outline amber.
		/// </summary>
		public static bool ColorSwatchButton(string id, Num.Vector4 color, bool highlight = false, float width = 0f)
		{
			if (width <= 0f)
				width = ImGui.GetFrameHeight() * 1.6f;

			ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
			ImGui.PushStyleColor(ImGuiCol.Border, highlight
				? new Num.Vector4(1.0f, 0.8f, 0.2f, 1.0f)
				: new Num.Vector4(0.85f, 0.85f, 0.85f, 0.85f));

			var clicked = ImGui.ColorButton(id, color,
				ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoAlpha,
				new Num.Vector2(width, ImGui.GetFrameHeight()));

			ImGui.PopStyleColor();
			ImGui.PopStyleVar();

			return clicked;
		}

		/// <summary>
		/// shows a tooltip informing the user they can right click
		/// </summary>
		public static void ShowContextMenuTooltip()
		{
			if (ImGui.IsItemHovered())
			{
				ImGui.BeginTooltip();
				ImGui.Text("Right click for more options");
				ImGui.EndTooltip();
			}
		}

		/// <summary>
		/// displays a simple dialog with some text and a couple buttons. Note that ImGui.OpenPopup( name ) has to be called
		/// in the same ID scope as this call.
		/// </summary>
		/// <param name="name"></param>
		/// <param name="message"></param>
		/// <param name="okButton"></param>
		/// <param name="cxlButton"></param>
		/// <returns></returns>
		public static bool SimpleDialog(string name, string message, string okButton = "OK",
		                                string cxlButton = "Cancel")
		{
			var result = false;
			var junkBool = true;
			if (Gui.BeginPopupModal(name, ref junkBool, ImGuiWindowFlags.AlwaysAutoResize))
			{
				result = false;

				ImGuiSafe.TextWrappedSafe(message);
				MediumVerticalSpace();
				ImGui.Separator();
				SmallVerticalSpace();

				if (Gui.Button(cxlButton, new Num.Vector2(120, 0)))
				{
					ImGui.CloseCurrentPopup();
				}

				ImGui.SetItemDefaultFocus();
				ImGui.SameLine();
				if (Gui.Button(okButton, new Num.Vector2(120, 0)))
				{
					result = true;
					ImGui.CloseCurrentPopup();
				}

				Gui.EndPopup();
			}

			return result;
		}

		#region Wrappers for unsinged Drag/SliderScaler

		/// <summary>
		/// wraps ImGui.DragScaler and handles all IntPtr conversion
		/// </summary>
		/// <param name="label"></param>
		/// <param name="value"></param>
		/// <param name="speed"></param>
		/// <param name="min"></param>
		/// <param name="max"></param>
		/// <returns></returns>
		public unsafe static bool DragScaler(string label, ref ulong value, float speed, int min, int max)
		{
			var tempValue = value;
			var valuePtr = new IntPtr(&tempValue);
			var minPtr = new IntPtr(&min);
			var maxPtr = new IntPtr(&max);

			if (ImGui.DragScalar(label, ImGuiDataType.U64, valuePtr, speed, minPtr, maxPtr))
			{
				value = Marshal.PtrToStructure<ulong>(valuePtr);
				return true;
			}

			return false;
		}

		public unsafe static bool DragScaler(string label, ref ulong value, float speed)
		{
			var tempValue = value;
			var valuePtr = new IntPtr(&tempValue);

			if (ImGui.DragScalar(label, ImGuiDataType.U64, valuePtr, speed))
			{
				value = Marshal.PtrToStructure<ulong>(valuePtr);
				return true;
			}

			return false;
		}

		/// <summary>
		/// wraps ImGui.DragScaler and handles all IntPtr conversion
		/// </summary>
		/// <param name="label"></param>
		/// <param name="value"></param>
		/// <param name="speed"></param>
		/// <param name="min"></param>
		/// <param name="max"></param>
		/// <returns></returns>
		public unsafe static bool DragScaler(string label, ref uint value, float speed, int min, int max)
		{
			var tempValue = value;
			var valuePtr = new IntPtr(&tempValue);
			var minPtr = new IntPtr(&min);
			var maxPtr = new IntPtr(&max);

			if (ImGui.DragScalar(label, ImGuiDataType.U32, valuePtr, speed, minPtr, maxPtr))
			{
				value = Marshal.PtrToStructure<uint>(valuePtr);
				return true;
			}

			return false;
		}

		public unsafe static bool DragScaler(string label, ref uint value, float speed)
		{
			var tempValue = value;
			var valuePtr = new IntPtr(&tempValue);

			if (ImGui.DragScalar(label, ImGuiDataType.U32, valuePtr, speed))
			{
				value = Marshal.PtrToStructure<uint>(valuePtr);
				return true;
			}

			return false;
		}

		/// <summary>
		/// wraps ImGui.SliderScalar and handles all IntPtr conversion
		/// </summary>
		/// <param name="label"></param>
		/// <param name="value"></param>
		/// <param name="speed"></param>
		/// <param name="min"></param>
		/// <param name="max"></param>
		/// <returns></returns>
		public unsafe static bool SliderScalar(string label, ref ulong value, int min, int max)
		{
			var tempValue = value;
			var valuePtr = new IntPtr(&tempValue);
			var minPtr = new IntPtr(&min);
			var maxPtr = new IntPtr(&max);

			if (ImGui.SliderScalar(label, ImGuiDataType.U64, valuePtr, minPtr, maxPtr))
			{
				value = Marshal.PtrToStructure<ulong>(valuePtr);
				return true;
			}

			return false;
		}

		/// <summary>
		/// wraps ImGui.SliderScalar and handles all IntPtr conversion
		/// </summary>
		/// <param name="label"></param>
		/// <param name="value"></param>
		/// <param name="speed"></param>
		/// <param name="min"></param>
		/// <param name="max"></param>
		/// <returns></returns>
		public unsafe static bool SliderScalar(string label, ref uint value, int min, int max)
		{
			var tempValue = value;
			var valuePtr = new IntPtr(&tempValue);
			var minPtr = new IntPtr(&min);
			var maxPtr = new IntPtr(&max);

			if (ImGui.SliderScalar(label, ImGuiDataType.U32, valuePtr, minPtr, maxPtr))
			{
				value = Marshal.PtrToStructure<uint>(valuePtr);
				return true;
			}

			return false;
		}

		public unsafe static bool InputScaler(string label, ref ulong value)
		{
			var tempValue = value;
			var valuePtr = new IntPtr(&tempValue);

			if (ImGui.InputScalar(label, ImGuiDataType.U64, valuePtr))
			{
				value = Marshal.PtrToStructure<ulong>(valuePtr);
				return true;
			}

			return false;
		}

		public unsafe static bool InputScaler(string label, ref uint value)
		{
			var tempValue = value;
			var valuePtr = new IntPtr(&tempValue);

			if (ImGui.InputScalar(label, ImGuiDataType.U32, valuePtr))
			{
				value = Marshal.PtrToStructure<uint>(valuePtr);
				return true;
			}

			return false;
		}

		#endregion
	}
}