using ImGuiNET;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.Effects;
using Voltage.Editor.Hotkeys;
using Voltage.Editor.Persistence;
using Voltage.Editor.SceneFile;
using Voltage.Editor.Tools;
using Voltage.Editor.Utils;
using Voltage.Utils;

namespace Voltage.Editor.ImGuiCore;

public partial class ImGuiManager
{
	#region Persistent Settings

	private PersistentBool _showStyleEditor = new("ImGui_ShowStyleEditor", false);

	public bool ShowStyleEditor
	{
		get => _showStyleEditor.Value;
		set => _showStyleEditor.Value = value;
	}

	private PersistentBool _showSceneGraphWindow = new("ImGui_ShowSceneGraphWindow", true);

	public bool ShowSceneGraphWindow
	{
		get => _showSceneGraphWindow.Value;
		set => _showSceneGraphWindow.Value = value;
	}

	private PersistentBool _showMainInspectorWindow = new("ImGui_ShowMainInspectorWindow", true);

	public bool ShowMainInspectorWindow
	{
		get => _showMainInspectorWindow.Value;
		set => _showMainInspectorWindow.Value = value;
	}

	private PersistentBool _showCoreWindow = new("ImGui_ShowCoreWindow", true);

	public bool ShowCoreWindow
	{
		get => _showCoreWindow.Value;
		set => _showCoreWindow.Value = value;
	}

	private PersistentBool _showSeparateGameWindow = new("ImGui_ShowSeparateGameWindow", true);

	public bool ShowSeparateGameWindow
	{
		get => _showSeparateGameWindow.Value;
		set => _showSeparateGameWindow.Value = value;
	}

	private PersistentBool _showAnimationEventInspector = new("ImGui_ShowAnimationEventInspector", false);

	public bool ShowAnimationEventInspector
	{
		get => _showAnimationEventInspector.Value;
		set => _showAnimationEventInspector.Value = value;
	}

	private PersistentBool _showMenuBar = new("ImGui_ShowMenuBar", true);

	public bool ShowMenuBar
	{
		get => _showMenuBar.Value;
		set => _showMenuBar.Value = value;
	}

	private PersistentBool _preserveGameWindowAspectRatio = new("ImGui_PreserveGameWindowAspectRatio", true);

	public bool PreserveGameWindowAspectRatio
	{
		get => _preserveGameWindowAspectRatio.Value;
		set => _preserveGameWindowAspectRatio.Value = value;
	}

	private PersistentBool _showAssetBrowser = new("ImGui_ShowAssetBrowser", true);

	// When set, the "Missing Engine Effects" startup prompt is suppressed permanently. Persisted across
	// sessions. Useful on Linux where compiling the effects fails, so the user can dismiss it for good.
	private PersistentBool _dontShowEngineEffectsPrompt = new("ImGui_DontShowEngineEffectsPrompt", false);

	// Transient checkbox state for the "Don't show again" option inside the prompt (not persisted directly;
	// only written to _dontShowEngineEffectsPrompt when the user dismisses with "No").
	private bool _engineEffectsDontShowAgainChecked = false;

	public bool ShowAssetBrowser
	{
		get => _showAssetBrowser.Value;
		set => _showAssetBrowser.Value = value;
	}

	private PersistentBool _showTimelineWindow = new("ImGui_ShowTimelineWindow", false);

	public bool ShowTimelineWindow
	{
		get => _showTimelineWindow.Value;
		set => _showTimelineWindow.Value = value;
	}

	private PersistentBool _showTilePaletteWindow = new("ImGui_ShowTilePaletteWindow", false);

	public bool ShowTilePaletteWindow
	{
		get => _showTilePaletteWindow.Value;
		set => _showTilePaletteWindow.Value = value;
	}

	private PersistentBool _showAudioProfilerWindow = new("ImGui_ShowAudioProfilerWindow", false);

	public bool ShowAudioProfilerWindow
	{
		get => _showAudioProfilerWindow.Value;
		set => _showAudioProfilerWindow.Value = value;
	}

	private PersistentBool _showPerformanceProfilerWindow = new("ImGui_ShowPerformanceProfilerWindow", false);

	public bool ShowPerformanceProfilerWindow
	{
		get => _showPerformanceProfilerWindow.Value;
		set => _showPerformanceProfilerWindow.Value = value;
	}

	private PersistentBool _showPlacementGrid = new("ImGui_ShowPlacementGrid", false);

	/// <summary>When true (and Snap to Grid is enabled), the placement grid is drawn in the game view.</summary>
	public bool ShowPlacementGrid
	{
		get => _showPlacementGrid.Value;
		set => _showPlacementGrid.Value = value;
	}

	private PersistentString _lastSelectedLayout = new("ImGui_LastSelectedLayout", "Default");
	private PersistentString _lastSelectedTheme = new("ImGui_LastSelectedTheme", "DarkTheme1");
	#endregion

	// Layouts queued for delete/rename by View > Layout, pending their confirmation popup.
	private string _layoutPendingDelete;
	private bool _showDeleteLayoutPopup;

	private string _layoutPendingRename;
	private string _renameLayoutName = "";
	private bool _showRenameLayoutPopup;

