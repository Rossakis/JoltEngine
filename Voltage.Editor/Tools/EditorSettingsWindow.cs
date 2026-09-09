using ImGuiNET;
using Voltage.Editor.Persistence;
using Voltage.Editor.Scripting;
using Voltage.Editor.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.Tools
{
	/// <summary>
	/// Editor settings window for managing editor preferences
	/// </summary>
	public class EditorSettingsWindow
	{
		private bool _isOpen = false;

		// Project Creation
		private static PersistentBool _autoOpenSolutionUponCreation = new("EditorSettings_AutoOpenSolutionUponCreation", true);
		public static bool AutoOpenSolutionUponCreation
		{
			get => _autoOpenSolutionUponCreation.Value;
			set => _autoOpenSolutionUponCreation.Value = value;
		}

		// Scripts
		private static PersistentBool _autoCloseScriptProgress = new("EditorSettings_AutoCloseScriptProgress", true);
		public static bool AutoCloseScriptProgress
		{
			get => _autoCloseScriptProgress.Value;
			set => _autoCloseScriptProgress.Value = value;
		}

		private static PersistentBool _autoReloadSceneAfterScriptCompile= new("EditorSettings_AutoReloadSceneAfterScriptCompile", true);
		public static bool AutoReloadSceneAfterScriptCompile
		{
			get => _autoReloadSceneAfterScriptCompile.Value;
			set => _autoReloadSceneAfterScriptCompile.Value = value;
		}
		// Effects
		private static PersistentBool _autoCloseEffectsProgress = new("EditorSettings_AutoCloseEffectsProgress", true);
		public static bool AutoCloseEffectsProgress
		{
			get => _autoCloseEffectsProgress.Value;
			set => _autoCloseEffectsProgress.Value = value;
		}

		// Debug
		private static PersistentBool _disableDebugInPlayMode = new("EditorSettings_DisableDebugInPlayMode", true);
		public static bool DisableDebugInPlayMode
		{
			get => _disableDebugInPlayMode.Value;
			set => _disableDebugInPlayMode.Value = value;
		}

		// Placement snapping
		private static PersistentBool _snapToGrid = new("EditorSettings_SnapToGrid", false);
		public static bool SnapToGrid
		{
			get => _snapToGrid.Value;
			set => _snapToGrid.Value = value;
		}

		private static PersistentInt _snapGridSize = new("EditorSettings_SnapGridSize", 16);
		public static int SnapGridSize
		{
			get => System.Math.Max(1, _snapGridSize.Value);
			set => _snapGridSize.Value = System.Math.Max(1, value);
		}

		/// <summary>Rounds a world position to the snap grid when snapping is on. Holding Ctrl inverts the setting.</summary>
		public static Microsoft.Xna.Framework.Vector2 ApplyPlacementSnap(Microsoft.Xna.Framework.Vector2 position, bool ctrlHeld)
		{
			var snap = SnapToGrid ^ ctrlHeld;
			if (!snap)
				return position;

			var g = SnapGridSize;
			return new Microsoft.Xna.Framework.Vector2(
				(float)System.Math.Round(position.X / g) * g,
				(float)System.Math.Round(position.Y / g) * g);
		}


		public bool IsOpen
		{
			get => _isOpen;
			set => _isOpen = value;
		}
		
		public void Draw()
		{
			if (!_isOpen)
				return;
			
			ImGui.SetNextWindowSize(new Num.Vector2(600, 400), ImGuiCond.FirstUseEver);
			
			bool open = _isOpen;
			if (Gui.Begin("Editor Settings", ref open))
			{
				_isOpen = open;
				
				ImGui.TextColored(new Num.Vector4(0.2f, 0.8f, 1.0f, 1.0f), "Editor Settings");
				ImGui.Separator();
				
				VoltageEditorUtils.MediumVerticalSpace();

				// Project Creation tab
				if (Gui.CollapsingHeader("Project Creation", ImGuiTreeNodeFlags.DefaultOpen))
				{
					ImGui.Indent();
					VoltageEditorUtils.SmallVerticalSpace();

					bool autoOpenSolution = AutoOpenSolutionUponCreation;
					if (Gui.Checkbox("Open 'Visual Studio' solution file upon Project Creation##Editor", ref autoOpenSolution))
					{
						AutoOpenSolutionUponCreation = autoOpenSolution;
					}

					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip("Automatically open the generated 'Visual Studio' Solution file when new Project is created.");
					}

					VoltageEditorUtils.SmallVerticalSpace();
					ImGui.Unindent();
				}

				// Scripts tab
				if (Gui.CollapsingHeader("Scripts", ImGuiTreeNodeFlags.DefaultOpen))
				{
					ImGui.Indent();
					VoltageEditorUtils.SmallVerticalSpace();

					bool compileOnStartup = ScriptManager.CompileOnStartup;
					if (Gui.Checkbox("Compile Scripts on Startup", ref compileOnStartup))
					{
						ScriptManager.CompileOnStartup = compileOnStartup;
					}
					
					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip("Automatically compile all scripts when the editor starts (requires restart to take effect)");
					}
					
					VoltageEditorUtils.SmallVerticalSpace();
					
					bool autoCloseScriptProgress = AutoCloseScriptProgress;
					if (Gui.Checkbox("Close Progress Bar When Finished##Scripts", ref autoCloseScriptProgress))
					{
						AutoCloseScriptProgress = autoCloseScriptProgress;
					}
					
					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip("Automatically close the script compilation progress window after 2 seconds when compilation completes");
					}

					VoltageEditorUtils.SmallVerticalSpace();

					bool autoReloadScene = AutoReloadSceneAfterScriptCompile;
					if (Gui.Checkbox("Reload The Scene On Script Compilation End##Scripts", ref autoReloadScene))
					{
						AutoReloadSceneAfterScriptCompile = autoReloadScene;
					}

					VoltageEditorUtils.SmallVerticalSpace();
					ImGui.Unindent();
				}
				
				// Effects tab
				if (Gui.CollapsingHeader("Effects", ImGuiTreeNodeFlags.DefaultOpen))
				{
					ImGui.Indent();
					VoltageEditorUtils.SmallVerticalSpace();

					bool autoCloseEffectsProgress = AutoCloseEffectsProgress;
					if (Gui.Checkbox("Close Progress Bar When Finished##Effects", ref autoCloseEffectsProgress))
					{
						AutoCloseEffectsProgress = autoCloseEffectsProgress;
					}
					
					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip("Automatically close the effects compilation progress window after 2 seconds when compilation completes");
					}

					VoltageEditorUtils.SmallVerticalSpace();
					ImGui.Unindent();
				}

				// Debug
				if (Gui.CollapsingHeader("Debug", ImGuiTreeNodeFlags.DefaultOpen))
				{
					ImGui.Indent();
					VoltageEditorUtils.SmallVerticalSpace();

					bool disableDebugInPlayMode = DisableDebugInPlayMode;
					if (Gui.Checkbox("Disable Debug Drawing (Lines, Shapes, etc.) In Play Mode##Debug", ref disableDebugInPlayMode))
					{
						DisableDebugInPlayMode = disableDebugInPlayMode;
					}

					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip("Disable debug features while the game is in play mode.");
					}

					VoltageEditorUtils.SmallVerticalSpace();
					ImGui.Unindent();
				}

				if (Gui.CollapsingHeader("Placement Snapping", ImGuiTreeNodeFlags.DefaultOpen))
				{
					ImGui.Indent();
					VoltageEditorUtils.SmallVerticalSpace();

					bool snap = SnapToGrid;
					if (Gui.Checkbox("Snap dragged/dropped entities to a grid##Snap", ref snap))
						SnapToGrid = snap;

					if (ImGui.IsItemHovered())
						ImGui.SetTooltip("Rounds entity position to the grid when moving or dropping. Hold Ctrl while dragging to invert.");

					int gridSize = SnapGridSize;
					ImGui.SetNextItemWidth(160f);
					if (Gui.InputInt("Snap grid size (px)##Snap", ref gridSize))
						SnapGridSize = gridSize;

					VoltageEditorUtils.SmallVerticalSpace();
					ImGui.Unindent();
				}

				Hotkeys.HotkeySettingsSection.Draw();

				Gui.End();
			}
			
			if (!open)
			{
				_isOpen = false;
			}
		}
	}
}