using System;
using System.Runtime.InteropServices;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Voltage.Console;
using Voltage.Utils;
using Voltage.Utils.Extensions;
using Voltage.Editor.Persistence;
using Voltage.Editor.Utils;
using Voltage.Persistence.Binary;
using Num = System.Numerics;
using Voltage;
using Voltage.Editor.SceneFile;
using Voltage.Editor.Inspectors.CustomInspectors;
using Voltage.Editor.Undo.Core;
using Voltage.Editor.Windows;
using Voltage.Editor.Assets;

namespace Voltage.Editor.ImGuiCore;

/// <summary>
/// Manages the ImGui game window specifically
/// </summary>
public partial class ImGuiManager : GlobalManager, IFinalRenderDelegate, IDisposable
{
	private const string kShowStyleEditor = "ImGui_ShowStyleEditor";
	private const string kShowSceneGraphWindow = "ImGui_ShowSceneGraphWindow";
	private const string kShowCoreWindow = "ImGui_ShowCoreWindow";
	private const string kShowMainInspectorWindow = "ImGui_ShowMainInspectorWindow";
	private const string kShowSeperateGameWindow = "ImGui_ShowSeperateGameWindow";
	private const string kPreserveGameWindowAspectRatio = "ImGui_PreserveGameWindowAspectRatio";
	private Num.Vector2 _gameImageScreenPos;
	private Num.Vector2 _gameImageSize;
	private bool _hasValidGameWindowData;
	private float _scale;
	private bool _isDisposed = false;

	[Flags]
	private enum WindowPosition
	{
		TopLeft,
		Top,
		TopRight,
		Left,
		Center,
		Right,
		BottomLeft,
		Bottom,
		BottomRight
	}

	private void LoadSettings()
	{
		var fileDataStore = Core.Services.GetService<FileDataStore>() ??
		                    new FileDataStore(EditorStorage.Root);
		KeyValueDataStore.Default.Load(fileDataStore);

		ShowStyleEditor = KeyValueDataStore.Default.GetBool(kShowStyleEditor, ShowStyleEditor);
		ShowSceneGraphWindow = KeyValueDataStore.Default.GetBool(kShowSceneGraphWindow, ShowSceneGraphWindow);
		ShowCoreWindow = KeyValueDataStore.Default.GetBool(kShowCoreWindow, ShowCoreWindow);
		ShowMainInspectorWindow = KeyValueDataStore.Default.GetBool(kShowMainInspectorWindow, ShowMainInspectorWindow);
		ShowSeparateGameWindow = KeyValueDataStore.Default.GetBool(kShowSeperateGameWindow, ShowSeparateGameWindow);
		PreserveGameWindowAspectRatio =
			KeyValueDataStore.Default.GetBool(kPreserveGameWindowAspectRatio, PreserveGameWindowAspectRatio);

		Core.Emitter.AddObserver(CoreEvents.Exiting, PersistSettings);
	}

	private void PersistSettings()
	{
		KeyValueDataStore.Default.Set(kShowStyleEditor, ShowStyleEditor);
		KeyValueDataStore.Default.Set(kShowSceneGraphWindow, ShowSceneGraphWindow);
		KeyValueDataStore.Default.Set(kShowCoreWindow, ShowCoreWindow);
		KeyValueDataStore.Default.Set(kShowMainInspectorWindow, ShowMainInspectorWindow);
		KeyValueDataStore.Default.Set(kShowSeperateGameWindow, ShowSeparateGameWindow);
		KeyValueDataStore.Default.Set(kPreserveGameWindowAspectRatio, PreserveGameWindowAspectRatio);

		KeyValueDataStore.Default.Flush(Core.Services.GetOrAddService<FileDataStore>());
	}

