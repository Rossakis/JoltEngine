using ImGuiNET;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Scripting;
using Voltage.Editor.Tools;
using Voltage.Editor.Utils;
using Voltage.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.ImGuiCore;

public partial class ImGuiManager
{
	private bool _showScriptingWindow = false;
	private string _compilationLog = "";

	private void InitializeScriptManager()
	{
		try
		{
			if (!_projectManager.HasActiveProject)
			{
				EditorDebug.Warn("No active project. Script manager will initialize when a project is loaded.", "ImGuiManager.Scripting");
				return;
			}

			string scriptsPath = _projectManager.CurrentProject.ScriptsFolder;
			
			_scriptManager = new ScriptManager(scriptsPath);
			_scriptManager.OnCompilationComplete += OnScriptCompilationComplete;
			_scriptManager.OnBeforeSceneReload += OnBeforeSceneReload;
			_scriptManager.OnAfterSceneReload += OnAfterSceneReload;

			EditorDebug.Log($"ScriptManager initialized successfully for project: {scriptsPath}", "ImGuiManager.Scripting");
		}
		catch (Exception ex)
		{
			EditorDebug.Error($"Failed to initialize ScriptManager: {ex.Message}", "ImGuiManager.Scripting");
		}
	}

	private void ReinitializeScriptManager()
	{
		_scriptManager?.Dispose();
		_scriptManager = null;
		InitializeScriptManager();
	}

	private void OnProjectLoaded(IGameProject project)
	{
		ReinitializeScriptManager();
		_assetBrowserWindow.OnProjectLoaded();
		EditorDebug.Log($"Project loaded: {project.ProjectName}");
	}

	private void OnProjectUnloaded()
	{
		_scriptManager?.Dispose();
		_scriptManager = null;
		_assetBrowserWindow.OnProjectUnloaded();
		EditorDebug.Log("Project unloaded");
	}

	private void OnScriptCompilationComplete(CompilationResult result, bool shouldReloadScene)
	{
		if (result.Success)
		{
			_compilationLog = $"[{DateTime.Now:HH:mm:ss}] [OK] Compilation successful\n" + _compilationLog;
			
			if (shouldReloadScene)
			{
				_compilationLog = $"[{DateTime.Now:HH:mm:ss}]   -> Scene will be reloaded\n" + _compilationLog;
			}
			else
			{
				_compilationLog = $"[{DateTime.Now:HH:mm:ss}]   -> Scene will NOT be reloaded\n" + _compilationLog;
			}
		}
		else
		{
			_compilationLog = $"[{DateTime.Now:HH:mm:ss}] [X] Compilation failed:\n";
			foreach (var error in result.Errors)
			{
				_compilationLog += $"  {error}\n";
			}
			_compilationLog += "\n" + _compilationLog;
		}

		var lines = _compilationLog.Split('\n');
		if (lines.Length > 100)
		{
			_compilationLog = string.Join('\n', lines.Take(100));
		}
	}

	private void OnBeforeSceneReload()
	{
		EditorDebug.Info("Preparing for scene reload...");
	}

	private void OnAfterSceneReload()
	{
		EditorDebug.Success("Scene reloaded successfully");
	}

