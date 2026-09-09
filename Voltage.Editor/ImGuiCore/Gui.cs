using System;
using System.Numerics;
using ImGuiNET;
using Voltage.Editor.Gateway;

namespace Voltage.Editor.ImGuiCore;

/// <summary>ImGui widgets that also report themselves to <see cref="UiRegistry"/>; same signatures as ImGui.NET so call sites only change the class name.</summary>
public static class Gui
{
	public static bool Begin(string name) => Window(ImGui.Begin(name), name);

	public static bool Begin(string name, ImGuiWindowFlags flags) => Window(ImGui.Begin(name, flags), name);

	public static bool Begin(string name, ref bool open) => Window(ImGui.Begin(name, ref open), name);

	public static bool Begin(string name, ref bool open, ImGuiWindowFlags flags) => Window(ImGui.Begin(name, ref open, flags), name);

	public static void End()
	{
		ImGui.End();
		UiRegistry.PopWindow("window");
	}

	public static bool BeginMainMenuBar()
	{
		var open = ImGui.BeginMainMenuBar();
		if (open)
			UiRegistry.PushWindow("menubar", "MainMenuBar", true);
		return open;
	}

	public static void EndMainMenuBar()
	{
		ImGui.EndMainMenuBar();
		UiRegistry.PopWindow("menubar");
	}

	public static bool BeginPopup(string id) => Popup(ImGui.BeginPopup(id), "popup", id);

	public static bool BeginPopup(string id, ImGuiWindowFlags flags) => Popup(ImGui.BeginPopup(id, flags), "popup", id);

	public static bool BeginPopupModal(string name) => Popup(ImGui.BeginPopupModal(name), "modal", name);

	public static bool BeginPopupModal(string name, ref bool open) => Popup(ImGui.BeginPopupModal(name, ref open), "modal", name);

	public static bool BeginPopupModal(string name, ref bool open, ImGuiWindowFlags flags) => Popup(ImGui.BeginPopupModal(name, ref open, flags), "modal", name);

	public static bool BeginPopupContextItem() => Popup(ImGui.BeginPopupContextItem(), "popup", UiRegistry.ContextPopupName(true));

	public static bool BeginPopupContextItem(string id) => Popup(ImGui.BeginPopupContextItem(id), "popup", id);

	public static bool BeginPopupContextItem(string id, ImGuiPopupFlags flags) => Popup(ImGui.BeginPopupContextItem(id, flags), "popup", id);

	public static bool BeginPopupContextWindow() => Popup(ImGui.BeginPopupContextWindow(), "popup", UiRegistry.ContextPopupName(false));

	public static bool BeginPopupContextWindow(string id) => Popup(ImGui.BeginPopupContextWindow(id), "popup", id);

	public static bool BeginPopupContextWindow(string id, ImGuiPopupFlags flags) => Popup(ImGui.BeginPopupContextWindow(id, flags), "popup", id);

	public static void EndPopup()
	{
		ImGui.EndPopup();
		UiRegistry.PopWindow("popup", "modal");
	}

	public static bool BeginMenu(string label) => Menu(ImGui.BeginMenu(label), label, true);

	public static bool BeginMenu(string label, bool enabled) => Menu(ImGui.BeginMenu(label, enabled), label, enabled);

	public static void EndMenu()
	{
		ImGui.EndMenu();
		UiRegistry.PopMenu();
	}

	public static bool MenuItem(string label) => Item(ImGui.MenuItem(label), "menuItem", label);

	public static bool MenuItem(string label, bool enabled) => Item(ImGui.MenuItem(label, enabled), "menuItem", label, enabled);

	public static bool MenuItem(string label, string shortcut) => Item(ImGui.MenuItem(label, shortcut), "menuItem", label);

	public static bool MenuItem(string label, string shortcut, bool selected) => Item(ImGui.MenuItem(label, shortcut, selected), "menuItem", label);