	/// <summary>
	/// here we do some cleanup in preparation for a new Scene
	/// </summary>
	private void OnSceneChanged()
	{
		// when the Scene changes we need to rewire ourselves up as the IFinalRenderDelegate in the new Scene
		// if we were previously enabled and do some cleanup
		Unload();
		SceneGraphWindow.OnSceneChanged();

		// The tile tool outlives the scene; drop the layer so the new scene doesn't inherit it.
		TilePaintTool.OnSceneChanged();

		if (Enabled)
			OnEnabled();

		var previousEntityId = MainEntityInspectorWindow?.Entity?.PersistentId ?? Guid.Empty;
		if (previousEntityId != Guid.Empty)
		{
			Scene.OnFinishedAddingEntities += RestorePreviousEntitySelection;
		}

		// Sync the camera target to the newly loaded scene's camera position so the lerp
		// doesn't drag it away from the deserialized position.
		Scene.OnFinishedAddingEntities += SyncCameraTargetToLoadedScene;

		return;

		void SyncCameraTargetToLoadedScene()
		{
			Scene.OnFinishedAddingEntities -= SyncCameraTargetToLoadedScene;

			if (Core.Scene?.Camera != null)
				_cameraTargetPosition = Core.Scene.Camera.Position;
		}

		void RestorePreviousEntitySelection()
		{
			Scene.OnFinishedAddingEntities -= RestorePreviousEntitySelection;

			var restoredEntity = Core.Scene?.FindEntityByPersistentId(previousEntityId);
			if (restoredEntity != null)
			{
				SceneGraphWindow.EntityPane.SetSelectedEntity(restoredEntity, false);
				MainEntityInspectorWindow?.DelayedSetEntity(restoredEntity);
			}
			else
			{
				// Entity no longer exists after reload  clear the inspector
				OpenMainEntityInspector(null);
			}
		}
	}

	private void Unload()
	{
		_drawCommands.Clear();
		_entityInspectors.Clear();

		// _drawCommands.Clear() just dropped the AnimationEventInspector's Draw delegate, but the
		// instance pointer would survive. If we leave it non-null, LayoutGui's "== null" guard never
		// fires and the draw command is never re-registered  the window shows as open in the menu
		// but is never actually drawn (and "Manage Events" becomes a no-op). Null it so LayoutGui
		// re-creates and re-registers it next frame whenever ShowAnimationEventInspector is true.
		_animationEventInspector = null;
		SpriteAnimatorFileInspector.AnimationEventInspectorInstance = null;

		if (_renderTargetId != IntPtr.Zero)
		{
			_renderer.UnbindTexture(_renderTargetId);
			_renderTargetId = IntPtr.Zero;
		}

		_lastRenderTarget = null;
	}

	/// <summary>
	/// Draws the game window and deals with overriding Voltage.Input when appropriate
	/// </summary>
	private void DrawGameWindow()
	{
		if (_lastRenderTarget == null)
			return;

		string gameWindowState = Core.IsEditMode ? "Paused (Editor)" : Core.IsPauseMode ? "Paused" : "Playing";

		ImGuiWindowFlags gameWindowFlags =
			_gameWindowFlags | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
			| ImGuiWindowFlags.NoBringToFrontOnFocus; // keep in background

		ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Num.Vector2(0, 0));
		Gui.Begin($"Game: {gameWindowState}###GameWindow", gameWindowFlags);

		IsGameWindowFocused = ImGui.IsWindowFocused();

