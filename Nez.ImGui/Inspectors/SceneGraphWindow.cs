using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using Microsoft.Xna.Framework.Input;
using Nez.ECS;
using Nez.ImGuiTools.SceneGraphPanes;
using Nez.Utils;
using Num = System.Numerics;

namespace Nez.ImGuiTools;

public class SceneGraphWindow
{
	/// <summary>
	/// A copy of a component that can be pasted to another entity
	/// </summary>
	public Component CopiedComponent { get; set; }
	public EntityPane EntityPane => _entityPane;
	private PostProcessorsPane _postProcessorsPane = new();
	private RenderersPane _renderersPane = new();
	private EntityPane _entityPane = new();
	private ImGuiManager _imGuiManager;
	public float SceneGraphWidth => _sceneGraphWidth;
	public float SceneGraphPosY { get; private set; }
	public bool IsOpen { get; private set; }

	private string _entityFilterName;

	private float _sceneGraphWidth = 420f;
	private readonly float _minSceneGraphWidth = 1f;
	private readonly float _maxSceneGraphWidth = Screen.MonitorWidth;

	// Key Hold duration params
	private float _upKeyHoldTime = 0f;
	private float _downKeyHoldTime = 0f;
	private double _lastRepeatTime = 0f;
	private const float RepeatDelay = 0.3f; // seconds before repeat starts
	private const float RepeatRate = 0.08f; // seconds between repeats
	public HashSet<Entity> ExpandedEntities = new();

	// TiledMap (tmx) File Picker
	private bool _showTmxFilePicker = false;
	private string _selectedTmxFile = null;
	public static event Action<string> OnTmxFileSelected;

	#region Event Handlers

	public event Action OnSaveSceneChanges;
	public event Action OnResetScene;
	public event Action<bool> OnSwitchEditMode;

	public void InvokeSaveSceneChanges()
	{
		OnSaveSceneChanges?.Invoke();
	}

	public void InvokeResetScene()
	{
		OnResetScene?.Invoke();
	}

	public void InvokeSwitchEditMode(bool isEditMode)
	{
		OnSwitchEditMode?.Invoke(isEditMode);
	}

	#endregion

	public void OnSceneChanged()
	{
		_postProcessorsPane.OnSceneChanged();
		_renderersPane.OnSceneChanged();
	}

	public void Show(ref bool isOpen)
	{
		IsOpen = isOpen;

		if (Nez.Core.Scene == null || !isOpen)
			return;

		if (_imGuiManager == null)
			_imGuiManager = Nez.Core.GetGlobalManager<ImGuiManager>();

		var topMargin = 20f * ImGui.GetIO().FontGlobalScale;
		var rightMargin = 10f;
		var leftMargin = 0f;
		var windowHeight = Screen.Height - topMargin;

		// Calculate left edge so right edge is always at Screen.Width - rightMargin
		SceneGraphPosY = topMargin;

		ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, 0.0f); // makes grip almost invisible
		ImGui.PushStyleColor(ImGuiCol.ResizeGrip, new Num.Vector4(0, 0, 0, 0)); // transparent grip

		ImGui.SetNextWindowPos(new Num.Vector2(0, SceneGraphPosY), ImGuiCond.Always);
		ImGui.SetNextWindowSize(new Num.Vector2(_sceneGraphWidth, windowHeight), ImGuiCond.FirstUseEver);

		var windowFlags = ImGuiWindowFlags.NoMove;