	private void DrawFileMenu()
	{
		if (Gui.BeginMenu("File"))
		{
			if (Gui.MenuItem("New Project"))
			{
				_projectCreatorWindow.OpenCreateProjectPopup();
			}

			if (Gui.MenuItem("Load Project..."))
			{
				OpenProjectFilePicker();
			}

			if (_projectManager.GetRecentProjects().Count > 0)
			{
				if (Gui.BeginMenu("Recent Projects"))
				{
					foreach (var projectPath in _projectManager.GetRecentProjects())
					{
						var projectDir = Path.GetDirectoryName(projectPath);
						var projectName = Path.GetFileName(projectDir);

						if (Gui.MenuItem(projectName))
						{
							RequestProjectLoad(projectPath);
						}

						if (ImGui.IsItemHovered())
						{
							ImGuiSafe.SetTooltipSafe(projectPath);
						}
					}

					ImGui.Separator();

					if (Gui.MenuItem("Clear Recent Projects"))
					{
						_projectManager.ClearRecentProjects();
					}

					Gui.EndMenu();
				}
			}

			ImGui.Separator();

			if (Gui.MenuItem("Settings"))
			{
				_editorSettingsWindow.IsOpen = true;
			}

			ImGui.Separator();

			if (Gui.MenuItem("Load Tiled Map"))
			{
				SceneGraphWindow.TmxFilePicker.Open();
			}

			if (Gui.MenuItem("Load Aseprite Images"))
			{
				SceneGraphWindow.AsepriteFilePicker.Open();
			}

			ImGui.Separator();

			if (Gui.MenuItem("Open Sprite Atlas Editor"))
				_spriteAtlasEditorWindow = _spriteAtlasEditorWindow ?? new SpriteAtlasEditorWindow();

			if (Gui.MenuItem("Open Timeline Editor"))
				ShowTimelineWindow = true;

			if (Gui.MenuItem("Close ImGui Editor"))
				SetEnabled(false);

			if (Gui.MenuItem("Exit"))
			{
				Core.ConfirmAndExit();
			}

			Plugins.EditorPluginHost.DrawMenuItems(Plugins.EditorMenu.File);

			Gui.EndMenu();
		}
	}

	private void DrawProjectMenu()
	{
		if (Gui.BeginMenu("Project"))
		{
			if (Gui.BeginMenu("New Scene"))
			{
				if (!_projectManager.HasActiveProject)
				{
					ImGui.TextDisabled("No active project");
				}
				else if (Gui.MenuItem("Create New Scene..."))
				{
					_sceneCreator.OpenCreateScenePopup();
				}
				
				Gui.EndMenu();
			}

			if (Gui.BeginMenu("Load Scene..."))
			{
				if (_projectManager.HasActiveProject)
				{
					var sceneManager = SceneManager.Instance;
					var sceneNames = sceneManager.GetAllSceneNames();
					
					if (sceneNames.Count > 0)
					{
						ImGui.TextColored(new Vector4(0.7f, 1.0f, 0.7f, 1.0f), "Project Scenes:");
						ImGui.Separator();
						
						foreach (var sceneName in sceneNames)
						{
							if (Gui.MenuItem(sceneName))
							{
								RequestSceneChange(sceneName);
							}
						}

					}
					else
					{
						ImGui.TextDisabled("No scenes found");
					}

				}
				else
				{
					ImGui.TextDisabled("No active project");
				}

				Gui.EndMenu();
			}

			ImGui.Separator();

			if (Gui.MenuItem("Save Scene", EditorHotkeys.MenuLabel(EditorHotkeys.SaveScene)))
			{
				if (Core.Scene == null)
				{
					Debug.Error("No active scene to save!");
					return;
				}

				if (!SceneManager.Instance.HasLoadedScene)
				{
					_newSceneNameForSave = "NewScene";
					_showCreateSceneForSavePrompt = true;
					Debug.Error("No Scene has been loaded yet!");
					return;
				}
				InvokeSaveSceneChanges();
			}

			ImGui.Separator();

			if (!_projectManager.HasActiveProject)
			{
				ImGui.BeginDisabled();
			}

			if (Gui.MenuItem("Settings"))
			{
				_projectSettingsWindow.IsOpen = true;
			}

			ImGui.Separator();

			if (_projectManager.HasActiveProject)
			{
				if (Gui.MenuItem("Close Project"))
				{
					RequestProjectClose();
				}
			}

			if (!_projectManager.HasActiveProject)
			{
				ImGui.EndDisabled();
			}

			Plugins.EditorPluginHost.DrawMenuItems(Plugins.EditorMenu.Project);

			Gui.EndMenu();
		}
	}