	private void DrawScriptingWindow()
	{
		if (!_showScriptingWindow)
			return;

		ImGui.SetNextWindowSize(new Num.Vector2(600, 400), ImGuiCond.FirstUseEver);
		
		bool open = _showScriptingWindow;
		if (Gui.Begin("Scripting", ref open))
		{
			_showScriptingWindow = open;

			if (!_projectManager.HasActiveProject)
			{
				ImGui.TextColored(new Num.Vector4(1.0f, 0.6f, 0.2f, 1.0f), "No active project loaded.");
				ImGui.TextWrapped("Please create or load a project to use the scripting system.");
				Gui.End();
				return;
			}

			if (_scriptManager == null)
			{
				ImGui.TextColored(new Num.Vector4(1.0f, 0.6f, 0.2f, 1.0f), "Script manager not initialized.");
				
				if (Gui.Button("Initialize Script Manager"))
				{
					InitializeScriptManager();
				}
				
				Gui.End();
				return;
			}

			ImGui.TextColored(new Num.Vector4(0.2f, 0.8f, 1.0f, 1.0f), "Active Project");
			ImGui.Separator();
			ImGuiSafe.TextSafe($"Project: {_projectManager.CurrentProject.ProjectName}");
			ImGuiSafe.TextSafe($"Scripts Folder: {_projectManager.CurrentProject.ScriptsFolder}");
			
			VoltageEditorUtils.MediumVerticalSpace();

			ImGui.TextColored(new Num.Vector4(0.2f, 0.8f, 1.0f, 1.0f), "Hot Reload Settings");
			ImGui.Separator();

			bool enableHotReload = _scriptManager.EnableHotReload;
			if (Gui.Checkbox("Enable Hot Reload", ref enableHotReload))
			{
				_scriptManager.EnableHotReload = enableHotReload;
			}
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Automatically compile scripts when they are modified");
			}

			bool autoReloadScene = _scriptManager.AutoReloadSceneOnChange;
			if (Gui.Checkbox("Auto Reload Scene", ref autoReloadScene))
			{
				_scriptManager.AutoReloadSceneOnChange = autoReloadScene;
			}
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Automatically reload the scene after successful compilation (only in Edit Mode)");
			}

			VoltageEditorUtils.MediumVerticalSpace();

			ImGui.TextColored(new Num.Vector4(0.2f, 0.8f, 1.0f, 1.0f), "Manual Actions");
			ImGui.Separator();

			if (Gui.Button("Compile Scripts", new Num.Vector2(150, 0)))
			{
				_scriptManager.CompileScripts(EditorSettingsWindow.AutoReloadSceneAfterScriptCompile);
			}
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Compile scripts without reloading the scene");
			}
			
			ImGui.SameLine();

			if (Gui.Button("Compile & Reload", new Num.Vector2(150, 0)))
			{
				_scriptManager.CompileScripts(reloadSceneOnSuccess: true);
			}
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Compile scripts and reload the scene");
			}
			
			ImGui.SameLine();

			if (Gui.Button("Reload Scene", new Num.Vector2(150, 0)))
			{
				_scriptManager.ReloadScene();
			}
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Reload the scene without recompiling");
			}

			VoltageEditorUtils.SmallVerticalSpace();

			if (Gui.Button("Open Scripts Folder", new Num.Vector2(150, 0)))
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

			VoltageEditorUtils.MediumVerticalSpace();

			ImGui.TextColored(new Num.Vector4(0.2f, 0.8f, 1.0f, 1.0f), "Loaded Script Types");
			ImGui.Separator();

			var componentTypes = _scriptManager.GetScriptComponentTypes();
			var entityTypes = _scriptManager.GetScriptEntityTypes();

			ImGuiSafe.TextSafe("Components: " + componentTypes.Length);
			ImGuiSafe.TextSafe("Entities: " + entityTypes.Length);

			if (Gui.TreeNode("Component Types"))
			{
				foreach (var type in componentTypes)
				{
					ImGuiSafe.BulletTextSafe(type.Name);
				}
				ImGui.TreePop();
			}

			if (Gui.TreeNode("Entity Types"))
			{
				foreach (var type in entityTypes)
				{
					ImGuiSafe.BulletTextSafe(type.Name);
				}
				ImGui.TreePop();
			}

			VoltageEditorUtils.MediumVerticalSpace();

			ImGui.TextColored(new Num.Vector4(0.2f, 0.8f, 1.0f, 1.0f), "Compilation Log");
			ImGui.Separator();

			if (ImGui.BeginChild("CompilationLog", new Num.Vector2(0, 150), true))
			{
				ImGuiSafe.TextWrappedSafe(_compilationLog);
				ImGui.EndChild();
			}

			Gui.End();
		}
	}

}