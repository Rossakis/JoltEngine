using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImGuiNET;
using Voltage.Diagnostics;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.Diagnostics;
using Voltage.Editor.Persistence;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.SceneFile;
using Voltage.Editor.Utils;
using Voltage.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.Builders;

/// <summary>
/// ImGui window that provides build options and displays build progress for game export.
/// </summary>
public class GameBuildWindow
{
	private bool _showBuildPopup = false;
	private bool _isBuilding = false;

	// Persistent user preferences
	private readonly PersistentBool _debugBuild = new("GameBuild.DebugBuild", false);
	private readonly PersistentBool _linuxCompatContainer = new("GameBuild.LinuxCompatContainer", true);
	private readonly PersistentBool _nativeAot = new("GameBuild.NativeAot", true);
	private readonly PersistentInt _selectedPlatformIndex = new("GameBuild.SelectedPlatformIndex", 0);

	// When set, the advisory build-dependency check is skipped before a build (the user chose "Don't show
	// again"). The check is purely informational — the real publish still verifies the toolchain.
	private readonly PersistentBool _suppressBuildDepCheck = new("GameBuild.SuppressDepCheck", false);

	private CancellationTokenSource _buildCancellationToken;

	// Build-dependency preflight (clang / ld / zlib for NativeAOT). Shown BEFORE publish so the
	// user gets a clear, actionable dialog instead of the cryptic "cannot find -lz" linker error.
	private readonly DependencyPreflightDialog _depPreflightDialog = new();

	// Initial scene selection
	private List<string> _availableScenes = new();
	private int _selectedSceneIndex = 0;

	// Progress tracking
	private CompilationProgressWindow _progressWindow;
	private CompilationProgress _currentProgress;
	private int _completedSteps;

	/// <summary>True while a build is in progress.</summary>
	public bool IsBuilding => _isBuilding;

	public GameBuildWindow()
	{
		_progressWindow = new CompilationProgressWindow(isScriptProgress: false, onCancel: OnCancelRequested);

		GameBuilder.OnBuildStarted += OnBuildStarted;
		GameBuilder.OnBuildStepStarted += OnBuildStepStarted;
		GameBuilder.OnBuildStepCompleted += OnBuildStepCompleted;
		GameBuilder.OnBuildFinished += OnBuildFinished;
	}

	/// <summary>
	/// Opens the build options popup.
	/// </summary>
	public void OpenBuildPopup()
	{
		_showBuildPopup = true;
		RefreshAvailableScenes();
	}

	/// <summary>
	/// Builds the game using the last selected options and then launches the executable.
	/// Called from the menu bar "Build and Run" shortcut.
	/// </summary>
	public void BuildAndRun()
	{
		var projectManager = ProjectManager.Instance;
		if (!projectManager.HasActiveProject)
		{
			Debug.Error("No active project to build.", "GameBuildWindow");
			return;
		}

		if (_isBuilding)
		{
			EditorDebug.Warn("A build is already in progress.", "GameBuildWindow");
			return;
		}

		RefreshAvailableScenes();
		if (_availableScenes.Count == 0)
		{
			Debug.Error("No scenes found. Create at least one scene before building.", "GameBuildWindow");
			return;
		}

		var project = projectManager.CurrentProject;
		var platform = BuildPlatform.All[_selectedPlatformIndex.Value];

		if (!platform.IsAvailable)
		{
			Debug.Error($"Selected platform '{platform.DisplayName}' is not available.", "GameBuildWindow");
			return;
		}

		SaveInitialSceneSetting(project);
		StartBuild(project, platform, runAfterBuild: true);
	}

	/// <summary>
	/// Draws the build options popup and progress window.
	/// </summary>
	public void Draw()
	{
		DrawBuildOptionsPopup();
		_depPreflightDialog.Draw();
		_progressWindow.Draw();
	}

	/// <summary>
	/// Refreshes the list of .vscene files from the current project and
	/// pre-selects the scene that matches ProjectSettings.InitialScene.
	/// </summary>
	private void RefreshAvailableScenes()
	{
		_availableScenes.Clear();
		_selectedSceneIndex = 0;

		var projectManager = ProjectManager.Instance;
		if (!projectManager.HasActiveProject)
			return;

		var sceneNames = SceneManager.Instance.GetAllSceneNames();
		_availableScenes = sceneNames;

		// Pre-select the scene stored in ProjectSettings
		var settings = projectManager.CurrentProject.Settings;
		if (settings != null && !string.IsNullOrEmpty(settings.InitialScene))
		{
			var idx = _availableScenes.IndexOf(settings.InitialScene);
			if (idx >= 0)
				_selectedSceneIndex = idx;
		}
	}

