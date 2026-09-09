using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.Styling;
using Voltage.Editor.FilePickers;
using Voltage.Editor.Gizmos;
using Voltage.Editor.Hotkeys;
using Voltage.Editor.Inspectors.CustomInspectors;
using Voltage.Editor.Persistence;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.SceneFile;
using Voltage.Editor.Scripting;
using Voltage.Editor.Serialization;
using Voltage.Editor.Tools;
using Voltage.Editor.Tools.Tilemap;
using Voltage.Editor.Undo.Core;
using Voltage.Editor.Utils;
using Voltage.Sprites;
using Voltage.Utils;
using System.Runtime.InteropServices;
using Num = System.Numerics;
using Voltage.Editor.Windows;
using Voltage.Editor.Builders;
using Voltage.Editor.Effects;

namespace Voltage.Editor.ImGuiCore;

public partial class ImGuiManager : GlobalManager, IFinalRenderDelegate, IDisposable
{
	#region Public values

	public bool ShowDemoWindow = false;
	public bool IsGameWindowFocused = false;
	public bool IsGameWindowHovered = false;

	public Num.Vector2 GameWindowPosition;
	public Num.Vector2 GameWindowSize;
	public bool FocusGameWindowOnMiddleClick = false;
	public bool FocusGameWindowOnRightClick = false;
	public bool DisableKeyboardInputWhenGameWindowUnfocused = true;
	public bool DisableMouseWheelWhenGameWindowUnfocused = true;
	public float GameWindowWidth { get; private set; }
	public float GameWindowHeight { get; private set; }

	public float FontSizeMultiplier;

	public GizmoSelectionManager CursorSelectionManager => _cursorSelectionManager;
	public SceneGraphWindow SceneGraphWindow { get; private set; }

	/// <summary>Shared tile-painting state: the palette configures it, the game view acts on it.</summary>
	public TilePaintTool TilePaintTool { get; } = new();

	public TilesetEditorWindow TilesetEditorWindow { get; } = new();

	/// <summary>Generic editor for .vasset data containers — one window serves every user-declared type.</summary>
	public DataAssetWindow DataAssetWindow { get; } = new();

	private List<EntityInspectorWindow> _entityInspectors = new();
	public EntityInspectorWindow MainEntityInspectorWindow { get; private set; }
	public bool IsInspectorTabLocked = false;

	private SceneComponentInspectorWindow _sceneComponentInspectorWindow;

	public AnimationEventInspector AnimationEventInspectorInstance
	{
		get => _animationEventInspector;
		private set => _animationEventInspector = value;
	}

	#endregion

	#region Private values

	private System.Reflection.MethodInfo[] _themes;
	private CoreWindow _coreWindow = new();
	private DebugWindow _debugWindow = new();
	private TilePaletteWindow _tilePaletteWindow = new();

	/// <summary>Tile Palette has focus, so its shortcuts take precedence over the global ones.</summary>
	public bool IsTilePaletteFocused => _tilePaletteWindow.IsFocused;
	private ProjectCreatorWindow _projectCreatorWindow = new();
	private SceneCreator _sceneCreator = new();
	private ProjectManager _projectManager;
	private SerializationManager _SerializationManager;
	
	private Num.Vector2 normalEntityInspectorStartPos;
	private int entitynspectorInitialSpawnOffset = 0;
	private static int entitynspectorSpawnOffsetIncremental = 20;

	private AnimationEventInspector _animationEventInspector;
	private SpriteAtlasEditorWindow _spriteAtlasEditorWindow;
	private List<Action> _drawCommands = new();
	private ImGuiRenderer _renderer;

	/// <summary>Closes the frame Update opened when Draw is skipped, as on a minimized window.</summary>
	public void DiscardFrame() => _renderer?.DiscardFrame();
	private GizmoSelectionManager _cursorSelectionManager;
	private ImGuiWindowFlags _gameWindowFlags = 0;

	private RenderTarget2D _lastRenderTarget;
	private IntPtr _renderTargetId = IntPtr.Zero;
	private Num.Vector2? _gameViewForcedSize;
	private WindowPosition? _gameViewForcedPos;
	private readonly float _editorToolsBarHeight = 30f;

	// Camera Params
	public static float EditModeCameraSpeed = 400f;
	public static float EditModeCameraFastSpeed = 800f;
	private const float EditorCameraZoomSpeed = 1f;
	public static float EditModeCameraMinSpeed = 100f;
	public static float EditModeCameraMaxSpeed = 3000f;
	private static float _dynamicCameraSpeed = EditModeCameraFastSpeed;
	private const float CameraSpeedAdjustmentStep = 20f; // How much to change per scroll
	public static float CurrentCameraSpeed { get; private set; }

	public Vector2 CameraTargetPosition
	{
		get => _cameraTargetPosition;
		set => _cameraTargetPosition = value;
	}

	private Vector2 _cameraTargetPosition;
	private float _cameraLerp = 0.4f;

	// Camera dragging with middle mouse button
	private bool _isCameraDragging = false;
	private Vector2 _cameraDragStartMouse;
	private Vector2 _cameraDragStartPosition;

	private bool _pendingExit = false;
	private bool _pendingSceneChange = false;
	private Type _requestedSceneType = null;
	private string _requestedSceneName = null;
	// Factory for an in-memory scene to switch to (used by "open prefab in isolation"); takes priority
	// over name/type when set, and routes through the same unsaved-changes prompt as a normal scene change.
	private Func<Scene> _requestedSceneFactory = null;
	private bool _pendingResetScene = false;
	private Type _requestedResetSceneType = null;
	private Task _pendingSaveTask = null;
	private bool _pendingProjectClose = false;
	private ExitPromptType _pendingActionAfterSave;

	// Two prompts: the shared unsaved-changes one, and a plain confirmation when the scene is clean.
	private bool _pendingRelaunch = false;
	private bool _pendingRelaunchConfirm = false;

	private Scene _pendingPrefabScene = null;
	private Voltage.Data.PrefabData _pendingPrefabData;
	private string _pendingPrefabName = null;
	private Guid _pendingPrefabGuid;
	private string _pendingPrefabPrevScenePath = null;
	private string _pendingPrefabPrevSceneName = null;

	// Non-null while an isolated prefab edit scene is active (see OpenPrefabIsolated).
	private PrefabEditSession _prefabEdit = null;

	/// <summary>True while the active scene is an isolated prefab edit scene.</summary>
	public bool IsInPrefabEditScene => _prefabEdit != null;

	/// <summary>The name of the prefab currently being edited in isolation (or null).</summary>
	public string PrefabEditName => _prefabEdit?.PrefabName;

	/// <summary>
	/// Tracks an active isolated prefab edit session: the single SerializedPrefab instance placed in the
	/// temporary scene, the prefab identity, and the scene to return to when the user goes back.
	/// </summary>
	private sealed class PrefabEditSession
	{
		public Entity Instance;
		public string PrefabName;
		public Guid PrefabGuid;
		public string PreviousScenePath;
		public string PreviousSceneName;
	}

	private string _layoutFilePath;
	private LayoutManager _layoutManager;
	private ThemeManager _themeManager;
	private string _newLayoutName = "";
	private bool _showSaveLayoutPopup = false;
	private bool _isFirstFrame = true;

	/// <summary>ImGui undocks a window that begins in a frame whose dockspace was not submitted, so the game window waits for a frame that has one.</summary>
	private int _dockspaceFrame = -1;

	// Build effects progress window
	private EffectsCompileProgressWindow _effectsCompileProgressWindow;
	private System.Threading.CancellationTokenSource _effectBuildCancelToken;

	// Game build window
	private GameBuildWindow _gameBuildWindow;

	// Engine effects check
	private bool _hasCheckedEngineEffects = false;
	private bool _showEngineEffectsPrompt = false;
	private bool _engineEffectsCheckComplete = false;

	private readonly AssetFileBrowser _projectFileBrowser =
		new("load-project-popup", new[] { ".voltage" }, "Voltage projects");

	private ScriptManager _scriptManager;

	/// <summary>Null until a project with a scripts folder is loaded.</summary>
	internal ScriptManager ScriptManager => _scriptManager;

	private EditorSettingsWindow _editorSettingsWindow = new();
	private ProjectSettingsWindow _projectSettingsWindow = new();
	private AssetBrowserWindow _assetBrowserWindow = new();
	private PluginManagerWindow _pluginManagerWindow = new();
	private TimelineWindow _timelineWindow = new();
	private AudioProfilerWindow _audioProfilerWindow = new();
	private PerformanceProfilerWindow _performanceProfilerWindow = new();

	private List<(Entity entity, Collider collider)> _highlightedEntities = new();
	private IReadOnlyList<Entity> _lastSelectedEntities = null;

	private bool _showCreateSceneForSavePrompt = false;
	private string _newSceneNameForSave = "";

	// Pinned handle for the ImGui ini filename to prevent GC relocation
	private GCHandle _iniFilenamePinnedHandle;

	#endregion

	#region Scene Saving UI

