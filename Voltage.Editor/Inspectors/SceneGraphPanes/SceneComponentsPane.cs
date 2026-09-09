using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ImGuiNET;
using Voltage.Editor.ImGuiCore;
using Voltage.Utils;
using Voltage.Editor.Undo.Core;
using Voltage.Editor.Undo.SceneComponentActions;
using Voltage.Editor.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.Inspectors.SceneGraphPanes;

/// <summary>
/// Draws the "Scene Components" section inside the Scene Graph window.
/// Provides a collapsible list of all scene-scoped <see cref="SceneComponent"/> instances,
/// an "Add Scene Component" button, and per-component inspector headers (with remove via context menu).
/// </summary>
public class SceneComponentsPane
{
	public SceneComponent SelectedSceneComponent { get; private set; }

	private bool _showAddPopup;
	private string _filterText = "";
	private static List<Type> _cachedSceneComponentTypes;

	private ImGuiManager _imGuiManager;

	/// <summary>
	/// Draws the full Scene Components panel section: the collapsible list header,
	/// item rows, and the "Add Scene Component" button.
	/// Must be called inside an active ImGui window.
	/// </summary>
	public void Draw()
	{
		if (_imGuiManager == null)
			_imGuiManager = Core.GetGlobalManager<ImGuiManager>();

		if (Core.Scene == null)
			return;

		DrawSceneComponentList();

		VoltageEditorUtils.SmallVerticalSpace();

		if (VoltageEditorUtils.CenteredButton("Add Scene Component", 0.75f))
		{
			_showAddPopup = true;
			_filterText = "";
		}

		DrawAddSceneComponentPopup();
	}

	/// <summary>
	/// Clears selection state and type cache. Call when the scene changes.
	/// </summary>
	public void OnSceneChanged()
	{
		SelectedSceneComponent = null;
		InvalidateSceneComponentTypeCache();
	}

