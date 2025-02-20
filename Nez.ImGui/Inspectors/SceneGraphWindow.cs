using System;
using ImGuiNET;
using Nez.ImGuiTools.SceneGraphPanes;
using Num = System.Numerics;

namespace Nez.ImGuiTools;

public class SceneGraphWindow
{
	private PostProcessorsPane _postProcessorsPane = new();
	private RenderersPane _renderersPane = new();
	private EntityPane _entityPane = new();

	#region Event Handlers

	public event Action OnSaveEntityChanges;
	public event Action OnSaveSceneChanges;
	public event Action OnSaveAllChanges;
	public event Action OnCancelChanges;

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

	public void InvokeCancelChanges()
	{
		OnCancelChanges?.Invoke();
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

		ImGui.SetNextWindowPos(new Num.Vector2(0, 25), ImGuiCond.FirstUseEver);
		ImGui.SetNextWindowSize(new Num.Vector2(300, Screen.Height / 2), ImGuiCond.FirstUseEver);

		if (ImGui.Begin("Scene Graph", ref isOpen))
		{
			NezImGui.SmallVerticalSpace();
			if (Core.IsEditMode)
			{
				if (NezImGui.CenteredButton("Edit Mode", 0.8f)) Core.IsEditMode = false;
			}
			else
			{
				if (NezImGui.CenteredButton("Play Mode", 0.8f)) Core.IsEditMode = true;
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
			if (NezImGui.CenteredButton("Cancel Changes", 0.7f))
				ImGui.OpenPopup("cancel-changes");

			DrawSaveChangesPopup();
			DrawCancelChangesPopup();

			ImGui.End();
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

	private void DrawCancelChangesPopup()
	{
		if (ImGui.BeginPopup("cancel-changes"))
		{
			ImGui.Text("Are you sure you want to CANCEL ALL changes?");

			if (ImGui.Button("Yes"))
			{
				InvokeCancelChanges();
				ImGui.CloseCurrentPopup();
			}

			if (ImGui.Button("No"))
				ImGui.CloseCurrentPopup();
		}
	}
}