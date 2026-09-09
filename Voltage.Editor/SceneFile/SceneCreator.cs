using System;
using System.IO;
using ImGuiNET;
using Voltage.Data;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Utils;
using Voltage.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.SceneFile
{
	/// <summary>
	/// Handles the creation of new scene files through an ImGui popup interface.
	/// </summary>
	public class SceneCreator
	{
		private string _sceneName = "";
		private bool _showCreateScenePopup = false;
		private string _sceneNameError = "";
		private bool _createAndLoad = true;
		
		/// <summary>
		/// Opens the scene creation popup.
		/// </summary>
		public void OpenCreateScenePopup()
		{
			_showCreateScenePopup = true;
			_sceneName = "";
			_sceneNameError = "";
		}
		
		/// <summary>
		/// Draws the scene creation popup UI.
		/// </summary>
		public void Draw()
		{
			if (_showCreateScenePopup)
			{
				ImGui.OpenPopup("create-scene-popup");
				_showCreateScenePopup = false;
			}
			
			var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.5f);
			ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
			ImGui.SetNextWindowSize(new Num.Vector2(400, 200), ImGuiCond.Appearing);
			
			bool open = true;
			if (Gui.BeginPopupModal("create-scene-popup", ref open, ImGuiWindowFlags.NoResize))
			{
				ImGui.TextColored(new Num.Vector4(0.2f, 0.8f, 1.0f, 1.0f), "Create New Scene");
				ImGui.Separator();
				
				VoltageEditorUtils.MediumVerticalSpace();
				
				DrawSceneInfo();
				VoltageEditorUtils.MediumVerticalSpace();
				
				DrawActionButtons();
				
				Gui.EndPopup();
			}
		}
		
		private void DrawSceneInfo()
		{
			ImGui.Text("Scene Name:");
			ImGui.SetNextItemWidth(-1);
			
			if (Gui.InputText("##SceneName", ref _sceneName, 100))
			{
				ValidateSceneName();
			}
			
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Name of the new scene file (without .scene extension)");
			}
			
			// Display error message if there's an error
			if (!string.IsNullOrWhiteSpace(_sceneNameError))
			{
				ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(1.0f, 0.3f, 0.3f, 1.0f));
				ImGuiSafe.TextWrappedSafe(_sceneNameError);
				ImGui.PopStyleColor();
			}
			
			VoltageEditorUtils.SmallVerticalSpace();
			
			Gui.Checkbox("Create and load scene", ref _createAndLoad);
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("If checked, the newly created scene will be loaded immediately");
			}
		}
		
		private void ValidateSceneName()
		{
			_sceneNameError = "";
			
			if (string.IsNullOrWhiteSpace(_sceneName))
			{
				return; // Don't show error for empty field while typing
			}
			
			var invalidChars = Path.GetInvalidFileNameChars();
			if (_sceneName.IndexOfAny(invalidChars) >= 0)
			{
				_sceneNameError = "Error: Scene name contains invalid characters.";
				return;
			}
			
			var sceneManager = SceneManager.Instance;
			if (sceneManager.SceneExists(_sceneName))
			{
				_sceneNameError = $"Error: A scene with the name '{_sceneName}' already exists.";
			}
		}
		
		private void DrawActionButtons()
		{
			ImGui.Separator();
			
			bool canCreate = !string.IsNullOrWhiteSpace(_sceneName) && 
			                 string.IsNullOrWhiteSpace(_sceneNameError);
			
			if (!canCreate)
			{
				ImGui.BeginDisabled();
			}
			
			var buttonWidth = 120f;
			var spacing = 10f;
			var totalButtonWidth = buttonWidth * 2 + spacing;
			var windowWidth = ImGui.GetWindowSize().X;
			var centerStart = (windowWidth - totalButtonWidth) * 0.5f;
			
			ImGui.SetCursorPosX(centerStart);
			
			if (Gui.Button("Create Scene", new Num.Vector2(buttonWidth, 30)))
			{
				if (canCreate)
				{
					CreateScene();
				}
			}
			
			if (!canCreate)
			{
				ImGui.EndDisabled();
			}
			
			ImGui.SameLine();
			
			if (Gui.Button("Cancel", new Num.Vector2(buttonWidth, 30)))
			{
				ImGui.CloseCurrentPopup();
				ResetFields();
			}
		}
		
		private void CreateScene()
		{
			EditorDebug.Log("=== Creating New Scene ===", "SceneCreation");
			EditorDebug.Log($"Scene name: {_sceneName}", "SceneCreation");

			try
			{
				var projectManager = ProjectManager.Instance;
				if (!projectManager.HasActiveProject)
				{
					EditorDebug.Error("No active project", "SceneCreation");
					_sceneNameError = "Error: No active project. Please load or create a project first.";
					return;
				}

				if (string.IsNullOrWhiteSpace(_sceneName))
				{
					_sceneNameError = "Error: Scene name cannot be empty.";
					return;
				}

				var scenesFolder = projectManager.CurrentProject.ScenesFolder;
				var sceneFilePath = Path.Combine(scenesFolder, $"{_sceneName}.vscene");

				if (File.Exists(sceneFilePath))
				{
					_sceneNameError = $"Error: A scene file already exists at: {sceneFilePath}";
					return;
				}

				// Ensure the Scenes folder exists
				if (!Directory.Exists(scenesFolder))
				{
					Directory.CreateDirectory(scenesFolder);
				}

				var sceneData = new SceneData
				{
					Name = _sceneName,
					FilePath = sceneFilePath,
					CreatedAt = DateTime.Now,
					ModifiedAt = DateTime.Now
				};

				var jsonContent = Voltage.Persistence.Json.ToJson(sceneData, new Voltage.Persistence.JsonSettings
				{
					PrettyPrint = true
				});

				File.WriteAllText(sceneFilePath, jsonContent, new System.Text.UTF8Encoding(false));

				EditorDebug.Log($"Successfully created scene: {_sceneName} at {sceneFilePath}", "SceneCreation");

				// Invoke scene created event
				var sceneManager = SceneManager.Instance;
				sceneManager.InvokeSceneCreated(sceneFilePath);

				// Load the scene if requested
				if (_createAndLoad)
				{
					sceneManager.LoadScene(sceneFilePath);
				}

				ImGui.CloseCurrentPopup();
				ResetFields();

				EditorDebug.Log("=== Scene Creation Complete ===", "SceneCreation");
			}
			catch (Exception ex)
			{
				EditorDebug.Error($"Exception: {ex.Message}", "SceneCreation");
				EditorDebug.Error($"Stack trace: {ex.StackTrace}", "SceneCreation");
			}
		}
		
		private void ResetFields()
		{
			_sceneName = "";
			_sceneNameError = "";
			_createAndLoad = true;
		}
	}
}