	private void DrawSceneComponentList()
	{
		if (Core.Scene._sceneComponents.Length == 0)
		{
			ImGui.TextDisabled("No Scene Components");
			return;
		}

		for (var i = 0; i < Core.Scene._sceneComponents.Length; i++)
		{
			var sc = Core.Scene._sceneComponents.Buffer[i];

			ImGui.PushID(i);

			bool isSelected = sc == SelectedSceneComponent;

			// Non-serialized components are shown in green to indicate they are runtime-only
			bool isNonSerialized = !sc.IsSerialized;
			if (isNonSerialized)
				ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(0.2f, 1.0f, 0.2f, 1.0f));

			// Flat selectable row — no inline inspector, click opens the dedicated window
			if (Gui.Selectable(sc.Name ?? sc.GetType().Name, isSelected))
			{
				SelectedSceneComponent = sc;
				_imGuiManager.OpenSceneComponentInspector(sc);
			}

			if (isNonSerialized)
				ImGui.PopStyleColor();

			// Tooltip for runtime-only components
			if (isNonSerialized && ImGui.IsItemHovered())
			{
				ImGui.BeginTooltip();
				ImGui.TextColored(new Num.Vector4(0.5f, 1f, 0.5f, 1f), "Runtime Only");
				ImGui.Text("Added via code — not saved to the .vscene file.");
				ImGui.EndTooltip();
			}

			// Right-click context menu
			if (Gui.BeginPopupContextItem("sc_ctx"))
			{
				if (sc.IsSerialized && Gui.Selectable($"Remove {sc.GetType().Name}"))
				{
					EditorChangeTracker.PushUndo(
						new SceneComponentRemovedUndoAction(Core.Scene, sc,
							$"Remove SceneComponent {sc.GetType().Name}"),
						Core.Scene,
						$"Remove SceneComponent {sc.GetType().Name}"
					);
					Core.Scene.RemoveSceneComponent(sc);
					if (SelectedSceneComponent == sc)
						SelectedSceneComponent = null;
					Gui.EndPopup();
					ImGui.PopID();
					return; // list just changed — stop drawing this frame
				}

				Gui.EndPopup();
			}

			ImGui.PopID();
		}
	}

	private void DrawAddSceneComponentPopup()
	{
		if (_showAddPopup)
		{
			ImGui.OpenPopup("add-scene-component-popup");
			_showAddPopup = false;
		}

		var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.4f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
		ImGui.SetNextWindowSize(new Num.Vector2(400, 500), ImGuiCond.Appearing);

		bool open = true;
		if (Gui.BeginPopupModal("add-scene-component-popup", ref open, ImGuiWindowFlags.NoResize))
		{
			ImGui.Text("Add Scene Component");
			ImGui.Separator();
			ImGui.Text("Search:");
			Gui.InputText("##SCFilter", ref _filterText, 50);

			VoltageEditorUtils.SmallVerticalSpace();

			var types = GetFilteredSceneComponentTypes();
			ImGuiSafe.TextSafe($"Available ({types.Count}):");
			ImGui.Separator();

			if (ImGui.BeginChild("SCList", new Num.Vector2(0, 350), true))
			{
				foreach (var type in types)
				{
					if (Gui.Selectable(type.Name))
					{
						AddSceneComponentToScene(type);
						ImGui.CloseCurrentPopup();
					}

					if (ImGui.IsItemHovered())
						ImGuiSafe.SetTooltipSafe(type.FullName);

					ImGui.SameLine();
					ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(0.6f, 0.6f, 0.6f, 1f));
					ImGuiSafe.TextSafe($"({type.Namespace})");
					ImGui.PopStyleColor();
				}
			}
			ImGui.EndChild();

			VoltageEditorUtils.SmallVerticalSpace();

			var bw = 80f;
			ImGui.SetCursorPosX((ImGui.GetWindowSize().X - bw) * 0.5f);
			if (Gui.Button("Cancel", new Num.Vector2(bw, 0)))
			{
				_filterText = "";
				ImGui.CloseCurrentPopup();
			}

			Gui.EndPopup();
		}
	}

	private void AddSceneComponentToScene(Type sceneComponentType)
	{
		if (Core.Scene == null || sceneComponentType == null)
			return;

		try
		{
			// Check for existing instance of the same type (scene components are typically singletons per scene)
			for (var i = 0; i < Core.Scene._sceneComponents.Length; i++)
			{
				if (Core.Scene._sceneComponents.Buffer[i].GetType() == sceneComponentType)
				{
					NotificationSystem.ShowTimedNotification(
						$"Scene already has a '{sceneComponentType.Name}' component!");
					return;
				}
			}

			var instance = (SceneComponent)Activator.CreateInstance(sceneComponentType);
			instance.SetSerialized(true);
			Core.Scene.AddSceneComponent(instance);

			EditorChangeTracker.PushUndo(
				new SceneComponentAddedUndoAction(Core.Scene, instance),
				Core.Scene,
				$"Add SceneComponent {sceneComponentType.Name}"
			);

			SelectedSceneComponent = instance;
			_imGuiManager?.OpenSceneComponentInspector(instance);

			Debug.Log($"[Editor] Added SceneComponent '{sceneComponentType.Name}' to scene.");
		}
		catch (Exception ex)
		{
			Debug.Error($"[Editor] Failed to add SceneComponent '{sceneComponentType.Name}': {ex.Message}");
		}
	}

	public static void InvalidateSceneComponentTypeCache()
	{
		_cachedSceneComponentTypes = null;
	}

	private List<Type> GetFilteredSceneComponentTypes()
	{
		if (_cachedSceneComponentTypes == null)
			CacheSceneComponentTypes();

		if (string.IsNullOrWhiteSpace(_filterText))
			return _cachedSceneComponentTypes;

		return _cachedSceneComponentTypes
			.Where(t => t.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase)
					 || (t.FullName?.Contains(_filterText, StringComparison.OrdinalIgnoreCase) ?? false))
			.ToList();
	}

	private static void CacheSceneComponentTypes()
	{
		var typesByFullName = new Dictionary<string, Type>();

		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			var name = assembly.GetName().Name;
			if (name != null && name.StartsWith("DynamicScripts") && assembly != Core.LatestScriptAssembly)
				continue;

			try
			{
				var types = assembly.GetTypes()
					.Where(t => typeof(SceneComponent).IsAssignableFrom(t)
					         && !t.IsAbstract
					         && !t.IsInterface
					         && HasPublicParameterlessCtor(t));

				foreach (var t in types)
					typesByFullName[t.FullName ?? t.Name] = t;
			}
			catch (ReflectionTypeLoadException ex)
			{
				Debug.Warn($"[SceneComponentsPane] Failed to load types from {assembly.FullName}: {ex.Message}");
			}
		}

		_cachedSceneComponentTypes = typesByFullName.Values.OrderBy(t => t.Name).ToList();
	}

	private static bool HasPublicParameterlessCtor(Type t)
	{
		return t.GetConstructor(
			BindingFlags.Public | BindingFlags.Instance,
			null, Type.EmptyTypes, null) != null;
	}
}