	public static bool MenuItem(string label, string shortcut, bool selected, bool enabled) => Item(ImGui.MenuItem(label, shortcut, selected, enabled), "menuItem", label, enabled);

	public static bool MenuItem(string label, string shortcut, ref bool selected) => Item(ImGui.MenuItem(label, shortcut, ref selected), "menuItem", label);

	public static bool MenuItem(string label, string shortcut, ref bool selected, bool enabled) => Item(ImGui.MenuItem(label, shortcut, ref selected, enabled), "menuItem", label, enabled);

	public static bool Button(string label) => Item(ImGui.Button(label), "button", label);

	public static bool Button(string label, Vector2 size) => Item(ImGui.Button(label, size), "button", label);

	public static bool SmallButton(string label) => Item(ImGui.SmallButton(label), "button", label);

	public static bool InvisibleButton(string id, Vector2 size) => Item(ImGui.InvisibleButton(id, size), "button", id);

	public static bool InvisibleButton(string id, Vector2 size, ImGuiButtonFlags flags) => Item(ImGui.InvisibleButton(id, size, flags), "button", id);

	public static bool ArrowButton(string id, ImGuiDir dir) => Item(ImGui.ArrowButton(id, dir), "button", id);

	public static bool ImageButton(string id, IntPtr texture, Vector2 size) => Item(ImGui.ImageButton(id, texture, size), "button", id);

	public static bool ImageButton(string id, IntPtr texture, Vector2 size, Vector2 uv0) => Item(ImGui.ImageButton(id, texture, size, uv0), "button", id);

	public static bool ImageButton(string id, IntPtr texture, Vector2 size, Vector2 uv0, Vector2 uv1) => Item(ImGui.ImageButton(id, texture, size, uv0, uv1), "button", id);

	public static bool ImageButton(string id, IntPtr texture, Vector2 size, Vector2 uv0, Vector2 uv1, Vector4 bg) => Item(ImGui.ImageButton(id, texture, size, uv0, uv1, bg), "button", id);

	public static bool ImageButton(string id, IntPtr texture, Vector2 size, Vector2 uv0, Vector2 uv1, Vector4 bg, Vector4 tint) => Item(ImGui.ImageButton(id, texture, size, uv0, uv1, bg, tint), "button", id);

	public static bool Selectable(string label) => Item(ImGui.Selectable(label), "selectable", label);

	public static bool Selectable(string label, bool selected) => Item(ImGui.Selectable(label, selected), "selectable", label);

	public static bool Selectable(string label, bool selected, ImGuiSelectableFlags flags) => Item(ImGui.Selectable(label, selected, flags), "selectable", label);

	public static bool Selectable(string label, bool selected, ImGuiSelectableFlags flags, Vector2 size) => Item(ImGui.Selectable(label, selected, flags, size), "selectable", label);

	public static bool Selectable(string label, ref bool selected) => Item(ImGui.Selectable(label, ref selected), "selectable", label);

	public static bool Selectable(string label, ref bool selected, ImGuiSelectableFlags flags) => Item(ImGui.Selectable(label, ref selected, flags), "selectable", label);

	public static bool Selectable(string label, ref bool selected, ImGuiSelectableFlags flags, Vector2 size) => Item(ImGui.Selectable(label, ref selected, flags, size), "selectable", label);

	public static bool Checkbox(string label, ref bool value) => Item(ImGui.Checkbox(label, ref value), "checkbox", label);

	public static bool RadioButton(string label, bool active) => Item(ImGui.RadioButton(label, active), "radio", label);

	public static bool RadioButton(string label, ref int value, int button) => Item(ImGui.RadioButton(label, ref value, button), "radio", label);

	public static bool TreeNode(string label) => Item(ImGui.TreeNode(label), "tree", label);

	public static bool TreeNode(string id, string text) => Item(ImGui.TreeNode(id, text), "tree", text);

