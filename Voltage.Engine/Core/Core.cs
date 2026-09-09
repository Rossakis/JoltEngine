using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Graphics;
using System.Collections;
using System.Diagnostics;
using Voltage.Audio;
using Voltage.Console;
using Voltage.Systems;
using Voltage.Textures;
using Voltage.Utils;
using Voltage.Utils.Collections;
using Voltage.Utils.Coroutines;
using Voltage.Utils.Timers;
using Voltage.Utils.Tweens;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Voltage.Editor")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Voltage.Persistence")]

namespace Voltage;

public class Core : Game
{
	/// <summary>
	/// The most recently compiled script assembly. Set this after each successful script compilation
	/// so that ResolveType can find types from the latest scripts instead of stale ones from old assemblies.
	/// </summary>
	public static System.Reflection.Assembly LatestScriptAssembly { get; set; }

	/// <summary>
	/// core emitter. emits only Core level events.
	/// </summary>
	public static Emitter<CoreEvents> Emitter;

	/// <summary>
	/// Core emitter that emits Core level events with a bool value. This is used for events that have a pending state, such as
	/// </summary>
	public static Emitter<CoreEvents, bool> EmitterWithPending = new Emitter<CoreEvents, bool>(new CoreEventsComparer());

	/// <summary>
	/// enables/disables if we should quit the app when escape is pressed
	/// </summary>
	public static bool ExitOnEscapeKeypress = true;

	/// <summary>
	/// enables/disables pausing when focus is lost. No update or render methods will be called if true when not in focus.
	/// </summary>
	public static bool PauseOnFocusLost = true;

	/// <summary>Overrides PauseOnFocusLost while something external, such as the editor gateway, is driving the process.</summary>
	public static volatile bool KeepRunningWhenUnfocused;

	private static bool ShouldPauseForFocus => PauseOnFocusLost && !KeepRunningWhenUnfocused && !_instance.IsActive;

	/// <summary>
	/// enables/disables debug rendering
	/// </summary>
#if EDITOR
	public static bool DebugRenderEnabled = true;
#else
	public static bool DebugRenderEnabled = false;
#endif

	/// <summary>
	/// If true, the scene will automatically reset after going from PlayMode back to EditMode (resetting all values that weren't saved beforehand, for consistency) .
	/// </summary>
	public static bool ResetSceneAutomatically = true;

	/// <summary>
	/// global access to the graphicsDevice
	/// </summary>
	public new static GraphicsDevice GraphicsDevice;

	/// <summary>
	/// global content manager for loading any assets that should stick around between scenes
	/// </summary>
	public new static VoltageContentManager Content;

	/// <summary>
	/// True when running inside the Voltage Editor. Set by the editor on startup.
	/// Defaults to false so that EDITOR-compiled DLLs embedded in VS-published game
	/// builds do not trigger editor-only exit logic.
	/// </summary>
	public static bool IsEditorMode = false;

	/// <summary>
	/// default SamplerState used by Materials. Note that this must be set at launch! Changing it after that time will result in only
	/// Materials created after it was set having the new SamplerState
	/// </summary>
	public static SamplerState DefaultSamplerState = new()
	{
		Filter = TextureFilter.Point
	};

	/// <summary>
	/// default wrapped SamplerState. Determined by the Filter of the defaultSamplerState.
	/// </summary>
	/// <value>The default state of the wrapped sampler.</value>
	public static SamplerState DefaultWrappedSamplerState =>
		DefaultSamplerState.Filter == TextureFilter.Point
			? SamplerState.PointWrap
			: SamplerState.LinearWrap;

	/// <summary>
	/// default GameServiceContainer access
	/// </summary>
	/// <value>The services.</value>
	public new static GameServiceContainer Services => ((Game)_instance).Services;

	/// <summary>
	/// When set, called during <see cref="OnExiting"/> to determine whether the exit should
	/// be intercepted (e.g. to show an unsaved-changes prompt).
	/// </summary>
	public static Func<bool> ShouldInterceptExit;

	/// <summary>
	/// provides access to the single Core/Game instance
	/// </summary>
	public static Core Instance => _instance;