	private void DrawCreateSceneForSavePrompt()
	{
		if (_showCreateSceneForSavePrompt)
		{
			ImGui.OpenPopup("create-scene-for-save");
			_showCreateSceneForSavePrompt = false;
		}

		var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.4f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
		ImGui.SetNextWindowSize(new Num.Vector2(500, 0), ImGuiCond.Appearing);

		bool open = true;
		if (Gui.BeginPopupModal("create-scene-for-save", ref open, ImGuiWindowFlags.AlwaysAutoResize))
		{
			ImGui.Text("Save Current Scene");
			ImGui.Separator();

			ImGui.TextWrapped("You have unsaved changes but no scene file is associated with this scene.");
			ImGui.TextWrapped("Would you like to create a new scene file to save your changes?");

			VoltageEditorUtils.MediumVerticalSpace();

			ImGui.Text("Scene Name:");
			ImGui.SetNextItemWidth(450);
			Gui.InputText("##SceneName", ref _newSceneNameForSave, 50);

			// Validate scene name
			bool isValidName = !string.IsNullOrWhiteSpace(_newSceneNameForSave);
			bool sceneExists = false;

			if (isValidName && _projectManager.HasActiveProject)
			{
				var scenePath = Path.Combine(
					_projectManager.CurrentProject.ScenesFolder,
					$"{_newSceneNameForSave}.vscene"
				);
				sceneExists = File.Exists(scenePath);
			}

			if (sceneExists)
			{
				ImGuiSafe.TextColoredSafe(new Num.Vector4(1.0f, 0.6f, 0.2f, 1.0f),
					$"Warning: A scene with name '{_newSceneNameForSave}' already exists!");
			}

			VoltageEditorUtils.MediumVerticalSpace();

			var buttonWidth = 100f;
			var spacing = 10f;
			var totalButtonWidth = (buttonWidth * 3) + (spacing * 2);
			var windowWidth = ImGui.GetWindowSize().X;
			var centerStart = (windowWidth - totalButtonWidth) * 0.5f;

			ImGui.SetCursorPosX(centerStart);

			// Disable save button if invalid
			if (!isValidName || sceneExists)
				ImGui.BeginDisabled();

			if (Gui.Button("Save", new Num.Vector2(buttonWidth, 0)))
			{
				CreateAndSaveScene(_newSceneNameForSave);
				ImGui.CloseCurrentPopup();
			}

			if (!isValidName || sceneExists)
				ImGui.EndDisabled();

			ImGui.SameLine();

			if (Gui.Button("Don't Save", new Num.Vector2(buttonWidth, 0)))
			{
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();

			if (Gui.Button("Cancel", new Num.Vector2(buttonWidth, 0))
				)
			{
				ImGui.CloseCurrentPopup();
			}

			Gui.EndPopup();
		}
	}

	private void CreateAndSaveScene(string sceneName)
	{
		if (!_projectManager.HasActiveProject)
		{
			Debug.Error("No active project!");
			return;
		}

		try
		{
			var sceneManager = SceneManager.Instance;

			// Create and save the new scene file
			if (sceneManager.CreateSceneFile(sceneName))
			{
				Debug.Info($"Scene created and saved: {sceneName}");
			}
			else
			{
				Debug.Error($"Failed to create scene: {sceneName}");
			}
		}
		catch (Exception ex)
		{
			Debug.Error($"Failed to create scene: {ex.Message}");
		}
	}

	#endregion

	public ImGuiManager(ImGuiOptions options = null)
	{
		Core.IsEditorMode = true;
		Core.DebugRenderEnabled = true;

		if (options == null)
			options = new ImGuiOptions();

		FontSizeMultiplier = options.FontSizeMultiplier;
		_gameWindowFlags = options._gameWindowFlags;
		_gameViewForcedPos = WindowPosition.Top;

		_layoutFilePath = Path.Combine(EditorStorage.LayoutsDirectory, "imgui_layout.ini");
		_layoutManager = new LayoutManager(_layoutFilePath);
		Core.ShouldInterceptExit = () => EditorChangeTracker.IsDirty;

		LoadSettings();

		_renderer = new ImGuiRenderer(Core.Instance);
		_renderer.RebuildFontAtlas(options);

		Core.Emitter.AddObserver(CoreEvents.SceneChanged, OnSceneChanged);

		ImGui.GetStyle().IndentSpacing = 12;
		ImGui.GetIO().ConfigWindowsMoveFromTitleBarOnly = true;

		var io = ImGui.GetIO();
		io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;

		// Pin the layout path bytes so the pointer remains valid for ImGui's entire lifetime.
		_layoutManager.LayoutInitPathUtf8 = System.Text.Encoding.UTF8.GetBytes(_layoutFilePath + "\0");
		_iniFilenamePinnedHandle = GCHandle.Alloc(_layoutManager.LayoutInitPathUtf8, GCHandleType.Pinned);
		unsafe
		{
			ImGuiNative.igGetIO()->IniFilename = (byte*)_iniFilenamePinnedHandle.AddrOfPinnedObject();
		}

		LoadLastSelectedLayout();

		// Initialize theme manager (will apply last selected theme automatically)
		_themeManager = new ThemeManager();

		SceneGraphWindow = new SceneGraphWindow();
		_cursorSelectionManager = new GizmoSelectionManager(this);

		ImguiImageLoader.LoadImages(_renderer);

		_effectsCompileProgressWindow = new EffectsCompileProgressWindow();
		_gameBuildWindow = new GameBuildWindow();
		_SerializationManager = SerializationManager.Instance;

		Core.EmitterWithPending.AddObserver(CoreEvents.Exiting, OnAppExitSaveChanges);
		Core.OnResetScene += RequestResetScene;
		Core.OnSwitchEditMode += OnEditModeSwitched;
		
		OpenMainEntityInspector();
		_entityInspectors.Add(MainEntityInspectorWindow);

		// Initialize project manager
		_projectManager = ProjectManager.Instance;
		_projectManager.OnProjectLoaded += OnProjectLoaded;
		_projectManager.OnProjectUnloaded += OnProjectUnloaded;
		Utils.ProjectLoadProgress.FramePump = RenderProjectLoadFrame;
		_projectManager.LoadLastProject();

		Core.WindowTitleStatusProvider = ComposeWindowTitleStatus;

		InitializeScriptManager();
	}

	/// <summary>
	/// Loads the last selected layout from persistent settings
	/// </summary>
	private void LoadLastSelectedLayout()
	{
		string lastLayout = _lastSelectedLayout.Value;

		if (!string.IsNullOrWhiteSpace(lastLayout) && !lastLayout.Equals("Default", StringComparison.OrdinalIgnoreCase))
		{
			string layoutPath = _layoutManager.GetLayoutPath(lastLayout);

			if (File.Exists(layoutPath))
			{
				_layoutManager.LoadLayout(lastLayout);
			}
			else
			{
				Debug.Warn($"Last used layout '{lastLayout}' not found, loading default");
				LoadDefaultLayout();
			}
		}
		else
		{
			LoadDefaultLayout();
		}
	}

	/// <summary>
	/// Loads the default layout
	/// </summary>
	private void LoadDefaultLayout()
	{
		if (_layoutManager.WasFreshInstall || !File.Exists(_layoutFilePath))
		{
			_layoutManager.LoadLayout("Default");
			return;
		}

		ImGui.LoadIniSettingsFromDisk(_layoutFilePath);
	}

	/// <summary>
	/// This is where we issue any and all ImGui commands to be drawn
	/// </summary>
	private void LayoutGui()
	{
		if (_layoutManager.HasPendingReload)
		{
			_layoutManager.ApplyPendingReload();
		}

		if (_isFirstFrame)
		{
			_isFirstFrame = false;
			return;
		}

		// Surface a one-time warning for any OPTIONAL native runtime dep that was missing at startup
		// (e.g. OpenAL → no audio). Critical deps already aborted launch before graphics init.
		Diagnostics.RuntimeDependencyPreflight.SurfaceOptionalWarningsOnce(
			warn: m => Debug.Warn(m, "Dependencies"),
			notify: NotificationSystem.ShowTimedNotification);

		if (ShowMenuBar)
			DrawMainMenuBar();

		CreateDockspace();
		DrawEditorToolsBar();
		DrawProjectFilePicker();
		DrawSaveChangesPrompt();
		DrawCreateSceneForSavePrompt();
		ShowSceneGraphWindow = SceneGraphWindow.Show(ShowSceneGraphWindow);
		ShowCoreWindow = _coreWindow.Show(ShowCoreWindow);
		_debugWindow.Draw();
		DrawEntityInspectors();
		_effectsCompileProgressWindow.Draw();
		_gameBuildWindow.Draw();
		_projectCreatorWindow.Draw();
		_sceneCreator.Draw();
		_projectSettingsWindow.Draw();

		if (ShowAssetBrowser)
		{
			_assetBrowserWindow.IsOpen = true;
			_assetBrowserWindow.Draw();
			// Persist toggle state if the user closed the window via its X button.
			ShowAssetBrowser = _assetBrowserWindow.IsOpen;
		}

		// Resolve a pending asset drag dropped onto the game viewport. Runs after the Scene Graph
		// and asset browser draws so their drop targets get first chance at the payload.
		HandleGameViewAssetDrop();

		// Add the prefab instance once a freshly-opened isolated prefab scene has actually become active.
		ProcessPendingPrefabInstantiation();

		for (var i = _drawCommands.Count - 1; i >= 0; i--)
			_drawCommands[i]();

		if (_spriteAtlasEditorWindow != null)
			if (!_spriteAtlasEditorWindow.Show())
				_spriteAtlasEditorWindow = null;

		if (ShowDemoWindow)
			ImGui.ShowDemoWindow(ref ShowDemoWindow);

		if (ShowStyleEditor)
		{
			var showStyleEditor = ShowStyleEditor;
			Gui.Begin("Style Editor", ref showStyleEditor);
			ShowStyleEditor = showStyleEditor;

			ImGui.ShowStyleEditor();
			Gui.End();
		}

		if (!_hasCheckedEngineEffects && !_engineEffectsCheckComplete)
		{
			CheckEngineEffectsExist();
			_hasCheckedEngineEffects = true;
		}

		if (_showEngineEffectsPrompt)
		{
			DrawEngineEffectsPrompt();
		}

		// Remap mouse coordinates to game-window space BEFORE UpdateCamera and UpdateSelection.
		// All editor ImGui widgets (menus, inspectors, scene graph, etc...) have already
		// finished drawing above with the raw OS mouse coordinates.
		ApplyGameWindowMouseOverride();

		if (DisableKeyboardInputWhenGameWindowUnfocused && !IsGameWindowFocused)
			Input.SetCurrentKeyboardState(new Microsoft.Xna.Framework.Input.KeyboardState());

		UpdateCamera();
		NotificationSystem.Draw();
		GlobalKeyCommands();

		DrawSelectedEntityOutlines();

		if (ShowAnimationEventInspector)
		{
			if (_animationEventInspector == null)
			{
				_animationEventInspector = new AnimationEventInspector(null);
				RegisterDrawCommand(_animationEventInspector.Draw);
			
				_animationEventInspector.SetWindowFocus();
			}
		}
		else
		{
			if (_animationEventInspector != null)
			{
				UnregisterDrawCommand(_animationEventInspector.Draw);
				_animationEventInspector = null;
			}
		}

		DrawScriptingWindow();
		_editorSettingsWindow.Draw();
		_pluginManagerWindow.Draw();

		// Timeline window: toggled via View → Window (persistent). Two-way sync so closing it with the
		// window's X also unticks the menu item.
		if (ShowTimelineWindow)
		{
			_timelineWindow.IsOpen = true;
			_timelineWindow.Draw();
			ShowTimelineWindow = _timelineWindow.IsOpen;
		}

		// Audio Profiler: toggled via the Profiler menu (persistent). Two-way sync so closing it with the
		// window's X also unticks the menu item.
		if (ShowAudioProfilerWindow)
		{
			_audioProfilerWindow.IsOpen = true;
			_audioProfilerWindow.Draw();
			ShowAudioProfilerWindow = _audioProfilerWindow.IsOpen;
		}

		// Performance Profiler: toggled via the Profiler menu (persistent). Two-way sync so closing it with
		// the window's X also unticks the menu item.
		if (ShowPerformanceProfilerWindow)
		{
			_performanceProfilerWindow.IsOpen = true;
			_performanceProfilerWindow.Draw();
			ShowPerformanceProfilerWindow = _performanceProfilerWindow.IsOpen;
		}

		if (ShowTilePaletteWindow)
		{
			_tilePaletteWindow.IsOpen = true;
			_tilePaletteWindow.Draw(TilePaintTool, TilesetEditorWindow, _cursorSelectionManager);
			ShowTilePaletteWindow = _tilePaletteWindow.IsOpen;
		}

		TilesetEditorWindow.Draw();
		DataAssetWindow.Draw();

		Plugins.EditorPluginHost.DrawWindows();
		_cursorSelectionManager.UpdateSelection();
	}

	private void OnEditModeSwitched(bool isEditMode)
	{
		if(EditorSettingsWindow.DisableDebugInPlayMode)
			Core.DebugRenderEnabled = isEditMode;

		// Returning to EditMode always clears PauseMode
		if (isEditMode)
			Core.IsPauseMode = false;

		// Only reset scene if switching to EditMode from PlayMode
		if (isEditMode && Core.ResetSceneAutomatically)
		{
			var sceneManager = SceneManager.Instance;
			if (sceneManager.HasLoadedScene)
			{
				sceneManager.ReloadCurrentScene();
			}
			else
			{
				Core.InvokeResetScene();
			}
		}
	}

	/// <summary>
	/// Opens the file browser for selecting a .voltage file
	/// </summary>
	private void OpenProjectFilePicker()
	{
		string startingPath = !string.IsNullOrWhiteSpace(_projectManager.LastProjectPath)
			? Path.GetDirectoryName(_projectManager.LastProjectPath)
			: Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

		_projectFileBrowser.Open("Load Project", startingPath, this);
	}

	/// <summary>
	/// Pumps the project file browser and loads whatever the user picked.
	/// </summary>
	private void DrawProjectFilePicker()
	{
		_projectFileBrowser.Draw("Select a .voltage file to load");

		if (!_projectFileBrowser.TryTakeResult(out var selectedFile) || string.IsNullOrEmpty(selectedFile))
			return;

		if (!Path.GetExtension(selectedFile).Equals(".voltage", StringComparison.OrdinalIgnoreCase))
		{
			Debug.Warn("Please select a valid .voltage file.");
			return;
		}

		RequestProjectLoad(selectedFile);
	}

	/// <summary>
	/// Checks if engine effects exist in Content/Voltage/Effects
	/// </summary>
	private void CheckEngineEffectsExist()
	{
		try
		{
			// User chose "Don't show again": never surface the prompt, regardless of effects state.
			if (_dontShowEngineEffectsPrompt.Value)
			{
				_engineEffectsCheckComplete = true;
				return;
			}

			var projectDir = FindProjectDir();
			var effectsDir = Path.Combine(projectDir, "Content", "Voltage", "Effects");

			bool needsBuild = false;

			if (!Directory.Exists(effectsDir))
			{
				Debug.Warn("Content/Voltage/Effects directory not found");
				needsBuild = true;
			}
			else
			{
				var effectFiles = Directory.GetFiles(effectsDir, "*.mgfxo", SearchOption.AllDirectories);
				if (effectFiles.Length == 0)
				{
					Debug.Warn("Content/Voltage/Effects directory is empty");
					needsBuild = true;
				}
			}

			if (needsBuild && EditorRunMode.NoPrompts)
			{
				Gateway.EditorGatewayDispatcher.SuppressPrompt("Missing Engine Effects", $"no compiled effects in {effectsDir}; run effects.compile");
				_engineEffectsCheckComplete = true;
			}
			else if (needsBuild)
			{
				_showEngineEffectsPrompt = true;
			}
			else
			{
				_engineEffectsCheckComplete = true;
			}
		}
		catch (Exception ex)
		{
			Debug.Error($"Error checking engine effects: {ex.Message}");
			_engineEffectsCheckComplete = true;
		}
	}

	/// <summary>Where the editor looks for its compiled engine effects.</summary>
	internal static string EngineEffectsDirectory => Path.Combine(FindProjectDir(), "Content", "Voltage", "Effects");

	/// <summary>Starts the same engine-effects build the Effects menu runs.</summary>
	internal void CompileEngineEffects() =>
		EffectsCompiler.BuildEditorEngineEffects(_effectsCompileProgressWindow, ref _effectBuildCancelToken);

	/// <summary>
	/// Finds the Voltage.Editor project directory
	/// </summary>
	private static string FindProjectDir()
	{
		var dir = AppContext.BaseDirectory;
		var di = new DirectoryInfo(dir);
		while (di != null)
		{
			if (File.Exists(Path.Combine(di.FullName, "Voltage.Editor.csproj")))
				return di.FullName;
			di = di.Parent;
		}

		return AppContext.BaseDirectory;
	}

	private void CreateDockspace()
	{
		var viewport = ImGui.GetMainViewport();
		var dockspaceSize = viewport.WorkSize;

		ImGui.SetNextWindowPos(viewport.WorkPos);
		ImGui.SetNextWindowSize(dockspaceSize);
		ImGui.SetNextWindowViewport(viewport.ID);

		ImGuiWindowFlags windowFlags = ImGuiWindowFlags.NoDocking;
		windowFlags |= ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
		windowFlags |= ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove;
		windowFlags |= ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus;
		windowFlags |= ImGuiWindowFlags.NoBackground;

		ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0.0f);
		ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0.0f);
		ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Num.Vector2(0.0f, 0.0f));
		ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Num.Vector2(0.0f, 0.0f));

		Gui.Begin("DockSpaceWindow", windowFlags);
		ImGui.PopStyleVar(4);

		var dockspaceId = ImGui.GetID("MainDockSpace");
		ImGui.DockSpace(dockspaceId, new Num.Vector2(0.0f, 0.0f),
			ImGuiDockNodeFlags.PassthruCentralNode | ImGuiDockNodeFlags.NoDockingInCentralNode);
		_dockspaceFrame = ImGui.GetFrameCount();

		Gui.End();
	}

	public void GlobalKeyCommands()
	{
		// Bindings match modifiers exactly, so Ctrl+F5 no longer also fires the bare-F5 reset.
		if (EditorHotkeys.Pressed(EditorHotkeys.BuildAndRun))
			_gameBuildWindow?.BuildAndRun();

		if (EditorHotkeys.Pressed(EditorHotkeys.ResetScene))
			Core.InvokeResetScene();

		if (EditorHotkeys.Pressed(EditorHotkeys.TogglePlay))
			Core.InvokeSwitchEditMode(!Core.IsEditMode);

		// Pause mode (only in PlayMode)
		if (EditorHotkeys.Pressed(EditorHotkeys.TogglePause) && !Core.IsEditMode)
			Core.InvokeSwitchPauseMode(!Core.IsPauseMode);

		if (EditorHotkeys.Pressed(EditorHotkeys.SaveScene))
			InvokeSaveSceneChanges();

		if (EditorHotkeys.Pressed(EditorHotkeys.ReloadScene))
			_scriptManager?.ReloadScene();

		// This triggers the same exit/save prompt as the window close event
#if OS_WINDOWS || LINUX
		if (ImGui.GetIO().KeyAlt && ImGui.IsKeyPressed(ImGuiKey.F4, false) && !_pendingExit)
			OnAppExitSaveChanges(true);
#elif OS_MAC
		if (ImGui.GetIO().KeySuper && ImGui.IsKeyPressed(ImGuiKey.Q, false) && !_pendingExit)
			OnAppExitSaveChanges(true);
#endif
	}

	/// <summary>
	/// Invokes scene save through SerializationManager with proper UI validation.
	/// </summary>
	private void InvokeSaveSceneChanges()
	{
		if (Core.Scene == null)
		{
			Debug.Error("No active scene to save!");
			return;
		}

		// In a prefab edit scene, Ctrl+S saves the prefab (not a scene file) — this is what keeps the
		// temporary prefab scene from ever being written to disk and restored on the next launch.
		if (IsInPrefabEditScene)
		{
			SavePrefabEditToOriginal();
			return;
		}

		if (!SceneManager.Instance.HasLoadedScene)
		{
			_newSceneNameForSave = "NewScene";
			_showCreateSceneForSavePrompt = true;
			return;
		}

		if (!Core.IsEditMode)
		{
			NotificationSystem.ShowTimedNotification("Scene Data can't be saved in Play/Pause Mode");
			return;
		}

		Core.Schedule(0f, false, this, async _ =>
		{
			try
			{
				await _SerializationManager.SaveSceneChangesAsync();
			}
			catch (Exception ex)
			{
				Debug.Error($"Failed to save scene: {ex.Message}");
			}
		});
	}

	private void ManageUndoAndRedo()
	{
		if (EditorHotkeys.Pressed(EditorHotkeys.Undo))
			EditorChangeTracker.Undo();

		if (EditorHotkeys.Pressed(EditorHotkeys.Redo))
			EditorChangeTracker.Redo();
	}

	#region Drawing Methods

	private void DrawMainMenuBar()
	{
		if (Gui.BeginMainMenuBar())
		{
			DrawFileMenu();
			DrawProjectMenu();
			DrawViewMenu();
			DrawProfilerMenu();
			DrawScriptingMenu();
			DrawEffectsMenu();
			DrawPluginsMenu();
			DrawBuildMenu();
			DrawHelpMenu();

			// Centering here only worked while the window was wide: the clamp that stops it overdrawing
			// the menus puts it flush against Help on a small screen.

			Gui.EndMainMenuBar();
		}
	}

	/// <summary>Project, scene and version for the window title. Asked for once a second.</summary>
	private string ComposeWindowTitleStatus()
	{
		if (!_projectManager.HasActiveProject)
			return "No Project";

		var project = _projectManager.CurrentProject;
		var sceneManager = SceneManager.Instance;
		var scene = sceneManager.HasLoadedScene ? sceneManager.CurrentSceneName : "no scene";

		return $"{project.ProjectName} | {scene} | v{project.Version}";
	}

	private void DrawEditorToolsBar()
	{
		Gui.Begin("Editor Tools", ImGuiWindowFlags.NoScrollbar);

		float spacing = 12f * FontSizeMultiplier;
		float iconSize = 24f * FontSizeMultiplier;


		System.Numerics.Vector4 normalButtonColor;
		if (_cursorSelectionManager.SelectionMode == CursorSelectionMode.Normal)
			normalButtonColor = new System.Numerics.Vector4(0.2f, 0.5f, 1f, 1f);
		else
			normalButtonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];

		ImGui.PushStyleColor(ImGuiCol.Button, normalButtonColor);
		bool normalHovered =
			Gui.ImageButton("Normal", ImguiImageLoader.NormalCursorIconID, new Num.Vector2(iconSize, iconSize));
		if (normalHovered)
			_cursorSelectionManager.SelectionMode = CursorSelectionMode.Normal;
		ImGui.PopStyleColor();

		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip($"Default Cursor ({EditorHotkeys.Hint(EditorHotkeys.CursorNormal)})");
		}

		ImGui.SameLine(0, spacing);

		System.Numerics.Vector4 resizeButtonColor;
		if (_cursorSelectionManager.SelectionMode == CursorSelectionMode.Resize)
			resizeButtonColor = new System.Numerics.Vector4(0.2f, 0.5f, 1f, 1f);
		else
			resizeButtonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];

		ImGui.PushStyleColor(ImGuiCol.Button, resizeButtonColor);
		bool resizeHovered =
			Gui.ImageButton("Resize", ImguiImageLoader.ResizeCursorIconID, new Num.Vector2(iconSize, iconSize));
		if (resizeHovered)
			_cursorSelectionManager.SelectionMode = CursorSelectionMode.Resize;
		ImGui.PopStyleColor();

		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip($"Resize Entities ({EditorHotkeys.Hint(EditorHotkeys.CursorResize)})");
		}

		ImGui.SameLine(0, spacing);

		System.Numerics.Vector4 rotateButtonColor;
		if (_cursorSelectionManager.SelectionMode == CursorSelectionMode.Rotate)
			rotateButtonColor = new System.Numerics.Vector4(0.2f, 0.5f, 1f, 1f);
		else
			rotateButtonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];

		ImGui.PushStyleColor(ImGuiCol.Button, rotateButtonColor);
		bool rotateHovered =
			Gui.ImageButton("Rotate", ImguiImageLoader.RotateCursorIconID, new Num.Vector2(iconSize, iconSize));
		if (rotateHovered)
			_cursorSelectionManager.SelectionMode = CursorSelectionMode.Rotate;
		ImGui.PopStyleColor();

		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip($"Rotate Entities ({EditorHotkeys.Hint(EditorHotkeys.CursorRotate)})");
		}

		ImGui.SameLine(0, spacing);

		System.Numerics.Vector4 colliderResizeButtonColor;
		if (_cursorSelectionManager.SelectionMode == CursorSelectionMode.ColliderResize)
			colliderResizeButtonColor = new System.Numerics.Vector4(0.2f, 0.5f, 1f, 1f);
		else
			colliderResizeButtonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];

		ImGui.PushStyleColor(ImGuiCol.Button, colliderResizeButtonColor);

		bool colliderResizeHovered = Gui.ImageButton("Collider Resize", ImguiImageLoader.ColliderResizeCursorIconID,
			new Num.Vector2(iconSize, iconSize));
		if (colliderResizeHovered)
			_cursorSelectionManager.SelectionMode = CursorSelectionMode.ColliderResize;

		ImGui.PopStyleColor();

		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip($"Resize Colliders ({EditorHotkeys.Hint(EditorHotkeys.CursorColliderResize)})");
		}

		ImGui.SameLine(0, spacing);

		System.Numerics.Vector4 tilePaintButtonColor;
		if (_cursorSelectionManager.SelectionMode == CursorSelectionMode.TilePaint)
			tilePaintButtonColor = new System.Numerics.Vector4(0.2f, 0.5f, 1f, 1f);
		else
			tilePaintButtonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];

		ImGui.PushStyleColor(ImGuiCol.Button, tilePaintButtonColor);
		bool tilePaintHovered = Gui.ImageButton("Tile Brush", ImguiImageLoader.TileBrushCursorIconID,
			new Num.Vector2(iconSize, iconSize));
		if (tilePaintHovered)
		{
			_cursorSelectionManager.SelectionMode = CursorSelectionMode.TilePaint;
			ShowTilePaletteWindow = true;
		}

		ImGui.PopStyleColor();

		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip($"Paint Tiles ({EditorHotkeys.Hint(EditorHotkeys.CursorTilePaint)})");
		}

		// Zoom indicator
		ImGui.SameLine(0, spacing * 2f);
		float currentZoom = Core.Scene.Camera.Zoom;
		int zoomPercentage = (int)(currentZoom * 100f);
		string zoomText = $"Zoom: {zoomPercentage}%";

		// NOTE: do NOT use ImGui.TextColored here. TextColored forwards the string to the
		// native printf-style igTextColored(col, fmt, ...), so the literal '%' in the zoom
		// text is parsed as a format specifier and ImGui reads a non-existent vararg —
		// over-reading adjacent memory and rendering garbage (log fragments, etc.), which
		// also balloons the text width and pushes the mode/audio buttons off-screen.
		// TextUnformatted renders the raw string verbatim; push the tint color manually.
		ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1.0f));
		ImGui.TextUnformatted(zoomText);
		ImGui.PopStyleColor();

		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Camera zoom level (Mouse Wheel to adjust)");
		}

		// Keep the mode cluster, grid toggles and audio toggle on the same row as the cursor tools.
		ImGui.SameLine(0, spacing);
		DrawEditorModeControls();

		ImGui.SameLine();
		DrawGridControlsRightAligned();

		ImGui.SameLine();
		DrawAudioToggleRightAligned();

		Gui.End();
	}

	/// <summary>
	/// draws all the separate EntityInspectors (not Main Entity Inspector)
	/// </summary>
	private void DrawEntityInspectors()
	{
		MainEntityInspectorWindow.IsOpen = ShowMainInspectorWindow;
		MainEntityInspectorWindow.Draw();

		// Only draw and remove non-main inspectors (make sure i starts from 1!)
		for (int i = 1; i < _entityInspectors.Count; i++)
		{
			if (_entityInspectors[i] == null)
				continue;

			if (!_entityInspectors[i].IsOpen)
			{
				_entityInspectors.RemoveAt(i);
				continue;
			}

			_entityInspectors[i].Draw();
		}

		// Draw the scene component inspector window if it is open
		if (_sceneComponentInspectorWindow != null)
		{
			if (!_sceneComponentInspectorWindow.IsOpen)
				_sceneComponentInspectorWindow = null;
			else
				_sceneComponentInspectorWindow.Draw();
		}
	}

	#endregion

	private void UpdateCamera()
	{
		if (Core.IsEditMode || Core.IsPauseMode)
		{
			if (IsGameWindowFocused)
			{
				ManageCameraZoom();

				var mousePos = Input.ScaledMousePosition;

				if (Input.MiddleMouseButtonPressed)
				{
					_isCameraDragging = true;
					_cameraDragStartMouse = mousePos;
					_cameraDragStartPosition = _cameraTargetPosition;
				}
				else if (_isCameraDragging && Input.MiddleMouseButtonDown)
				{
					var delta = mousePos - _cameraDragStartMouse;
					_cameraTargetPosition = _cameraDragStartPosition - delta;
				}
				else if (_isCameraDragging && !Input.MiddleMouseButtonDown)
				{
					_isCameraDragging = false;
				}

				if (_cameraTargetPosition == default)
					_cameraTargetPosition = Core.Scene.Camera.Position;

				bool isMovingCamera = Input.IsKeyDown(Keys.W) || Input.IsKeyDown(Keys.A) ||
				                      Input.IsKeyDown(Keys.S) || Input.IsKeyDown(Keys.D);

				if (Input.IsKeyDown(Keys.LeftShift))
				{
					if (isMovingCamera)
					{
						CurrentCameraSpeed = _dynamicCameraSpeed;
					}
					else
					{
						CurrentCameraSpeed = EditModeCameraFastSpeed;
					}
				}
				else
				{
					CurrentCameraSpeed = EditModeCameraSpeed;
				}

				if (!Input.IsKeyDown(Keys.LeftControl) && !Input.IsKeyDown(Keys.RightControl))
				{
					if (Input.IsKeyDown(Keys.D))
						_cameraTargetPosition += new Vector2(CurrentCameraSpeed, 0) * Time.DeltaTime;
					if (Input.IsKeyDown(Keys.A))
						_cameraTargetPosition -= new Vector2(CurrentCameraSpeed, 0) * Time.DeltaTime;
					if (Input.IsKeyDown(Keys.W))
						_cameraTargetPosition -= new Vector2(0, CurrentCameraSpeed) * Time.DeltaTime;
					if (Input.IsKeyDown(Keys.S))
						_cameraTargetPosition += new Vector2(0, CurrentCameraSpeed) * Time.DeltaTime;
				}
			}

			// Works regardless of game window focus in case we select entities in EntityPane
			Core.Scene.Camera.Position = Vector2.Lerp(Core.Scene.Camera.Position, _cameraTargetPosition, _cameraLerp);
		}
	}

	private void ManageCameraZoom()
	{
		if (!IsGameWindowFocused)
			return;

		if (Input.MouseWheelDelta != 0)
		{
			bool isShiftHeld = Input.IsKeyDown(Keys.LeftShift);

			if (isShiftHeld)
			{
				// Modify camera movement speed instead of zoom
				float speedDelta = Input.MouseWheelDelta * CameraSpeedAdjustmentStep * Time.DeltaTime;
				SetDynamicCameraSpeed(_dynamicCameraSpeed + speedDelta);
			}
			else
			{
				// Normal zoom behavior
				if (Input.MouseWheelDelta > 0)
				{
					Core.Scene.Camera.Zoom += EditorCameraZoomSpeed * Time.DeltaTime;
				}
				else if (Input.MouseWheelDelta < 0)
				{
					if (Core.Scene.Camera.Zoom - EditorCameraZoomSpeed * Time.DeltaTime > -0.9)
						Core.Scene.Camera.Zoom -= EditorCameraZoomSpeed * Time.DeltaTime;
				}
			}
		}
		else if (!Core.IsEditMode && !Core.IsPauseMode)
		{
			Core.Scene.Camera.Zoom = Camera.DefaultZoom;
		}
	}

	/// <summary>
	/// Reset dynamic camera speed to default
	/// </summary>
	public static void ResetDynamicCameraSpeed()
	{
		_dynamicCameraSpeed = EditModeCameraSpeed;
	}

	/// <summary>
	/// Set the dynamic camera speed directly
	/// </summary>
	public static void SetDynamicCameraSpeed(float speed)
	{
		_dynamicCameraSpeed = MathHelper.Clamp(speed, EditModeCameraMinSpeed, EditModeCameraMaxSpeed);
	}

	/// <summary>
	/// Get the current dynamic camera speed
	/// </summary>
	public static float GetDynamicCameraSpeed()
	{
		return _dynamicCameraSpeed;
	}

	#region Public API

	/// <summary>
	/// registers an Action that will be called and any ImGui drawing can be done in it
	/// </summary>
	/// <param name="drawCommand"></param>
	public void RegisterDrawCommand(Action drawCommand)
	{
		_drawCommands.Add(drawCommand);
	}

	/// <summary>
	/// removes the Action from the draw commands
	/// </summary>
	/// <param name="drawCommand"></param>
	public void UnregisterDrawCommand(Action drawCommand)
	{
		_drawCommands.Remove(drawCommand);
	}

	/// <summary>
	/// Creates a pointer to a texture, which can be passed through ImGui calls such as <see cref="ImGui.Image" />.
	/// That pointer is then used by ImGui to let us know what texture to draw
	/// </summary>
	/// <param name="textureId"></param>
	public void UnbindTexture(IntPtr textureId)
	{
		_renderer.UnbindTexture(textureId);
	}

	/// <summary>
	/// Removes a previously created texture pointer, releasing its reference and allowing it to be deallocated
	/// </summary>
	/// <param name="texture"></param>
	/// <returns></returns>
	public IntPtr BindTexture(Texture2D texture)
	{
		return _renderer.BindTexture(texture);
	}

	/// <summary>
	/// Creates a normal EntityInspectorWindow window
	/// </summary>
	/// <param name="entity"></param>
	public void OpenSeparateEntityInspector(Entity entity)
	{
		// Only check pop-out inspectors (skip [0] which is always the main inspector)
		if (_entityInspectors.Skip(1).Any(i => i.Entity == entity))
			return;

		entitynspectorInitialSpawnOffset += entitynspectorSpawnOffsetIncremental;

		var inspector = new EntityInspectorWindow(this, entity, isMain: false, entitynspectorInitialSpawnOffset);
		_entityInspectors.Add(inspector);

		inspector.SetWindowFocus();
	}

	/// <summary>
	/// Creates (or replaces) a EntityInspectorWindow
	/// </summary>
	/// <param name="entity"></param>
	public void OpenMainEntityInspector(Entity entity = null)
	{
		if (IsInspectorTabLocked)
			return;

		if (MainEntityInspectorWindow != null)
		{
			if (MainEntityInspectorWindow.Entity == entity)
				return;

			MainEntityInspectorWindow.SetEntity(entity);
		}
		else
		{
			MainEntityInspectorWindow = new EntityInspectorWindow(this, null, isMain: true);
		}
	}

	/// <summary>
	/// removes the EntityInspectorWindow for this Entity
	/// </summary>
	/// <param name="entity"></param>
	public void CloseEntityInspector(Entity entity)
	{
		for (var i = 0; i < _entityInspectors.Count; i++)
		{
			var inspector = _entityInspectors[i];
			if (inspector.Entity == entity)
			{
				_entityInspectors.RemoveAt(i);

				if (entitynspectorInitialSpawnOffset - entitynspectorSpawnOffsetIncremental >=
				    0) // Reset the previous spawn offset 
					entitynspectorInitialSpawnOffset -= entitynspectorSpawnOffsetIncremental;

				return;
			}
		}
	}

	/// <summary>
	/// removes the EntityInspectorWindow
	/// </summary>
	/// <param name="entityInspectorWindow"></param>
	public void CloseEntityInspector(EntityInspectorWindow entityInspectorWindow)
	{
		_entityInspectors.RemoveAt(_entityInspectors.IndexOf(entityInspectorWindow));

		// Reset the previous spawn offset 
		if (entitynspectorInitialSpawnOffset - entitynspectorSpawnOffsetIncremental >= 0)
			entitynspectorInitialSpawnOffset -= entitynspectorSpawnOffsetIncremental;
	}

	public void CloseMainEntityInspector()
	{
		MainEntityInspectorWindow = null;
	}

	/// <summary>
	/// Refreshes the main entity inspector's component inspectors.
	/// Call this after making changes to entity components.
	/// </summary>
	public void RefreshMainEntityInspector()
	{
		MainEntityInspectorWindow?.RefreshComponentInspectors();
	}

	/// <summary>
	/// Opens (or re-focuses) the dedicated <see cref="SceneComponentInspectorWindow"/> for
	/// the given component. If the window is already showing a different component it is
	/// replaced; if it is showing the same one it is just brought to the front.
	/// </summary>
	public void OpenSceneComponentInspector(SceneComponent component)
	{
		if (_sceneComponentInspectorWindow == null)
		{
			_sceneComponentInspectorWindow = new SceneComponentInspectorWindow(component);
		}
		else
		{
			_sceneComponentInspectorWindow.IsOpen = true;
			_sceneComponentInspectorWindow.SetComponent(component);
		}

		_sceneComponentInspectorWindow.SetWindowFocus();
	}

	#endregion

	#region Save Changes for AppExit/ SceneChange / SceneReset

	private void OnAppExitSaveChanges(bool pending)
	{
		if (pending)
		{
			// Only show the prompt if there are unsaved changes
			if (EditorChangeTracker.IsDirty)
				_pendingExit = true;
			else
				Core.ConfirmAndExit();
		}
	}

	private void DrawSaveChangesPrompt()
	{
		// Handle exit prompt
		if (_pendingExit)
		{
			ImGui.OpenPopup("Save Changes?##Exit");
			_pendingExit = false;
		}

		// Handle scene change prompt
		if (_pendingSceneChange)
		{
			ImGui.OpenPopup("Save Changes?##SceneChange");
			_pendingSceneChange = false;
		}

		// Handle reset scene prompt
		if (_pendingResetScene)
		{
			ImGui.OpenPopup("Save Changes?##ResetScene");
			_pendingResetScene = false;
		}

		// Handle project close prompt
		if (_pendingProjectClose)
		{
			ImGui.OpenPopup("Save Changes?##ProjectClose");
			_pendingProjectClose = false;
		}

		if (_pendingRelaunch)
		{
			ImGui.OpenPopup("Save Changes?##Relaunch");
			_pendingRelaunch = false;
		}

		DrawSavePromptModal("Save Changes?##Exit", () =>
		{
			Core.ConfirmAndExit();
		});

		DrawSavePromptModal("Save Changes?##SceneChange", () =>
		{
			if (_requestedSceneFactory != null)
			{
				ActivateScene(_requestedSceneFactory());
			}
			else if (!string.IsNullOrEmpty(_requestedSceneName))
			{
				LoadSceneByName(_requestedSceneName);
			}
			else if (_requestedSceneType != null)
			{
				// Fallback for old system
				var scene = (Scene)Activator.CreateInstance(_requestedSceneType);
				Core.StartSceneTransition(new FadeTransition(() => scene));
			}
			_requestedSceneName = null;
			_requestedSceneType = null;
			_requestedSceneFactory = null;
		});

		DrawSavePromptModal("Save Changes?##ResetScene", () =>
		{
			ResetScene();
		});

		DrawSavePromptModal("Save Changes?##ProjectClose", () =>
		{
			CloseCurrentProject();
		});

		DrawSavePromptModal("Save Changes?##Relaunch", () =>
		{
			RelaunchEditor();
		});

		DrawRelaunchConfirmPrompt();
	}

	private void DrawSavePromptModal(string popupId, Action onDiscardChanges)
	{
		var center = new System.Numerics.Vector2(Screen.Width * 0.5f, Screen.Height * 0.4f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new System.Numerics.Vector2(0.5f, 0.5f));
		ImGui.SetNextWindowSize(new System.Numerics.Vector2(450, 0), ImGuiCond.Appearing);

		bool open = true;
		if (Gui.BeginPopupModal(popupId, ref open, ImGuiWindowFlags.AlwaysAutoResize))
		{
			// Determine the action context from popup ID
			string actionContext = "continuing";
			if (popupId.Contains("Exit"))
				actionContext = "exiting";
			else if (popupId.Contains("SceneChange"))
				actionContext = "changing scenes";
			else if (popupId.Contains("ResetScene"))
				actionContext = "resetting the scene";
			else if (popupId.Contains("ProjectClose"))
				actionContext = "closing the project";
			else if (popupId.Contains("Relaunch"))
				actionContext = "restarting the editor";

			ImGui.Text("You have unsaved changes!");
			ImGui.Spacing();

			// Display pending changes
			var pendingChanges = EditorChangeTracker.ChangedObjects;
			if (pendingChanges.Count > 0)
			{
				ImGuiSafe.TextWrappedSafe($"The following changes will be lost if you don't save before {actionContext}:");
				ImGui.Spacing();

				float maxHeight = Math.Min(300f, pendingChanges.Count * 25f);

				ImGui.BeginChild("##changes_list", new System.Numerics.Vector2(420, maxHeight), true);

				foreach (var (obj, description) in pendingChanges)
				{
					ImGuiSafe.BulletTextSafe(description);

					if (ImGui.IsItemHovered() && obj != null)
					{
						ImGuiSafe.SetTooltipSafe($"Type: {obj.GetType().Name}");
					}
				}

				ImGui.EndChild();
				ImGui.Spacing();
			}

			ImGuiSafe.TextWrappedSafe($"Do you want to save your changes before {actionContext}?");

			VoltageEditorUtils.MediumVerticalSpace();

			var buttonWidth = 120f;
			var spacing = 10f;
			var totalButtonWidth = (buttonWidth * 3) + (spacing * 2);
			var windowWidth = ImGui.GetWindowSize().X;
			var centerStart = (windowWidth - totalButtonWidth) * 0.5f;

			ImGui.SetCursorPosX(centerStart);

			if (Gui.Button("Save", new System.Numerics.Vector2(buttonWidth, 0)))
			{
				Core.Schedule(0.1f, false, this, async _ =>
				{
					await SaveSceneAsyncAndThenAct();
					onDiscardChanges();
				});
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();

			if (Gui.Button("Don't Save", new System.Numerics.Vector2(buttonWidth, 0)))
			{
				// Re-read from disk: clearing the dirty mark alone leaves the edits in the shared cache instance.
				DataAssetWindow?.DiscardChanges();
				EditorChangeTracker.Clear();
				onDiscardChanges();
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();

			if (Gui.Button("Cancel", new System.Numerics.Vector2(buttonWidth, 0)))
			{
				_requestedSceneName = null;
				_requestedSceneType = null;
				_requestedSceneFactory = null;
				_pendingProjectClose = false;
				ImGui.CloseCurrentPopup();
			}

			Gui.EndPopup();
		}
	}

	/// <summary>Restarts the editor in place, reopening the current project. The only way to pick up a rebuilt plugin or script assembly, since .NET cannot unload one.</summary>
	public void RequestEditorRelaunch()
	{
		if (EditorChangeTracker.IsDirty)
			_pendingRelaunch = true;
		else
			_pendingRelaunchConfirm = true;
	}

	/// <summary>Starts a second instance, then exits this one. Order matters: if the new process fails to start we must not exit.</summary>
	private void RelaunchEditor()
	{
		System.Diagnostics.ProcessStartInfo startInfo;
		try
		{
			startInfo = BuildRelaunchStartInfo();
		}
		catch (Exception ex)
		{
			Plugins.PluginLog.Error($"Could not prepare the relaunch, so nothing was closed: {ex.Message}");
			return;
		}

		if (startInfo == null)
		{
			Plugins.PluginLog.Error(
				"Could not work out how to relaunch the editor, so nothing was closed. Restart it manually.");
			return;
		}

		// Written now rather than at exit, or the replacement starts from the previous session.
		try
		{
			PersistSettings();
		}
		catch (Exception ex)
		{
			Plugins.PluginLog.Warn($"Could not save editor settings before relaunching: {ex.Message}");
		}

		var command = DescribeRelaunch(startInfo);

		System.Diagnostics.Process child;
		try
		{
			child = System.Diagnostics.Process.Start(startInfo);
		}
		catch (Exception ex)
		{
			Plugins.PluginLog.Error($"Relaunch failed, so nothing was closed: {ex.Message}\nCommand: {command}");
			return;
		}

		if (child == null)
		{
			Plugins.PluginLog.Error($"Relaunch failed: no process started, so nothing was closed.\nCommand: {command}");
			return;
		}

		// Long enough to cover graphics and content init, where a dying replacement dies.
		if (child.WaitForExit(2500))
		{
			var reason = ReadRelaunchLogTail();
			Plugins.PluginLog.Error(
				$"The new editor started and then exited (code {child.ExitCode}), so this one was left open." +
				$"\nCommand: {command}" +
				(reason == null ? $"\nNothing was written to {RelaunchLogPath}." : $"\nIt reported:\n{reason}"));
			return;
		}

		Plugins.PluginLog.Log($"Relaunching the editor: {command}");
		Core.ConfirmAndExit();
	}

	/// <summary>The command being run, so a failed relaunch can be reproduced from a terminal.</summary>
	private static string DescribeRelaunch(System.Diagnostics.ProcessStartInfo startInfo)
	{
		var args = string.Join(" ", startInfo.ArgumentList);
		return string.IsNullOrEmpty(args) ? startInfo.FileName : $"{startInfo.FileName} {args}";
	}

	/// <summary>The last few lines the failed replacement wrote, or null when it wrote nothing.</summary>
	private static string ReadRelaunchLogTail()
	{
		try
		{
			if (!File.Exists(RelaunchLogPath))
				return null;

			var lines = File.ReadAllLines(RelaunchLogPath);
			var tail = lines.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(12).ToArray();
			return tail.Length == 0 ? null : string.Join("\n", tail);
		}
		catch (Exception ex)
		{
			return $"(could not read {RelaunchLogPath}: {ex.Message})";
		}
	}

	/// <summary>Resolves how this editor was launched. The dotnet host needs the managed dll passed back as the first argument; an apphost launch does not.</summary>
	private static System.Diagnostics.ProcessStartInfo BuildRelaunchStartInfo()
	{
		var executable = Environment.ProcessPath;
		if (string.IsNullOrEmpty(executable))
			executable = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;

		if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
			return null;

		var arguments = new List<string>();

		var host = Path.GetFileNameWithoutExtension(executable);
		if (host.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
		{
			var managedDll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
			if (string.IsNullOrEmpty(managedDll) || !File.Exists(managedDll))
				return null;

			arguments.Add(managedDll);
		}

		// Passed explicitly so the new editor opens the project that was actually in front of you.
		try
		{
			var project = ProjectManager.Instance?.LastProjectPath;
			if (!string.IsNullOrWhiteSpace(project) && File.Exists(project))
				arguments.Add(project);
		}
		catch (Exception ex)
		{
			Plugins.PluginLog.Warn($"Relaunching without a project argument: {ex.Message}");
		}

		var startInfo = new System.Diagnostics.ProcessStartInfo
		{
			UseShellExecute = false,
			WorkingDirectory = AppContext.BaseDirectory,
		};

		// Launched through a shell so startup output is redirected to a file; without it a failed
		// start writes nothing. 'exec' makes the returned process really be the editor.
		if (!OperatingSystem.IsWindows())
		{
			var quoted = string.Join(" ", new[] { executable }.Concat(arguments).Select(ShellQuote));

			startInfo.FileName = "/bin/sh";
			startInfo.ArgumentList.Add("-c");
			startInfo.ArgumentList.Add($"exec {quoted} > {ShellQuote(RelaunchLogPath)} 2>&1");
			return startInfo;
		}

		startInfo.FileName = executable;
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);

		return startInfo;
	}

	/// <summary>Where a relaunched editor's own output goes, so a failed restart leaves evidence.</summary>
	private static string RelaunchLogPath =>
		Path.Combine(EditorStorage.LogsDirectory, "relaunch.log");

	private static string ShellQuote(string value) => "'" + (value ?? string.Empty).Replace("'", "'\\''") + "'";

	/// <summary>The plain "are you sure" shown when there is nothing unsaved to lose.</summary>
	private void DrawRelaunchConfirmPrompt()
	{
		if (_pendingRelaunchConfirm)
		{
			ImGui.OpenPopup("Restart Editor?##Relaunch");
			_pendingRelaunchConfirm = false;
		}

		var center = new System.Numerics.Vector2(Screen.Width * 0.5f, Screen.Height * 0.4f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new System.Numerics.Vector2(0.5f, 0.5f));
		ImGui.SetNextWindowSize(new System.Numerics.Vector2(460, 0), ImGuiCond.Appearing);

		bool open = true;
		if (!Gui.BeginPopupModal("Restart Editor?##Relaunch", ref open, ImGuiWindowFlags.AlwaysAutoResize))
			return;

		ImGui.Text("Restart the editor?");
		ImGui.Spacing();
		ImGuiSafe.TextWrappedSafe(
			"This closes the editor and opens it again on the same project. It is the only way to pick up a " +
			"rebuilt plugin or script assembly, because .NET cannot unload one from a running process.");

		ImGui.Spacing();
		ImGuiSafe.TextColoredSafe(new System.Numerics.Vector4(0.6f, 0.6f, 0.6f, 1f),
			"Your scene has no unsaved changes. Window layout and the open project are restored.");

		VoltageEditorUtils.MediumVerticalSpace();

		var buttonWidth = 120f;
		var totalButtonWidth = (buttonWidth * 2) + 10f;
		ImGui.SetCursorPosX((ImGui.GetWindowSize().X - totalButtonWidth) * 0.5f);

		if (Gui.Button("Restart", new System.Numerics.Vector2(buttonWidth, 0)))
		{
			ImGui.CloseCurrentPopup();
			RelaunchEditor();
		}

		ImGui.SameLine();

		if (Gui.Button("Cancel", new System.Numerics.Vector2(buttonWidth, 0)))
			ImGui.CloseCurrentPopup();

		Gui.EndPopup();
	}

	public void RequestResetScene()
	{
		if (EditorChangeTracker.IsDirty)
		{
			_pendingResetScene = true;
			_requestedResetSceneType = Core.Scene.GetType();
		}
		else
		{
			ResetScene();
		}
	}

	private void RequestProjectClose()
	{
		if (EditorChangeTracker.IsDirty)
		{
			_pendingProjectClose = true;
		}
		else
		{
			CloseCurrentProject();
		}
	}

	private void CloseCurrentProject()
	{
		var projectName = _projectManager.CurrentProject?.ProjectName;

		_projectManager.UnloadCurrentProject();
		EditorDebug.Log($"Project closed: {projectName}");

		EditorChangeTracker.Clear();
	}

	private void ResetScene()
	{
		// An isolated prefab scene has no file behind it, so reset means re-instantiating the .vprefab.
		if (IsInPrefabEditScene && ReloadPrefabEditScene())
			return;

		var sceneManager = SceneManager.Instance;
		if (sceneManager.HasLoadedScene)
		{
			// Reload from file
			sceneManager.ReloadCurrentScene();
		}
		else
		{
			// Fallback to old system for backwards compatibility
			var newScene = (Scene)Activator.CreateInstance(_requestedResetSceneType ?? Core.Scene.GetType());
			Core.Scene = newScene;
		}
		EditorChangeTracker.Clear();
	}

	/// <summary>Re-opens the prefab under edit from its .vprefab, discarding the isolated scene. False when it cannot be resolved, so the caller can fall back.</summary>
	private bool ReloadPrefabEditScene()
	{
		var guid = _prefabEdit.PrefabGuid;
		var prefabName = _prefabEdit.PrefabName;
		var path = Voltage.Editor.Assets.AssetDatabase.Instance?.GetPath(guid);

		if (string.IsNullOrEmpty(path) || !File.Exists(path))
		{
			Debug.Warn($"Reset Scene: no .vprefab on disk for '{prefabName}' - it may have been moved or deleted.");
			return false;
		}

		Voltage.Data.PrefabData? prefabData;
		try
		{
			prefabData = SerializationManager.Instance.LoadPrefabDataFromPath(path);
		}
		catch (Exception ex)
		{
			Debug.Error($"Reset Scene: failed to reload prefab '{prefabName}': {ex.Message}");
			return false;
		}

		if (!prefabData.HasValue)
			return false;

		// Read before the reload: ActivateScene already cleared the current scene path.
		var prevScenePath = _prefabEdit.PreviousScenePath;
		var prevSceneName = _prefabEdit.PreviousSceneName;

		// Reset discards edits, so drop the session and the dirty flag first.
		_prefabEdit = null;
		EditorChangeTracker.Clear();
		OpenPrefabIsolated(prefabData.Value, prefabName, guid, prevScenePath, prevSceneName,
			promptOnUnsaved: false);
		return true;
	}

	private async Task SaveSceneAsyncAndThenAct()
	{
		// Data assets ride along with the same prompt; they are in the same unsaved list.
		DataAssetWindow?.SaveIfDirty();

		// While editing a prefab in isolation the "scene" is the prefab — save it back to its .vprefab
		// rather than trying (and failing) to write a scene file for the temporary in-memory scene.
		if (IsInPrefabEditScene)
		{
			SavePrefabEditToOriginal();
			return;
		}

		await _SerializationManager.SaveSceneChangesAsync();
	}

	#endregion

	public void OpenAnimationEventInspector(SpriteAnimator animator)
	{
		if (_animationEventInspector == null || !ShowAnimationEventInspector)
		{
			_animationEventInspector = new AnimationEventInspector(animator);
			SpriteAnimatorFileInspector.AnimationEventInspectorInstance = _animationEventInspector;
			RegisterDrawCommand(_animationEventInspector.Draw);
		}
		else
		{
			_animationEventInspector.SetAnimator(animator);
		}

		ShowAnimationEventInspector = true;
		_animationEventInspector.SetWindowFocus();
	}

	public void DrawSelectedEntityOutlines()
	{
		// Only update cache if selection changed
		if (_lastSelectedEntities == null ||
		    !SceneGraphWindow.EntityPane.SelectedEntities.SequenceEqual(_lastSelectedEntities))
		{
			_highlightedEntities.Clear();
			foreach (var entity in SceneGraphWindow.EntityPane.SelectedEntities)
			{
				var collider = entity.GetComponent<Collider>();
				_highlightedEntities.Add((entity, collider));
			}

			_lastSelectedEntities = SceneGraphWindow.EntityPane.SelectedEntities.ToList();
		}

		// Draw highlights using cached info
		foreach (var (entity, collider) in _highlightedEntities)
		{
			RectangleF bounds;
			if (collider != null)
			{
				bounds = collider.Bounds;
			}
			else
			{
				var pos = entity.Transform.Position;
				bounds = new RectangleF(pos.X - 8, pos.Y - 8, 16, 16);
			}

			Debug.DrawHollowRect(bounds, Color.Yellow);
		}
	}

	public void ClearHighlightCache()
	{
		_highlightedEntities.Clear();
		_lastSelectedEntities = null;
	}

	/// <summary>
	/// Requests a scene change, showing the unsaved-changes prompt when the scene is dirty.
	/// Internal so that editor windows (e.g. SceneGraphWindow) can trigger scene loads.
	/// </summary>
	internal void RequestSceneChange(string sceneName)
	{
		if (EditorChangeTracker.IsDirty)
		{
			TriggerSceneChangePrompt(sceneName);
		}
		else
		{
			LoadSceneByName(sceneName);
		}
	}

	private void TriggerSceneChangePrompt(string sceneName)
	{
		_pendingSceneChange = true;
		_requestedSceneName = sceneName;
		_requestedSceneType = null;
		_requestedSceneFactory = null;
		_pendingExit = false;
	}

	/// <summary>
	/// Opens a prefab in an isolated, in-memory edit scene: a fresh scene containing only a single
	/// <see cref="Entity.InstanceType.SerializedPrefab"/> instance of the prefab, so it can be inspected
	/// and tweaked without touching the real game scenes. Routes through the unsaved-changes prompt just
	/// like a normal scene change. The scene is never written to disk; edits are pushed back to the
	/// <c>.vprefab</c> via the inspector's existing "save / apply to prefab" actions.
	/// </summary>
	internal void OpenPrefabIsolated(Voltage.Data.PrefabData prefabData, string prefabName, Guid prefabGuid)
	{
		// Remember the scene to return to (the current real, file-backed scene). Captured now, before
		// the swap, so "Go Back" can reload it.
		OpenPrefabIsolated(prefabData, prefabName, prefabGuid,
			SceneManager.Instance.CurrentScenePath, SceneManager.Instance.CurrentSceneName,
			promptOnUnsaved: true);
	}

	/// <summary>The previous scene is passed in because an isolated prefab scene has no current path to read; promptOnUnsaved is false when the caller is already inside the unsaved-changes prompt.</summary>
	private void OpenPrefabIsolated(Voltage.Data.PrefabData prefabData, string prefabName, Guid prefabGuid,
		string prevScenePath, string prevSceneName, bool promptOnUnsaved)
	{
		Func<Scene> factory = () =>
		{
			var scene = new Scene();
			scene.SceneData = new Voltage.Data.SceneData { Name = $"[Prefab] {prefabName}" };

			// Core.Scene assignment is deferred to the next frame's swap when a scene already exists,
			// so we cannot instantiate the prefab now (Core.Scene still points at the old scene). Queue
			// it; ProcessPendingPrefabInstantiation adds the instance once the swap has actually landed.
			_pendingPrefabScene = scene;
			_pendingPrefabData = prefabData;
			_pendingPrefabName = prefabName;
			_pendingPrefabGuid = prefabGuid;
			_pendingPrefabPrevScenePath = prevScenePath;
			_pendingPrefabPrevSceneName = prevSceneName;
			return scene;
		};

		if (promptOnUnsaved && EditorChangeTracker.IsDirty)
		{
			_pendingSceneChange = true;
			_requestedSceneName = null;
			_requestedSceneType = null;
			_requestedSceneFactory = factory;
			_pendingExit = false;
		}
		else
		{
			ActivateScene(factory());
		}
	}

	/// <summary>
	/// Adds the queued prefab instance to the isolated scene once the deferred Core.Scene swap has made
	/// that scene active. Runs every frame from <see cref="LayoutGui"/>; a no-op until the swap lands.
	/// </summary>
	private void ProcessPendingPrefabInstantiation()
	{
		if (_pendingPrefabScene == null)
			return;

		// Wait for the deferred swap: Core.Scene only becomes our scene after Core.Update runs.
		if (Core.Scene != _pendingPrefabScene)
			return;

		Entity instance = null;
		try
		{
			instance = SceneGraphWindow.CreateEntityFromPrefabData(
				_pendingPrefabData, _pendingPrefabName, _pendingPrefabGuid, Vector2.Zero);
		}
		catch (Exception ex)
		{
			Debug.Error($"OpenPrefabIsolated: failed to instantiate '{_pendingPrefabName}': {ex.Message}");
		}

		_prefabEdit = new PrefabEditSession
		{
			Instance = instance,
			PrefabName = _pendingPrefabName,
			PrefabGuid = _pendingPrefabGuid,
			PreviousScenePath = _pendingPrefabPrevScenePath,
			PreviousSceneName = _pendingPrefabPrevSceneName,
		};

		// CreateEntityFromPrefabData already selects the instance and routes it to the inspector.
		// Focus the editor camera on the prefab instance, matching the Scene Graph's click-to-focus.
		if (instance != null)
			_cursorSelectionManager.SetCameraTargetPosition(instance.Transform.Position);

		_pendingPrefabScene = null;
		_pendingPrefabData = default;
		_pendingPrefabName = null;
		_pendingPrefabGuid = default;
		_pendingPrefabPrevScenePath = null;
		_pendingPrefabPrevSceneName = null;

		EditorChangeTracker.Clear();
		EditorDebug.Log($"Opened prefab '{_prefabEdit.PrefabName}' in isolated edit scene.");
	}

	/// <summary>
	/// Saves the prefab being edited in isolation back to its source <c>.vprefab</c> (apply-to-original).
	/// Invoked by the Scene Graph's "Save Prefab" button while in a prefab edit scene.
	/// </summary>
	public async void SavePrefabEditToOriginal()
	{
		if (_prefabEdit?.Instance == null)
			return;

		var instance = _prefabEdit.Instance;
		if (instance.Type != Entity.InstanceType.SerializedPrefab || string.IsNullOrEmpty(instance.OriginalPrefabName))
		{
			EditorDebug.Error("Save Prefab: the edited entity is not a valid prefab instance.");
			return;
		}

		bool saved = await SerializationManager.Instance.InvokePrefabCreated(instance, true);
		if (saved)
		{
			EditorChangeTracker.Clear();
			NotificationSystem.ShowTimedNotification($"Saved prefab '{instance.OriginalPrefabName}'");
			EditorDebug.Log($"Saved prefab '{instance.OriginalPrefabName}' from edit scene.");
		}
		else
		{
			EditorDebug.Error($"Failed to save prefab '{instance.OriginalPrefabName}'.");
		}
	}

	/// <summary>
	/// Applies the edited prefab instance's component data to every other SerializedPrefab copy of the
	/// same prefab present in the active scene (apply-to-copies). In an isolated edit scene there are
	/// usually no other copies, so this is typically a no-op — the button exists for parity with the
	/// inspector and for scenes that contain multiple instances.
	/// </summary>
	public void ApplyPrefabEditToCopies()
	{
		if (_prefabEdit?.Instance == null)
			return;

		MainEntityInspectorWindow.ApplyEntityToPrefabCopies(_prefabEdit.Instance);
	}

	/// <summary>
	/// Leaves the isolated prefab edit scene and returns to the scene the user came from. Reloads that
	/// scene from its file; if it had no file (unsaved), falls back to the last used scene.
	/// </summary>
	public void ExitPrefabEditScene()
	{
		if (_prefabEdit == null)
			return;

		var prevPath = _prefabEdit.PreviousScenePath;
		_prefabEdit = null;

		// Clear any queued instantiation that might still be pending.
		_pendingPrefabScene = null;

		if (!string.IsNullOrEmpty(prevPath) && File.Exists(prevPath))
		{
			SceneManager.Instance.LoadScene(prevPath);
		}
		else
		{
			SceneManager.Instance.LoadLastUsedScene();
		}

		EditorChangeTracker.Clear();
	}

	/// <summary>
	/// Makes <paramref name="scene"/> the active scene, applying the project design resolution (mirroring
	/// SceneManager.LoadScene) and clearing the SceneManager's file path so this temporary scene is never
	/// mistaken for a saved one.
	/// </summary>
	private void ActivateScene(Scene scene)
	{
		if (scene == null)
			return;

		if (ProjectManager.Instance.HasActiveProject)
		{
			var designRes = ProjectManager.Instance.CurrentProject.Settings.DesignResolution;
			scene.SetDesignResolution(designRes.Width, designRes.Height, designRes.ResolutionPolicy,
				designRes.HorizontalBleed, designRes.VerticalBleed);
		}

		Core.Scene = scene;
		SceneManager.Instance.ClearCurrentScene();
		EditorChangeTracker.Clear();
	}

	private void LoadSceneByName(string sceneName)
	{
		// Switching to a normal scene ends any prefab edit session.
		_prefabEdit = null;
		_pendingPrefabScene = null;
		SceneManager.Instance.LoadSceneByName(sceneName);
	}

	public void OnAnimationEventInspectorClosed()
	{
		ShowAnimationEventInspector = false;
		if (_animationEventInspector != null)
			UnregisterDrawCommand(_animationEventInspector.Draw);
		_animationEventInspector = null;
		SpriteAnimatorFileInspector.AnimationEventInspectorInstance = null;
	}
}