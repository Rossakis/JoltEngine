using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using System;
using System.Linq;
using System.Reflection;
using Voltage.Console;
using Voltage.Editor.Gateway;
using Voltage.Gateway;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.Persistence;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.SceneFile;
using Voltage.Utils;

#if OS_MAC
using System;
using System.IO;
#endif

namespace Voltage.Editor;

public class Editor : Core
{
	private EditorGatewayDispatcher _gateway;

	protected override void Initialize()
	{
		base.Initialize();

		var font = Content.LoadBitmapFont("DefaultContent/Fonts/VoltageDefaultBMFont.fnt");
		Graphics.Instance = new Graphics(font);

		// Component-bearing assemblies (e.g. Farseer) are no longer force-loaded here — they are
		// plugins now, loaded per-project by PluginManager when the project's plugins.json asks for them.
		
#if OS_MAC
        Directory.SetCurrentDirectory(AppContext
            .BaseDirectory); //For some reason, on Mac directory needs to be set manually, or it won't find the Content folder
#endif
		Content.RootDirectory = "Content";

		IsEditMode = true;
		var options = new ImGuiOptions();

		if (Screen.ActualMonitorWidth <= 1920)
		{
			options.FontSizeMultiplier = 1f;
			DebugConsole.RenderScale = 1.5f;
		}
		else if (Screen.ActualMonitorWidth < 3840)
		{ 
			options.FontSizeMultiplier = 1.1f;
			DebugConsole.RenderScale = 2.5f;
		}
		else
		{
			options.FontSizeMultiplier = 1.2f;
			DebugConsole.RenderScale = 3f;
		}

		options.IncludeDefaultFont(true);
		var imGuiManager = new ImGuiManager(options);

		RegisterGlobalManager(imGuiManager);

		// Registered after ImGuiManager so it updates before it: managers run in reverse registration order.
		var gatewayOptions = GatewayOptions.FromArgs(Program.CommandLineArgs, true);
		_gateway = new EditorGatewayDispatcher(new GatewayOptions
		{
			Enabled = gatewayOptions.Enabled,
			Port = gatewayOptions.Port,
			Safe = gatewayOptions.Safe,
			InfoFilePath = gatewayOptions.InfoFilePath ?? System.IO.Path.Combine(EditorStorage.Root, "gateway.json"),
			Args = Program.CommandLineArgs,
			LogsDirectory = EditorStorage.LogsDirectory,
			Host = "editor",
			Name = "Voltage Editor",
			NoPrompts = gatewayOptions.NoPrompts,
			Headless = gatewayOptions.Headless
		}, imGuiManager);
		RegisterGlobalManager(_gateway);

		Scene.OnSceneBegin += TrackSceneChange;
		Scene.OnSceneBegin += SetSceneClearColor;

		Window.AllowUserResizing = true;
		ExitOnEscapeKeypress = false;

		IsFixedTimeStep = false; 
		Screen.SynchronizeWithVerticalRetrace = true;
		Screen.HardwareModeSwitch = false; // Fix for the Display to not fight against Monogame's F11 fullscreen toggle

		if (EditorRunMode.Headless)
			EnterHeadless();
		else
		{
			ScreenUtils.ApplyScreenChange(ScreenUtils.ScreenMode.WindowedMax);
			Voltage.Editor.Utils.EditorWindowLayout.FitToUsableBounds();
		}
		HandleCommandLineArguments(); // when we open a project file through the file explorer
		SceneManager.Instance.LoadLastUsedScene();

		Voltage.Editor.Utils.EditorBackgroundColor.Apply();
	}

	protected override void EndRun()
	{
		base.EndRun();
		_gateway?.Shutdown();
		Scene.OnSceneBegin -= TrackSceneChange;
		Scene.OnSceneBegin -= SetSceneClearColor;
	}


	private void SetSceneClearColor()
	{
		Voltage.Editor.Utils.EditorBackgroundColor.Apply();
	}

	/// <summary>
	/// Tracks scene changes and persists the last opened scene.
	/// </summary>
	private void TrackSceneChange()
	{
		var sceneManager = SceneManager.Instance;

		if (sceneManager.HasLoadedScene && !string.IsNullOrWhiteSpace(sceneManager.CurrentScenePath))
		{
			PersistentScene.SetLastScenePath(sceneManager.CurrentScenePath);
		}
		else
		{
			PersistentScene.Clear();
		}
	}

