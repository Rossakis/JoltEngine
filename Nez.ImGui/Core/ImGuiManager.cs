using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Nez.Data;
using Nez.Editor;
using Nez.ImGuiTools.Inspectors.CustomInspectors;
using Nez.ImGuiTools.UndoActions;
using Nez.Sprites;
using Nez.UI;
using Nez.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using Nez.ImGuiTools.Inspectors;
using Nez.ImGuiTools.Utils;
using Num = System.Numerics;
using Nez.ImGuiTools.FilePickers;


namespace Nez.ImGuiTools;

public partial class ImGuiManager : GlobalManager, IFinalRenderDelegate, IDisposable
{
	// Public values
	public bool ShowDemoWindow = false;
	public bool ShowStyleEditor = false;
	public bool ShowSceneGraphWindow = true;
	public bool ShowCoreWindow = true;
	public bool ShowSeparateGameWindow = true;
	public bool ShowAnimationEventInspector = false;
	public bool ShowMenuBar = true;

	public string GameWindowTitle => _gameWindowTitle;
	public bool IsGameWindowFocused = false;
	public Num.Vector2 GameWindowPosition;
	public Num.Vector2 GameWindowSize;
	public float MainWindowPositionY => _mainMenuBarHeight + _editorToolsBarHeight * FontSizeMultiplier + 1f;
	public bool FocusGameWindowOnMiddleClick = false;
	public bool FocusGameWindowOnRightClick = false;
	public bool DisableKeyboardInputWhenGameWindowUnfocused = true;
	public bool DisableMouseWheelWhenGameWindowUnfocused = true;
	public float GameWindowWidth { get; private set; }
	public float GameWindowHeight { get; private set; }

	public float FontSizeMultiplier => ImGui.GetIO().FontGlobalScale;

	// Public instances
	public ImGuiCursorSelectionManager CursorSelectionManager => _cursorSelectionManager;
	public ImguiImageLoader ImageLoader => _imageLoader;
	public SceneGraphWindow SceneGraphWindow { get; private set; }
	public MainEntityInspector MainEntityInspector { get; private set; }
	public bool IsInspectorTabLocked = false;
	public AnimationEventInspector AnimationEventInspectorInstance
	{
		get => _animationEventInspector;
		private set => _animationEventInspector = value;
	}

	private List<Type> _sceneSubclasses = new();
	private System.Reflection.MethodInfo[] _themes;
	private CoreWindow _coreWindow = new();
	private DebugWindow _debugWindow = new();

	private Num.Vector2 normalEntityInspectorStartPos;
	private int entitynspectorInitialSpawnOffset = 0;
	private static int entitynspectorSpawnOffsetIncremental = 20;

	private AnimationEventInspector _animationEventInspector;
	private SpriteAtlasEditorWindow _spriteAtlasEditorWindow;
	private List<EntityInspector> _entityInspectors = new();
	private List<Action> _drawCommands = new();
	private ImGuiRenderer _renderer;
	private ImGuiInput _input = new ImGuiInput();
	private ImGuiCursorSelectionManager _cursorSelectionManager;
	private ImguiImageLoader _imageLoader;
	private string _gameWindowTitle;
	private ImGuiWindowFlags _gameWindowFlags = 0;

	private RenderTarget2D _lastRenderTarget;
	private IntPtr _renderTargetId = IntPtr.Zero;
	private Num.Vector2? _gameViewForcedSize;
	private WindowPosition? _gameViewForcedPos;
	private float _mainMenuBarHeight;
	private readonly float _editorToolsBarHeight = 30f;

	#region Inspector Tab
	public float InspectorTabWidth => _inspectorTabWidth;
	private float _inspectorTabWidth = 500f;
	private readonly float _minInspectorWidth = 1f;
	private readonly float _maxInspectorWidth = Screen.MonitorWidth;
	public float InspectorWidthOffset = 4f; // offset the inner windows (core, entity inspector) to allow for edge selecting 
	
	public enum InspectorTab
	{
		EntityInspector,
		Core,
		Debug
	}