	public static bool TreeNodeEx(string label) => Item(ImGui.TreeNodeEx(label), "tree", label);

	public static bool TreeNodeEx(string label, ImGuiTreeNodeFlags flags) => Item(ImGui.TreeNodeEx(label, flags), "tree", label);

	public static bool TreeNodeEx(string id, ImGuiTreeNodeFlags flags, string text) => Item(ImGui.TreeNodeEx(id, flags, text), "tree", text);

	public static bool CollapsingHeader(string label) => Item(ImGui.CollapsingHeader(label), "header", label);

	public static bool CollapsingHeader(string label, ImGuiTreeNodeFlags flags) => Item(ImGui.CollapsingHeader(label, flags), "header", label);

	public static bool CollapsingHeader(string label, ref bool visible) => Item(ImGui.CollapsingHeader(label, ref visible), "header", label);

	public static bool CollapsingHeader(string label, ref bool visible, ImGuiTreeNodeFlags flags) => Item(ImGui.CollapsingHeader(label, ref visible, flags), "header", label);

	public static bool BeginTabItem(string label) => Item(ImGui.BeginTabItem(label), "tab", label);

	public static bool BeginTabItem(string label, ref bool open) => Item(ImGui.BeginTabItem(label, ref open), "tab", label);

	public static bool BeginTabItem(string label, ref bool open, ImGuiTabItemFlags flags) => Item(ImGui.BeginTabItem(label, ref open, flags), "tab", label);

	public static bool InputText(string label, ref string input, uint maxLength) => Item(ImGui.InputText(label, ref input, maxLength), "input", label);

	public static bool InputText(string label, ref string input, uint maxLength, ImGuiInputTextFlags flags) => Item(ImGui.InputText(label, ref input, maxLength, flags), "input", label);

	public static bool InputText(string label, ref string input, uint maxLength, ImGuiInputTextFlags flags, ImGuiInputTextCallback callback) => Item(ImGui.InputText(label, ref input, maxLength, flags, callback), "input", label);

	public static bool InputText(string label, ref string input, uint maxLength, ImGuiInputTextFlags flags, ImGuiInputTextCallback callback, IntPtr userData) => Item(ImGui.InputText(label, ref input, maxLength, flags, callback, userData), "input", label);

	public static bool InputTextWithHint(string label, string hint, ref string input, uint maxLength) => Item(ImGui.InputTextWithHint(label, hint, ref input, maxLength), "input", label);

	public static bool InputTextWithHint(string label, string hint, ref string input, uint maxLength, ImGuiInputTextFlags flags) => Item(ImGui.InputTextWithHint(label, hint, ref input, maxLength, flags), "input", label);

	public static bool InputTextMultiline(string label, ref string input, uint maxLength, Vector2 size) => Item(ImGui.InputTextMultiline(label, ref input, maxLength, size), "input", label);

	public static bool InputTextMultiline(string label, ref string input, uint maxLength, Vector2 size, ImGuiInputTextFlags flags) => Item(ImGui.InputTextMultiline(label, ref input, maxLength, size, flags), "input", label);

	public static bool InputInt(string label, ref int value) => Item(ImGui.InputInt(label, ref value), "input", label);

	public static bool InputInt(string label, ref int value, int step) => Item(ImGui.InputInt(label, ref value, step), "input", label);

	public static bool InputInt(string label, ref int value, int step, int stepFast) => Item(ImGui.InputInt(label, ref value, step, stepFast), "input", label);

	public static bool InputInt(string label, ref int value, int step, int stepFast, ImGuiInputTextFlags flags) => Item(ImGui.InputInt(label, ref value, step, stepFast, flags), "input", label);

	public static bool InputFloat(string label, ref float value) => Item(ImGui.InputFloat(label, ref value), "input", label);

	public static bool InputFloat(string label, ref float value, float step) => Item(ImGui.InputFloat(label, ref value, step), "input", label);