	private void DrawBuildOptionsPopup()
	{
		if (_showBuildPopup)
		{
			ImGui.OpenPopup("Build Game##Options");
			_showBuildPopup = false;
		}

		var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.4f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
		ImGui.SetNextWindowSize(new Num.Vector2(550, 0), ImGuiCond.Appearing);

		bool open = true;
		if (Gui.BeginPopupModal("Build Game##Options", ref open, ImGuiWindowFlags.AlwaysAutoResize))
		{
			var projectManager = ProjectManager.Instance;
			if (!projectManager.HasActiveProject)
			{
				ImGui.TextColored(new Num.Vector4(1.0f, 0.4f, 0.4f, 1.0f), "No active project.");
				if (Gui.Button("Close", new Num.Vector2(100, 0)))
					ImGui.CloseCurrentPopup();
				Gui.EndPopup();
				return;
			}

			var project = projectManager.CurrentProject;
			var selectedPlatform = BuildPlatform.All[_selectedPlatformIndex.Value];

			ImGui.TextColored(new Num.Vector4(0.2f, 0.8f, 1.0f, 1.0f), "Build Game");
			ImGui.Separator();

			VoltageEditorUtils.SmallVerticalSpace();

			// Project info
			ImGui.Text("Project:");
			ImGui.SameLine();
			ImGuiSafe.TextColoredSafe(new Num.Vector4(0.7f, 1.0f, 0.7f, 1.0f), project.ProjectName);

			ImGui.Text("Version:");
			ImGui.SameLine();
			ImGuiSafe.TextDisabledSafe(project.Version.ToString());

			var buildDir = GameBuilder.GetBuildOutputDirectory(project, selectedPlatform, _debugBuild.Value);
			ImGui.Text("Output:");
			ImGui.SameLine();
			ImGuiSafe.TextDisabledSafe(buildDir);

			VoltageEditorUtils.MediumVerticalSpace();

			// Initial Scene
			ImGui.TextColored(new Num.Vector4(0.8f, 0.9f, 1.0f, 1.0f), "Initial Scene");
			ImGui.Separator();

			VoltageEditorUtils.SmallVerticalSpace();

			DrawInitialSceneSelector();

			VoltageEditorUtils.MediumVerticalSpace();

			// Target Platform 
			ImGui.TextColored(new Num.Vector4(0.8f, 0.9f, 1.0f, 1.0f), "Target Platform");
			ImGui.Separator();

			VoltageEditorUtils.SmallVerticalSpace();

			DrawPlatformSelector();

			VoltageEditorUtils.MediumVerticalSpace();

			// Build Options
			ImGui.TextColored(new Num.Vector4(0.8f, 0.9f, 1.0f, 1.0f), "Build Options");
			ImGui.Separator();

			VoltageEditorUtils.SmallVerticalSpace();

			// Debug / Release toggle  use a local for ImGui ref, write back if changed
			var debugBuildValue = _debugBuild.Value;
			if (Gui.Checkbox("Debug Build", ref debugBuildValue))
				_debugBuild.Value = debugBuildValue;

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("When enabled, the game is published in Debug configuration.\n" +
				                 "Debug builds include full symbols and skip optimizations,\n" +
				                 "making it easier to diagnose runtime issues.\n\n" +
				                 "When disabled (default), the game is published in Release\n" +
				                 "configuration with full optimizations.");
			}

			ImGui.SameLine();
			ImGuiSafe.TextColoredSafe(
				_debugBuild.Value
					? new Num.Vector4(1.0f, 0.8f, 0.2f, 1.0f)
					: new Num.Vector4(0.5f, 1.0f, 0.5f, 1.0f),
				_debugBuild.Value ? "(Debug)" : "(Release)");

			VoltageEditorUtils.SmallVerticalSpace();

			var nativeAotValue = _nativeAot.Value;
			if (Gui.Checkbox("Native AOT", ref nativeAotValue))
				_nativeAot.Value = nativeAotValue;
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("On: the trimmed native build you ship.\nOff: a plain self-contained build that is faster and needs no C++ build tools.");

			VoltageEditorUtils.SmallVerticalSpace();

			ImGui.TextWrapped(_nativeAot.Value
				? "The game will be published as a self-contained, trimmed executable for standalone deployment."
				: "The game will be published as a self-contained executable without native compilation or trimming.");

				VoltageEditorUtils.SmallVerticalSpace();

				// Linux glibc-compat container option (only relevant for Linux targets).
				if (LinuxContainerBuild.IsLinux(selectedPlatform))
				{
					var compatValue = _linuxCompatContainer.Value;
					if (Gui.Checkbox("Build in glibc-compat container (recommended)", ref compatValue))
						_linuxCompatContainer.Value = compatValue;

					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip(
							"NativeAOT links the Linux binary against the build machine's glibc. When the editor\n" +
							"runs inside a newer-glibc container (e.g. distrobox), a direct build only runs there.\n\n" +
							"When enabled (default), the AOT publish runs inside an Ubuntu 20.04 (glibc 2.31)\n" +
							"container via podman, so the binary also runs on stock SteamOS and older distros.\n" +
							"Requires podman (reached on the host automatically). The first build provisions the\n" +
							"image and may take several minutes.\n\n" +
							"When disabled, the game is built directly and may not run outside this environment.");
					}

					ImGui.SameLine();
					ImGuiSafe.TextColoredSafe(
						_linuxCompatContainer.Value
							? new Num.Vector4(0.5f, 1.0f, 0.5f, 1.0f)
							: new Num.Vector4(1.0f, 0.8f, 0.2f, 1.0f),
						_linuxCompatContainer.Value ? "(glibc 2.31)" : "(host glibc)");
				}