	/// <summary>
	/// Global audio hub (mixer buses, music, SFX, positional playback). Ticks every frame as a
	/// <see cref="GlobalManager"/>, across scene changes and pause.
	/// </summary>
	public static AudioManager Audio => _instance?._audioManager;

	/// <summary>
	/// facilitates easy access to the global Content instance for internal classes
	/// </summary>
	internal static Core _instance;
	private string _windowTitle;

#if EDITOR
	internal static long drawCalls;
	private TimeSpan _frameCounterElapsedTime = TimeSpan.Zero;
	private int _frameCounter = 0;

	/// <summary>Extra context for the window title. A provider, not a string, because the title is rebuilt once a second and the editor would otherwise have to push changes.</summary>
	public static Func<string> WindowTitleStatusProvider;
#endif

	private Scene _scene;
	private Scene _nextScene;
	internal SceneTransition _sceneTransition;
	public SceneTransition SceneTransition => _sceneTransition;

	/// <summary>
	/// used to coalesce GraphicsDeviceReset events
	/// </summary>
	private ITimer _graphicsDeviceChangeTimer;

	private FastList<GlobalManager> _globalManagers = new();
	private CoroutineManager _coroutineManager = new();
	private TimerManager _timerManager = new();
	private AudioManager _audioManager = new();

	/// <summary>
	/// The currently active Scene. Note that if set, the Scene will not actually change until the end of the Update
	/// </summary>
	public static Scene Scene
	{
		get => _instance._scene;
		set
		{
			Insist.IsNotNull(value, "Scene cannot be null!");

			// handle our initial Scene. If we have no Scene and one is assigned directly wire it up
			if (_instance._scene == null)
			{
				_instance._scene = value;
				_instance.OnSceneChanged();
				_instance._scene.Begin();
			}
			else
			{
				_instance._nextScene = value;
			}
		}
	}


	public Core(int width = 1280, int height = 720, bool isFullScreen = false, string windowTitle = "Voltage",
		string contentDirectory = "Content", bool hardwareModeSwitch = true)
	{
		_windowTitle = windowTitle;
#if EDITOR
		IsEditMode = true;
#else
		IsEditMode = false;
#endif
		_instance = this;
		Emitter = new Emitter<CoreEvents>(new CoreEventsComparer());

		var graphicsManager = new GraphicsDeviceManager(this)
		{
			PreferredBackBufferWidth = width,
			PreferredBackBufferHeight = height,
			IsFullScreen = isFullScreen,
			SynchronizeWithVerticalRetrace = true,
			HardwareModeSwitch = hardwareModeSwitch,
			PreferHalfPixelOffset = false
		};
		graphicsManager.DeviceReset += OnGraphicsDeviceReset;
		graphicsManager.PreferredDepthStencilFormat = DepthFormat.Depth24Stencil8;

		OnSwitchEditMode += (editMode) => { Instance.IsMouseVisible = editMode; };
		Screen.Initialize(graphicsManager);
		Window.ClientSizeChanged += OnGraphicsDeviceReset;
		Window.OrientationChanged += OnOrientationChanged;

		base.Content.RootDirectory = contentDirectory;
		Content = new VoltageGlobalContentManager(Services, base.Content.RootDirectory);
		IsMouseVisible = true;
		IsFixedTimeStep = false;

		// setup systems
		RegisterGlobalManager(_coroutineManager);
		RegisterGlobalManager(new TweenManager());
		RegisterGlobalManager(_timerManager);
		RegisterGlobalManager(_audioManager);
		RegisterGlobalManager(new RenderTarget());
	}

	private void OnOrientationChanged(object sender, EventArgs e)
	{
		Emitter.Emit(CoreEvents.OrientationChanged);
	}