	public static bool InputFloat(string label, ref float value, float step, float stepFast) => Item(ImGui.InputFloat(label, ref value, step, stepFast), "input", label);

	public static bool InputFloat(string label, ref float value, float step, float stepFast, string format) => Item(ImGui.InputFloat(label, ref value, step, stepFast, format), "input", label);

	public static bool InputFloat(string label, ref float value, float step, float stepFast, string format, ImGuiInputTextFlags flags) => Item(ImGui.InputFloat(label, ref value, step, stepFast, format, flags), "input", label);

	public static bool InputFloat2(string label, ref Vector2 value) => Item(ImGui.InputFloat2(label, ref value), "input", label);

	public static bool InputFloat2(string label, ref Vector2 value, string format) => Item(ImGui.InputFloat2(label, ref value, format), "input", label);

	public static bool InputFloat2(string label, ref Vector2 value, string format, ImGuiInputTextFlags flags) => Item(ImGui.InputFloat2(label, ref value, format, flags), "input", label);

	public static bool InputFloat3(string label, ref Vector3 value) => Item(ImGui.InputFloat3(label, ref value), "input", label);

	public static bool InputFloat3(string label, ref Vector3 value, string format) => Item(ImGui.InputFloat3(label, ref value, format), "input", label);

	public static bool DragFloat(string label, ref float value) => Item(ImGui.DragFloat(label, ref value), "drag", label);

	public static bool DragFloat(string label, ref float value, float speed) => Item(ImGui.DragFloat(label, ref value, speed), "drag", label);

	public static bool DragFloat(string label, ref float value, float speed, float min) => Item(ImGui.DragFloat(label, ref value, speed, min), "drag", label);

	public static bool DragFloat(string label, ref float value, float speed, float min, float max) => Item(ImGui.DragFloat(label, ref value, speed, min, max), "drag", label);

	public static bool DragFloat(string label, ref float value, float speed, float min, float max, string format) => Item(ImGui.DragFloat(label, ref value, speed, min, max, format), "drag", label);

	public static bool DragFloat(string label, ref float value, float speed, float min, float max, string format, ImGuiSliderFlags flags) => Item(ImGui.DragFloat(label, ref value, speed, min, max, format, flags), "drag", label);

	public static bool DragFloat2(string label, ref Vector2 value) => Item(ImGui.DragFloat2(label, ref value), "drag", label);

	public static bool DragFloat2(string label, ref Vector2 value, float speed) => Item(ImGui.DragFloat2(label, ref value, speed), "drag", label);

	public static bool DragFloat2(string label, ref Vector2 value, float speed, float min) => Item(ImGui.DragFloat2(label, ref value, speed, min), "drag", label);

	public static bool DragFloat2(string label, ref Vector2 value, float speed, float min, float max) => Item(ImGui.DragFloat2(label, ref value, speed, min, max), "drag", label);

	public static bool DragFloat2(string label, ref Vector2 value, float speed, float min, float max, string format) => Item(ImGui.DragFloat2(label, ref value, speed, min, max, format), "drag", label);

	public static bool DragFloat3(string label, ref Vector3 value) => Item(ImGui.DragFloat3(label, ref value), "drag", label);

	public static bool DragFloat3(string label, ref Vector3 value, float speed) => Item(ImGui.DragFloat3(label, ref value, speed), "drag", label);

	public static bool DragFloat3(string label, ref Vector3 value, float speed, float min, float max) => Item(ImGui.DragFloat3(label, ref value, speed, min, max), "drag", label);

	public static bool DragFloat3(string label, ref Vector3 value, float speed, float min, float max, string format) => Item(ImGui.DragFloat3(label, ref value, speed, min, max, format), "drag", label);

	public static bool DragInt(string label, ref int value) => Item(ImGui.DragInt(label, ref value), "drag", label);

	public static bool DragInt(string label, ref int value, float speed) => Item(ImGui.DragInt(label, ref value, speed), "drag", label);