	private InspectorTab _selectedInspectorTab = InspectorTab.EntityInspector;
	public InspectorTab SelectedInspectorTab
	{
		get => _selectedInspectorTab;
		set
		{
			if(IsInspectorTabLocked)
				return;
			_selectedInspectorTab = value;
		}
	}
	#endregion


	// Camera Params
	public static float EditModeCameraSpeed = 250f;
	public static float EditModeCameraFastSpeed = 500f;
	private const float EditorCameraZoomSpeed = 1f;

	// Add these new fields for dynamic speed control
	public static float EditModeCameraMinSpeed = 50f;
	public static float EditModeCameraMaxSpeed = 3000f;
	private static float _dynamicCameraSpeed = EditModeCameraSpeed; // Current dynamic speed
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

	// Before game exits, we may need to prompt to save changes (aka, pending actions)
	private bool _pendingExit = false;
	private bool _pendingSceneChange = false;
	private Type _requestedSceneType = null;
	private bool _pendingResetScene = false;
	private Type _requestedResetSceneType = null;
	private Task _pendingSaveTask = null;
	private ExitPromptType _pendingActionAfterSave;

	#region Event Handlers

	/// <summary>
	/// Can be used to wait for the scene changes to happen first.
	/// </summary>
	public event Func<Task> OnSaveSceneAsync;

	public event Func<Entity, bool, Task<bool>> OnPrefabCreated;
	public event Func<string, PrefabData> OnPrefabLoadRequested;
	public event Action<Entity, object> OnLoadEntityData; // Add this for loading entity data

	public void InvokeSaveSceneChanges()
	{
		OnSaveSceneAsync?.Invoke();
	}

	public async Task<bool> InvokePrefabCreated(Entity prefabEntity, bool overrideExistingPrefab)
	{
		if (OnPrefabCreated != null)
		{
			return await OnPrefabCreated.Invoke(prefabEntity, overrideExistingPrefab);
		}

		return false;
	}

	public PrefabData InvokePrefabLoadRequested(string prefabName)
	{
		if (OnPrefabLoadRequested != null)
		{
			return OnPrefabLoadRequested.Invoke(prefabName);
		}

		return new PrefabData();
	}

	public void InvokeLoadEntityData(Entity entity, object entityData)
	{
		OnLoadEntityData?.Invoke(entity, entityData);
	}

	#endregion

	public ImGuiManager(ImGuiOptions options = null)
	{
		if (options == null)
			options = new ImGuiOptions();

		_gameWindowTitle = options._gameWindowTitle;
		_gameWindowFlags = options._gameWindowFlags;
		_gameViewForcedPos = WindowPosition.Top;

		LoadSettings();

		_renderer = new ImGuiRenderer(Core.Instance, _input);
		_renderer.RebuildFontAtlas(options);

		Core.Emitter.AddObserver(CoreEvents.SceneChanged, OnSceneChanged);
		NezImGuiThemes.DarkTheme1();

		// find all Scenes
		_sceneSubclasses = ReflectionUtils.GetAllSubclasses(typeof(Scene), true);

		// tone down indent
		ImGui.GetStyle().IndentSpacing = 12;
		ImGui.GetIO().ConfigWindowsMoveFromTitleBarOnly = true;

		// find all themes
		_themes = typeof(NezImGuiThemes).GetMethods(System.Reflection.BindingFlags.Static |
		                                            System.Reflection.BindingFlags.Public);
		SceneGraphWindow = new SceneGraphWindow();
		_cursorSelectionManager = new ImGuiCursorSelectionManager(this);

		_imageLoader = new ImguiImageLoader();
		_imageLoader.LoadImages(_renderer);

		// Create default Main Entity Inspector window when current scene is finished loading the entities
		Scene.OnFinishedAddingEntitiesWithData += OpenMainEntityInspector;
		Core.EmitterWithPending.AddObserver(CoreEvents.Exiting, OnAppExitSaveChanges);

		Core.OnResetScene += RequestResetScene;
		Core.OnSwitchEditMode += OnEditModeSwitched;
	}