	/// <summary>
	/// this gets called whenever the screen size changes
	/// </summary>
	/// <param name="sender">Sender.</param>
	/// <param name="e">E.</param>
	protected void OnGraphicsDeviceReset(object sender, EventArgs e)
	{
		// we coalese these to avoid spamming events
		if (_graphicsDeviceChangeTimer != null)
			_graphicsDeviceChangeTimer.Reset();
		else
			_graphicsDeviceChangeTimer = Schedule(0.05f, false, this, t =>
			{
				(t.Context as Core)._graphicsDeviceChangeTimer = null;
				Emitter.Emit(CoreEvents.GraphicsDeviceReset);
			});
	}


	#region Passthroughs to Game

	public new static void Exit()
	{
		((Game)_instance).Exit();
	}

	#endregion

	#region Game overides

	protected override void Initialize()
	{
		base.Initialize();

		GraphicsDevice = base.GraphicsDevice;
		StartRuntimeGateway();
	}

	private Gateway.GatewayDispatcher _runtimeGateway;

	/// <summary>A built game only listens when --gateway or VOLTAGE_GATEWAY=1 asks it to; the editor overrides this with its own dispatcher.</summary>
	protected virtual void StartRuntimeGateway()
	{
		if (IsEditorMode)
			return;

		var args = Environment.GetCommandLineArgs();
		var name = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? _windowTitle;
		var options = Gateway.GatewayOptions.ForRuntime(args.Length > 1 ? args[1..] : Array.Empty<string>(), name);
		if (!options.Enabled)
			return;

		_runtimeGateway = new Gateway.GatewayDispatcher(options);
		RegisterGlobalManager(_runtimeGateway);
	}

	protected override void Update(GameTime gameTime)
	{
		if (ShouldPauseForFocus)
		{
			SuppressDraw();
			return;
		}

		try
		{
			// update all our systems and global managers
			Time.Update((float)gameTime.ElapsedGameTime.TotalSeconds);
			Input.Update();

			if (ExitOnEscapeKeypress &&
			    (Input.IsKeyPressed(Keys.Escape) || Input.GamePads[0].IsButtonReleased(Buttons.Back)))
			{
				base.Exit();
				return;
			}

			if (_scene != null)
			{
				for (var i = _globalManagers.Length - 1; i >= 0; i--)
					if (_globalManagers.Buffer[i].Enabled)
						_globalManagers.Buffer[i].Update();

				// read carefully:
				// - we do not update the Scene while a SceneTransition is happening
				// 		- unless it is SceneTransition that doesn't change Scenes (no reason not to update)
				//		- or it is a SceneTransition that has already switched to the new Scene (the new Scene needs to do its thing)

				// Note: we call Scene.Update() even while in PauseMode. The pause does not skip the
				// whole update — it is applied deeper so that UI components (IUpdatableInPauseMode) keep
				// running while gameplay components and SceneComponents are frozen.
				// See Scene.Update() and ComponentList.Update().
				if (_sceneTransition == null ||
				     _sceneTransition != null &&
				     (!_sceneTransition._loadsNewScene || _sceneTransition._isNewSceneLoaded))
					_scene.Update();

				if (_nextScene != null)
				{
					_scene.End();

					_scene = _nextScene;
					_nextScene = null;
					OnSceneChanged();

					_scene.Begin();
				}
			}
		}
		catch (Exception ex)
		{
			Debug.Error($"[Core.Update] Unhandled exception escaped the scene update loop: {ex.Message}\n{ex.StackTrace}");
		}

		EndDebugUpdate();
	}