	public static bool DragInt(string label, ref int value, float speed, int min) => Item(ImGui.DragInt(label, ref value, speed, min), "drag", label);

	public static bool DragInt(string label, ref int value, float speed, int min, int max) => Item(ImGui.DragInt(label, ref value, speed, min, max), "drag", label);

	public static bool DragInt(string label, ref int value, float speed, int min, int max, string format) => Item(ImGui.DragInt(label, ref value, speed, min, max, format), "drag", label);

	public static bool SliderFloat(string label, ref float value, float min, float max) => Item(ImGui.SliderFloat(label, ref value, min, max), "slider", label);

	public static bool SliderFloat(string label, ref float value, float min, float max, string format) => Item(ImGui.SliderFloat(label, ref value, min, max, format), "slider", label);

	public static bool SliderFloat(string label, ref float value, float min, float max, string format, ImGuiSliderFlags flags) => Item(ImGui.SliderFloat(label, ref value, min, max, format, flags), "slider", label);

	public static bool SliderFloat2(string label, ref Vector2 value, float min, float max) => Item(ImGui.SliderFloat2(label, ref value, min, max), "slider", label);

	public static bool SliderFloat2(string label, ref Vector2 value, float min, float max, string format) => Item(ImGui.SliderFloat2(label, ref value, min, max, format), "slider", label);

	public static bool SliderInt(string label, ref int value, int min, int max) => Item(ImGui.SliderInt(label, ref value, min, max), "slider", label);

	public static bool SliderInt(string label, ref int value, int min, int max, string format) => Item(ImGui.SliderInt(label, ref value, min, max, format), "slider", label);

	public static bool Combo(string label, ref int current, string[] items, int count) => Item(ImGui.Combo(label, ref current, items, count), "combo", label);

	public static bool Combo(string label, ref int current, string[] items, int count, int popupMaxHeight) => Item(ImGui.Combo(label, ref current, items, count, popupMaxHeight), "combo", label);

	public static bool Combo(string label, ref int current, string itemsSeparatedByZeros) => Item(ImGui.Combo(label, ref current, itemsSeparatedByZeros), "combo", label);

	public static bool BeginCombo(string label, string preview) => Item(ImGui.BeginCombo(label, preview), "combo", label);

	public static bool BeginCombo(string label, string preview, ImGuiComboFlags flags) => Item(ImGui.BeginCombo(label, preview, flags), "combo", label);

	public static bool ColorEdit3(string label, ref Vector3 color) => Item(ImGui.ColorEdit3(label, ref color), "color", label);

	public static bool ColorEdit3(string label, ref Vector3 color, ImGuiColorEditFlags flags) => Item(ImGui.ColorEdit3(label, ref color, flags), "color", label);

	public static bool ColorEdit4(string label, ref Vector4 color) => Item(ImGui.ColorEdit4(label, ref color), "color", label);

	public static bool ColorEdit4(string label, ref Vector4 color, ImGuiColorEditFlags flags) => Item(ImGui.ColorEdit4(label, ref color, flags), "color", label);

	private static bool Item(bool result, string kind, string label, bool enabled = true)
	{
		UiRegistry.Record(kind, label, enabled);
		return result;
	}

	/// <summary>Begin pushes even when it returns false because End is still owed.</summary>
	private static bool Window(bool open, string name)
	{
		UiRegistry.PushWindow("window", name, open);
		return open;
	}

	/// <summary>Popups push only when open because EndPopup is only owed then.</summary>
	private static bool Popup(bool open, string kind, string name)
	{
		if (open)
			UiRegistry.PushWindow(kind, name, true);
		return open;
	}

	private static bool Menu(bool open, string label, bool enabled)
	{
		UiRegistry.Record("menu", label, enabled);
		if (open)
			UiRegistry.PushMenu(label);
		return open;
	}
}