		if (ImGui.Begin("Scene Graph", ref isOpen, windowFlags))
		{
			// Update width after user resizes
			var currentWidth = ImGui.GetWindowSize().X;
			if (Math.Abs(currentWidth - _sceneGraphWidth) > 0.01f)
				_sceneGraphWidth = Math.Clamp(currentWidth, _minSceneGraphWidth, _maxSceneGraphWidth);

			NezImGui.SmallVerticalSpace();
			if (Nez.Core.IsEditMode)
			{
				ImGui.TextWrapped("Press F1/F2 to switch to Play mode.");
				NezImGui.SmallVerticalSpace();

				if (NezImGui.CenteredButton("Edit Mode", 0.8f))
					InvokeSwitchEditMode(Nez.Core.IsEditMode = false);

				NezImGui.SmallVerticalSpace();
				if (NezImGui.CenteredButton("Reset Scene", 0.8f))
					InvokeResetScene();
			}
			else
			{
				ImGui.TextWrapped("Press F1/F2 to switch to Edit mode.");
				NezImGui.SmallVerticalSpace();

				if (NezImGui.CenteredButton("Play Mode", 0.8f))
					InvokeSwitchEditMode(Nez.Core.IsEditMode = true);
			}

			NezImGui.MediumVerticalSpace();
			if (ImGui.CollapsingHeader("Post Processors"))
				_postProcessorsPane.Draw();

			if (ImGui.CollapsingHeader("Renderers"))
				_renderersPane.Draw();

			if (ImGui.CollapsingHeader("Entities (double-click label to inspect)", ImGuiTreeNodeFlags.DefaultOpen))
				_entityPane.Draw();

			NezImGui.MediumVerticalSpace();
			if (NezImGui.CenteredButton("Save Scene", 0.7f))
				InvokeSaveSceneChanges();

			NezImGui.MediumVerticalSpace();
			if (NezImGui.CenteredButton("Add Entity", 0.6f))
			{
				_entityFilterName = "";
				ImGui.OpenPopup("entity-selector");
			}

			NezImGui.MediumVerticalSpace();
			if (NezImGui.CenteredButton("Load Tiled Map", 0.6f))
			{
				_showTmxFilePicker = true;
			}

			if (_showTmxFilePicker)
			{
				ImGui.OpenPopup("tmx-file-picker");
				_showTmxFilePicker = false;
			}

			// Show Copied Component
			NezImGui.MediumVerticalSpace();
			if (CopiedComponent != null)
			{
				NezImGui.VeryBigVerticalSpace();
				ImGui.TextWrapped($"Component Copied: {CopiedComponent.GetType().Name}");

				NezImGui.SmallVerticalSpace();
				if (NezImGui.CenteredButton("Clear Copied Component", 0.8f))
					CopiedComponent = null;
			}

			DrawTmxFilePickerPopup();
			DrawEntitySelectorPopup();

			ImGui.End();
			ImGui.PopStyleVar();
			ImGui.PopStyleColor();
		}

		if (Input.IsKeyDown(Keys.LeftControl) && Input.IsKeyPressed(Keys.S))
			InvokeSaveSceneChanges();

