using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Nez.Editor;
using Nez.ImGuiTools.UndoActions;
using Nez.Sprites;
using Nez.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nez.Data;
using Nez.ImGuiTools.Inspectors.CustomInspectors;
using Num = System.Numerics;


namespace Nez.ImGuiTools;

public partial class ImGuiManager : GlobalManager, IFinalRenderDelegate, IDisposable
{
	public bool ShowDemoWindow = false;
	public bool ShowStyleEditor = false;
	public bool ShowSceneGraphWindow = true;
	public bool ShowCoreWindow = true;
	public bool ShowSeperateGameWindow = true;
	public bool ShowAnimationEventInspector = false;
	public bool ShowMenuBar = true;

	public bool FocusGameWindowOnMiddleClick = false;
	public bool FocusGameWindowOnRightClick = false;
	public bool DisableKeyboardInputWhenGameWindowUnfocused = true;
	public bool DisableMouseWheelWhenGameWindowUnfocused = true;

	private List<Type> _sceneSubclasses = new();
	private System.Reflection.MethodInfo[] _themes;

	private CoreWindow _coreWindow = new();
	public SceneGraphWindow SceneGraphWindow { get; private set; }
	public MainEntityInspector MainEntityInspector { get; private set; }

	public AnimationEventInspector AnimationEventInspectorInstance
	{
		get => _animationEventInspector;
		private set => _animationEventInspector = value;
	}

	private Num.Vector2 normalEntityInspectorStartPos;
	private int entitynspectorInitialSpawnOffset = 0;
	private static int entitynspectorSpawnOffsetIncremental = 20;

	private AnimationEventInspector _animationEventInspector;
	private SpriteAtlasEditorWindow _spriteAtlasEditorWindow;
	private List<EntityInspector> _entityInspectors = new();
	private List<Action> _drawCommands = new();
	private ImGuiRenderer _renderer;

	private Num.Vector2 _gameWindowFirstPosition;
	private string _gameWindowTitle;
	private ImGuiWindowFlags _gameWindowFlags = 0;

	private RenderTarget2D _lastRenderTarget;
	private IntPtr _renderTargetId = IntPtr.Zero;
	private Num.Vector2? _gameViewForcedSize;
	private WindowPosition? _gameViewForcedPos;
	private float _mainMenuBarHeight;
	public float GameWindowWidth { get; private set; }
	public float GameWindowHeight { get; private set; }

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

    private Vector2 _cameraTargetPosition;
    private float _cameraLerp = 0.4f;

    // Camera dragging with middle mouse button
    private bool _isCameraDragging = false;
    private Vector2 _cameraDragStartMouse;
    private Vector2 _cameraDragStartPosition;

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

		_gameWindowFirstPosition = options._gameWindowFirstPosition;
		_gameWindowTitle = options._gameWindowTitle;
		_gameWindowFlags = options._gameWindowFlags;
		_gameViewForcedPos = WindowPosition.Top;

		LoadSettings();
		_renderer = new ImGuiRenderer(Core.Instance);

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

		// Create default Main Entity Inspector window when current scene is finished loading the entities
		Scene.OnFinishedAddingEntitiesWithData += OpenMainEntityInspector;
		Core.EmitterWithPending.AddObserver(CoreEvents.Exiting, OnAppExitSaveChanges);

