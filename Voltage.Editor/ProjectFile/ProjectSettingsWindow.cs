using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.Utils;
using Voltage.Utils;

namespace Voltage.Editor.ProjectFile
{
	/// <summary>
	/// ImGui window for editing project settings at runtime.
	/// </summary>
	public class ProjectSettingsWindow
	{
		private bool _isOpen = false;
		private ProjectManager _projectManager;

		// Cached settings values
		private int _screenWidth;
		private int _screenHeight;
		private bool _isFullscreen;
		private bool _enableVSync;
		private float _masterVolume;
		private float _musicVolume;
		private float _sfxVolume;
		private string _contentDirectory;

		private Vector4 _backgroundClearColor;

		// Design resolution
		private int _designWidth;
		private int _designHeight;
		private int _selectedResolutionPolicy;
		private readonly string[] _resolutionPolicies = new[]
		{
			"None",
			"ExactFit",
			"NoBorder",
			"NoBorderPixelPerfect",
			"ShowAll",
			"ShowAllPixelPerfect",
			"FixedHeight",
			"FixedHeightPixelPerfect",
			"FixedWidth",
			"FixedWidthPixelPerfect",
			"BestFit"
		};

		// Physics, Rendering, and Entity settings
		private Dictionary<string, int> _physicsLayers;
		private Dictionary<string, int> _renderingLayers;
		private Dictionary<string, int> _entityTags;

		// For adding new entries
		private string _newPhysicsLayerName = "";
		private int _newPhysicsLayerValue = 0;
		private string _newRenderingLayerName = "";
		private int _newRenderingLayerValue = 0;
		private string _newEntityTagName = "";
		private int _newEntityTagValue = 0;

		// Project info
		private string _editableVersion = "";
		private bool _versionIsValid = true;

		private bool _hasUnsavedChanges = false;

		public bool IsOpen
		{
			get => _isOpen;
			set => _isOpen = value;
		}

		public ProjectSettingsWindow()
		{
			_projectManager = ProjectManager.Instance;
		}

		public void Draw()
		{
			if (!_isOpen)
				return;

			if (!_projectManager.HasActiveProject)
			{
				_isOpen = false;
				EditorDebug.Log("No active project to configure.");
				return;
			}

			// Load settings when first opened
			if (_isOpen && !ImGui.IsPopupOpen("Project Settings"))
			{
				LoadSettings();
				ImGui.OpenPopup("Project Settings");
			}

			var center = new Vector2(Screen.Width * 0.5f, Screen.Height * 0.5f);
			ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
			ImGui.SetNextWindowSize(new Vector2(700, 700), ImGuiCond.Appearing);

			bool open = true;
			if (Gui.BeginPopupModal("Project Settings", ref open, ImGuiWindowFlags.None))
			{
				var project = _projectManager.CurrentProject;

				ImGuiSafe.TextColoredSafe(new Vector4(0.2f, 0.8f, 1.0f, 1.0f), $"Project: {project.ProjectName}");
				ImGuiSafe.TextColoredSafe(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), $"Version: {project.Version}");
				ImGui.Separator();

				VoltageEditorUtils.SmallVerticalSpace();

				// Begin tabs
				if (ImGui.BeginTabBar("ProjectSettingsTabs"))
				{
					if (Gui.BeginTabItem("Project Info"))
					{
						DrawProjectInfoSettings();
						ImGui.EndTabItem();
					}

					if (Gui.BeginTabItem("Display & Audio"))
					{
						DrawDisplaySettings();
						VoltageEditorUtils.MediumVerticalSpace();
						DrawBackgroundSettings();
						VoltageEditorUtils.MediumVerticalSpace();
						DrawDesignResolutionSettings();
						VoltageEditorUtils.MediumVerticalSpace();
						DrawAudioSettings();
						VoltageEditorUtils.MediumVerticalSpace();
						DrawContentSettings();
						ImGui.EndTabItem();
					}
					
					if (Gui.BeginTabItem("Physics Layers"))
					{
						DrawPhysicsLayersSettings();
						ImGui.EndTabItem();
					}
					
					if (Gui.BeginTabItem("Rendering Layers"))
					{
						DrawRenderingLayersSettings();
						ImGui.EndTabItem();
					}
					
					if (Gui.BeginTabItem("Entity Tags"))
					{
						DrawEntityTagsSettings();
						ImGui.EndTabItem();
					}
					
					ImGui.EndTabBar();
				}
				
				VoltageEditorUtils.MediumVerticalSpace();
				DrawActionButtons();
				
				Gui.EndPopup();
			}
			