			VoltageEditorUtils.SmallVerticalSpace();

			var assetBuild = AssetBuildSettingsStore.Get();
			var compileAssetsValue = assetBuild.Enabled;
			if (Gui.Checkbox("Compile assets on build", ref compileAssetsValue))
			{
				assetBuild.Enabled = compileAssetsValue;
				try { AssetBuildSettingsStore.Save(ProjectManager.Instance.CurrentProject); }
				catch (Exception ex) { Debug.Error($"Failed to save the asset build setting: {ex.Message}", "GameBuildWindow"); }
			}

			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Compiles textures, Aseprite files and fonts to .xnb with MGCB; configure it under Build > Asset Build.");

			VoltageEditorUtils.SmallVerticalSpace();

			ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(0.6f, 0.6f, 0.6f, 1.0f));
			ImGui.TextWrapped("Assets will be copied directly to the output Content folder without compilation. " +
			                  "MGCB asset compilation will be available in a future update.");
			ImGui.PopStyleColor();

			VoltageEditorUtils.MediumVerticalSpace();

			// Build contents summary
			if (Gui.CollapsingHeader("Build Contents", ImGuiTreeNodeFlags.DefaultOpen))
			{
				ImGui.Indent();
				ImGui.BulletText("Game executable (AOT + Trimmed)");
				ImGui.BulletText("Voltage engine content (compiled effects, fonts)");
				ImGui.BulletText("Project assets (Content folder, copied as-is)");
				ImGui.BulletText("Project scripts (compiled into executable)");
				ImGui.BulletText("Project data (scenes, prefabs, component data)");
				ImGui.BulletText("Project settings (ProjectSettings.json)");
				ImGui.Unindent();
			}

			VoltageEditorUtils.MediumVerticalSpace();

			// On-demand toolchain check (explicit; never blocks a build).
			if (Gui.Button("Check Dependencies", new Num.Vector2(-1, 0)))
				OpenDependencyCheck();
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(
					"Verify the NativeAOT build toolchain for the current platform\n" +
					"(clang/ld/zlib on Linux, MSVC + Windows SDK on Windows, Xcode tools on macOS).\n\n" +
					"Informational only — this never blocks building.");
			}

			VoltageEditorUtils.MediumVerticalSpace();

			// Action buttons
			ImGui.Separator();

			var buttonWidth = 120f;
			var spacing = 10f;
			var totalButtonWidth = buttonWidth * 3 + spacing * 2;
			var windowWidth = ImGui.GetWindowSize().X;
			var centerStart = (windowWidth - totalButtonWidth) * 0.5f;

			ImGui.SetCursorPosX(centerStart);

			bool hasScene = _availableScenes.Count > 0;
			bool canBuild = selectedPlatform.IsAvailable && !_isBuilding && hasScene;
			if (!canBuild)
				ImGui.BeginDisabled();

			if (Gui.Button("Build", new Num.Vector2(buttonWidth, 30)))
			{
				SaveInitialSceneSetting(project);
				ImGui.CloseCurrentPopup();
				StartBuild(project, selectedPlatform, runAfterBuild: false);
			}

			ImGui.SameLine();

			if (Gui.Button("Build and Run", new Num.Vector2(buttonWidth, 30)))
			{
				SaveInitialSceneSetting(project);
				ImGui.CloseCurrentPopup();
				StartBuild(project, selectedPlatform, runAfterBuild: true);
			}

			if (!canBuild)
				ImGui.EndDisabled();

			ImGui.SameLine();

			if (Gui.Button("Cancel", new Num.Vector2(buttonWidth, 30)))
			{
				ImGui.CloseCurrentPopup();
			}

			Gui.EndPopup();
		}
	}

	/// <summary>
	/// Draws the initial scene combo selector.
	/// </summary>
	private void DrawInitialSceneSelector()
	{
		if (_availableScenes.Count == 0)
		{
			ImGui.TextColored(new Num.Vector4(1.0f, 0.4f, 0.4f, 1.0f),
				"No scenes found. Create at least one scene before building.");
			return;
		}

		ImGui.Text("Scene to load on startup:");

		ImGui.SetNextItemWidth(-1);
		if (Gui.BeginCombo("##InitialScene", _availableScenes[_selectedSceneIndex]))
		{
			for (int i = 0; i < _availableScenes.Count; i++)
			{
				bool isSelected = _selectedSceneIndex == i;
				if (Gui.Selectable(_availableScenes[i], isSelected))
				{
					_selectedSceneIndex = i;
				}

				if (isSelected)
					ImGui.SetItemDefaultFocus();
			}

			ImGui.EndCombo();
		}

		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("The .vscene file that will be loaded when the game starts.\n" +
			                 "This is saved into ProjectSettings.json so the game executable\n" +
			                 "knows which scene to load at runtime.");
		}
	}

	/// <summary>
	/// Writes the selected initial scene name into ProjectSettings.json
	/// so the built game executable reads it at startup.
	/// </summary>
	private void SaveInitialSceneSetting(IGameProject project)
	{
		if (_availableScenes.Count == 0 || _selectedSceneIndex < 0 || _selectedSceneIndex >= _availableScenes.Count)
			return;

		var selectedScene = _availableScenes[_selectedSceneIndex];

		var settings = project.Settings;
		if (settings == null)
			return;

		settings.InitialScene = selectedScene;

		try
		{
			var settingsPath = Path.Combine(project.ProjectPath, "ProjectSettings.json");
			var json = Voltage.Persistence.Json.ToJson(settings, new Voltage.Persistence.JsonSettings { PrettyPrint = true });
			File.WriteAllText(settingsPath, json, new System.Text.UTF8Encoding(false));
		}
		catch (Exception ex)
		{
			Debug.Error($"Failed to save InitialScene setting: {ex.Message}", "GameBuildWindow");
		}
	}

	private void DrawPlatformSelector()
	{
		var platforms = BuildPlatform.All;

		for (int i = 0; i < platforms.Count; i++)
		{
			var platform = platforms[i];

			if (!platform.IsAvailable)
				ImGui.BeginDisabled();

			bool isSelected = _selectedPlatformIndex.Value == i;

			if (Gui.RadioButton(platform.DisplayName, isSelected))
				_selectedPlatformIndex.Value = i;

			if (!platform.IsAvailable)
			{
				ImGui.EndDisabled();

				ImGui.SameLine();
				ImGui.TextColored(new Num.Vector4(1.0f, 0.6f, 0.2f, 1.0f), "(Not Available)");

				if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
				{
					ImGuiSafe.SetTooltipSafe(platform.UnavailableReason ?? "This platform is not yet supported.");
				}
			}
		}
	}

	/// <summary>
	/// Entry point for a build. Runs the NativeAOT build-dependency preflight (clang / ld / zlib, MSVC,
	/// Xcode tools) first as an ADVISORY check: if something isn't detected, a non-blocking dialog offers
	/// install guidance, an opt-in auto-install, and a "Build anyway" option. Detection is best-effort, so it
	/// never hard-blocks a build that would otherwise succeed — the real publish is the authority. The check
	/// is skipped entirely when the user previously chose "Don't show again".
	/// </summary>
	private void StartBuild(IGameProject project, BuildPlatform platform, bool runAfterBuild)
	{
		WarnAboutUnshippedDataAssets(project);

		// User opted out of the advisory check — go straight to building.
		if (_suppressBuildDepCheck.Value)
		{
			BeginBuildTask(project, platform, runAfterBuild);
			return;
		}

		var result = NativeDependencyChecker.Check(NativeDependencyCatalog.BuildDependencies);

		if (result.AllPresent)
		{
			BeginBuildTask(project, platform, runAfterBuild);
			return;
		}

		// Advisory only — surface a heads-up but always allow proceeding ("Build anyway").
		var missingNames = string.Join(", ", result.Missing.Select(s => s.Dependency.FriendlyName));
		Debug.Warn($"Build dependency check: {missingNames} not detected. The build can still proceed; " +
		           "the toolchain will report a specific error if they are genuinely missing.", "GameBuildWindow");

		_depPreflightDialog.Open(
			title: "Build dependencies may be missing",
			intro: "Building a standalone game uses NativeAOT, which needs a few native toolchain components. " +
			       "The following were not detected on this system. Detection is best-effort — if you've " +
			       "installed them in a non-standard way the build may still succeed. Install them and press " +
			       "Recheck, or just Build anyway.",
			result: result,
			dependencySet: NativeDependencyCatalog.BuildDependencies,
			onProceed: () => { PersistSuppressIfRequested(); BeginBuildTask(project, platform, runAfterBuild); },
			onCancel: () => { PersistSuppressIfRequested(); Debug.Log("Build cancelled by user.", "GameBuildWindow"); },
			allowProceedWhenMissing: true,
			showSuppressOption: true);
	}

	/// <summary>Warns about any .vasset that will not ship.</summary>
	private static void WarnAboutUnshippedDataAssets(IGameProject project)
	{
		if (project == null || string.IsNullOrEmpty(project.ProjectPath))
			return;

		try
		{
			var stray = new List<string>();

			foreach (var path in Directory.EnumerateFiles(project.ProjectPath,
						 "*" + Voltage.Data.DataAssetIO.FileExtension, SearchOption.AllDirectories))
			{
				if (IsUnder(path, project.ContentsFolder) || IsUnder(path, project.DataFolder))
					continue;

				if (IsUnder(path, Path.Combine(project.ProjectPath, "obj")) ||
					IsUnder(path, Path.Combine(project.ProjectPath, "bin")))
					continue;

				stray.Add(Path.GetRelativePath(project.ProjectPath, path));
			}

			if (stray.Count == 0)
				return;

			Debug.Warn(
				$"{stray.Count} data asset(s) will NOT be included in the build because they are outside " +
				$"Content/ and Data/:\n  {string.Join("\n  ", stray)}\n" +
				"Move them under Content/ or Data/ (references resolve by GUID, so moving is safe), or add " +
				"a matching <Content Include=\"…\"> item to the project's .csproj.",
				"GameBuildWindow");
		}
		catch (Exception ex)
		{
			EditorDebug.Log($"GameBuildWindow: data asset location check failed: {ex.Message}", "GameBuildWindow");
		}
	}

	private static bool IsUnder(string path, string folder)
	{
		if (string.IsNullOrEmpty(folder))
			return false;

		var normalizedFolder = Path.GetFullPath(folder)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

		return Path.GetFullPath(path).StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Opens the build-dependency dialog on demand (the "Check Dependencies" button). Purely informational:
	/// it re-runs detection and shows status/install guidance, but starts no build and offers no opt-out.
	/// </summary>
	private void OpenDependencyCheck()
	{
		var result = NativeDependencyChecker.ReCheck(NativeDependencyCatalog.BuildDependencies);

		_depPreflightDialog.Open(
			title: result.AllPresent
				? "Build dependencies look good"
				: "Some build dependencies were not detected",
			intro: "These are the native toolchain components NativeAOT needs to publish a standalone game " +
			       "for the selected platform. Detection is best-effort; a build may still succeed even if " +
			       "something shows as missing here.",
			result: result,
			dependencySet: NativeDependencyCatalog.BuildDependencies,
			onProceed: null,
			onCancel: () => { },
			allowProceedWhenMissing: false,
			showSuppressOption: false);
	}

	/// <summary>Persists the "Don't show again" choice if the user ticked it in the preflight dialog.</summary>
	private void PersistSuppressIfRequested()
	{
		if (_depPreflightDialog.SuppressFutureChecksRequested)
			_suppressBuildDepCheck.Value = true;
	}

	private void BeginBuildTask(IGameProject project, BuildPlatform platform, bool runAfterBuild)
	{
		_isBuilding = true;

		_buildCancellationToken?.Cancel();
		_buildCancellationToken?.Dispose();
		_buildCancellationToken = new CancellationTokenSource();

		var token = _buildCancellationToken.Token;
		var debugBuild = _debugBuild.Value;

		Task.Run(async () =>
		{
			try
			{
				bool success = await GameBuilder.BuildGameAsync(project, platform, AssetBuildSettingsStore.Get().Enabled, debugBuild, _linuxCompatContainer.Value, _nativeAot.Value, token);

				if (success && runAfterBuild)
				{
					LaunchGameExecutable(project, platform);
				}
			}
			catch (OperationCanceledException)
			{
				Debug.Log("Game build was cancelled.", "GameBuildWindow");
			}
			catch (Exception ex)
			{
				Debug.Error($"Unexpected build error: {ex.Message}", "GameBuildWindow");
			}
			finally
			{
				_isBuilding = false;
			}
		}, token);
	}

	/// <summary>
	/// Launches the built game executable and captures its console output.
	/// In debug builds, a visible console window is created alongside the game for diagnostics.
	/// In release builds, output is silently captured and forwarded to the editor log.
	/// </summary>
	private void LaunchGameExecutable(IGameProject project, BuildPlatform platform)
	{
		try
		{
			var exePath = GameBuilder.FindGameExecutable(project, platform, _debugBuild.Value);
			if (exePath == null)
			{
				Debug.Error("Could not find the game executable in the build output.");
				return;
			}

			ProcessStartInfo startInfo;

			if (_debugBuild.Value)
			{
				// Debug: let the OS open the exe normally so a console window appears
				// alongside the game. Stream redirection is not possible in this mode.
				startInfo = new ProcessStartInfo
				{
					FileName = exePath,
					WorkingDirectory = Path.GetDirectoryName(exePath)!,
					UseShellExecute = true
				};
			}
			else
			{
				// Release: redirect stdout/stderr into the editor log so errors
				// are visible without a separate console window.
				startInfo = new ProcessStartInfo
				{
					FileName = exePath,
					WorkingDirectory = Path.GetDirectoryName(exePath)!,
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = false
				};
			}

			var process = Process.Start(startInfo);
			if (process == null)
			{
				Debug.Error("Failed to start game process.");
				return;
			}

			if (!_debugBuild.Value)
			{
				// Read stdout/stderr on background threads so the editor doesn't block
				process.OutputDataReceived += (sender, e) =>
				{
					if (!string.IsNullOrEmpty(e.Data))
						EditorDebug.Log($"[Game] {e.Data}", "GameOutput");
				};

				process.ErrorDataReceived += (sender, e) =>
				{
					if (!string.IsNullOrEmpty(e.Data))
						Debug.Error($"[Game] {e.Data}");
				};

				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
			}
		}
		catch (Exception ex)
		{
			Debug.Error($"Failed to launch game executable: {ex.Message}");
		}
	}

	private void OnBuildStarted(int totalSteps)
	{
		_completedSteps = 0;

		_currentProgress = new CompilationProgress
		{
			Title = "Building Game",
			TotalItems = totalSteps,
			CompletedItems = 0,
			SuccessCount = 0,
			FailureCount = 0,
			CurrentItem = "Initializing...",
			IsComplete = false
		};

		_progressWindow.Show(_currentProgress);
	}

	private void OnBuildStepStarted(string stepName)
	{
		if (_currentProgress != null && !_currentProgress.IsComplete)
		{
			_currentProgress.CurrentItem = stepName;
		}
	}

	private void OnBuildStepCompleted(string stepName, bool success)
	{
		if (_currentProgress != null && !_currentProgress.IsComplete)
		{
			_completedSteps++;
			_currentProgress.CompletedItems = _completedSteps;

			if (success)
				_currentProgress.SuccessCount++;
			else
				_currentProgress.FailureCount++;
		}
	}

	private void OnBuildFinished(bool success, string message)
	{
		if (_currentProgress != null)
		{
			_currentProgress.IsComplete = true;
			_currentProgress.CurrentItem = "";
			_currentProgress.CompletionMessage = message;
		}
	}

	private void OnCancelRequested()
	{
		if (_currentProgress != null)
		{
			_currentProgress.IsComplete = true;
		}

		if (_buildCancellationToken != null && !_buildCancellationToken.IsCancellationRequested)
		{
			_buildCancellationToken.Cancel();
		}

		_currentProgress = null;
		_progressWindow.Hide();
		_isBuilding = false;
	}
}