	private void DrawViewMenu()
	{
		if (Gui.BeginMenu("View"))
		{
			if (Gui.BeginMenu("Themes"))
			{
				ImGuiSafe.TextColoredSafe(new Vector4(0.5f, 0.8f, 1.0f, 1.0f),
					$"Current: {_themeManager.CurrentThemeName}");
				ImGui.Separator();

				foreach (var themeName in _themeManager.GetAvailableThemes())
				{
					bool isCurrentTheme = themeName.Equals(_themeManager.CurrentThemeName, 
						StringComparison.OrdinalIgnoreCase);

					if (Gui.MenuItem(themeName, "", isCurrentTheme))
					{
						if (_themeManager.ApplyTheme(themeName))
						{
							EditorDebug.Log($"Theme changed to: {themeName}");
						}
					}
				}

				Gui.EndMenu();
			}

			if (Gui.BeginMenu("Layout"))
			{
				ImGuiSafe.TextColoredSafe(new Vector4(0.5f, 0.8f, 1.0f, 1.0f),
					$"Current: {_layoutManager.CurrentLayoutName}");
				ImGui.Separator();

				if (Gui.MenuItem("Save Layout As..."))
				{
					_newLayoutName = "";
					_showSaveLayoutPopup = true;
				}

				ImGui.Separator();

				if (Gui.BeginMenu("Load Layout"))
				{
					ImGui.TextDisabled("Right-click a custom layout to rename or delete it.");
					ImGui.Separator();

					foreach (var layoutName in _layoutManager.GetLayoutNames())
					{
						bool isCurrentLayout = layoutName.Equals(_layoutManager.CurrentLayoutName,
							StringComparison.OrdinalIgnoreCase);
						bool isDefaultLayout = layoutName.Equals("Default", StringComparison.OrdinalIgnoreCase);

						if (Gui.MenuItem(layoutName, "", isCurrentLayout))
						{
							_layoutManager.LoadLayout(layoutName);
							_lastSelectedLayout.Value = layoutName;
							Debug.Log($"Loaded layout: {layoutName}");

							if (!layoutName.Equals("Default", StringComparison.OrdinalIgnoreCase))
							{
								EditorDebug.Log(
									$"Layout '{layoutName}' loaded. Some windows may require restart for full effect.");
							}
						}

						if (!isDefaultLayout && Gui.BeginPopupContextItem($"layout-ctx-{layoutName}"))
						{
							if (Gui.MenuItem("Rename"))
							{
								_layoutPendingRename = layoutName;
								_renameLayoutName = layoutName;
								_showRenameLayoutPopup = true;
							}

							if (Gui.MenuItem("Delete"))
							{
								_layoutPendingDelete = layoutName;
								_showDeleteLayoutPopup = true;
							}

							Gui.EndPopup();
						}
					}

					Gui.EndMenu();
				}

				if (Gui.BeginMenu("Rename Layout"))
				{
					var renameable = 0;

					foreach (var layoutName in _layoutManager.GetLayoutNames())
					{
						if (layoutName.Equals("Default", StringComparison.OrdinalIgnoreCase))
							continue;

						renameable++;

						if (Gui.MenuItem(layoutName))
						{
							_layoutPendingRename = layoutName;
							_renameLayoutName = layoutName;
							_showRenameLayoutPopup = true;
						}
					}

					if (renameable == 0)
						ImGui.TextDisabled("No custom layouts yet");

					Gui.EndMenu();
				}

				if (Gui.BeginMenu("Delete Layout"))
				{
					var layoutNames = new List<string>(_layoutManager.GetLayoutNames());
					string layoutToDelete = null;

					foreach (var layoutName in layoutNames)
					{
						if (layoutName != "Default" && Gui.MenuItem(layoutName))
						{
							layoutToDelete = layoutName;
							break;
						}
					}

					// Delete outside of the enumeration
					if (layoutToDelete != null)
						DeleteLayout(layoutToDelete);

					Gui.EndMenu();
				}

				ImGui.Separator();

				// Show different text depending on current layout
				string resetText =
					_layoutManager.CurrentLayoutName.Equals("Default", StringComparison.OrdinalIgnoreCase)
						? "Reset to Default"
						: "Switch to Default";

				if (Gui.MenuItem(resetText))
				{
					_layoutManager.LoadLayout("Default");
					_lastSelectedLayout.Value = "Default";
				}

				Gui.EndMenu();
			}

			if (Gui.BeginMenu("Window"))
			{
				var showMainInspector = ShowMainInspectorWindow;
				Gui.MenuItem("Inspector Window", null, ref showMainInspector);
				ShowMainInspectorWindow = showMainInspector;

				if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
				{
					ImGui.OpenPopup("MainInspectorContextMenu");
				}

				var showCoreWindow = ShowCoreWindow;
				Gui.MenuItem("Core Window", null, ref showCoreWindow);
				ShowCoreWindow = showCoreWindow;

				if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
				{
					ImGui.OpenPopup("CoreWindowContextMenu");
				}

				var showSceneGraphWindow = ShowSceneGraphWindow;
				Gui.MenuItem("Scene Graph Window", null, ref showSceneGraphWindow);
				ShowSceneGraphWindow = showSceneGraphWindow;

				if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
				{
					ImGui.OpenPopup("SceneGraphWindowContextMenu");
				}

				var showSeparateGameWindow = ShowSeparateGameWindow;
				Gui.MenuItem("Separate Game Window", null, ref showSeparateGameWindow);
				ShowSeparateGameWindow = showSeparateGameWindow;

				if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
				{
					ImGui.OpenPopup("SeparateGameWindowContextMenu");
				}

				var showAnimationEventInspector = ShowAnimationEventInspector;
				Gui.MenuItem("Animation Event Inspector", null, ref showAnimationEventInspector);
				ShowAnimationEventInspector = showAnimationEventInspector;

				var showAssetBrowser = ShowAssetBrowser;
				Gui.MenuItem("Asset Browser", null, ref showAssetBrowser);
				ShowAssetBrowser = showAssetBrowser;

				var showTimeline = ShowTimelineWindow;
				Gui.MenuItem("Timeline Editor", null, ref showTimeline);
				ShowTimelineWindow = showTimeline;

				var showTilePalette = ShowTilePaletteWindow;
				Gui.MenuItem("Tile Palette", null, ref showTilePalette);
				ShowTilePaletteWindow = showTilePalette;

				if (Gui.MenuItem("Tileset Editor"))
					TilesetEditorWindow.IsOpen = true;

				if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
				{
					ImGui.OpenPopup("AnimationEventInspectorContextMenu");
				}

				ImGui.Separator();

				var preserveAspectRatio = PreserveGameWindowAspectRatio;
				Gui.MenuItem("Preserve Game Window Aspect Ratio", null, ref preserveAspectRatio);
				PreserveGameWindowAspectRatio = preserveAspectRatio;

				Gui.EndMenu();
			}

			Plugins.EditorPluginHost.DrawMenuItems(Plugins.EditorMenu.View);

			Gui.EndMenu();
		}

		DrawSaveLayoutPopup();
		DrawRenameLayoutPopup();
		DrawDeleteLayoutPopup();
	}