	/// <summary>The editor registers its own dispatcher; IsEditorMode is not yet set when Core initializes.</summary>
	protected override void StartRuntimeGateway()
	{
	}

	protected override void Draw(GameTime gameTime)
	{
		// The gateway keeps the loop alive while unfocused, but a minimized window has no surface to draw to.
		// Update may leave a render target bound for Draw to clear, and Present refuses to run with one active.
		if (!EditorRunMode.Headless && IsWindowMinimized())
		{
			_gateway?.ImGuiManager.DiscardFrame();
			GraphicsDevice.SetRenderTarget(null);
			return;
		}

		base.Draw(gameTime);
		_gateway?.AfterDraw();
	}

	[System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private delegate uint SdlGetWindowFlags(IntPtr window);

	[System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private delegate void SdlHideWindow(IntPtr window);

	/// <summary>A fixed windowed size keeps screenshot coordinates stable; the hidden SDL window still owns a GL context, so drawing and back-buffer reads keep working.</summary>
	private void EnterHeadless()
	{
		ScreenUtils.ApplyScreenChange(ScreenUtils.ScreenMode.Windowed);
		Screen.SetSize(1600, 900);
		KeepRunningWhenUnfocused = true;
		if (Voltage.Editor.Utils.SdlNative.TryGet<SdlHideWindow>("SDL_HideWindow", out var hide))
			hide(Window.Handle);
		else
			Debug.Warn("[Headless] SDL_HideWindow unavailable; the window stays visible");
	}

	private const uint SdlWindowMinimized = 0x40;

	private bool IsWindowMinimized()
	{
		return Voltage.Editor.Utils.SdlNative.TryGet<SdlGetWindowFlags>("SDL_GetWindowFlags", out var getFlags)
			&& (getFlags(Window.Handle) & SdlWindowMinimized) != 0;
	}

	protected override void Update(GameTime gameTime)
	{
		base.Update(gameTime);

#if OS_WINDOWS || LINUX
		if (Input.IsKeyPressed(Keys.F11))
		{
			if (ScreenUtils.Mode == ScreenUtils.ScreenMode.FullScreen)
			{
				ScreenUtils.ApplyScreenChange(ScreenUtils.ScreenMode.Windowed);
			}
			else
			{
				ScreenUtils.ApplyScreenChange(ScreenUtils.ScreenMode.FullScreen);
			}
		}
#elif OS_MAC
    if (Input.IsKeyDown(Keys.LeftControl) && Input.IsKeyDown(Keys.LeftWindows) && Input.IsKeyPressed(Keys.F))
    {
	    if (ScreenUtils.Mode == ScreenUtils.ScreenMode.FullScreen)
	    {
		    ScreenUtils.ApplyScreenChange(ScreenUtils.ScreenMode.Windowed);
	    }
	    else
	    {
		    ScreenUtils.ApplyScreenChange(ScreenUtils.ScreenMode.FullScreen);
	    }
    }
#endif
	}

	/// <summary>
	/// Processes command-line arguments to load a project file if specified.
	/// </summary>
	private void HandleCommandLineArguments()
	{
		var args = Program.CommandLineArgs;
		
		if (args != null && args.Length > 0)
		{
			// The project path is the first argument that is not a --flag (or a flag's value).
			string projectPath = null;
			for (var i = 0; i < args.Length; i++)
			{
				if (args[i].StartsWith("--", System.StringComparison.Ordinal))
				{
					if (GatewayOptions.TakesValue(args[i]))
						i++;
					continue;
				}
				projectPath = args[i];
				break;
			}
			
			if (!string.IsNullOrWhiteSpace(projectPath) && 
				System.IO.File.Exists(projectPath) &&
				System.IO.Path.GetExtension(projectPath).Equals(".voltage", System.StringComparison.OrdinalIgnoreCase))
			{
				var projectManager = ProjectManager.Instance;
				bool success = projectManager.LoadProject(projectPath);
				
				if (!success)
					Debug.Error($"Failed to load project from: {projectPath}");
			}
			else if (!string.IsNullOrWhiteSpace(projectPath))
			{
				Debug.Warn($"Invalid project file specified: {projectPath}");
			}
		}
	}

}