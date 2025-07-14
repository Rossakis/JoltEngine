using System;
using ImGuiNET;
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

	#region Event Handlers

	public event Action OnSaveEntityChanges;
	public event Action OnSaveSceneChanges;
	public event Action OnSaveAllChanges;
	public event Action OnResetScene;
	public event Action<bool> OnSwitchEditMode;

	public void InvokeSaveEntityChanges()
	{
		OnSaveEntityChanges?.Invoke();
	}

	public void InvokeSaveSceneChanges()
	{
		OnSaveSceneChanges?.Invoke();
	}

	public void InvokeSaveAllChanges()
	{
		OnSaveAllChanges?.Invoke();
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

		if (Core.Scene == null || !isOpen)
			return;

		if (_imGuiManager == null)
			_imGuiManager = Core.GetGlobalManager<ImGuiManager>();

		var topMargin = 20f;
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
			if (Core.IsEditMode)
			{
				ImGui.TextWrapped("Press F1/F2 to switch to Play mode.");
				NezImGui.SmallVerticalSpace();

				if (NezImGui.CenteredButton("Edit Mode", 0.8f))
					InvokeSwitchEditMode(Core.IsEditMode = false);

				NezImGui.SmallVerticalSpace();
				if (NezImGui.CenteredButton("Reset Scene", 0.8f))
					InvokeResetScene();
			}
			else
			{
				ImGui.TextWrapped("Press F1/F2 to switch to Edit mode.");
				NezImGui.SmallVerticalSpace();

				if (NezImGui.CenteredButton("Play Mode", 0.8f))
					InvokeSwitchEditMode(Core.IsEditMode = true);
			}

			NezImGui.MediumVerticalSpace();
			if (ImGui.CollapsingHeader("Post Processors"))
				_postProcessorsPane.Draw();

			if (ImGui.CollapsingHeader("Renderers"))
				_renderersPane.Draw();

			if (ImGui.CollapsingHeader("Entities (double-click label to inspect)", ImGuiTreeNodeFlags.DefaultOpen))
				_entityPane.Draw();

			NezImGui.MediumVerticalSpace();
			if (NezImGui.CenteredButton("Save Changes", 0.7f))
				ImGui.OpenPopup("save-changes");

			NezImGui.MediumVerticalSpace();
			if (NezImGui.CenteredButton("Add Entity", 0.6f))
			{
				_entityFilterName = "";
				ImGui.OpenPopup("entity-selector");
			}

			DrawEntitySelectorPopup();

			// Show Copied Component
			NezImGui.MediumVerticalSpace();
			if (_imGuiManager.SceneGraphWindow.CopiedComponent != null)
			{
				NezImGui.VeryBigVerticalSpace();
				ImGui.TextWrapped($"Component Copied: {_imGuiManager.SceneGraphWindow.CopiedComponent.GetType().Name}");

				NezImGui.SmallVerticalSpace();
				if (NezImGui.CenteredButton("Clear Copied Component", 0.8f))
					_imGuiManager.SceneGraphWindow.CopiedComponent = null;
			}

			DrawSaveChangesPopup();

			ImGui.End();
			ImGui.PopStyleVar();
			ImGui.PopStyleColor();
		}
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
						var uniqueName = Core.Scene.GetUniqueEntityName(typeName);

						// Use the factory registry to create the entity
						if (EntityFactoryRegistry.TryCreate(typeName, out var entity))
						{
							entity.Type = Entity.InstanceType.Dynamic;
							entity.Name = uniqueName;
							entity.Transform.Position = Core.Scene.Camera.Transform.Position;
							EntityFactoryRegistry.InvokeEntityCreated(entity);
						}

						ImGui.CloseCurrentPopup();
					}

			ImGui.EndPopup();
		}
	}

	private void DrawSaveChangesPopup()
	{
		if (ImGui.BeginPopup("save-changes"))
		{
			ImGui.Text("Select SAVE mode");

			NezImGui.SmallVerticalSpace();
			if (NezImGui.CenteredButton("Save ENTITY", 1f))
			{
				InvokeSaveEntityChanges();
				ImGui.CloseCurrentPopup();
			}

			if (!Core.IsEditMode)
				return;

			NezImGui.SmallVerticalSpace();
			if (NezImGui.CenteredButton("Save SCENE", 1f))
			{
				InvokeSaveSceneChanges();
				ImGui.CloseCurrentPopup();
			}

			NezImGui.SmallVerticalSpace();
			if (NezImGui.CenteredButton("Save ALL", 1f))
			{
				InvokeSaveAllChanges();
				ImGui.CloseCurrentPopup();
			}
		}
	}

	public void SetWidth(float width)
	{
		_sceneGraphWidth = Math.Clamp(width, _minSceneGraphWidth, _maxSceneGraphWidth);
	}
}