	protected override void Draw(GameTime gameTime)
	{
		if (ShouldPauseForFocus)
		{
			// KeepRunningWhenUnfocused can flip between Update and Draw; Present still needs no target bound.
			UnbindRenderTarget();
			return;
		}

		StartDebugDraw(gameTime.ElapsedGameTime);

		try
		{
			if (_sceneTransition != null)
				_sceneTransition.PreRender(Graphics.Instance.Batcher);

			if (_sceneTransition != null)
			{
				if (_scene != null && _sceneTransition.WantsPreviousSceneRender &&
				    !_sceneTransition.HasPreviousSceneRender)
				{
					_scene.Render();
					_scene.PostRender(_sceneTransition.PreviousSceneRender);
					StartCoroutine(_sceneTransition.OnBeginTransition());
				}
				else if (_scene != null && _sceneTransition._isNewSceneLoaded)
				{
					_scene.Render();
					_scene.PostRender();
				}

				_sceneTransition.Render(Graphics.Instance.Batcher);
			}
			else if (_scene != null)
			{
				_scene.Render();

				if (DebugRenderEnabled)
					Debug.Render();

				_scene.PostRender();
			}
		}
		catch (Exception ex)
		{
			Debug.Error($"[Core.Draw] Unhandled exception escaped the scene render loop: {ex.Message}\n{ex.StackTrace}");

		}

		// Present refuses to run with a render target bound. A throw mid-render, a frame with no scene, or an
		// update-time render that expected the scene to reset it can all leave one active.
		UnbindRenderTarget();

		EndDebugDraw();
		_runtimeGateway?.AfterDraw();
	}

	private void UnbindRenderTarget()
	{
		try
		{
			if (GraphicsDevice.GetRenderTargets().Length > 0)
				GraphicsDevice.SetRenderTarget(null);
		}
		catch (Exception)
		{
		}
	}

	private static bool _allowExit = false;

	protected override void OnExiting(object sender, ExitingEventArgs args)
	{
#if EDITOR
		if (IsEditorMode && !_allowExit && (ShouldInterceptExit?.Invoke() ?? false))
		{
			args.Cancel = true;
			EmitterWithPending.Emit(CoreEvents.Exiting, true);
		}
		else
		{
			base.OnExiting(sender, args);
			Emitter.Emit(CoreEvents.Exiting);
		}
#else
		base.OnExiting(sender, args);
		Emitter.Emit(CoreEvents.Exiting);
#endif
	}

	public static void ConfirmAndExit()
	{
		_allowExit = true;
		Exit();
	}

#endregion

	#region Debug Injection

	[Conditional("EDITOR")]
	private void EndDebugUpdate()
	{
#if EDITOR
		DebugConsole.Instance.Update();
		drawCalls = 0;
#endif
	}

	[Conditional("EDITOR")]
	private void StartDebugDraw(TimeSpan elapsedGameTime)
	{
#if EDITOR
		_frameCounter++;
		_frameCounterElapsedTime += elapsedGameTime;
		if (_frameCounterElapsedTime >= TimeSpan.FromSeconds(1))
		{
			var totalMemory = (GC.GetTotalMemory(false) / 1048576f).ToString("F");
			var status = WindowTitleStatusProvider?.Invoke();

			// The status already names the project, so repeating the title in front of it is noise.
			Window.Title = string.IsNullOrEmpty(status)
				? string.Format("{0} {1} fps - {2} MB", _windowTitle, _frameCounter, totalMemory)
				: string.Format("{0} - {1} fps - {2} MB", status, _frameCounter, totalMemory);
			_frameCounter = 0;
			_frameCounterElapsedTime -= TimeSpan.FromSeconds(1);
		}
#endif
	}

	[Conditional("EDITOR")]
	private void EndDebugDraw()
	{
#if EDITOR
		DebugConsole.Instance.Render();
		drawCalls = GraphicsDevice.Metrics.DrawCount;
#endif
	}

#endregion

	/// <summary>
	/// Called after a Scene ends, before the next Scene begins
	/// </summary>
	private void OnSceneChanged()
	{
		Emitter.Emit(CoreEvents.SceneChanged);
		Time.SceneChanged();
		GC.Collect();
	}

	/// <summary>
	/// temporarily runs SceneTransition allowing one Scene to transition to another smoothly with custom effects.
	/// </summary>
	/// <param name="sceneTransition">Scene transition.</param>
	public static T StartSceneTransition<T>(T sceneTransition) where T : SceneTransition
	{
		Insist.IsNull(_instance._sceneTransition,
			"You cannot start a new SceneTransition until the previous one has completed");
		_instance._sceneTransition = sceneTransition;
		return sceneTransition;
	}

	#region Global Managers