			if (!open)
			{
				_isOpen = false;
			}
		}
		
		private void LoadSettings()
		{
			var settings = _projectManager.CurrentProject.Settings;
			
			if (settings.Display != null)
			{
				_screenWidth = settings.Display.ScreenWidth;
				_screenHeight = settings.Display.ScreenHeight;
				_isFullscreen = settings.Display.IsFullscreen;
				_enableVSync = settings.Display.EnableVSync;
			}
			
			if (settings.Audio != null)
			{
				_masterVolume = settings.Audio.MasterVolume;
				_musicVolume = settings.Audio.MusicVolume;
				_sfxVolume = settings.Audio.SFXVolume;
			}
			
			// Load design resolution settings
			if (settings.DesignResolution != null)
			{
				_designWidth = settings.DesignResolution.Width;
				_designHeight = settings.DesignResolution.Height;
				_selectedResolutionPolicy = (int)settings.DesignResolution.ResolutionPolicy;
			}
			
			if (settings.Rendering != null)
			{
				var background = settings.Rendering.BackgroundClearColor;
				_backgroundClearColor = new Vector4(background.R / 255f, background.G / 255f,
					background.B / 255f, background.A / 255f);
			}

			// Load dictionary settings (deep copy to allow editing)
			_physicsLayers = new Dictionary<string, int>(settings.Physics.PhysicsLayers);
			_renderingLayers = new Dictionary<string, int>(settings.Rendering.RenderingLayers);
			_entityTags = new Dictionary<string, int>(settings.Entities.EntityTags);

			_contentDirectory = settings.ContentDirectory ?? "Content";

			// Load version from the live project
			_editableVersion = _projectManager.CurrentProject.Version.ToString();
			_versionIsValid = true;

			_hasUnsavedChanges = false;
		}

		private string _engineVersionMessage;
		private bool _engineVersionMessageIsError;

