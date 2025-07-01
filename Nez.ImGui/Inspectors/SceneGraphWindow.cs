using System;
using ImGuiNET;
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

	private string _entityFilterName;


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
		if (Core.Scene == null || !isOpen)
			return;

		if (_imGuiManager == null)
			_imGuiManager = Core.GetGlobalManager<ImGuiManager>();

		ImGui.SetNextWindowPos(new Num.Vector2(0, 25), ImGuiCond.FirstUseEver);
		ImGui.SetNextWindowSize(new Num.Vector2(300, Screen.Height / 2), ImGuiCond.FirstUseEver);

		if (ImGui.Begin("Scene Graph", ref isOpen))
		{
			NezImGui.SmallVerticalSpace();
			if (Core.IsEditMode)
			{
				if (NezImGui.CenteredButton("Edit Mode", 0.8f))
					InvokeSwitchEditMode(Core.IsEditMode = false);

				NezImGui.SmallVerticalSpace();
				if (NezImGui.CenteredButton("Reset Scene", 0.8f))
					InvokeResetScene();
			}
			else
			{
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
		}
	}

	private string GetUniqueEntityName(string baseName)
	{
		var counter = 1;
		var uniqueName = baseName;
		while (Core.Scene.Entities.FindEntity(uniqueName) != null)
		{
			uniqueName = $"{baseName}{counter}";
			counter++;
		}

		return uniqueName;
	}

	private void DrawEntitySelectorPopup()
	{
		if (ImGui.BeginPopup("entity-selector"))
		{
			ImGui.InputText("###EntityFilter", ref _entityFilterName, 25);
			ImGui.Separator();

			var isNezType = false;
			foreach (var subclassType in InspectorCache.GetAllEntitySubclassTypes())
				if (string.IsNullOrEmpty(_entityFilterName) ||
				    subclassType.Name.ToLower().Contains(_entityFilterName.ToLower()))
				{
					// stick a separator in after custom Entities and before Nez Entities
					if (!isNezType && subclassType.Namespace != null && subclassType.Namespace.StartsWith("Nez"))
					{
						isNezType = true;
						ImGui.Separator();
					}

					if (ImGui.Selectable(subclassType.Name))
					{
						// Generate a unique name for the new entity
						var baseName = subclassType.Name;
						var uniqueName = GetUniqueEntityName(baseName);

						// Create an instance of the selected Entity subclass and set its name
						var entity = (Entity)Activator.CreateInstance(subclassType);
						entity.Name = uniqueName;
						entity.Transform.Position = Core.Scene.Camera.Transform.Position;
						Core.Scene.AddEntity(entity);
						ImGui.CloseCurrentPopup();
					}
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
}