	/// <summary>
	/// This is where we issue any and all ImGui commands to be drawn
	/// </summary>
	private void LayoutGui()
	{
		ImGui.GetIO().ConfigWindowsResizeFromEdges = true;

		if (ShowMenuBar)
			DrawMainMenuBar();

		DrawEditorToolsBar();

		if (ShowSeparateGameWindow)
			DrawGameWindow();

		SceneGraphWindow.Show(ref ShowSceneGraphWindow);
		DrawInspectorWindows();
		DrawEntityInspectors();

		for (var i = _drawCommands.Count - 1; i >= 0; i--)
			_drawCommands[i]();

		if (_spriteAtlasEditorWindow != null)
			if (!_spriteAtlasEditorWindow.Show())
				_spriteAtlasEditorWindow = null;

		if (ShowDemoWindow)
			ImGui.ShowDemoWindow(ref ShowDemoWindow);

		if (ShowStyleEditor)
		{
			ImGui.Begin("Style Editor", ref ShowStyleEditor);
			ImGui.ShowStyleEditor();
			ImGui.End();
		}

		UpdateCamera();
		NotificationSystem.Draw();
		GlobalKeyCommands();

		_cursorSelectionManager.UpdateSelection();
		DrawSelectedEntityOutlines();

		if (ShowAnimationEventInspector)
		{
			if (_animationEventInspector == null)
			{
				_animationEventInspector = new AnimationEventInspector(null);
				RegisterDrawCommand(_animationEventInspector.Draw);
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
	}

	public void GlobalKeyCommands()
	{
		if (ImGui.IsKeyPressed(ImGuiKey.F5, false))
			Core.InvokeResetScene();

		if (ImGui.IsKeyPressed(ImGuiKey.F1, false) || ImGui.IsKeyPressed(ImGuiKey.F2, false))
			Core.InvokeSwitchEditMode(!Core.IsEditMode);

		if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S, false))
			InvokeSaveSceneChanges();

		// This triggers the same exit/save prompt as the window close event
#if OS_WINDOWS || LINUX
		if (ImGui.GetIO().KeyAlt && ImGui.IsKeyPressed(ImGuiKey.F4, false) && !_pendingExit)
			OnAppExitSaveChanges(true);
#elif OS_MAC
		if (ImGui.GetIO().KeySuper && ImGui.IsKeyPressed(ImGuiKey.Q, false) && !_pendingExit)
			OnAppExitSaveChanges(true);
#endif
	}