		/// <summary>Which engine version the project targets. The mismatch that matters is a project newer than the editor, where saving can drop what this build did not understand.</summary>
		private void DrawEngineVersion()
		{
			ImGui.Text("Engine Version:");
			ImGui.SameLine();

			var state = ProjectEngineVersion.State;
			var project = ProjectEngineVersion.ProjectVersion;
			var editor = ProjectEngineVersion.EditorVersion;

			switch (state)
			{
				case ProjectVersionState.Match:
					ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), $"{project} (matches this editor)");
					break;
				case ProjectVersionState.Older:
					ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), $"{project}  (editor is {editor})");
					break;
				case ProjectVersionState.Newer:
					ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), $"{project}  (editor is only {editor})");
					break;
				case ProjectVersionState.Unstamped:
					ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "not recorded");
					break;
				default:
					ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), $"'{project}' (unreadable)");
					break;
			}

			if (ProjectEngineVersion.Summary != null)
				ImGui.TextWrapped(ProjectEngineVersion.Summary);

			// Not offered when the project is newer: stamping it down would claim support this build lacks.
			if (state is ProjectVersionState.Older or ProjectVersionState.Unstamped or ProjectVersionState.Unreadable)
			{
				if (Gui.Button($"Set project to Voltage {editor}"))
				{
					_engineVersionMessageIsError = !ProjectEngineVersion.StampCurrentVersion(
						_projectManager.CurrentProject, _projectManager.LastProjectPath, out _engineVersionMessage);
				}

				if (ImGui.IsItemHovered())
					ImGui.SetTooltip("Writes the version into the .voltage file. Commit it so the team moves together.");
			}

			if (!string.IsNullOrEmpty(_engineVersionMessage))
			{
				ImGui.TextColored(
					_engineVersionMessageIsError ? new Vector4(1f, 0.35f, 0.35f, 1f) : new Vector4(0.4f, 0.9f, 0.4f, 1f),
					_engineVersionMessage);
			}
		}

		private void DrawProjectInfoSettings()
		{
			ImGui.TextColored(new Vector4(0.7f, 1.0f, 0.7f, 1.0f), "Project Information");
			ImGui.Separator();

			VoltageEditorUtils.SmallVerticalSpace();

			var project = _projectManager.CurrentProject;

			ImGui.Text("Project Name:");
			ImGui.SameLine();
			ImGuiSafe.TextDisabledSafe(project.ProjectName);

			ImGui.Text("Project Path:");
			ImGui.SameLine();
			ImGuiSafe.TextDisabledSafe(project.ProjectPath);

			DrawEngineVersion();

			VoltageEditorUtils.MediumVerticalSpace();

			ImGui.TextColored(new Vector4(0.7f, 1.0f, 0.7f, 1.0f), "Version");
			ImGui.Separator();

			VoltageEditorUtils.SmallVerticalSpace();

			ImGui.TextWrapped("The project version is written to the game's .csproj file and embedded into the built executable.");

			VoltageEditorUtils.SmallVerticalSpace();

			ImGui.Text("Version:");
			ImGui.SameLine();

			
			var frameBgColor = _versionIsValid
				? ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg]
				: new Vector4(0.5f, 0.1f, 0.1f, 1.0f);
			ImGui.PushStyleColor(ImGuiCol.FrameBg, frameBgColor);

			ImGui.SetNextItemWidth(200);
			if (Gui.InputText("##ProjectVersion", ref _editableVersion, 32))
			{
				_versionIsValid = Version.TryParse(_editableVersion, out _);
				if (_versionIsValid)
					_hasUnsavedChanges = true;
			}

			ImGui.PopStyleColor();

			ImGui.SameLine();
			if (!_versionIsValid)
				ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "Invalid format (e.g. 1.0.0)");
			else
				ImGui.TextDisabled("e.g. 1.0.0");
		}

		private void DrawDisplaySettings()
		{
			if (Gui.CollapsingHeader("Display Settings", ImGuiTreeNodeFlags.DefaultOpen))
			{
				ImGui.Indent();
				
				ImGui.Text("Screen Resolution:");
				ImGui.SetNextItemWidth(150);
				if (Gui.InputInt("Width##ScreenWidth", ref _screenWidth))
					_hasUnsavedChanges = true;
				
				ImGui.SameLine();
				ImGui.SetNextItemWidth(150);
				if (Gui.InputInt("Height##ScreenHeight", ref _screenHeight))
					_hasUnsavedChanges = true;
				
				VoltageEditorUtils.SmallVerticalSpace();
				
				if (Gui.Checkbox("Fullscreen", ref _isFullscreen))
					_hasUnsavedChanges = true;
				
				if (Gui.Checkbox("Enable VSync", ref _enableVSync))
					_hasUnsavedChanges = true;
				
				ImGui.Unindent();
			}
		}
		
		/// <summary>Background clear colour, applied to every scene the game loads.</summary>
		private void DrawBackgroundSettings()
		{
			if (Gui.CollapsingHeader("Background", ImGuiTreeNodeFlags.DefaultOpen))
			{
				ImGui.Indent();

				ImGui.TextWrapped("Colour the screen is cleared to. Applied to every scene the game loads, " +
				                  "so this is the background the built game ships with.");

				VoltageEditorUtils.SmallVerticalSpace();

				ImGui.AlignTextToFramePadding();
				ImGui.TextUnformatted("Select Color");
				ImGui.SameLine();

				if (VoltageEditorUtils.ColorSwatchButton("##BackgroundClearColor", _backgroundClearColor))
					ImGui.OpenPopup("project-background-color-popup");

				if (ImGui.IsItemHovered())
					ImGui.SetTooltip("Background Clear Color - click to pick.");

				if (Gui.BeginPopup("project-background-color-popup"))
				{
					if (ImGui.ColorPicker4("##BackgroundClearColorPicker", ref _backgroundClearColor,
						    ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoSidePreview))
					{
						_hasUnsavedChanges = true;
					}

					Gui.EndPopup();
				}

				if (EditorBackgroundColor.UseTemporary)
				{
					VoltageEditorUtils.SmallVerticalSpace();
					ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.2f, 1.0f),
						"The editor is showing a temporary background colour, so the game view will not change.");
					ImGui.TextDisabled("Switch it back from the colour swatch in the editor tools bar.");
				}

				ImGui.Unindent();
			}
		}

		private void DrawDesignResolutionSettings()
		{
			if (Gui.CollapsingHeader("Design Resolution", ImGuiTreeNodeFlags.DefaultOpen))
			{
				ImGui.Indent();
		
				ImGui.TextWrapped("Design resolution determines how your game scales across different screen sizes.");
				VoltageEditorUtils.SmallVerticalSpace();
				
				ImGui.Text("Design Resolution:");
				ImGui.SetNextItemWidth(150);
				if (Gui.InputInt("Width##DesignWidth", ref _designWidth))
					_hasUnsavedChanges = true;
				
				ImGui.SameLine();
				ImGui.SetNextItemWidth(150);
				if (Gui.InputInt("Height##DesignHeight", ref _designHeight))
					_hasUnsavedChanges = true;
				
				VoltageEditorUtils.SmallVerticalSpace();
				
				ImGui.Text("Resolution Policy:");
				ImGui.SetNextItemWidth(-1);
				
				// Store current policy name for tooltip
				var currentPolicyName = _resolutionPolicies[_selectedResolutionPolicy];
				
				if (Gui.Combo("##ResolutionPolicy", ref _selectedResolutionPolicy, _resolutionPolicies, _resolutionPolicies.Length))
					_hasUnsavedChanges = true;
				
				if (ImGui.IsItemHovered())
				{
					var description = GetResolutionPolicyDescription(currentPolicyName);
					ImGui.BeginTooltip();
					ImGui.PushTextWrapPos(400f);
					ImGuiSafe.TextColoredSafe(new Vector4(0.7f, 1.0f, 0.7f, 1.0f), currentPolicyName);
					ImGui.Separator();
					ImGuiSafe.TextWrappedSafe(description);
					ImGui.PopTextWrapPos();
					ImGui.EndTooltip();
				}
				
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
				if (Gui.SliderFloat("##MasterVolume", ref _masterVolume, 0.0f, 1.0f, "%.2f"))
					_hasUnsavedChanges = true;
				
				VoltageEditorUtils.SmallVerticalSpace();
				
				ImGui.Text("Music Volume:");
				ImGui.SetNextItemWidth(-1);
				if (Gui.SliderFloat("##MusicVolume", ref _musicVolume, 0.0f, 1.0f, "%.2f"))
					_hasUnsavedChanges = true;
				
				VoltageEditorUtils.SmallVerticalSpace();
				
				ImGui.Text("SFX Volume:");
				ImGui.SetNextItemWidth(-1);
				if (Gui.SliderFloat("##SFXVolume", ref _sfxVolume, 0.0f, 1.0f, "%.2f"))
					_hasUnsavedChanges = true;
				
				ImGui.Unindent();
			}
		}
		
		private void DrawContentSettings()
		{
			if (Gui.CollapsingHeader("Content Settings"))
			{
				ImGui.Indent();
				
				ImGui.Text("Content Directory:");
				ImGui.SetNextItemWidth(-1);
				if (Gui.InputText("##ContentDirectory", ref _contentDirectory, 256))
					_hasUnsavedChanges = true;
				
				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("Relative path to the content folder");
				}
				
				ImGui.Unindent();
			}
		}
		
		private void DrawPhysicsLayersSettings()
		{
			ImGui.TextColored(new Vector4(0.7f, 1.0f, 0.7f, 1.0f), "Physics Layers");
			ImGui.TextWrapped("Define collision layers for physics objects.");
			ImGui.Separator();
			VoltageEditorUtils.SmallVerticalSpace();
			
			// Display existing layers in a table
			if (ImGui.BeginTable("PhysicsLayersTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
			{
				ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
				ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 100);
				ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 80);
				ImGui.TableHeadersRow();
				
				var layersToRemove = new List<string>();
				
				foreach (var kvp in _physicsLayers.ToList())
				{
					ImGui.TableNextRow();
					ImGui.TableNextColumn();
					ImGuiSafe.TextSafe(kvp.Key);
					
					ImGui.TableNextColumn();
					ImGuiSafe.TextSafe(kvp.Value.ToString());
					
					ImGui.TableNextColumn();
					if (Gui.Button($"Remove##physics_{kvp.Key}"))
					{
						layersToRemove.Add(kvp.Key);
						_hasUnsavedChanges = true;
					}
				}
				
				foreach (var key in layersToRemove)
				{
					_physicsLayers.Remove(key);
				}
				
				ImGui.EndTable();
			}
			
			VoltageEditorUtils.MediumVerticalSpace();
			
			// Add new layer
			ImGui.TextColored(new Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Add New Layer:");
			ImGui.SetNextItemWidth(200);
			Gui.InputText("##NewPhysicsLayerName", ref _newPhysicsLayerName, 64);
			ImGui.SameLine();
			ImGui.SetNextItemWidth(100);
			Gui.InputInt("##NewPhysicsLayerValue", ref _newPhysicsLayerValue);
			ImGui.SameLine();
			
			bool canAdd = !string.IsNullOrWhiteSpace(_newPhysicsLayerName) && !_physicsLayers.ContainsKey(_newPhysicsLayerName);
			if (!canAdd) ImGui.BeginDisabled();
			
			if (Gui.Button("Add##AddPhysicsLayer"))
			{
				_physicsLayers[_newPhysicsLayerName.Trim()] = _newPhysicsLayerValue;
				_newPhysicsLayerName = "";
				_newPhysicsLayerValue = _physicsLayers.Count;
				_hasUnsavedChanges = true;
			}
			
			if (!canAdd) ImGui.EndDisabled();
		}
		
		private void DrawRenderingLayersSettings()
		{
			ImGui.TextColored(new Vector4(0.7f, 1.0f, 0.7f, 1.0f), "Rendering Layers");
			ImGui.TextWrapped("Define render order layers. Lower values render first (background), higher values render last (foreground).");
			ImGui.Separator();
			VoltageEditorUtils.SmallVerticalSpace();
			
			// Display existing layers in a table
			if (ImGui.BeginTable("RenderingLayersTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
			{
				ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
				ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 100);
				ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 80);
				ImGui.TableHeadersRow();
				
				var layersToRemove = new List<string>();
				
				// Sort by value for better visualization
				foreach (var kvp in _renderingLayers.OrderBy(x => x.Value))
				{
					ImGui.TableNextRow();
					ImGui.TableNextColumn();
					ImGuiSafe.TextSafe(kvp.Key);
					
					ImGui.TableNextColumn();
					ImGuiSafe.TextSafe(kvp.Value.ToString());
					
					ImGui.TableNextColumn();
					if (Gui.Button($"Remove##rendering_{kvp.Key}"))
					{
						layersToRemove.Add(kvp.Key);
						_hasUnsavedChanges = true;
					}
				}
				
				foreach (var key in layersToRemove)
				{
					_renderingLayers.Remove(key);
				}
				
				ImGui.EndTable();
			}
			
			VoltageEditorUtils.MediumVerticalSpace();
			
			// Add new layer
			ImGui.TextColored(new Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Add New Layer:");
			ImGui.SetNextItemWidth(200);
			Gui.InputText("##NewRenderingLayerName", ref _newRenderingLayerName, 64);
			ImGui.SameLine();
			ImGui.SetNextItemWidth(100);
			Gui.InputInt("##NewRenderingLayerValue", ref _newRenderingLayerValue);
			ImGui.SameLine();
			
			bool canAdd = !string.IsNullOrWhiteSpace(_newRenderingLayerName) && !_renderingLayers.ContainsKey(_newRenderingLayerName);
			if (!canAdd) ImGui.BeginDisabled();
			
			if (Gui.Button("Add##AddRenderingLayer"))
			{
				_renderingLayers[_newRenderingLayerName.Trim()] = _newRenderingLayerValue;
				_newRenderingLayerName = "";
				_newRenderingLayerValue = 0;
				_hasUnsavedChanges = true;
			}
			
			if (!canAdd) ImGui.EndDisabled();
		}
		
		private void DrawEntityTagsSettings()
		{
			ImGui.TextColored(new Vector4(0.7f, 1.0f, 0.7f, 1.0f), "Entity Tags");
			ImGui.TextWrapped("Define tags for categorizing entities in your game.");
			ImGui.Separator();
			VoltageEditorUtils.SmallVerticalSpace();
			
			// Display existing tags in a table
			if (ImGui.BeginTable("EntityTagsTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
			{
				ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
				ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 100);
				ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 80);
				ImGui.TableHeadersRow();
				
				var tagsToRemove = new List<string>();
				
				foreach (var kvp in _entityTags.ToList())
				{
					ImGui.TableNextRow();
					ImGui.TableNextColumn();
					ImGuiSafe.TextSafe(kvp.Key);
					
					ImGui.TableNextColumn();
					ImGuiSafe.TextSafe(kvp.Value.ToString());
					
					ImGui.TableNextColumn();
					if (Gui.Button($"Remove##tag_{kvp.Key}"))
					{
						tagsToRemove.Add(kvp.Key);
						_hasUnsavedChanges = true;
					}
				}
				
				foreach (var key in tagsToRemove)
				{
					_entityTags.Remove(key);
				}
				
				ImGui.EndTable();
			}
			
			VoltageEditorUtils.MediumVerticalSpace();
			
			// Add new tag
			ImGui.TextColored(new Vector4(0.5f, 0.8f, 1.0f, 1.0f), "Add New Tag:");
			ImGui.SetNextItemWidth(200);
			Gui.InputText("##NewEntityTagName", ref _newEntityTagName, 64);
			ImGui.SameLine();
			ImGui.SetNextItemWidth(100);
			Gui.InputInt("##NewEntityTagValue", ref _newEntityTagValue);
			ImGui.SameLine();
			
			bool canAdd = !string.IsNullOrWhiteSpace(_newEntityTagName) && !_entityTags.ContainsKey(_newEntityTagName);
			if (!canAdd) ImGui.BeginDisabled();
			
			if (Gui.Button("Add##AddEntityTag"))
			{
				_entityTags[_newEntityTagName.Trim()] = _newEntityTagValue;
				_newEntityTagName = "";
				_newEntityTagValue = _entityTags.Count;
				_hasUnsavedChanges = true;
			}
			
			if (!canAdd) ImGui.EndDisabled();
		}
		
		private void DrawActionButtons()
		{
			ImGui.Separator();
			
			if (_hasUnsavedChanges)
			{
				ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.2f, 1.0f), "You have unsaved changes");
			}
			
			var buttonWidth = 120f;
			var spacing = 10f;
			var totalButtonWidth = (buttonWidth * 3) + (spacing * 2);
			var windowWidth = ImGui.GetWindowSize().X;
			var centerStart = (windowWidth - totalButtonWidth) * 0.5f;

			ImGui.SetCursorPosX(centerStart);

			bool canSave = _versionIsValid;
			if (!canSave)
				ImGui.BeginDisabled();

			if (Gui.Button("Save", new Vector2(buttonWidth, 0)))
			{
				SaveSettings();
			}

			if (!canSave)
				ImGui.EndDisabled();

			ImGui.SameLine();

			if (Gui.Button("Apply", new Vector2(buttonWidth, 0)))
			{
				ApplySettings();
			}
			
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Apply settings to current session without saving");
			}
			
			ImGui.SameLine();
			
			if (Gui.Button("Close", new Vector2(buttonWidth, 0)))
			{
				if (_hasUnsavedChanges)
				{
					// Could add a confirmation dialog here
					LoadSettings(); // Revert changes
				}
				ImGui.CloseCurrentPopup();
				_isOpen = false;
			}
		}

		private void SaveSettings()
		{
			try
			{
				var project = _projectManager.CurrentProject;
				var settings = project.Settings;
				settings.Display.ScreenWidth = _screenWidth;
				settings.Display.ScreenHeight = _screenHeight;
				settings.Display.IsFullscreen = _isFullscreen;
				settings.Display.EnableVSync = _enableVSync;
				settings.Audio.MasterVolume = _masterVolume;
				settings.Audio.MusicVolume = _musicVolume;
				settings.Audio.SFXVolume = _sfxVolume;
				settings.DesignResolution.Width = _designWidth;
				settings.DesignResolution.Height = _designHeight;
				settings.DesignResolution.ResolutionPolicy = (Scene.SceneResolutionPolicy)_selectedResolutionPolicy;
				settings.Physics.PhysicsLayers = new Dictionary<string, int>(_physicsLayers);
				settings.Rendering.RenderingLayers = new Dictionary<string, int>(_renderingLayers);
				settings.Rendering.BackgroundClearColor = ToXnaColor(_backgroundClearColor);
				settings.Entities.EntityTags = new Dictionary<string, int>(_entityTags);
				settings.ContentDirectory = _contentDirectory;

				var settingsPath = Path.Combine(project.ProjectPath, "ProjectSettings.json");
				var json = Voltage.Persistence.Json.ToJson(settings, new Voltage.Persistence.JsonSettings { PrettyPrint = true });
				File.WriteAllText(settingsPath, json, new System.Text.UTF8Encoding(false));

				if (project is RuntimeGameProject runtimeProject)
				{
					if (!runtimeProject.SetVersion(_editableVersion))
						Debug.Error($"Failed to save version '{_editableVersion}' to .csproj.");
				}

				ApplySettings();
				_hasUnsavedChanges = false;
			}
			catch (Exception ex)
			{
				Debug.Error($"Failed to save project settings: {ex.Message}");
			}
		}
		
		private static Microsoft.Xna.Framework.Color ToXnaColor(Vector4 color) =>
			new Microsoft.Xna.Framework.Color(color.X, color.Y, color.Z, color.W);

		private void ApplySettings()
		{
			try
			{
				// Only apply design resolution if scene is available
				if (Core.Scene != null && _designWidth > 0 && _designHeight > 0)
				{
					var resolutionPolicy = (Scene.SceneResolutionPolicy)_selectedResolutionPolicy;
					Core.Scene.SetDesignResolution(_designWidth, _designHeight, resolutionPolicy);
				}

				var settings = _projectManager.CurrentProject?.Settings;
				if (settings?.Rendering != null)
				{
					settings.Rendering.BackgroundClearColor = ToXnaColor(_backgroundClearColor);
					EditorBackgroundColor.Apply();
				}

				//TODO:AUDIO SETTINGS
				//AudioManager.SetMasterVolume(_masterVolume);
			}
			catch (Exception ex)
			{
				Debug.Error($"Failed to apply project settings: {ex.Message}");
			}
		}
		
		/// <summary>
		/// Gets the description for a resolution policy based on its name.
		/// </summary>
		private string GetResolutionPolicyDescription(string policyName)
		{
			return policyName switch
			{
				"None" => "Default. RenderTarget matches the screen size.",
				
				"ExactFit" => "The entire application is visible in the specified area without trying to preserve the original aspect ratio. Distortion can occur, and the application may appear stretched or compressed.",
				
				"NoBorder" => "The entire application fills the specified area, without distortion but possibly with some cropping, while maintaining the original aspect ratio of the application.",
				
				"NoBorderPixelPerfect" => "Pixel perfect version of NoBorder. Scaling is limited to integer values.",
				
				"ShowAll" => "The entire application is visible in the specified area without distortion while maintaining the original aspect ratio of the application. Borders can appear on two sides of the application.",
				
				"ShowAllPixelPerfect" => "Pixel perfect version of ShowAll. Scaling is limited to integer values.",
				
				"FixedHeight" => "The application takes the height of the design resolution size and modifies the width of the internal canvas so that it fits the aspect ratio of the device. No distortion will occur however you must make sure your application works on different aspect ratios.",
				
				"FixedHeightPixelPerfect" => "Pixel perfect version of FixedHeight. Scaling is limited to integer values.",
				
				"FixedWidth" => "The application takes the width of the design resolution size and modifies the height of the internal canvas so that it fits the aspect ratio of the device. No distortion will occur however you must make sure your application works on different aspect ratios.",
				
				"FixedWidthPixelPerfect" => "Pixel perfect version of FixedWidth. Scaling is limited to integer values.",
				
				"BestFit" => "The application takes the width and height that best fits the design resolution with optional cropping inside of the \"bleed area\" and possible letter/pillar boxing. Works just like ShowAll except with horizontal/vertical bleed (padding). Gives you an area much like the old TitleSafeArea. Example: if design resolution is 1348x900 and bleed is 148x140 the safe area would be 1200x760 (design resolution - bleed).",
				
				_ => "Unknown resolution policy."
			};
		}
	}
}