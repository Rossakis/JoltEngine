using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using Voltage.Data;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.FilePickers;
using Voltage.Editor.Persistence;
using Voltage.Editor.Tools;
using Voltage.Editor.Utils;
using Voltage.Persistence;
using Voltage.Project;
using Voltage.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.ProjectFile
{
	/// <summary>
	/// Handles the creation of new game projects through an ImGui popup interface
	/// </summary>
	public class ProjectCreatorWindow
	{
		private string _projectName = "";
		private string _projectPath = "";
		private bool _showCreateProjectPopup = false;
		private FilePicker _folderPicker;
		private bool _showFolderPicker = false;
		private bool _reopenCreateProjectPopup = false;
		private string _projectNameError = "";
		
		// ProjectSettings fields
		private int _screenWidth = 1280;
		private int _screenHeight = 720;
		private bool _isFullscreen = false;
		private bool _enableVSync = true;
		private float _masterVolume = 1.0f;
		private float _musicVolume = 1.0f;
		private float _sfxVolume = 1.0f;
		
		// Version fields
		private int _majorVersion = 1;
		private int _minorVersion = 0;
		private int _buildVersion = 0;
		

		public class ProjectMetadata
		{
			public string ProjectName;
			[JsonExclude]
			public string ProjectPath;//saved locally, not on file
			public string ScriptsFolder;
			public string EffectsFolder;
			public string ContentsFolder;
			public string DataFolder;
			public string ScenesFolder;
			public string PrefabsFolder;
			public DateTime CreatedDate;

			/// <summary>
			/// Engine version this project targets. Empty on a project created before the field existed.
			///
			/// <para>Changed only by an explicit upgrade, never stamped on open. Stamping would rewrite the
			/// file every time a teammate on a different version opened it, so the two of them would trade
			/// commits back and forth - noise in the one file that is supposed to make version differences
			/// legible.</para>
			/// </summary>
			public string EngineVersion;
		}

		public void OpenCreateProjectPopup()
		{
			_showCreateProjectPopup = true;
			
			// Set default project path to user's documents folder
			var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
			_projectPath = Path.Combine(documentsPath, "VoltageProjects");
		}
		
		public void Draw()
		{
			if (_reopenCreateProjectPopup)
			{
				_showCreateProjectPopup = true;
				_reopenCreateProjectPopup = false;
			}
			
			if (_showCreateProjectPopup)
			{
				ImGui.OpenPopup("create-project-popup");
				_showCreateProjectPopup = false;
			}
			
			DrawFolderPickerPopup();
			
			var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.5f);
			ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
			ImGui.SetNextWindowSize(new Num.Vector2(600, 700), ImGuiCond.Appearing);
			
			bool open = true;
			if (Gui.BeginPopupModal("create-project-popup", ref open, ImGuiWindowFlags.NoResize))
			{
				ImGui.TextColored(new Num.Vector4(0.2f, 0.8f, 1.0f, 1.0f), "Create New Game Project");
				ImGui.Separator();
				
				VoltageEditorUtils.MediumVerticalSpace();
				
				DrawProjectInfo();
				VoltageEditorUtils.MediumVerticalSpace();
				
				DrawDisplaySettings();
				VoltageEditorUtils.MediumVerticalSpace();
				
				DrawAudioSettings();
				VoltageEditorUtils.MediumVerticalSpace();
				
				DrawVersionInfo();
				VoltageEditorUtils.MediumVerticalSpace();
				
				DrawFolderInfo();
				VoltageEditorUtils.MediumVerticalSpace();
				
				DrawActionButtons();
				
				Gui.EndPopup();
			}
		}
		
		private void DrawProjectInfo()
		{
			ImGui.TextColored(new Num.Vector4(0.8f, 0.9f, 1.0f, 1.0f), "Project Information");
			ImGui.Separator();
			
			ImGui.Text("Project Name:");
			ImGui.SetNextItemWidth(-1);
			
			// Check for project name conflicts in real-time
			if (Gui.InputText("##ProjectName", ref _projectName, 100))
			{
				ValidateProjectName();
			}
			
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Name of your game project");
			}
			
			// Display error message under the input field if there's an error
			if (!string.IsNullOrWhiteSpace(_projectNameError))
			{
				ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(1.0f, 0.3f, 0.3f, 1.0f));
				ImGuiSafe.TextWrappedSafe(_projectNameError);
				ImGui.PopStyleColor();
			}
			
			VoltageEditorUtils.SmallVerticalSpace();
			
			ImGui.Text("Project Path:");
			ImGui.SetNextItemWidth(-250);
			Gui.InputText("##ProjectPath", ref _projectPath, 500);
			
			ImGui.SameLine();
			
			if (Gui.Button("Browse...", new Num.Vector2(100, 0)))
			{
				OpenFolderPicker();
			}
			
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Directory where the project will be created");
			}
			
			// Show the full project path that will be created
			if (!string.IsNullOrWhiteSpace(_projectName) && !string.IsNullOrWhiteSpace(_projectPath))
			{
				var fullPath = Path.Combine(_projectPath, _projectName);
				ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(0.7f, 0.7f, 0.7f, 1.0f));
				ImGuiSafe.TextWrappedSafe($"Full path: {fullPath}");
				ImGui.PopStyleColor();
			}
		}
		
		private void ValidateProjectName()
		{
			_projectNameError = "";

			// Don't show error for empty field while typing
			if (string.IsNullOrWhiteSpace(_projectName))
			{
				return; 
			}
			
			if (!string.IsNullOrWhiteSpace(_projectPath))
			{
				if (Directory.Exists(Path.Combine(_projectPath, _projectName)))
				{
					_projectNameError = $"Error: A project with the name '{_projectName}' already exists at this location.";
				}
			}
			
			// Validate project name for invalid characters
			var invalidChars = Path.GetInvalidFileNameChars();
			if (_projectName.IndexOfAny(invalidChars) >= 0)
			{
				_projectNameError = "Error: Project name contains invalid characters.";
			}
		}
		
		private void OpenFolderPicker()
		{
			// Initialize the folder picker with the current path or a default
			string startingPath = !string.IsNullOrWhiteSpace(_projectPath)
				? _projectPath
				: Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

			// Prefer the OS-native folder dialog. Being an OS-level modal, it sidesteps the ImGui
			// modal-within-modal reopen dance below and returns the chosen folder synchronously.
			if (NativeFileDialogs.IsAvailable)
			{
				if (NativeFileDialogs.TryPickFolder("Select Project Directory", startingPath, out var picked)
				    && !string.IsNullOrEmpty(picked))
				{
					_projectPath = picked;
					ValidateProjectName();
				}
				return;
			}

			_folderPicker = FilePicker.GetFolderPicker(this, startingPath);
			_folderPicker.DontAllowTraverselBeyondRootFolder = false; // Allow navigation beyond root
			_showFolderPicker = true;
		}
		
		private void DrawFolderPickerPopup()
		{
			if (_showFolderPicker && _folderPicker != null)
			{
				ImGui.OpenPopup("folder-picker-popup");
				_showFolderPicker = false;
			}
			
			var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.5f);
			ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
			ImGui.SetNextWindowSize(new Num.Vector2(600, 500), ImGuiCond.Appearing);
			
			bool open = true;
			if (Gui.BeginPopupModal("folder-picker-popup", ref open, ImGuiWindowFlags.NoResize))
			{
				ImGui.TextColored(new Num.Vector4(0.2f, 0.8f, 1.0f, 1.0f), "Select Project Directory");
				ImGui.Separator();
				VoltageEditorUtils.SmallVerticalSpace();
				
				if (_folderPicker != null && _folderPicker.Draw())
				{
					_projectPath = _folderPicker.SelectedFile;
					
					ValidateProjectName();
					
					FilePicker.RemoveFilePicker(_folderPicker);
					_folderPicker = null;
					ImGui.CloseCurrentPopup();
					
					_reopenCreateProjectPopup = true;
				}
				
				Gui.EndPopup();
			}
			
			// Handle cancel/close via X button
			if (!open && _folderPicker != null)
			{
				FilePicker.RemoveFilePicker(_folderPicker);
				_folderPicker = null;
				
				// Reopen the create project popup after this frame
				_reopenCreateProjectPopup = true;
			}
		}
		
		private void DrawDisplaySettings()
		{
			if (Gui.CollapsingHeader("Display Settings", ImGuiTreeNodeFlags.DefaultOpen))
			{
				ImGui.Indent();
				
				ImGui.Text("Screen Resolution:");
				ImGui.SetNextItemWidth(150);
				Gui.InputInt("Width##ScreenWidth", ref _screenWidth);
				ImGui.SameLine();
				ImGui.SetNextItemWidth(150);
				Gui.InputInt("Height##ScreenHeight", ref _screenHeight);
				
				VoltageEditorUtils.SmallVerticalSpace();
				
				Gui.Checkbox("Fullscreen", ref _isFullscreen);
				Gui.Checkbox("Enable VSync", ref _enableVSync);
				
				ImGui.Unindent();
			}
		}
		
		private void DrawAudioSettings()
		{
			if (Gui.CollapsingHeader("Audio Settings", ImGuiTreeNodeFlags.DefaultOpen))
			{
				ImGui.Indent();
				
				ImGui.Text("Master Volume:");
				ImGui.SetNextItemWidth(-1);
				Gui.SliderFloat("##MasterVolume", ref _masterVolume, 0.0f, 1.0f, "%.2f");
				
				VoltageEditorUtils.SmallVerticalSpace();
				
				ImGui.Text("Music Volume:");
				ImGui.SetNextItemWidth(-1);
				Gui.SliderFloat("##MusicVolume", ref _musicVolume, 0.0f, 1.0f, "%.2f");
				
				VoltageEditorUtils.SmallVerticalSpace();
				
				ImGui.Text("SFX Volume:");
				ImGui.SetNextItemWidth(-1);
				Gui.SliderFloat("##SFXVolume", ref _sfxVolume, 0.0f, 1.0f, "%.2f");
				
				ImGui.Unindent();
			}
		}
		
		private void DrawVersionInfo()
		{
			if (Gui.CollapsingHeader("Version", ImGuiTreeNodeFlags.DefaultOpen))
			{
				ImGui.Indent();
				
				ImGui.SetNextItemWidth(100);
				Gui.InputInt("Major", ref _majorVersion);
				_majorVersion = Math.Max(0, _majorVersion);
				
				ImGui.SameLine();
				ImGui.SetNextItemWidth(100);
				Gui.InputInt("Minor", ref _minorVersion);
				_minorVersion = Math.Max(0, _minorVersion);
				
				ImGui.SameLine();
				ImGui.SetNextItemWidth(100);
				Gui.InputInt("Build", ref _buildVersion);
				_buildVersion = Math.Max(0, _buildVersion);
				
				ImGuiSafe.TextColoredSafe(new Num.Vector4(0.7f, 0.7f, 0.7f, 1.0f),
					$"Version: {_majorVersion}.{_minorVersion}.{_buildVersion}");
				
				ImGui.Unindent();
			}
		}
		
		private void DrawFolderInfo()
		{
			if (Gui.CollapsingHeader("Project Structure", ImGuiTreeNodeFlags.DefaultOpen))
			{
				ImGui.Indent();
				
				ImGui.TextColored(new Num.Vector4(0.7f, 1.0f, 0.7f, 1.0f), "Standard folders that will be created:");
				
				ImGuiSafe.BulletTextSafe($"{ProjectCreator.ScriptsFolder}/ - For game scripts and logic");
				ImGuiSafe.BulletTextSafe($"{ProjectCreator.EffectsFolder}/ - For shader effects");
				ImGuiSafe.BulletTextSafe($"{ProjectCreator.ContentsFolder}/ - For game assets");
				ImGuiSafe.BulletTextSafe($"{ProjectCreator.DataFolder}/ - For game data and serialization");
				ImGuiSafe.BulletTextSafe($"{ProjectCreator.DataFolder}/{ProjectCreator.ScenesFolder}/ - For scene files");
				ImGuiSafe.BulletTextSafe($"{ProjectCreator.DataFolder}/{ProjectCreator.PrefabsFolder}/ - For entity prefabs");
				
				ImGui.Unindent();
			}
		}
		
		private void DrawActionButtons()
		{
			ImGui.Separator();
			
		 bool canCreate = !string.IsNullOrWhiteSpace(_projectName) && 
			                 !string.IsNullOrWhiteSpace(_projectPath) &&
			                 string.IsNullOrWhiteSpace(_projectNameError) && 
			                 _screenWidth > 0 && 
			                 _screenHeight > 0;
			
			if (!canCreate)
			{
				ImGui.BeginDisabled();
			}
			
			var buttonWidth = 120f;
			var spacing = 10f;
			var totalButtonWidth = (buttonWidth * 2) + spacing;
			var windowWidth = ImGui.GetWindowSize().X;
			var centerStart = (windowWidth - totalButtonWidth) * 0.5f;
			
			ImGui.SetCursorPosX(centerStart);
			
			if (Gui.Button("Create Project", new Num.Vector2(buttonWidth, 30)))
			{
				if (canCreate)
				{
					CreateProject();
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

		#region Actual Project Creation
		private void CreateProject()
		{
			try
			{
				var result = ProjectCreator.Create(_projectName, _projectPath, CreateDefaultSettings(), new Version(_majorVersion, _minorVersion, _buildVersion));
				EditorDebug.Log($"Project '{_projectName}' created at {result.ProjectPath}", "ProjectCreation");

				if (EditorSettingsWindow.AutoOpenSolutionUponCreation)
				{
					var solutionPath = Path.Combine(result.ProjectPath, $"{_projectName}.sln");
					if (File.Exists(solutionPath))
					{
						System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
						{
							FileName = solutionPath,
							UseShellExecute = true
						});
					}
				}

				ImGui.CloseCurrentPopup();
				ResetFields();
			}
			catch (Exception ex)
			{
				EditorDebug.Error($"Project creation failed: {ex.Message}", "ProjectCreation");
				_projectNameError = $"Error: {ex.Message}";
			}
		}
		#endregion

		private void ResetFields()
		{
			_projectName = "";
			_projectPath = "";
			_projectNameError = "";
			_screenWidth = 1280;
			_screenHeight = 720;
			_isFullscreen = false;
			_enableVSync = true;
			_masterVolume = 1.0f;
			_musicVolume = 1.0f;
			_sfxVolume = 1.0f;
			_majorVersion = 1;
			_minorVersion = 0;
			_buildVersion = 0;
			
			if (_folderPicker != null)
			{
				FilePicker.RemoveFilePicker(_folderPicker);
				_folderPicker = null;
			}
		}
		
		public ProjectSettings CreateDefaultSettings() =>
			CreateSettings(_screenWidth, _screenHeight, _isFullscreen, _enableVSync, _masterVolume, _musicVolume, _sfxVolume);

		/// <summary>The settings a new project starts from; the design resolution follows the screen size.</summary>
		public static ProjectSettings CreateSettings(int screenWidth = 1280, int screenHeight = 720, bool fullscreen = false, bool vsync = true, float masterVolume = 1f, float musicVolume = 1f, float sfxVolume = 1f)
		{
			return new ProjectSettings
			{
				Display = new ProjectSettings.DisplaySettings
				{
					ScreenWidth = screenWidth,
					ScreenHeight = screenHeight,
					IsFullscreen = fullscreen,
					EnableVSync = vsync
				},
				Audio = new ProjectSettings.AudioSettings
				{
					MasterVolume = masterVolume,
					MusicVolume = musicVolume,
					SFXVolume = sfxVolume
				},
				DesignResolution = new ProjectSettings.DesignResolutionSettings
				{
					Width = screenWidth,
					Height = screenHeight,
					ResolutionPolicy = Scene.SceneResolutionPolicy.BestFit,
					HorizontalBleed = 0,
					VerticalBleed = 0
				},
				Physics = new ProjectSettings.PhysicsSettings
				{
					PhysicsLayers = new Dictionary<string, int>
					{
						{ "Default", 0 },
						{ "Ground", 1 },
						{ "Player", 2 },
						{ "Enemy", 3 },
						{ "Projectile", 4 }
					}
				},
				Rendering = new ProjectSettings.RenderingSettings
				{
					RenderingLayers = new Dictionary<string, int>
					{
						{ "Lighting", 100 },
						{ "BehindAll", 99 },
						{ "HideObject", 30 },
						{ "Background", 0 },
						{ "Entities", 1 },
						{ "Foreground", -2 },
						{ "InFrontOfAll", -30 },
						{ "UIElement", -99 }
					}
				},
				Entities = new ProjectSettings.EntitySettings
				{
					EntityTags = new Dictionary<string, int>
					{
						{ "Default", 0 },
						{ "Player", 1 },
						{ "Enemy", 2 },
						{ "Collectible", 3 },
						{ "Environment", 4 }
					}
				},
				ContentDirectory = "Content",
				InitialScene = "MainScene"
			};
		}
	}
}