	/// <summary>
	/// adds a global manager object that will have its update method called each frame before Scene.update is called
	/// </summary>
	/// <returns>The global manager.</returns>
	/// <param name="manager">Manager.</param>
	public static void RegisterGlobalManager(GlobalManager manager)
	{
		_instance._globalManagers.Add(manager);
		manager.Enabled = true;
	}

	/// <summary>
	/// removes the global manager object
	/// </summary>
	/// <returns>The global manager.</returns>
	/// <param name="manager">Manager.</param>
	public static void UnregisterGlobalManager(GlobalManager manager)
	{
		_instance._globalManagers.Remove(manager);
		manager.Enabled = false;
	}

	/// <summary>
	/// gets the global manager of type T
	/// </summary>
	/// <returns>The global manager.</returns>
	/// <typeparam name="T">The 1st type parameter.</typeparam>
	public static T GetGlobalManager<T>() where T : GlobalManager
	{
		for (var i = 0; i < _instance._globalManagers.Length; i++)
			if (_instance._globalManagers.Buffer[i] is T)
				return _instance._globalManagers.Buffer[i] as T;

		return null;
	}

	#endregion

	#region Systems access

	/// <summary>
	/// starts a coroutine. Coroutines can yeild ints/floats to delay for seconds or yeild to other calls to startCoroutine.
	/// Yielding null will make the coroutine get ticked the next frame.
	/// </summary>
	/// <returns>The coroutine.</returns>
	/// <param name="enumerator">Enumerator.</param>
	public static ICoroutine StartCoroutine(IEnumerator enumerator)
	{
		return _instance._coroutineManager.StartCoroutine(enumerator);
	}

	/// <summary>
	/// schedules a one-time or repeating timer that will call the passed in Action
	/// </summary>
	/// <param name="timeInSeconds">Time in seconds.</param>
	/// <param name="repeats">If set to <c>true</c> repeats.</param>
	/// <param name="context">Context.</param>
	/// <param name="onTime">On time.</param>
	public static ITimer Schedule(float timeInSeconds, bool repeats, object context, Action<ITimer> onTime)
	{
		return _instance._timerManager.Schedule(timeInSeconds, repeats, context, onTime);
	}

	/// <summary>
	/// schedules a one-time timer that will call the passed in Action after timeInSeconds
	/// </summary>
	/// <param name="timeInSeconds">Time in seconds.</param>
	/// <param name="context">Context.</param>
	/// <param name="onTime">On time.</param>
	public static ITimer Schedule(float timeInSeconds, object context, Action<ITimer> onTime)
	{
		return _instance._timerManager.Schedule(timeInSeconds, false, context, onTime);
	}

	/// <summary>
	/// schedules a one-time or repeating timer that will call the passed in Action
	/// </summary>
	/// <param name="timeInSeconds">Time in seconds.</param>
	/// <param name="repeats">If set to <c>true</c> repeats.</param>
	/// <param name="onTime">On time.</param>
	public static ITimer Schedule(float timeInSeconds, bool repeats, Action<ITimer> onTime)
	{
		return _instance._timerManager.Schedule(timeInSeconds, repeats, null, onTime);
	}

	/// <summary>
	/// schedules a one-time timer that will call the passed in Action after timeInSeconds
	/// </summary>
	/// <param name="timeInSeconds">Time in seconds.</param>
	/// <param name="onTime">On time.</param>
	public static ITimer Schedule(float timeInSeconds, Action<ITimer> onTime)
	{
		return _instance._timerManager.Schedule(timeInSeconds, false, null, onTime);
	}

	#endregion

	#region Edit Mode
	public static event Action OnResetScene;
	public static event Action<bool> OnSwitchEditMode;
	public static event Action<bool> OnSwitchPauseMode;

	/// <summary>
	/// Raised when audio is muted or un-muted.  Payload: true = audio is ON, false = muted.
	/// Components that implement <see cref="IAudioComponent"/> subscribe here to respond.
	/// </summary>
	public static event Action<bool> OnSwitchAudio;

	private static bool _isEditMode;
	private static bool _isPauseMode;
	private static bool _isAudioOn = true;