	/// <summary>Renames the layout picked in View > Layout, moving its .ini file to match.</summary>
	private void DrawRenameLayoutPopup()
	{
		if (_showRenameLayoutPopup)
		{
			ImGui.OpenPopup("RenameLayoutPopup");
			_showRenameLayoutPopup = false;
		}

		var center = new Vector2(Screen.Width * 0.5f, Screen.Height * 0.4f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
		ImGui.SetNextWindowSize(new Vector2(400, 0), ImGuiCond.Appearing);

		bool open = true;
		if (!Gui.BeginPopupModal("RenameLayoutPopup", ref open, ImGuiWindowFlags.AlwaysAutoResize))
		{
			if (!open)
				_layoutPendingRename = null;

			return;
		}

		ImGuiSafe.TextWrappedSafe($"Rename layout '{_layoutPendingRename}'");
		ImGui.Separator();

		ImGui.Text("New name:");
		ImGui.SetNextItemWidth(350);
		Gui.InputText("##renamelayout", ref _renameLayoutName, 50);

		var trimmed = _renameLayoutName.Trim();
		var unchanged = trimmed.Equals(_layoutPendingRename, StringComparison.Ordinal);

		// Same name in a different case is a valid rename, so only a different layout is a clash.
		var clashes = !unchanged &&
		              _layoutManager.GetLayoutNames().Contains(trimmed, StringComparer.OrdinalIgnoreCase) &&
		              !trimmed.Equals(_layoutPendingRename, StringComparison.OrdinalIgnoreCase);

		if (clashes)
		{
			ImGuiSafe.TextColoredSafe(new Vector4(1.0f, 0.4f, 0.4f, 1.0f),
				$"A layout named '{trimmed}' already exists.");
		}

		VoltageEditorUtils.MediumVerticalSpace();

		var canRename = trimmed.Length > 0 && !clashes && !unchanged;

		if (!canRename)
			ImGui.BeginDisabled();

		if (Gui.Button("Rename", new Vector2(100, 0)) && canRename)
		{
			RenameLayout(_layoutPendingRename, trimmed);
			_layoutPendingRename = null;
			ImGui.CloseCurrentPopup();
		}

		if (!canRename)
			ImGui.EndDisabled();

		ImGui.SameLine();

		if (Gui.Button("Cancel", new Vector2(100, 0)))
		{
			_layoutPendingRename = null;
			ImGui.CloseCurrentPopup();
		}

		Gui.EndPopup();
	}

	/// <summary>Renames a layout, keeping the "last selected layout" setting pointing at it.</summary>
	private void RenameLayout(string oldName, string newName)
	{
		if (string.IsNullOrEmpty(oldName) || !_layoutManager.RenameLayout(oldName, newName))
			return;

		if (_lastSelectedLayout.Value.Equals(oldName, StringComparison.OrdinalIgnoreCase))
			_lastSelectedLayout.Value = newName;
	}

	/// <summary>Confirms deleting the layout right-clicked in View > Layout > Load Layout.</summary>
	private void DrawDeleteLayoutPopup()
	{
		if (_showDeleteLayoutPopup)
		{
			ImGui.OpenPopup("DeleteLayoutPopup");
			_showDeleteLayoutPopup = false;
		}

		var center = new Vector2(Screen.Width * 0.5f, Screen.Height * 0.4f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

		bool open = true;
		if (!Gui.BeginPopupModal("DeleteLayoutPopup", ref open, ImGuiWindowFlags.AlwaysAutoResize))
		{
			if (!open)
				_layoutPendingDelete = null;

			return;
		}

		ImGuiSafe.TextWrappedSafe($"Delete the layout '{_layoutPendingDelete}'?");
		ImGui.TextDisabled("This removes its .ini file. It cannot be undone.");

		VoltageEditorUtils.MediumVerticalSpace();

		if (Gui.Button("Delete", new Vector2(100, 0)))
		{
			DeleteLayout(_layoutPendingDelete);
			_layoutPendingDelete = null;
			ImGui.CloseCurrentPopup();
		}

		ImGui.SameLine();

		if (Gui.Button("Cancel", new Vector2(100, 0)))
		{
			_layoutPendingDelete = null;
			ImGui.CloseCurrentPopup();
		}

		Gui.EndPopup();
	}

	/// <summary>Deletes a saved layout, falling back to Default when it was the active one.</summary>
	private void DeleteLayout(string layoutName)
	{
		if (string.IsNullOrEmpty(layoutName))
			return;

		if (layoutName.Equals(_layoutManager.CurrentLayoutName, StringComparison.OrdinalIgnoreCase))
		{
			_lastSelectedLayout.Value = "Default";
			_layoutManager.LoadLayout("Default");
		}

		_layoutManager.DeleteLayout(layoutName);
		_layoutManager.RefreshLayoutList();
	}

	private void DrawSaveLayoutPopup()
	{
		if (_showSaveLayoutPopup)
		{
			ImGui.OpenPopup("SaveLayoutPopup");
			_showSaveLayoutPopup = false;
		}

		var center = new Vector2(Screen.Width * 0.5f, Screen.Height * 0.4f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
		ImGui.SetNextWindowSize(new Vector2(400, 0), ImGuiCond.Appearing);

		bool open = true;
		if (Gui.BeginPopupModal("SaveLayoutPopup", ref open, ImGuiWindowFlags.AlwaysAutoResize))
		{
			ImGui.Text("Save Layout");
			ImGui.Separator();

			ImGui.Text("Enter layout name:");
			ImGui.SetNextItemWidth(350);
			Gui.InputText("##layoutname", ref _newLayoutName, 50);

			bool layoutExists = _layoutManager.GetLayoutNames().Contains(_newLayoutName.Trim());
			if (!string.IsNullOrWhiteSpace(_newLayoutName) && layoutExists)
			{
				ImGuiSafe.TextColoredSafe(new Vector4(1.0f, 0.6f, 0.2f, 1.0f),
					$"Warning: Layout '{_newLayoutName.Trim()}' already exists and will be overwritten!");
			}

			VoltageEditorUtils.MediumVerticalSpace();

			var buttonWidth = 100f;
			var spacing = 10f;
			var totalButtonWidth = (buttonWidth * 2) + spacing;
			var windowWidth = ImGui.GetWindowSize().X;
			var centerStart = (windowWidth - totalButtonWidth) * 0.5f;

			ImGui.SetCursorPosX(centerStart);

			bool canSave = !string.IsNullOrWhiteSpace(_newLayoutName);
			if (!canSave)
				ImGui.BeginDisabled();

			if (Gui.Button("Save", new Vector2(buttonWidth, 0)))
			{
				if (canSave)
				{
					string layoutName = _newLayoutName.Trim();
					_layoutManager.SaveLayout(layoutName);
					_layoutManager.RefreshLayoutList();

					// Update the last selected layout to the newly saved one
					_lastSelectedLayout.Value = layoutName;

					Debug.Success($"Layout saved: {layoutName}");

					_newLayoutName = "";
					ImGui.CloseCurrentPopup();
				}
			}

			if (!canSave)
				ImGui.EndDisabled();

			ImGui.SameLine();

			if (Gui.Button("Cancel", new Vector2(buttonWidth, 0)))
			{
				_newLayoutName = "";
				ImGui.CloseCurrentPopup();
			}

			Gui.EndPopup();
		}
	}

	private void DrawEngineEffectsPrompt()
	{
		ImGui.OpenPopup("Missing Engine Effects");

		var center = new Vector2(Screen.Width * 0.5f, Screen.Height * 0.4f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
		ImGui.SetNextWindowSize(new Vector2(500, 0), ImGuiCond.Appearing);

		bool open = true;
		if (Gui.BeginPopupModal("Missing Engine Effects", ref open, ImGuiWindowFlags.AlwaysAutoResize))
		{
			ImGui.PushTextWrapPos(480);
			ImGui.TextWrapped(
				"Warning: No compiled default engine effects were found in the Editor's 'Content/Voltage/Effects' directory.");
			ImGui.PopTextWrapPos();

			ImGui.Spacing();
			ImGui.TextWrapped("Would you like to compile them now?");

			VoltageEditorUtils.SmallVerticalSpace();

			// "Don't show again": when checked and the user dismisses with "No", the prompt is suppressed
			// permanently. Handy on Linux where compiling the effects fails.
			Gui.Checkbox("Don't show again", ref _engineEffectsDontShowAgainChecked);
			if (ImGui.IsItemHovered())
				ImGuiSafe.SetTooltipSafe("If checked and you press \"No\", this prompt will not appear again.\n" +
				                         "You can re-enable it later from the Effects menu.");

			VoltageEditorUtils.MediumVerticalSpace();

			var buttonWidth = 120f;
			var spacing = 10f;
			var totalButtonWidth = (buttonWidth * 2) + spacing;
			var windowWidth = ImGui.GetWindowSize().X;
			var centerStart = (windowWidth - totalButtonWidth) * 0.5f;

		ImGui.SetCursorPosX(centerStart);

		if (Gui.Button("Yes", new Vector2(buttonWidth, 0)))
		{
			EffectsCompiler.BuildEditorEngineEffects(_effectsCompileProgressWindow, ref _effectBuildCancelToken);
			_showEngineEffectsPrompt = false;
			_engineEffectsCheckComplete = true;
			ImGui.CloseCurrentPopup();
		}

			ImGui.SameLine();

			if (Gui.Button("No", new Vector2(buttonWidth, 0)))
			{
				// Persist the suppression only when dismissing with "No" (per the user's request).
				if (_engineEffectsDontShowAgainChecked)
					_dontShowEngineEffectsPrompt.Value = true;

				_showEngineEffectsPrompt = false;
				_engineEffectsCheckComplete = true;
				ImGui.CloseCurrentPopup();
			}

			Gui.EndPopup();
		}

		if (!open)
		{
			_showEngineEffectsPrompt = false;
			_engineEffectsCheckComplete = true;
		}
	}

	/// <summary>
	/// Top-level "Profiler" menu: toggles for the live profiling windows (audio DSP load, runtime
	/// performance). Each item is a checkable toggle backed by a persistent bool, mirroring the View menu.
	/// </summary>
	private void DrawProfilerMenu()
	{
		if (Gui.BeginMenu("Profiler"))
		{
			var showAudioProfiler = ShowAudioProfilerWindow;
			Gui.MenuItem("Audio Profiler", null, ref showAudioProfiler);
			ShowAudioProfilerWindow = showAudioProfiler;

			var showPerformanceProfiler = ShowPerformanceProfilerWindow;
			Gui.MenuItem("Performance Profiler", null, ref showPerformanceProfiler);
			ShowPerformanceProfilerWindow = showPerformanceProfiler;

			Gui.EndMenu();
		}
	}

	private void DrawScriptingMenu()
	{
		if (Gui.BeginMenu("Scripting"))
		{
			if (!_projectManager.HasActiveProject)
			{
				ImGui.TextDisabled("No active project");
				ImGui.Separator();
			}
			else
			{
				ImGuiSafe.TextColoredSafe(new Vector4(0.7f, 1.0f, 0.7f, 1.0f),
					$"Project: {_projectManager.CurrentProject.ProjectName}");
				ImGui.Separator();
			}

			if (Gui.MenuItem("Scripting Window", "", _showScriptingWindow))
			{
				_showScriptingWindow = !_showScriptingWindow;
			}

			ImGui.Separator();

			bool hasProject = _projectManager.HasActiveProject;
			bool hasScriptManager = _scriptManager != null;

			if (!hasProject)
			{
				ImGui.BeginDisabled();
			}

			if (Gui.MenuItem("Compile Scripts"))
			{
				if (hasScriptManager)
				{
					_scriptManager.CompileScripts(EditorSettingsWindow.AutoReloadSceneAfterScriptCompile);
				}
				else
				{
					EditorDebug.Log("Script manager not initialized. Open Scripting window to initialize.");
				}
			}

			if (Gui.MenuItem("Compile & Reload Scene"))
			{
				if (hasScriptManager)
				{
					_scriptManager.CompileScripts(reloadSceneOnSuccess: true);
				}
				else
				{
					EditorDebug.Log("Script manager not initialized.");
				}
			}

			if (Gui.MenuItem("Reload Scene", EditorHotkeys.MenuLabel(EditorHotkeys.ReloadScene)))
			{
				if (hasScriptManager)
				{
					_scriptManager.ReloadScene();
				}
				else
				{
					EditorDebug.Log("Script manager not initialized.");
				}
			}

			ImGui.Separator();

			if (Gui.MenuItem("Open Scripts Folder"))
			{
				var scriptsPath = _projectManager.CurrentProject.ScriptsFolder;
				if (Directory.Exists(scriptsPath))
				{
					System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
					{
						FileName = scriptsPath,
						UseShellExecute = true
					});
				}
				else
				{
					EditorDebug.Log($"Scripts folder not found: {scriptsPath}");
				}
			}

			if (hasScriptManager)
			{
				bool enableHotReload = _scriptManager.EnableHotReload;
				if (Gui.MenuItem("Enable Hot Reload", "", enableHotReload))
				{
					_scriptManager.EnableHotReload = !enableHotReload;
				}
			}
			else
			{
				Gui.MenuItem("Enable Hot Reload", "", false);
			}

			if (!hasProject)
			{
				ImGui.EndDisabled();
			}

			Plugins.EditorPluginHost.DrawMenuItems(Plugins.EditorMenu.Scripting);

			Gui.EndMenu();
		}
	}

	/// <summary>
	/// "Snap to Grid" toggle in the Editor Tools bar, positioned just left of the audio toggle. When snapping
	/// is on, a "Show Grid" toggle appears next to it to draw the placement grid in the game view.
	/// </summary>
	private void DrawGridControlsRightAligned()
	{
		var style = ImGui.GetStyle();
		float framePad = style.FramePadding.X * 2f;
		float spacing = style.ItemSpacing.X;

		bool snap = EditorSettingsWindow.SnapToGrid;

		const string snapLabel = "Snap to Grid";
		const string showLabel = "Show Grid";

		float snapWidth = ImGui.CalcTextSize(snapLabel).X + framePad;
		float showWidth = ImGui.CalcTextSize(showLabel).X + framePad + spacing;
		float backgroundWidth = ImGui.CalcTextSize(BackgroundLabel).X + spacing + BackgroundSwatchWidth + spacing;

		// Reserve the audio toggle's footprint on the right (mirrors DrawAudioToggleRightAligned).
		float audioIcon = 24f * FontSizeMultiplier;
		float audioReserved = audioIcon + style.FramePadding.X * 2f + 8f;
		float gap = spacing;

		float posX = ImGui.GetWindowWidth() - audioReserved - gap - (backgroundWidth + snapWidth + showWidth);
		if (posX > ImGui.GetCursorPosX())
			ImGui.SetCursorPosX(posX);

		DrawBackgroundColorControl();
		ImGui.SameLine();

		DrawToggleButton(snapLabel, snap, () => EditorSettingsWindow.SnapToGrid = !snap);
		if (ImGui.IsItemHovered())
			ImGui.SetTooltip("Snap dragged/dropped entities to the grid.\nHold Ctrl while dragging to invert.");

		ImGui.SameLine();
		bool show = ShowPlacementGrid;
		DrawToggleButton(showLabel, show, () => ShowPlacementGrid = !show);
		if (ImGui.IsItemHovered())
			ImGui.SetTooltip("Draw the placement grid in the game view.\nThe Tileset Editor's tile grid overrides it while that window is open.");
	}

	private const string BackgroundLabel = "Background";

	internal static float BackgroundSwatchWidth => ImGui.GetFrameHeight() * 1.6f;

	/// <summary>Labelled swatch of the game view clear colour. Clicking picks between the project colour and a temporary editor-only one.</summary>
	private void DrawBackgroundColorControl()
	{
		var active = ToVector4(EditorBackgroundColor.Active);
		var temporary = EditorBackgroundColor.UseTemporary;

		ImGui.AlignTextToFramePadding();
		ImGui.TextUnformatted(BackgroundLabel);
		ImGui.SameLine();

		if (VoltageEditorUtils.ColorSwatchButton("##BackgroundColor", active, temporary, BackgroundSwatchWidth))
			ImGui.OpenPopup("background-color-popup");

		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip(temporary
				? "Background Color - temporary (this editor only, not saved to the project)"
				: "Background Color - project (saved, used in builds)");
		}

		DrawBackgroundColorPopup();

		// Persisted here rather than off the picker's deactivation, so a colour typed into its
		// nested popup is saved too.
		if (!ImGui.IsAnyMouseDown())
			EditorBackgroundColor.FlushTemporary();
	}

	private void DrawBackgroundColorPopup()
	{
		if (!Gui.BeginPopup("background-color-popup"))
			return;

		ImGui.TextUnformatted("Background Color");
		ImGui.Separator();

		var useTemporary = EditorBackgroundColor.UseTemporary;

		if (Gui.RadioButton("Project color", !useTemporary))
			EditorBackgroundColor.UseTemporary = false;

		ImGui.Indent();
		VoltageEditorUtils.ColorSwatchButton("##BackgroundProjectColor",
			ToVector4(EditorBackgroundColor.Project), false, BackgroundSwatchWidth);

		ImGui.SameLine();
		ImGui.TextDisabled("Saved in Project Settings, used by the build.");

		if (_projectManager.HasActiveProject && Gui.SmallButton("Edit in Project Settings"))
		{
			_projectSettingsWindow.IsOpen = true;
			ImGui.CloseCurrentPopup();
		}

		ImGui.Unindent();
		VoltageEditorUtils.SmallVerticalSpace();

		if (Gui.RadioButton("Temporary color", useTemporary))
			EditorBackgroundColor.UseTemporary = true;

		ImGui.Indent();

		var temporary = ToVector4(EditorBackgroundColor.Temporary);
		ImGui.SetNextItemWidth(220f);
		if (Gui.ColorEdit4("##BackgroundTemporaryColor", ref temporary, ImGuiColorEditFlags.NoAlpha))
			EditorBackgroundColor.Temporary = ToXnaColor(temporary);

		ImGui.TextDisabled("Kept in this editor's settings, for every scene you open.");
		ImGui.TextDisabled("Never written to ProjectSettings.json, a scene file or a build.");
		ImGui.Unindent();

		Gui.EndPopup();
	}

	private static Vector4 ToVector4(Microsoft.Xna.Framework.Color color) =>
		new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);

	private static Microsoft.Xna.Framework.Color ToXnaColor(Vector4 color) =>
		new Microsoft.Xna.Framework.Color(color.X, color.Y, color.Z, color.W);

	// Small button that tints blue when active, mirroring the Editor Tools bar toggle style.
	private static void DrawToggleButton(string label, bool active, Action onClick)
	{
		if (active)
		{
			ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 1f, 1f));
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.6f, 1f, 1f));
			ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.4f, 0.7f, 1f, 1f));
		}

		if (Gui.Button(label))
			onClick();

		if (active)
			ImGui.PopStyleColor(3);
	}


	private void DrawEffectsMenu()
	{
		if (Gui.BeginMenu("Effects"))
		{
			bool hasProject = _projectManager.HasActiveProject;

			if (!hasProject)
			{
				ImGui.BeginDisabled();
			}

			if (hasProject)
			{
				var projectName = _projectManager.CurrentProject.ProjectName;

				if (Gui.MenuItem($"Compile \"{projectName}\" Effects"))
				{
					EffectsCompiler.BuildEditorProjectEffects(_projectManager, _effectsCompileProgressWindow, ref _effectBuildCancelToken);
				}
			}
			else
			{
				if (Gui.MenuItem("Compile Project Effects"))
				{
					EditorDebug.Log("No active project loaded!");
				}
			}

			if (!hasProject)
			{
				ImGui.EndDisabled();
			}

			if (Gui.MenuItem("Compile Engine Effects"))
			{
				EffectsCompiler.BuildEditorEngineEffects(_effectsCompileProgressWindow, ref _effectBuildCancelToken);
			}

			ImGui.Separator();

			if (!hasProject)
			{
				ImGui.BeginDisabled();
			}

			if (Gui.MenuItem("Compile ALL Effects"))
			{
				EffectsCompiler.BuildEditorAllEffects(_projectManager, _effectsCompileProgressWindow, ref _effectBuildCancelToken);
			}

			if (!hasProject)
			{
				ImGui.EndDisabled();
			}

			// Let the user re-enable the "Missing Engine Effects" startup prompt if they previously
			// dismissed it with "Don't show again".
			if (_dontShowEngineEffectsPrompt.Value)
			{
				ImGui.Separator();
				if (Gui.MenuItem("Re-enable \"Missing Effects\" startup prompt"))
				{
					_dontShowEngineEffectsPrompt.Value = false;
				}
				if (ImGui.IsItemHovered())
					ImGuiSafe.SetTooltipSafe("The startup warning about missing compiled engine effects is " +
					                         "currently hidden. Click to show it again on the next launch.");
			}

			Plugins.EditorPluginHost.DrawMenuItems(Plugins.EditorMenu.Effects);

			Gui.EndMenu();
		}
	}

	private void DrawBuildMenu()
	{
		if (Gui.BeginMenu("Build"))
		{
			bool hasProject = _projectManager.HasActiveProject;

			if (!hasProject)
			{
				ImGui.BeginDisabled();
			}

			if (Gui.MenuItem("Build Game"))
			{
				_gameBuildWindow.OpenBuildPopup();
			}

			bool isBuildInProgress = _gameBuildWindow.IsBuilding;
			if (isBuildInProgress)
			{
				ImGui.BeginDisabled();
			}

			if (Gui.MenuItem("Build and Run", EditorHotkeys.MenuLabel(EditorHotkeys.BuildAndRun)))
			{
				_gameBuildWindow.BuildAndRun();
			}

			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				if (isBuildInProgress)
					ImGui.SetTooltip("A build is already in progress.");
				else if (!hasProject)
					ImGui.SetTooltip("No active project.");
				else
					ImGui.SetTooltip("Builds with the last selected options and launches the game executable.");
			}

			if (isBuildInProgress)
			{
				ImGui.EndDisabled();
			}

			DrawAssetBuildMenu(hasProject);

			if (!hasProject)
			{
				ImGui.EndDisabled();
			}

			Plugins.EditorPluginHost.DrawMenuItems(Plugins.EditorMenu.Build);

			Gui.EndMenu();
		}
	}

	private void DrawAssetBuildMenu(bool hasProject)
	{
		if (!Gui.BeginMenu("Asset Build"))
			return;

		var settings = Builders.AssetBuildSettingsStore.Get();
		var enabled = settings.Enabled;
		if (Gui.MenuItem("Compile assets on build", "", ref enabled, hasProject))
		{
			settings.Enabled = enabled;
			try { Builders.AssetBuildSettingsStore.Save(_projectManager.CurrentProject); }
			catch (System.Exception ex) { Debug.Error($"Failed to save the asset build setting: {ex.Message}"); }
		}

		var busy = Builders.AssetBuildService.IsRunning;
		if (Gui.MenuItem("Build assets now", "", false, hasProject && !busy))
			_assetBuildReportWindow.BuildNow();
		if (Gui.MenuItem("Clean asset build", "", false, hasProject && !busy))
			Builders.AssetBuildService.Clean(_projectManager.CurrentProject, settings.Platform);
		if (Gui.MenuItem("Settings", "", false, hasProject))
			_assetBuildSettingsWindow.Open();
		if (Gui.MenuItem("New pipeline extension", "", false, hasProject))
			_assetBuildSettingsWindow.OpenScaffold();
		if (Gui.MenuItem("Show last report", "", false, Builders.AssetBuildService.LastReport != null))
			_assetBuildReportWindow.Show();

		var output = hasProject ? Builders.AssetBuildSettingsStore.DefaultOutputDirectory(_projectManager.CurrentProject, settings.Platform) : null;
		if (Gui.MenuItem("Open output folder", "", false, output != null && System.IO.Directory.Exists(output)))
		{
			try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(output) { UseShellExecute = true }); }
			catch (System.Exception ex) { Debug.Error($"Could not open {output}: {ex.Message}"); }
		}

		Gui.EndMenu();
	}

	private void DrawPluginsMenu()
	{
		var hasProblems = Plugins.PluginManager.Instance.HasProblems;
		var label = hasProblems ? "Plugins (!)" : "Plugins";

		if (Gui.BeginMenu(label))
		{
			if (Gui.MenuItem("Plugin Manager"))
			{
				_pluginManagerWindow.IsOpen = true;
			}

			if (ImGui.IsItemHovered() && hasProblems)
				ImGui.SetTooltip("Some plugins are unavailable or failed to load.");

			// Also the way to ask for it after pulling a branch that changed the plugin set.
			var missing = Plugins.PluginRestorePrompt.MissingCount;

			ImGui.BeginDisabled(missing == 0);

			if (Gui.MenuItem(missing > 0 ? $"Restore Plugins ({missing})" : "Restore Plugins"))
				Plugins.PluginRestorePrompt.Show();

			ImGui.EndDisabled();

			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				ImGui.SetTooltip(missing > 0
					? $"{missing} plugin(s) this project declares are not on this machine. Fetch them from the\n" +
					  "source the project names."
					: "Every plugin this project declares is present.");
			}

			ImGui.Separator();

			// Entries registered by editor plugins via IEditorPluginContext.AddMenuItem.
			Plugins.EditorPluginHost.DrawMenuItems();

			Gui.EndMenu();
		}
	}

	private void DrawHelpMenu()
	{
		if (Gui.BeginMenu("Help"))
		{
			Gui.MenuItem("ImGui Demo Window", null, ref ShowDemoWindow);

			var showStyleEditor = ShowStyleEditor;
			Gui.MenuItem("Style Editor", null, ref showStyleEditor);
			ShowStyleEditor = showStyleEditor;

			if (Gui.MenuItem("Open imgui_demo.cpp on GitHub"))
			{
				var url = "https://github.com/ocornut/imgui/blob/master/imgui_demo.cpp";
				var startInfo = new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true };
				System.Diagnostics.Process.Start(startInfo);
			}

			Plugins.EditorPluginHost.DrawMenuItems(Plugins.EditorMenu.Help);

			Gui.EndMenu();
		}
	}

	/// <summary>
	/// Audio mute toggle — right-aligned inside the Editor Tools bar.
	/// Shows volume-up icon when <see cref="Core.IsAudioOn"/> is true, volume-mute when false.
	/// Clicking toggles <see cref="Core.IsAudioOn"/>.
	/// </summary>
	private void DrawAudioToggleRightAligned()
	{
		float iconSize    = 24f * FontSizeMultiplier;
		float framePad    = ImGui.GetStyle().FramePadding.X;
		float buttonWidth = iconSize + framePad * 2f;
		const float rightPadding = 8f;

		// Position the cursor so the button sits flush against the right edge.
		float posX = ImGui.GetWindowWidth() - buttonWidth - rightPadding;
		if (posX > ImGui.GetCursorPosX())
			ImGui.SetCursorPosX(posX);

		var iconSizeVec = new Vector2(iconSize, iconSize);
		bool audioOn = Core.IsAudioOn;

		IntPtr icon = audioOn
			? ImguiImageLoader.AudioOn
			: ImguiImageLoader.AudioMute;

		// Tint the button red-ish when muted so the state is immediately obvious.
		if (!audioOn)
		{
			ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.6f, 0.1f, 0.1f, 1.0f));
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.2f, 0.2f, 1.0f));
			ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.9f, 0.3f, 0.3f, 1.0f));
		}

		if (Gui.ImageButton("AudioToggle", icon, iconSizeVec))
			Core.IsAudioOn = !Core.IsAudioOn;

		if (!audioOn)
			ImGui.PopStyleColor(3);

		if (ImGui.IsItemHovered())
			ImGuiSafe.SetTooltipSafe(Core.IsAudioOn ? "Audio: ON (click to mute)" : "Audio: MUTED (click to unmute)");
	}

	/// <summary>
	/// Play / Stop / Pause / Reset icon-button cluster — drawn inside the Editor Tools bar.
	///
	/// Centering math:
	///   Preferred center: game-window center X = _gameImageScreenPos.X + _gameImageSize.X * 0.5f
	///   Fall-back center: tools-bar center = ImGui.GetWindowWidth() * 0.5f
	///   cursor X = center - (totalClusterWidth * 0.5f)
	///
	/// Because the audio toggle is right-aligned AFTER this call, the cluster's
	/// SetCursorPosX is applied before the audio button takes the right side.
	/// </summary>
	private void DrawEditorModeControls()
	{
		float iconSize    = 24f * FontSizeMultiplier;
		float buttonSpace = iconSize + ImGui.GetStyle().FramePadding.X * 2f;
		float spacing     = 4f * FontSizeMultiplier;

		float totalWidth  = buttonSpace * 3f + spacing * 2f;

		// Center the cluster within the Editor Tools bar.
		float centerX = ImGui.GetWindowWidth() * 0.5f;
		float cursorX = centerX - totalWidth * 0.5f;

		// Clamp so the cluster never goes off-screen on very small windows.
		cursorX = Math.Max(cursorX, ImGui.GetCursorPosX());

		ImGui.SetCursorPosX(cursorX);

		var iconSizeVec = new Vector2(iconSize, iconSize);

		if (Core.IsEditMode)
		{
			if (Gui.ImageButton("EditorPlay", ImguiImageLoader.EditorModePlay, iconSizeVec))
				Core.InvokeSwitchEditMode(false);

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip($"Play ({EditorHotkeys.Hint(EditorHotkeys.TogglePlay)})");
		}
		else
		{
			ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.6f, 0.1f, 0.1f, 0.9f));
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.2f, 0.2f, 1.0f));
			ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.9f, 0.3f, 0.3f, 1.0f));

			if (Gui.ImageButton("EditorPlay", ImguiImageLoader.EditorModeStop, iconSizeVec))
			{
				Core.IsPauseMode = false;
				Core.InvokeSwitchEditMode(true);
			}

			ImGui.PopStyleColor(3);

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip($"Stop ({EditorHotkeys.Hint(EditorHotkeys.TogglePlay)})");
		}

		ImGui.SameLine(0, spacing);

		if (Core.IsEditMode)
			ImGui.BeginDisabled();

		bool isPaused = Core.IsPauseMode;
		if (isPaused)
		{
			ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.2f, 0.5f, 1.0f, 0.9f));
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.6f, 1.0f, 1.0f));
			ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.4f, 0.7f, 1.0f, 1.0f));
		}

		if (Gui.ImageButton("EditorPause", ImguiImageLoader.EditorModePause, iconSizeVec))
			Core.InvokeSwitchPauseMode(!Core.IsPauseMode);

		if (isPaused)
			ImGui.PopStyleColor(3);

		if (Core.IsEditMode)
			ImGui.EndDisabled();

		if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			ImGuiSafe.SetTooltipSafe(Core.IsEditMode
				? "Pause (unavailable in Edit Mode)"
				: $"{(isPaused ? "Unpause" : "Pause")} ({EditorHotkeys.Hint(EditorHotkeys.TogglePause)})");

		ImGui.SameLine(0, spacing);

		if (Gui.ImageButton("EditorReset", ImguiImageLoader.EditorModeReset, iconSizeVec))
			Core.InvokeResetScene();

		if (ImGui.IsItemHovered())
			ImGui.SetTooltip($"Reset Scene ({EditorHotkeys.Hint(EditorHotkeys.ResetScene)})");

		// Keep cursor on the same line so DrawAudioToggleRightAligned can
		// use SetCursorPosX to jump to the right edge within the same row.
		ImGui.SameLine();
	}
}