		Core.OnResetScene += RequestResetScene;
		Core.OnSwitchEditMode += OnEditModeSwitched;
	}

	/// <summary>
	/// this is where we issue any and all ImGui commands to be drawn
	/// </summary>
	private void LayoutGui()
	{
		ImGui.GetIO().ConfigWindowsResizeFromEdges = true;

		if (ShowMenuBar)
			DrawMainMenuBar();

		if (ShowSeperateGameWindow)
			DrawGameWindow();

		if (MainEntityInspector != null)
			MainEntityInspector.Draw();

		DrawEntityInspectors();

		for (var i = _drawCommands.Count - 1; i >= 0; i--)
			_drawCommands[i]();

		SceneGraphWindow.Show(ref ShowSceneGraphWindow);
		_coreWindow.Show(ref ShowCoreWindow);

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
				if (ImGui.MenuItem("Open Sprite Atlas Editor"))
					_spriteAtlasEditorWindow = _spriteAtlasEditorWindow ?? new SpriteAtlasEditorWindow();

				if (ImGui.MenuItem("Quit ImGui"))
				 SetEnabled(false);
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

			if (ImGui.BeginMenu("Game View"))
			{
				var rtSize = Core.Scene.SceneRenderTargetSize;

				if (ImGui.BeginMenu("Resize"))
				{
					if (ImGui.MenuItem("0.25x"))
						_gameViewForcedSize = new Num.Vector2(rtSize.X / 4f, rtSize.Y / 4f);
					if (ImGui.MenuItem("0.5x"))
						_gameViewForcedSize = new Num.Vector2(rtSize.X / 2f, rtSize.Y / 2f);
					if (ImGui.MenuItem("0.75x"))
						_gameViewForcedSize = new Num.Vector2(rtSize.X / 1.33f, rtSize.Y / 1.33f);
					if (ImGui.MenuItem("1x"))
						_gameViewForcedSize = new Num.Vector2(rtSize.X, rtSize.Y);
					if (ImGui.MenuItem("1.5x"))
						_gameViewForcedSize = new Num.Vector2(rtSize.X * 1.5f, rtSize.Y * 1.5f);
					if (ImGui.MenuItem("2x"))
						_gameViewForcedSize = new Num.Vector2(rtSize.X * 2, rtSize.Y * 2);
					if (ImGui.MenuItem("3x"))
						_gameViewForcedSize = new Num.Vector2(rtSize.X * 3, rtSize.Y * 3);
					ImGui.EndMenu();
				}

				if (ImGui.BeginMenu("Reposition"))
				{
					foreach (var pos in Enum.GetNames(typeof(WindowPosition)))
						if (ImGui.MenuItem(pos))
							_gameViewForcedPos = (WindowPosition)Enum.Parse(typeof(WindowPosition), pos);

					ImGui.EndMenu();
				}


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
				ImGui.MenuItem("Separate Game Window", null, ref ShowSeperateGameWindow);
				ImGui.MenuItem("Animation Event Inspector", null, ref ShowAnimationEventInspector);
				ImGui.EndMenu();
			}

			ImGui.EndMainMenuBar();
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
				
				// Move camera opposite to mouse drag direction
				_cameraTargetPosition = _cameraDragStartPosition - delta;
			}
			else if (_isCameraDragging && !Input.MiddleMouseButtonDown)
			{
				_isCameraDragging = false;
			}

			if (_cameraTargetPosition == default)
				_cameraTargetPosition = Core.Scene.Camera.Position;

			// Check if we're actively moving the camera with WASD
			bool isMovingCamera = Input.IsKeyDown(Keys.W) || Input.IsKeyDown(Keys.A) || 
								 Input.IsKeyDown(Keys.S) || Input.IsKeyDown(Keys.D);

			// Handle camera speed - use dynamic speed when Shift is held and moving
			if (Input.IsKeyDown(Keys.LeftShift))
			{
				if (isMovingCamera)
				{
					// Use dynamic speed when actively moving with Shift
					CurrentCameraSpeed = _dynamicCameraSpeed;
				}
				else
				{
					// When just holding Shift but not moving, use fast speed
					CurrentCameraSpeed = EditModeCameraFastSpeed;
				}
			}
			else
			{
				// Normal speed when Shift is not held
				CurrentCameraSpeed = EditModeCameraSpeed;
			}

			// Camera movement with WASD (only when not holding Ctrl)
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

		if (ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow))
		{
			// Double-click selection logic
			if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
			{
				TrySelectEntityAtMouse();
			}
			// Single click anywhere in game view: deselect if something is selected and not dragging gizmo
			else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				var entityPane = SceneGraphWindow.EntityPane;
				if (entityPane.SelectedEntity != null && !entityPane.IsDraggingGizmo)
				{
					DeselectEntity();
				}
			}
		}
	}

	private void ManageCameraZoom()
	{
		if (Core.IsEditMode && Input.MouseWheelDelta != 0)
		{
			// Check if we're actively moving camera with WASD while holding Shift
			bool isMovingCamera = Input.IsKeyDown(Keys.W) || Input.IsKeyDown(Keys.A) || 
						 Input.IsKeyDown(Keys.S) || Input.IsKeyDown(Keys.D);
			bool isShiftHeld = Input.IsKeyDown(Keys.LeftShift);

			if (isShiftHeld && isMovingCamera)
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
		if (MainEntityInspector != null)
		{
			if(MainEntityInspector.Entity == entity)
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

				if (entitynspectorInitialSpawnOffset - entitynspectorSpawnOffsetIncremental >= 0) // Reset the previous spawn offset 
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

		if (entitynspectorInitialSpawnOffset - entitynspectorSpawnOffsetIncremental >=
		    0) // Reset the previous spawn offset 
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

	#region Entity Selection
	private void TrySelectEntityAtMouse()
	{
		var mouseWorld = Core.Scene.Camera.ScreenToWorldPoint(Input.ScaledMousePosition);
	    Entity selected = null;

	    // Check entities with colliders (topmost first)
	    for (int i = Core.Scene.Entities.Count - 1; i >= 0; i--)
	    {
	        var entity = Core.Scene.Entities[i];
	        var collider = entity.GetComponent<Collider>();
	        if (collider != null && collider.Bounds.Contains(mouseWorld))
	        {
	            selected = entity;
	            break;
	        }
	    }

	    // If not found, check entities with SpriteRenderer or by proximity to Transform.Position
	    if (selected == null)
	    {
	        float minDist = 16f; // pixel threshold
	        for (int i = Core.Scene.Entities.Count - 1; i >= 0; i--)
	        {
	            var entity = Core.Scene.Entities[i];
	            var sprite = entity.GetComponent<SpriteRenderer>();
	            if (sprite != null)
	            {
	                // Only select if IsSelectableInEditor is true
	                if (!sprite.IsSelectableInEditor)
	                    continue;

	                var bounds = sprite.Bounds;
	                if (bounds.Contains(mouseWorld))
	                {
	                    selected = entity;
	                    break;
                    }
	            }
	            else
	            {
	                // Fallback: select if mouse is close to entity's position
	                float dist = Vector2.Distance(entity.Transform.Position, mouseWorld);
	                if (dist < minDist)
	                {
	                    selected = entity;
	                    minDist = dist;
	                }
	            }
	        }
	    }

	    // Set selection in the editor
	    if (selected != null)
	    {
	        SceneGraphWindow.EntityPane.SelectedEntity = selected;
	        OpenMainEntityInspector(selected);

	        // Move camera to the selected entity
	        SetCameraTargetPosition(selected.Transform.Position);
		}
	}
	
	public void SetCameraTargetPosition(Vector2 position)
	{
		// Smoothly move camera to the selected entity's position
		_cameraTargetPosition = position;
	}

	public void DeselectEntity()
	{
		if (SceneGraphWindow?.EntityPane != null)
		    SceneGraphWindow.EntityPane.SelectedEntity = null;
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
}