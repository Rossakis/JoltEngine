using ImGuiNET;
using System;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.Inspectors.ObjectInspectors;
using Voltage.Editor.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.Windows;

/// <summary>
/// A free-floating ImGui inspector window for a single <see cref="SceneComponent"/>.
/// Mirrors the lifecycle and structure of <see cref="EntityInspectorWindow"/> but
/// targets scene-scoped components instead of entities.
/// </summary>
public class SceneComponentInspectorWindow
{
	private static int _nextId = 0;
	private readonly int _id;

	public SceneComponent Component { get; private set; }
	public bool IsOpen { get; set; } = true;

	private SceneComponentInspector _inspector;
	private bool _shouldFocusWindow;

	public SceneComponentInspectorWindow(SceneComponent component)
	{
		_id = _nextId++;
		SetComponent(component);
	}

	/// <summary>
	/// Replaces the displayed component. Rebuilds the internal inspector.
	/// </summary>
	public void SetComponent(SceneComponent component)
	{
		Component = component;
		_inspector = component != null ? new SceneComponentInspector(component) : null;
	}

	/// <summary>
	/// Asks ImGui to bring this window to the front on the next frame.
	/// </summary>
	public void SetWindowFocus()
	{
		_shouldFocusWindow = true;
	}

	/// <summary>
	/// Draws the window. Call once per frame from the ImGui draw loop.
	/// </summary>
	public void Draw()
	{
		if (!IsOpen)
			return;

		var open = IsOpen;

		var title = Component != null
			? $"{Component.Name ?? Component.GetType().Name} (Scene Component)###SceneComponentInspector_{_id}"
			: $"Scene Component Inspector###SceneComponentInspector_{_id}";

		Gui.Begin(title, ref open);

		if (_shouldFocusWindow)
		{
			ImGui.SetWindowFocus();
			_shouldFocusWindow = false;
		}

		if (Component == null)
		{
			ImGui.TextColored(new Num.Vector4(1f, 1f, 0f, 1f), "No Scene Component selected.");
		}
		else
		{
			// Display the component type name as a header
			ImGui.SetWindowFontScale(1.5f);
			ImGuiSafe.TextSafe(Component.GetType().Name);
			ImGui.SetWindowFontScale(1.0f);

			VoltageEditorUtils.BigVerticalSpace();

			// Delegate all field drawing to the shared SceneComponentInspector
			_inspector?.Draw();
		}

		IsOpen = open;
		Gui.End();
	}
}