	/// In EditMode, Entities' components aren't updated, and instead, user can use the ImGui inspector to move the objects in the scene manually.
	public static bool IsEditMode
	{
		get => _isEditMode;
		set
		{
			if (_isEditMode != value)
			{
				_isEditMode = value;
				InvokeSwitchEditMode(value);
			}
		}
	}

	/// <summary>
	/// When true, the game is in PlayMode but entity/component updates are suspended.
	/// The editor camera remains free to move. No data is saved (treated as PlayMode).
	/// Automatically cleared when switching back to EditMode.
	/// </summary>
	public static bool IsPauseMode
	{
		get => _isPauseMode;
		set
		{
			if (_isPauseMode != value)
			{
				_isPauseMode = value;
				OnSwitchPauseMode?.Invoke(value);
			}
		}
	}

	public static void InvokeSwitchEditMode(bool isEditMode)
	{
		if (isEditMode)
			IsPauseMode = false;

		// The property setter raises OnSwitchEditMode itself; going through it fired every subscriber twice.
		_isEditMode = isEditMode;
		OnSwitchEditMode?.Invoke(isEditMode);

#if EDITOR
		if (LogModeCycleDiagnostics)
			Debug.Log(ModeCycleDiagnostics());
#endif
	}

#if EDITOR
	/// <summary>Logs subscriber and object counts on every edit-mode switch, to catch listeners that outlive their scene.</summary>
	public static bool LogModeCycleDiagnostics = false;

	/// <summary>Subscriber, entity and heap counts; numbers that climb every Play/Stop cycle are listeners outliving their scene.</summary>
	public static string ModeCycleDiagnostics()
	{
		static int Subscribers(Delegate handler) => handler?.GetInvocationList().Length ?? 0;

		return "[mode-cycle] " +
		       $"OnSwitchEditMode={Subscribers(OnSwitchEditMode)} " +
		       $"OnResetScene={Subscribers(OnResetScene)} " +
		       $"OnSwitchPauseMode={Subscribers(OnSwitchPauseMode)} " +
		       $"OnSwitchAudio={Subscribers(OnSwitchAudio)} " +
		       $"globalManagers={_instance?._globalManagers.Length ?? 0} " +
		       $"entities={Scene?.Entities.Count ?? 0} " +
		       $"heapMB={GC.GetTotalMemory(false) / (1024f * 1024f):F1}";
	}
#endif

	/// <summary>
	/// Toggles PauseMode. Only meaningful while in PlayMode (IsEditMode == false).
	/// </summary>
	public static void InvokeSwitchPauseMode(bool isPauseMode)
	{
		IsPauseMode = isPauseMode;
	}

	public static void InvokeResetScene()
	{
		OnResetScene?.Invoke();
	}

	/// <summary>
	/// Whether game audio is currently active.  Defaults to <c>true</c>.
	/// Setting this to <c>false</c> mutes all <see cref="IAudioComponent"/> instances and
	/// also sets <see cref="Microsoft.Xna.Framework.Audio.SoundEffect.MasterVolume"/> to 0
	/// as a global backstop.
	/// </summary>
	public static bool IsAudioOn
	{
		get => _isAudioOn;
		set
		{
			if (_isAudioOn != value)
			{
				_isAudioOn = value;
				InvokeSwitchAudio(value);
			}
		}
	}

	/// <summary>
	/// Explicitly raises the <see cref="OnSwitchAudio"/> event and applies the global
	/// MonoGame master-volume backstop.  Mirrors the Invoke* convention used by
	/// <see cref="InvokeSwitchEditMode"/> and <see cref="InvokeSwitchPauseMode"/>.
	/// </summary>
	public static void InvokeSwitchAudio(bool isAudioOn)
	{
		_isAudioOn = isAudioOn;

		// Global backstop: mute/restore MonoGame's master volume so any sound that
		// slips through without implementing IAudioComponent is also silenced.
		Microsoft.Xna.Framework.Audio.SoundEffect.MasterVolume = isAudioOn ? 1f : 0f;

		OnSwitchAudio?.Invoke(isAudioOn);
	}
	#endregion

}