		HandleEntitySelectionNavigation();
	}


	private void DrawEntitySelectorPopup()
	{
		if (ImGui.BeginPopup("entity-selector"))
		{
			ImGui.InputText("###EntityFilter", ref _entityFilterName, 25);
			ImGui.Separator();

			foreach (var typeName in EntityFactoryRegistry.GetRegisteredTypes())
				if (string.IsNullOrEmpty(_entityFilterName) ||
				    typeName.ToLower().Contains(_entityFilterName.ToLower()))
					if (ImGui.Selectable(typeName))
					{
						// Generate a unique name for the new entity
						var uniqueName = Nez.Core.Scene.GetUniqueEntityName(typeName);

						// Use the factory registry to create the entity
						if (EntityFactoryRegistry.TryCreate(typeName, out var entity))
						{
							entity.Type = Entity.InstanceType.Dynamic;
							entity.Name = uniqueName;
							entity.Transform.Position = Nez.Core.Scene.Camera.Transform.Position;
							EntityFactoryRegistry.InvokeEntityCreated(entity);
						}

						ImGui.CloseCurrentPopup();
					}

			ImGui.EndPopup();
		}
	}

	private void HandleEntitySelectionNavigation()
	{
		if (!Core.IsEditMode)
			return; 

		var hierarchyList = BuildHierarchyList();
		var currentEntity = _imGuiManager?.MainEntityInspector?.Entity;
		if (currentEntity == null || hierarchyList.Count == 0)
			return;

		bool upPressed = ImGui.IsKeyPressed(ImGuiKey.UpArrow);
		bool downPressed = ImGui.IsKeyPressed(ImGuiKey.DownArrow);
		bool upHeld = ImGui.IsKeyDown(ImGuiKey.UpArrow);
		bool downHeld = ImGui.IsKeyDown(ImGuiKey.DownArrow);

		double now = ImGui.GetTime();

		// Up key logic
		if (upPressed)
		{
			_upKeyHoldTime = (float)now;
			var next = NavigateUp(currentEntity, hierarchyList);
			if (next != null)
			{
				_imGuiManager.OpenMainEntityInspector(next);
				_entityPane.SelectedEntity = next;
				ExpandParentsAndChildren(next);
				_imGuiManager.SetCameraTargetPosition(next.Transform.Position);
			}
			_lastRepeatTime = now;
		}
		else if (upHeld)
		{
			if (now - _upKeyHoldTime > RepeatDelay && now - _lastRepeatTime > RepeatRate)
			{
				var next = NavigateUp(currentEntity, hierarchyList);
				if (next != null)
				{
					_imGuiManager.OpenMainEntityInspector(next);
					_entityPane.SelectedEntity = next;
					ExpandParentsAndChildren(next);
					_imGuiManager.SetCameraTargetPosition(next.Transform.Position);
				}
				_lastRepeatTime = now;
			}
		}
		else if (!upHeld)
		{
			_upKeyHoldTime = 0f;
		}

		// Down key logic
		if (downPressed)
		{
			_downKeyHoldTime = (float)now;
			var next = NavigateDown(currentEntity, hierarchyList);
			if (next != null)
			{
				_imGuiManager.OpenMainEntityInspector(next);
				_entityPane.SelectedEntity = next;
				ExpandParentsAndChildren(next);
				_imGuiManager.SetCameraTargetPosition(next.Transform.Position); 
			}
			_lastRepeatTime = now;
		}
		else if (downHeld)
		{
			if (now - _downKeyHoldTime > RepeatDelay && now - _lastRepeatTime > RepeatRate)
			{
				var next = NavigateDown(currentEntity, hierarchyList);
				if (next != null)
				{
					_imGuiManager.OpenMainEntityInspector(next);
					_entityPane.SelectedEntity = next;
					ExpandParentsAndChildren(next);
					_imGuiManager.SetCameraTargetPosition(next.Transform.Position);
				}
				_lastRepeatTime = now;
			}
		}
		else if (!downHeld)
		{
			_downKeyHoldTime = 0f;
		}
	}

	private List<Entity> BuildHierarchyList()
	{
		var result = new List<Entity>();
		var entities = Nez.Core.Scene?.Entities;
		if (entities == null) return result;

		for (int i = 0; i < entities.Count; i++)
		{
			var entity = entities[i];
			if (entity.Transform.Parent == null)
				AddEntityAndChildren(entity, result);
		}
		return result;
	}

	private void AddEntityAndChildren(Entity entity, List<Entity> result)
	{
		result.Add(entity);
		for (int i = 0; i < entity.Transform.ChildCount; i++)
		{
			AddEntityAndChildren(entity.Transform.GetChild(i).Entity, result);
		}
	}

	private Entity GetLastDescendant(Entity entity)
	{
		while (entity.Transform.ChildCount > 0)
			entity = entity.Transform.GetChild(entity.Transform.ChildCount - 1).Entity;
		return entity;
	}

	private Entity NavigateUp(Entity current, List<Entity> hierarchyList)
	{
		int idx = hierarchyList.IndexOf(current);
		if (idx <= 0)
			return null; // Already at top

		Entity prev = hierarchyList[idx - 1];


		// If current is the first child of its parent, and prev is that parent, just select the parent
		if (current.Transform.Parent != null && prev == current.Transform.Parent.Entity)
			return prev;

		// Otherwise, if prev has children, descend into its last descendant
		if (prev.Transform.ChildCount > 0)
			return GetLastDescendant(prev);

		return prev;
	}

	private Entity NavigateDown(Entity current, List<Entity> hierarchyList)
	{
		int idx = hierarchyList.IndexOf(current);
		if (idx < 0 || idx >= hierarchyList.Count - 1)
			return null; // Already at bottom
		return hierarchyList[idx + 1];
	}

	private void ExpandParentsAndChildren(Entity entity)
	{
		// Expand all parents up to the root
		var parent = entity.Transform.Parent;
		while (parent != null)
		{
			ExpandedEntities.Add(parent.Entity);
			parent = parent.Parent;
		}

		// Expand all children (non-recursive using a stack)
		var stack = new Stack<Entity>();
		stack.Push(entity);

		while (stack.Count > 0)
		{
			var current = stack.Pop();
			ExpandedEntities.Add(current);

			for (int i = 0; i < current.Transform.ChildCount; i++)
			{
				var child = current.Transform.GetChild(i).Entity;
				if (!ExpandedEntities.Contains(child))
					stack.Push(child);
			}
		}
	}

	private void DrawTmxFilePickerPopup()
	{
		bool isOpen = true;
		if (ImGui.BeginPopupModal("tmx-file-picker", ref isOpen, ImGuiWindowFlags.AlwaysAutoResize))
		{
			var picker = FilePicker.GetFilePicker(this, Path.Combine(Environment.CurrentDirectory, "Content"), ".tmx");
			picker.DontAllowTraverselBeyondRootFolder = true;
			if (picker.Draw())
			{
				var file = picker.SelectedFile;
				if (file.EndsWith(".tmx"))
				{
					string fullPath = file;
					string contentRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Content"));

					if (fullPath.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase))
					{
						string relativePath = "Content" + fullPath.Substring(contentRoot.Length).Replace('\\', '/');
						HandleTmxFileSelected(relativePath);
						ImGui.CloseCurrentPopup();
					}
					else
					{
						ImGui.Text("Selected file is not inside Content folder!");
					}
				}
				else
				{
					ImGui.Text("Selected file is not a valid TMX file.");
				}
				FilePicker.RemoveFilePicker(this);
			}
			ImGui.EndPopup();
		}
	}

	private void HandleTmxFileSelected(string relativePath)
	{
		_selectedTmxFile = relativePath;
		OnTmxFileSelected?.Invoke(relativePath);
	}
}