	private void ManageUndoAndRedo()
	{
		if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Z, false))
		{
			EditorChangeTracker.Undo();
		}

		if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Y, false))
		{
			EditorChangeTracker.Redo();
		}
	}

	#region Drawing Methods
	/// <summary>
	/// draws the main menu bar
	/// </summary>
	private void DrawMainMenuBar()
	{
		if (ImGui.BeginMainMenuBar())
		{
			_mainMenuBarHeight = ImGui.GetWindowHeight();
			if (ImGui.BeginMenu("File"))
			{
				if (ImGui.MenuItem("Save Scene", "Ctrl+S"))
				{
					InvokeSaveSceneChanges();
				}

				ImGui.Separator();

				if (ImGui.MenuItem("Load Tiled Map"))
				{
					SceneGraphWindow.TmxFilePicker.Open();
				}

				if (ImGui.MenuItem("Load Aseprite Images"))
				{
					SceneGraphWindow.AsepriteFilePicker.Open();
				}

				ImGui.Separator();

				if (ImGui.MenuItem("Open Sprite Atlas Editor"))
					_spriteAtlasEditorWindow = _spriteAtlasEditorWindow ?? new SpriteAtlasEditorWindow();

				if (ImGui.MenuItem("Close ImGui Editor"))
					SetEnabled(false);

				if (ImGui.MenuItem("Exit"))
				{
					Core.ConfirmAndExit();
				}

				ImGui.EndMenu();
			}

			if (_sceneSubclasses.Count > 0 && ImGui.BeginMenu("Scenes"))
			{
				foreach (var sceneType in _sceneSubclasses)
					if (ImGui.MenuItem(sceneType.Name))
					{
						RequestSceneChange(sceneType);
					}

				ImGui.EndMenu();
			}

			if (_themes.Length > 0 && ImGui.BeginMenu("Themes"))
			{
				foreach (var theme in _themes)
					if (ImGui.MenuItem(theme.Name))
						theme.Invoke(null, new object[] { });

				ImGui.EndMenu();
			}

			if (ImGui.BeginMenu("Window"))
			{
				ImGui.MenuItem("ImGui Demo Window", null, ref ShowDemoWindow);
				ImGui.MenuItem("Style Editor", null, ref ShowStyleEditor);
				if (ImGui.MenuItem("Open imgui_demo.cpp on GitHub"))
				{
					var url = "https://github.com/ocornut/imgui/blob/master/imgui_demo.cpp";
					var startInfo = new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true };
					System.Diagnostics.Process.Start(startInfo);
				}

				ImGui.Separator();
				ImGui.MenuItem("Core Window", null, ref ShowCoreWindow);
				ImGui.MenuItem("Scene Graph Window", null, ref ShowSceneGraphWindow);
				ImGui.MenuItem("Separate Game Window", null, ref ShowSeparateGameWindow);
				ImGui.MenuItem("Animation Event Inspector", null, ref ShowAnimationEventInspector);
				ImGui.EndMenu();
			}

			ImGui.EndMainMenuBar();
		}
	}

	/// <summary>
	/// Handles the sprites selected from the Aseprite file picker.
	/// Creates entities with SpriteRenderer components for each selected sprite.
	/// </summary>
	private void HandleAsepriteSelection(AsepriteFilePicker.AsepriteSelection selection)
	{
		if (selection == null || selection.Sprites == null || selection.Sprites.Count == 0)
		{
			NotificationSystem.ShowTimedNotification("No sprites were generated from the Aseprite file.");
			return;
		}

		try
		{
			// Create entities for each sprite
			for (int i = 0; i < selection.Sprites.Count; i++)
			{
				var sprite = selection.Sprites[i];
				if (sprite == null)
					continue;

				// Create a new entity
				var entityName = $"{System.IO.Path.GetFileNameWithoutExtension(selection.FilePath)}_Frame{selection.FrameNumbers[i]}";
				var entity = new Entity(entityName);

				// Add SpriteRenderer component
				var spriteRenderer = entity.AddComponent(new SpriteRenderer(sprite));

				// Position entities in a grid layout for easier viewing
				int entitiesPerRow = 5;
				int row = i / entitiesPerRow;
				int col = i % entitiesPerRow;
				entity.Position = new Vector2(col * 100, row * 100); // Adjust spacing as needed

				// Add to scene
				Core.Scene.AddEntity(entity);
			}

			var layerInfo = selection.LayerNames != null && selection.LayerNames.Count > 0
				? $" (Layers: {string.Join(", ", selection.LayerNames)})"
				: "";

			NotificationSystem.ShowTimedNotification(
				$"Successfully loaded {selection.Sprites.Count} sprite(s) from {System.IO.Path.GetFileName(selection.FilePath)}{layerInfo}"
			);
		}
		catch (Exception ex)
		{
			NotificationSystem.ShowTimedNotification($"Error creating entities from Aseprite selection: {ex.Message}");
		}
	}

	private void DrawEditorToolsBar()
	{
		ImGui.SetNextWindowPos(new Num.Vector2(0, _mainMenuBarHeight), ImGuiCond.Always);
		ImGui.SetNextWindowSize(new Num.Vector2(Screen.Width, _editorToolsBarHeight * FontSizeMultiplier), ImGuiCond.Always);

		ImGui.Begin("EditorToolsBar",
			ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
			ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings);

		float buttonWidth = 80f * FontSizeMultiplier;
		float spacing = 12f * FontSizeMultiplier;
		float totalWidth = buttonWidth * 3 + spacing * 2;
		float windowWidth = ImGui.GetWindowWidth();
		float iconSize = 24f * FontSizeMultiplier;

		float windowHeight = ImGui.GetWindowHeight();
		float iconRowY = (windowHeight - iconSize) * 0.5f;
		ImGui.SetCursorPos(new Num.Vector2((windowWidth - totalWidth) * 0.5f, iconRowY));

		// Normal Button
		System.Numerics.Vector4 normalButtonColor;
		if (_cursorSelectionManager.SelectionMode == CursorSelectionMode.Normal)
			normalButtonColor = new System.Numerics.Vector4(0.2f, 0.5f, 1f, 1f);
		else
			normalButtonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];

		ImGui.PushStyleColor(ImGuiCol.Button, normalButtonColor);
		bool normalHovered = ImGui.ImageButton("Normal", _imageLoader.NormalCursorIconID, new Num.Vector2(iconSize, iconSize));
		if (normalHovered)
			_cursorSelectionManager.SelectionMode = CursorSelectionMode.Normal;
		ImGui.PopStyleColor();

		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Default Cursor (Press Q or 1)");
		}
		ImGui.SameLine(0, spacing);

		// Resize Button 
		System.Numerics.Vector4 resizeButtonColor;
		if (_cursorSelectionManager.SelectionMode == CursorSelectionMode.Resize)
			resizeButtonColor = new System.Numerics.Vector4(0.2f, 0.5f, 1f, 1f);
		else
			resizeButtonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];

		ImGui.PushStyleColor(ImGuiCol.Button, resizeButtonColor);
		bool resizeHovered = ImGui.ImageButton("Resize", _imageLoader.ResizeCursorIconID, new Num.Vector2(iconSize, iconSize));
		if (resizeHovered)
			_cursorSelectionManager.SelectionMode = CursorSelectionMode.Resize;
		ImGui.PopStyleColor();

		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Resize Entities (Press E or 2)");
		}
		ImGui.SameLine(0, spacing);

		// Rotate Button
		System.Numerics.Vector4 rotateButtonColor;
		if (_cursorSelectionManager.SelectionMode == CursorSelectionMode.Rotate)
			rotateButtonColor = new System.Numerics.Vector4(0.2f, 0.5f, 1f, 1f);
		else
			rotateButtonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];

		ImGui.PushStyleColor(ImGuiCol.Button, rotateButtonColor);
		bool rotateHovered = ImGui.ImageButton("Rotate", _imageLoader.RotateCursorIconID, new Num.Vector2(iconSize, iconSize));
		if (rotateHovered)
			_cursorSelectionManager.SelectionMode = CursorSelectionMode.Rotate;
		ImGui.PopStyleColor();

		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Rotate Entities (Press R or 3)");
		}

		ImGui.SameLine(0, spacing);

		// Collider Resize Button
		System.Numerics.Vector4 colliderResizeButtonColor;
		if (_cursorSelectionManager.SelectionMode == CursorSelectionMode.ColliderResize)
			colliderResizeButtonColor = new System.Numerics.Vector4(0.2f, 0.5f, 1f, 1f);
		else
			colliderResizeButtonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];

		ImGui.PushStyleColor(ImGuiCol.Button, colliderResizeButtonColor);

		bool colliderResizeHovered = ImGui.ImageButton("Collider Resize", _imageLoader.ColliderResizeCursorIconID, new Num.Vector2(iconSize, iconSize));
		if (colliderResizeHovered)
			_cursorSelectionManager.SelectionMode = CursorSelectionMode.ColliderResize;

		ImGui.PopStyleColor();

		if (ImGui.IsItemHovered())
		{
			ImGui.SetTooltip("Resize Colliders (Press T or 4)");
		}

		ImGui.End();
	}

	/// <summary>
	/// Inspector tabs for Entity Inspector and Core Window
	/// </summary>
	private void DrawInspectorWindows()
	{
		var inspectorPosX = Screen.Width - _inspectorTabWidth;
		var inspectorPosY = MainWindowPositionY;
		var inspectorSize = new Num.Vector2(_inspectorTabWidth, Screen.Height - MainWindowPositionY);
		var inspectorPos = new Num.Vector2(inspectorPosX, inspectorPosY);

		ImGui.SetNextWindowPos(inspectorPos, ImGuiCond.FirstUseEver);
		ImGui.SetNextWindowSize(inspectorSize, ImGuiCond.FirstUseEver);
		ImGui.Begin("##InspectorArea",
			ImGuiWindowFlags.NoTitleBar |
			ImGuiWindowFlags.NoMove |
			ImGuiWindowFlags.NoBringToFrontOnFocus |
			ImGuiWindowFlags.NoScrollbar);

		var currentWidth = ImGui.GetWindowSize().X;
		if (Math.Abs(currentWidth - _inspectorTabWidth) > 0.01f)
			_inspectorTabWidth = Math.Clamp(currentWidth, _minInspectorWidth, _maxInspectorWidth);

		float tabSpacing = 8f * FontSizeMultiplier;
		Num.Vector4 selectedColor = new Num.Vector4(0.2f, 0.5f, 1f, 1f); // blue
		Num.Vector4 defaultColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];

		// Entity Inspector Tab
		if (SelectedInspectorTab == InspectorTab.EntityInspector)
			ImGui.PushStyleColor(ImGuiCol.Button, selectedColor);
		else
			ImGui.PushStyleColor(ImGuiCol.Button, defaultColor);

		if (ImGui.Button("Inspector"))
			SelectedInspectorTab = InspectorTab.EntityInspector;
		if (ImGui.IsItemHovered())
			ImGui.SetTooltip("Show Entity Inspector");

		ImGui.PopStyleColor();
		ImGui.SameLine(0, tabSpacing);

		if (ShowCoreWindow)
		{
			if (SelectedInspectorTab == InspectorTab.Core)
				ImGui.PushStyleColor(ImGuiCol.Button, selectedColor);
			else
				ImGui.PushStyleColor(ImGuiCol.Button, defaultColor);

			if (ImGui.Button("Core"))
				SelectedInspectorTab = InspectorTab.Core;
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Show Core Window");

			ImGui.PopStyleColor();
			ImGui.SameLine(0, tabSpacing);
		}

		if (SelectedInspectorTab == InspectorTab.Debug)
			ImGui.PushStyleColor(ImGuiCol.Button, selectedColor);
		else
			ImGui.PushStyleColor(ImGuiCol.Button, defaultColor);

		if (ImGui.Button("Debug"))
			SelectedInspectorTab = InspectorTab.Debug;
		if (ImGui.IsItemHovered())
			ImGui.SetTooltip("Show Debug messages");

		ImGui.PopStyleColor();
		ImGui.Separator();
		ImGui.End();

		if (SelectedInspectorTab == InspectorTab.EntityInspector && MainEntityInspector != null && MainEntityInspector.IsOpen)
		{
			MainEntityInspector.Draw();
		}
		else if(SelectedInspectorTab == InspectorTab.Core && ShowCoreWindow)
		{
			_coreWindow.Show(ref ShowCoreWindow);
		}
		else if (SelectedInspectorTab == InspectorTab.Debug)
		{
			_debugWindow.Draw();
		}
	}

	/// <summary>
	/// draws all the EntityInspectors
	/// </summary>
	private void DrawEntityInspectors()
	{
		for (var i = _entityInspectors.Count - 1; i >= 0; i--)
			_entityInspectors[i].Draw();
	}
	#endregion


	private void UpdateCamera()
	{
		ManageCameraZoom();

		if (Core.IsEditMode)
		{
			// Camera Dragging with Middle Mouse
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

			Core.Scene.Camera.Position = Vector2.Lerp(Core.Scene.Camera.Position, _cameraTargetPosition, _cameraLerp);
		}

		// Remove entity selection logic from here
	}

	private void ManageCameraZoom()
	{
		if (Core.IsEditMode && Input.MouseWheelDelta != 0)
		{
			bool isShiftHeld = Input.IsKeyDown(Keys.LeftShift);

			if (isShiftHeld)
			{
				// Modify camera movement speed instead of zoom
				float speedDelta = Input.MouseWheelDelta * CameraSpeedAdjustmentStep * Time.DeltaTime;
				_dynamicCameraSpeed = MathHelper.Clamp(_dynamicCameraSpeed + speedDelta,
					EditModeCameraMinSpeed,
					EditModeCameraMaxSpeed);
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
		else if (!Core.IsEditMode)
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
		Scene.OnFinishedAddingEntitiesWithData -= OpenMainEntityInspector;
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
	/// Creates a normal EntityInspector window
	/// </summary>
	/// <param name="entity"></param>
	public void OpenSeparateEntityInspector(Entity entity)
	{
		// Only add if not already present as a pop-out
		if (_entityInspectors.Any(i => i.Entity == entity))
			return;

		entitynspectorInitialSpawnOffset += entitynspectorSpawnOffsetIncremental;
		var inspector = new EntityInspector(entity, entitynspectorInitialSpawnOffset);
		_entityInspectors.Add(inspector);

		inspector.SetWindowFocus();
	}

	/// <summary>
	/// Creates (or replaces) a MainEntityInspector
	/// </summary>
	/// <param name="entity"></param>
	public void OpenMainEntityInspector(Entity entity = null)
	{
		if (IsInspectorTabLocked)
			return;

		if (MainEntityInspector != null)
		{
			if (MainEntityInspector.Entity == entity)
				return;

			MainEntityInspector.SetEntity(entity);
		}
		else
		{
			MainEntityInspector = new MainEntityInspector(entity);
		}
	}

	/// <summary>
	/// removes the EntityInspector for this Entity
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
	/// removes the EntityInspector
	/// </summary>
	/// <param name="entityInspector"></param>
	public void CloseEntityInspector(EntityInspector entityInspector)
	{
		_entityInspectors.RemoveAt(_entityInspectors.IndexOf(entityInspector));

		// Reset the previous spawn offset 
		if (entitynspectorInitialSpawnOffset - entitynspectorSpawnOffsetIncremental >= 0) 
			entitynspectorInitialSpawnOffset -= entitynspectorSpawnOffsetIncremental;
	}

	public void CloseMainEntityInspector()
	{
		MainEntityInspector = null;
	}

	/// <summary>
	/// Refreshes the main entity inspector's component inspectors.
	/// Call this after making changes to entity components.
	/// </summary>
	public void RefreshMainEntityInspector()
	{
		MainEntityInspector?.RefreshComponentInspectors();
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

	private void RequestSceneChange(Type sceneType)
	{
		if (EditorChangeTracker.IsDirty)
		{
			TriggerSceneChangePrompt(sceneType);
		}
		else
		{
			ChangeScene(sceneType);
		}
	}

	private void TriggerSceneChangePrompt(Type sceneType)
	{
		_pendingSceneChange = true;
		_requestedSceneType = sceneType;
		_pendingExit = false;
	}

	private void ChangeScene(Type sceneType)
	{
		var scene = (Scene)Activator.CreateInstance(sceneType);
		Core.StartSceneTransition(new FadeTransition(() => scene));
	}

	private void OnEditModeSwitched(bool isEditMode)
	{
		// Only reset scene if switching to EditMode from PlayMode
		if (isEditMode && Core.ResetSceneAutomatically)
		{
			ResetScene();
		}
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

	private void ResetScene()
	{
		var newScene = (Scene)Activator.CreateInstance(_requestedResetSceneType ?? Core.Scene.GetType());
		Core.Scene = newScene;
		EditorChangeTracker.Clear();
		ShowAnimationEventInspector = false;
	}

	private async Task SaveSceneAsyncAndThenAct()
	{
		if (OnSaveSceneAsync != null)
			await OnSaveSceneAsync();
	}

	#endregion

	public void OpenAnimationEventInspector(SpriteAnimator animator)
	{
		if (_animationEventInspector == null)
		{
			_animationEventInspector = new AnimationEventInspector(animator);
			AnimationEventInspectorInstance = _animationEventInspector;
			RegisterDrawCommand(_animationEventInspector.Draw);
		}
		else
		{
			_animationEventInspector.SetAnimator(animator);
		}

		ShowAnimationEventInspector = true;
	}

	private List<(Entity entity, Collider collider)> _highlightedEntities = new();
	private IReadOnlyList<Entity> _lastSelectedEntities = null;

	public void DrawSelectedEntityOutlines()
	{
		// Only update cache if selection changed
		if (_lastSelectedEntities == null || !SceneGraphWindow.EntityPane.SelectedEntities.SequenceEqual(_lastSelectedEntities))
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
}