		// Hit-tested by ImGui, so a panel floating over the view makes this false.
		IsGameWindowHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows);

		GameWindowPosition = ImGui.GetWindowPos();
		GameWindowSize = ImGui.GetWindowSize();

		GameWindowWidth = ImGui.GetWindowSize().X;
		GameWindowHeight = ImGui.GetWindowSize().Y;

		Num.Vector2 imageSize;
		Num.Vector2 cursorOffset = Num.Vector2.Zero;

		if (PreserveGameWindowAspectRatio)
		{
			var availableRegion = ImGui.GetContentRegionAvail();
			var targetAspect = (float)_lastRenderTarget.Width / _lastRenderTarget.Height;
			var availableAspect = availableRegion.X / availableRegion.Y;

			if (availableAspect > targetAspect)
			{
				imageSize = new Num.Vector2(availableRegion.Y * targetAspect, availableRegion.Y);
				cursorOffset.X = (availableRegion.X - imageSize.X) * 0.5f;
			}
			else
			{
				imageSize = new Num.Vector2(availableRegion.X, availableRegion.X / targetAspect);
				cursorOffset.Y = (availableRegion.Y - imageSize.Y) * 0.5f;
			}

			if (cursorOffset.X > 0 || cursorOffset.Y > 0)
			{
				var currentCursor = ImGui.GetCursorPos();
				ImGui.SetCursorPos(currentCursor + cursorOffset);
			}
		}
		else
		{
			imageSize = ImGui.GetContentRegionAvail();
		}

		// Capture the screen-space position of the image BEFORE drawing it.
		// After ImGui.Image(), the cursor advances past the image, so
		// GetCursorScreenPos() would return the wrong position if queried later.
		_gameImageScreenPos = ImGui.GetCursorScreenPos();
		_gameImageSize = imageSize;
		_hasValidGameWindowData = true;

		if (_renderTargetId != IntPtr.Zero)
		{
			ImGui.Image(_renderTargetId, imageSize);
		}
		else
		{
			ImGui.Dummy(imageSize);
		}

		// Draw buttons and text on top using screen coordinates
		var camera = Core.Scene?.Camera;
		bool showZoomButton = camera != null && Math.Abs(camera.Zoom - Camera.DefaultZoom) > 0.01f;
		bool showSpeedButton = Math.Abs(GetDynamicCameraSpeed() - EditModeCameraSpeed) > 0.1f;

		if (showZoomButton || showSpeedButton)
		{
			var windowPos = ImGui.GetWindowPos();
			var contentMin = ImGui.GetWindowContentRegionMin();
			var buttonStartPos = windowPos + contentMin + cursorOffset +
			                     new Num.Vector2(8, 8) * ImGui.GetIO().FontGlobalScale;

			ImGui.SetCursorScreenPos(buttonStartPos);

			if (showZoomButton)
			{
				var zoomButtonText = "Reset Camera Zoom";
				var zoomTextSize = ImGui.CalcTextSize(zoomButtonText);
				var zoomPadding = new Num.Vector2(16, 8) * ImGui.GetIO().FontGlobalScale;
				var zoomButtonSize = zoomTextSize + zoomPadding;

				if (Gui.Button(zoomButtonText, zoomButtonSize))
				{
					camera.Zoom = Camera.DefaultZoom;
				}

				if (showSpeedButton)
				{
					ImGui.SameLine(0, 8f * ImGui.GetIO().FontGlobalScale);
				}
			}

			if (showSpeedButton)
			{
				var speedButtonText = "Reset Camera Speed";
				var speedTextSize = ImGui.CalcTextSize(speedButtonText);
				var speedPadding = new Num.Vector2(16, 8) * ImGui.GetIO().FontGlobalScale;
				var speedButtonSize = speedTextSize + speedPadding;

				if (Gui.Button(speedButtonText, speedButtonSize))
				{
					ResetDynamicCameraSpeed();
				}
			}
		}

		if (Core.IsEditMode && Math.Abs(GetDynamicCameraSpeed() - EditModeCameraSpeed) > 0.1f)
		{
			var speedText = $"Camera Speed: {(int)GetDynamicCameraSpeed()}";
			var speedTextSize = ImGui.CalcTextSize(speedText);

			var windowPos = ImGui.GetWindowPos();
			var contentMin = ImGui.GetWindowContentRegionMin();
			var margin = new Num.Vector2(8, 8) * ImGui.GetIO().FontGlobalScale;

			var speedTextPos = new Num.Vector2(
				windowPos.X + contentMin.X + cursorOffset.X + imageSize.X - speedTextSize.X - margin.X,
				windowPos.Y + contentMin.Y + cursorOffset.Y + margin.Y
			);

			var drawList = ImGui.GetWindowDrawList();
			var bgPadding = new Num.Vector2(8, 4) * ImGui.GetIO().FontGlobalScale;
			var bgMin = speedTextPos - bgPadding;
			var bgMax = speedTextPos + speedTextSize + bgPadding;

			drawList.AddRectFilled(
				bgMin,
				bgMax,
				ImGui.ColorConvertFloat4ToU32(new Num.Vector4(0.0f, 0.0f, 0.0f, 0.6f)),
				4.0f * ImGui.GetIO().FontGlobalScale
			);

			ImGui.SetCursorScreenPos(speedTextPos);
			ImGuiSafe.TextColoredSafe(new Num.Vector4(1.0f, 1.0f, 0.0f, 1.0f), speedText);
		}

		OverrideMouseInput();

		if (!ImGui.IsWindowFocused())
		{
			var focusedWindow = false;
			Mouse.SetCursor(MouseCursor.Arrow);

			if (ImGui.IsWindowHovered())
				if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)
				    || (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && FocusGameWindowOnRightClick)
				    || (ImGui.IsMouseClicked(ImGuiMouseButton.Middle) && FocusGameWindowOnMiddleClick))
				{
					ImGui.SetWindowFocus();
					focusedWindow = true;
					IsGameWindowFocused = true;
				}

			if (!focusedWindow)
			{
				var mouseState = new MouseState(
					Input.CurrentMouseState.X,
					Input.CurrentMouseState.Y,
					DisableMouseWheelWhenGameWindowUnfocused ? 0 : Input.MouseWheel,
					ButtonState.Released,
					ButtonState.Released,
					ButtonState.Released,
					ButtonState.Released,
					ButtonState.Released
				);
				Input.SetCurrentMouseState(mouseState);
			}
		}

		Gui.End();
		ImGui.PopStyleVar();
	}

	/// <summary>
	/// converts the mouse position from global window position to the game window's coordinates and overrides Voltage.Input with
	/// the new value. This keeps input working properly in the game window.
	/// </summary>
	private void OverrideMouseInput()
	{
		// Use the image's top-left screen position captured BEFORE ImGui.Image() was drawn.
		var offset = new Vector2(_gameImageScreenPos.X, _gameImageScreenPos.Y);

		// Remove the game window offset from raw mouse input, giving us a position
		// relative to the top-left of the game image.
		var normalizedPos = Input.RawMousePosition.ToVector2() - offset;

		// Use the captured image size (not GetContentRegionAvail which returns
		// remaining space after the image and would be near zero at this point).
		var scaleX = _gameImageSize.X / _lastRenderTarget.Width;
		var scaleY = _gameImageSize.Y / _lastRenderTarget.Height;

		// When preserving aspect ratio, use the uniform scale
		if (PreserveGameWindowAspectRatio)
		{
			_scale = Math.Min(scaleX, scaleY);
		}
		else
		{
			// Non-uniform scaling when not preserving aspect ratio
			normalizedPos.X /= scaleX;
			normalizedPos.Y /= scaleY;

			var unNormalizedPos = normalizedPos / Input.ResolutionScale;
			unNormalizedPos += Input.ResolutionOffset;

			var mouseState = Input.CurrentMouseState;
			var newMouseState = new MouseState((int)unNormalizedPos.X, (int)unNormalizedPos.Y,
				mouseState.ScrollWheelValue,
				mouseState.LeftButton, mouseState.MiddleButton, mouseState.RightButton, mouseState.XButton1,
				mouseState.XButton2);
			Input.SetCurrentMouseState(newMouseState);
			return;
		}

		// Uniform scaling for aspect ratio preservation
		normalizedPos /= _scale;

		// trick the input system. Take our normalizedPos and undo the scale and offsets (do the
		// reverse of what Input.scaledPosition does) so that any consumers of mouse input can get
		// the correct coordinates.
		var unNormalizedPos2 = normalizedPos / Input.ResolutionScale;
		unNormalizedPos2 += Input.ResolutionOffset;

		var mouseState2 = Input.CurrentMouseState;
		var newMouseState2 = new MouseState((int)unNormalizedPos2.X, (int)unNormalizedPos2.Y,
			mouseState2.ScrollWheelValue,
			mouseState2.LeftButton, mouseState2.MiddleButton, mouseState2.RightButton, mouseState2.XButton1,
			mouseState2.XButton2);
		Input.SetCurrentMouseState(newMouseState2);
	}


	/// <summary>
	/// Remaps raw OS mouse coordinates to game-window render-target coordinates.
	/// Uses the game image position and size captured during the previous frame's DrawGameWindow.
	/// Must be called before any code that reads Input.ScaledMousePosition for game-window interaction.
	/// </summary>
	private void ApplyGameWindowMouseOverride()
	{
		if (!_hasValidGameWindowData || _lastRenderTarget == null)
			return;

		var offset = new Vector2(_gameImageScreenPos.X, _gameImageScreenPos.Y);
		var normalizedPos = Input.RawMousePosition.ToVector2() - offset;

		var scaleX = _gameImageSize.X / _lastRenderTarget.Width;
		var scaleY = _gameImageSize.Y / _lastRenderTarget.Height;

		if (PreserveGameWindowAspectRatio)
		{
			var scale = Math.Min(scaleX, scaleY);
			normalizedPos /= scale;
		}
		else
		{
			normalizedPos.X /= scaleX;
			normalizedPos.Y /= scaleY;
		}

		var unNormalizedPos = normalizedPos / Input.ResolutionScale;
		unNormalizedPos += Input.ResolutionOffset;

		var mouseState = Input.CurrentMouseState;
		var newMouseState = new MouseState((int)unNormalizedPos.X, (int)unNormalizedPos.Y,
			mouseState.ScrollWheelValue,
			mouseState.LeftButton, mouseState.MiddleButton, mouseState.RightButton,
			mouseState.XButton1, mouseState.XButton2);
		Input.SetCurrentMouseState(newMouseState);
	}

	/// <summary>
	/// Resolves an asset dragged from the Asset Browser onto the game viewport. The viewport is a
	/// dockspace passthrough with no ImGui window to host a drop target, so we poll for the mouse
	/// release instead: if a drag is still pending (the Scene Graph drop target didn't consume it
	/// earlier this frame) and the cursor is over the game window, spawn at the cursor's world
	/// position via Input.ScaledMousePosition into Camera.ScreenToWorldPoint (the gizmo path).
	/// </summary>
	private void HandleGameViewAssetDrop()
	{
		if (AssetBrowserWindow.DraggedReference.IsEmpty)
			return;

		if (!Input.LeftMouseButtonReleased)
			return;

		var reference = AssetBrowserWindow.DraggedReference;
		AssetBrowserWindow.DraggedReference = AssetReference.Empty;

		if (!_cursorSelectionManager.IsCursorWithinGameWindow())
			return;

		Vector2? worldPos = Core.Scene?.Camera?.ScreenToWorldPoint(Input.ScaledMousePosition);

		// Snap the drop point to the placement grid, matching drag-snap.
		if (worldPos.HasValue)
		{
			var ctrl = ImGuiNET.ImGui.GetIO().KeyCtrl || ImGuiNET.ImGui.GetIO().KeySuper;
			worldPos = Tools.EditorSettingsWindow.ApplyPlacementSnap(worldPos.Value, ctrl);
		}

		var ext        = System.IO.Path.GetExtension(reference.HintPath);
		var descriptor = AssetTypeRegistry.Resolve(ext);
		descriptor.DropFactory?.Invoke(reference, worldPos);
	}

	#region GlobalManager Lifecycle

	public override void OnEnabled()
	{
		if (Core.Scene != null)
		{
			Core.Scene.FinalRenderDelegate = this;

			// why call beforeLayout here? If added from the DebugConsole we missed the GlobalManger.update call and ImGui needs NextFrame
			// called or it fails. Calling NextFrame twice in a frame causes no harm, just missed input.
			_renderer.BeforeLayout(Time.DeltaTime);
		}
	}

	public override void OnDisabled()
	{
		Unload();
		if (Core.Scene != null)
			Core.Scene.FinalRenderDelegate = null;
	}

	public override void Update()
	{
		// Before the frame is begun: a load drives frames of its own to stay responsive.
		ProcessPendingProjectLoad();

		// we have to do our layout in update so that if the game window is not focused or being displayed we can wipe
		// the Input, essentially letting ImGui consume it
		Gateway.UiRegistry.BeginFrame();
		_renderer.BeforeLayout(Time.DeltaTime);

		// Exit prompt drawing and management
		DrawApplicationExitPrompt(ref _pendingExit, ExitPromptType.Exit);
		DrawApplicationExitPrompt(ref _pendingSceneChange, ExitPromptType.SceneChange);
		DrawApplicationExitPrompt(ref _pendingResetScene, ExitPromptType.ResetScene);
		ManageApplicationExitPrompt();
		ManageUndoAndRedo();

		LayoutGui();
	}

	private enum ExitPromptType
	{
		Exit,
		SceneChange,
		ResetScene
	}

	private void DrawApplicationExitPrompt(ref bool pendingValue, ExitPromptType exitPromptType)
	{
		if (!pendingValue)
			return;

		// Only open the popup if there are unsaved changes
		if (EditorChangeTracker.IsDirty)
		{
			ImGui.OpenPopup("Unsaved Changes");
		}
		else
		{
			// No unsaved changes, reset the flag so prompt doesn't get stuck
			pendingValue = false;
			return;
		}

		if (Gui.BeginPopupModal("Unsaved Changes", ref pendingValue, ImGuiWindowFlags.AlwaysAutoResize))
		{
			ImGui.TextWrapped("You have unsaved changes for:");

			ImGui.Spacing();
			VoltageEditorUtils.MediumVerticalSpace();
			ImGui.Separator();

			int i = 1;
			foreach (var (_, description) in EditorChangeTracker.ChangedObjects)
			{
				ImGuiSafe.BulletTextSafe($"{i++}. {description}");
			}

			ImGui.Separator();
			VoltageEditorUtils.MediumVerticalSpace();

			if (Gui.Button("Save", new Num.Vector2(120, 0)))
			{
				_pendingActionAfterSave = exitPromptType;
				_pendingSaveTask = SaveSceneAsyncAndThenAct();
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();

			if (Gui.Button("Discard", new Num.Vector2(120, 0))
			   )
			{
				EditorChangeTracker.Revert();
				ImGui.CloseCurrentPopup();
				if (exitPromptType == ExitPromptType.SceneChange && _requestedSceneType != null)
				{
					LoadSceneByName(SceneManager.Instance.CurrentSceneName);
					pendingValue = false;
					_requestedSceneType = null;
				}
				else if (exitPromptType == ExitPromptType.ResetScene)
				{
					ResetScene();
					pendingValue = false;
				}
				else
				{
					pendingValue = false;
					Core.ConfirmAndExit();
				}
			}

			ImGui.SameLine();

			if (Gui.Button("Cancel", new Num.Vector2(120, 0)))
			{
				pendingValue = false;
				_requestedSceneType = null;
				ImGui.CloseCurrentPopup();
			}

			Gui.EndPopup();
		}

	}

	private void ManageApplicationExitPrompt()
	{
		if (_pendingSaveTask != null && _pendingSaveTask.IsCompleted)
		{
			switch (_pendingActionAfterSave)
			{
				case ExitPromptType.SceneChange:
					LoadSceneByName(SceneManager.Instance.CurrentSceneName);
					_pendingSceneChange = false;
					_requestedSceneType = null;
					break;
				case ExitPromptType.ResetScene:
					ResetScene();
					_pendingResetScene = false;
					_requestedResetSceneType = null;
					break;
				case ExitPromptType.Exit:
					Core.ConfirmAndExit();
					_pendingExit = false;
					break;
			}

			_pendingSaveTask = null;
		}
	}

	#endregion


	#region IFinalRenderDelegate

	void IFinalRenderDelegate.HandleFinalRender(RenderTarget2D finalRenderTarget, Color letterboxColor,
		RenderTarget2D source, Rectangle finalRenderDestinationRect,
		SamplerState samplerState)
	{
		if (ShowSeparateGameWindow)
		{
			// SAFETY CHECK: Don't bind texture on first frame or during layout reload
			if (_isFirstFrame || _layoutManager.HasPendingReload || ImGui.GetFrameCount() != _dockspaceFrame)
			{
				// Just render normally without separate game window
				Core.GraphicsDevice.SetRenderTarget(finalRenderTarget);
				Core.GraphicsDevice.Clear(letterboxColor);
				var blitSampler = samplerState.Filter == TextureFilter.Linear ? SamplerState.LinearClamp : SamplerState.PointClamp;
				Graphics.Instance.Batcher.Begin(BlendState.Opaque, blitSampler, null, null);
				Graphics.Instance.Batcher.Draw(source, finalRenderDestinationRect, Color.White);
				Graphics.Instance.Batcher.End();
				_renderer.AfterLayout();
				return;
			}

			if (_lastRenderTarget != source)
			{
				// unbind the old texture if we had one
				if (_lastRenderTarget != null && _renderTargetId != IntPtr.Zero)
				{
					try
					{
						_renderer.UnbindTexture(_renderTargetId);
					}
					catch (Exception ex)
					{
						Debug.Error($"Error unbinding render target: {ex.Message}");
					}

					_renderTargetId = IntPtr.Zero;
				}

				// bind the new texture
				_lastRenderTarget = source;

				try
				{
					_renderTargetId = _renderer.BindTexture(source);
				}
				catch (Exception ex)
				{
					Debug.Error($"Error binding render target: {ex.Message}");
					_renderTargetId = IntPtr.Zero;
				}
			}

			DrawGameWindow();

			Core.GraphicsDevice.SamplerStates[0] = samplerState;
			Core.GraphicsDevice.SetRenderTarget(finalRenderTarget);
			Core.GraphicsDevice.Clear(letterboxColor);
		}
		else
		{
			Core.GraphicsDevice.SetRenderTarget(finalRenderTarget);
			Core.GraphicsDevice.Clear(letterboxColor);
			var blitSampler = samplerState.Filter == TextureFilter.Linear ? SamplerState.LinearClamp : SamplerState.PointClamp;
			Graphics.Instance.Batcher.Begin(BlendState.Opaque, blitSampler, null, null);
			Graphics.Instance.Batcher.Draw(source, finalRenderDestinationRect, Color.White);
			Graphics.Instance.Batcher.End();
		}

		_renderer.AfterLayout();
	}

	void IFinalRenderDelegate.OnAddedToScene(Scene scene)
	{
	}

	void IFinalRenderDelegate.OnSceneBackBufferSizeChanged(int newWidth, int newHeight)
	{
	}

	void IFinalRenderDelegate.Unload()
	{
	}

	#endregion


	#region IDisposable Support
	private void Dispose(bool disposing)
	{
		if (!_isDisposed)
		{
			if (disposing)
			{
				Core.Emitter.RemoveObserver(CoreEvents.SceneChanged, OnSceneChanged);
				_layoutManager.AutoSaveCurrentLayout();

				// Always save the default .ini for next startup
				if (!string.IsNullOrEmpty(_layoutFilePath))
				{
					ImGui.SaveIniSettingsToDisk(_layoutFilePath);
				}
			}

			// Free the pinned GCHandle to avoid a memory leak.
			// This must happen outside the 'if (disposing)' block to ensure
			// cleanup even during finalizer runs.
			if (_iniFilenamePinnedHandle.IsAllocated)
			{
				// Clear ImGui's pointer first to avoid it writing through a freed handle
				unsafe
				{
					ImGuiNative.igGetIO()->IniFilename = null;
				}
				_iniFilenamePinnedHandle.Free();
			}

			_isDisposed = true;
		}
	}

	void IDisposable.Dispose()
	{
		Dispose(true);
	}

	#endregion
}