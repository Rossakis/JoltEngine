using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.Persistence;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.SceneFile;
using Voltage.Editor.Tools;
using Voltage.Editor.Utils;
using Voltage.Editor.Windows;
using Voltage.Utils;

namespace Voltage.Editor.Scripting
{
	public class ScriptManager : IDisposable
	{
		private ScriptWatcher _scriptWatcher;
		private Assembly _currentScriptAssembly;
		private readonly string _scriptsDirectory;
		private readonly PersistentBool _enableHotReload;
		private readonly PersistentBool _autoReloadSceneOnChange;
		private static PersistentBool _compileOnStartup = new PersistentBool("ScriptManager_CompileOnStartup", true);
		
		public static bool CompileOnStartup
		{
			get => _compileOnStartup.Value;
			set => _compileOnStartup.Value = value;
		}

		public bool EnableHotReload
		{
			get => _enableHotReload.Value;
			set
			{
				_enableHotReload.Value = value;
				if (_scriptWatcher != null)
					_scriptWatcher.AutoCompileOnFileChange = value;
			}
		}

		public bool AutoReloadSceneOnChange
		{
			get => _autoReloadSceneOnChange.Value;
			set => _autoReloadSceneOnChange.Value = value;
		}

		public Assembly CurrentScriptAssembly => _currentScriptAssembly;
		
		/// <summary>
		/// Event fired when compilation completes, with a boolean indicating whether to reload the scene
		/// </summary>
		public event Action<CompilationResult, bool> OnCompilationComplete;
		
		public event Action OnBeforeSceneReload;
		public event Action OnAfterSceneReload;

		/// <summary>True while the file watcher has a compile pending or running.</summary>
		public bool IsReloadPending => _scriptWatcher?.IsBusy == true;

		public ScriptManager(string scriptsDirectory = null)
		{
			_scriptsDirectory = scriptsDirectory ?? Path.Combine(Environment.CurrentDirectory, "Scripts");
			_enableHotReload = new PersistentBool("ScriptManager_EnableHotReload", true);
			_autoReloadSceneOnChange = new PersistentBool("ScriptManager_AutoReloadSceneOnChange", true);

			Initialize();
		}

		private void Initialize()
		{
			if (!Directory.Exists(_scriptsDirectory))
			{
				Directory.CreateDirectory(_scriptsDirectory);
				CreateExampleScript();
			}

			_scriptWatcher = new ScriptWatcher(_scriptsDirectory);
			_scriptWatcher.AutoCompileOnFileChange = _enableHotReload.Value;
			_scriptWatcher.OnCompilationComplete += HandleCompilationComplete;

			CompileScripts(EditorSettingsWindow.AutoReloadSceneAfterScriptCompile);
		}

		private void CreateExampleScript()
		{
			var exampleScriptPath = Path.Combine(_scriptsDirectory, "ExampleScript.cs");
			var exampleCode = @"using System;
using Voltage;
using Microsoft.Xna.Framework;

namespace GameScripts
{
	/// <summary>
	/// Example script component
	/// </summary>
	public class ExampleScript : Component, IUpdatable
	{
		public float Speed = 100f;

		public override void OnStart()
		{
			Console.WriteLine(""ExampleScript added to entity: "" + Entity.Name);
		}

		public void Update()
		{
			// Example: Move entity based on input
			var moveDir = Vector2.Zero;
			
			if (Input.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.W))
				moveDir.Y += 1;
			if (Input.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.S))
				moveDir.Y -= 1;
			if (Input.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.A))
				moveDir.X += 1;
			if (Input.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.D))
				moveDir.X -= 1;
			
			if (moveDir != Vector2.Zero)
			{
				moveDir.Normalize();
				Entity.Transform.Position += moveDir * Speed * Time.DeltaTime;
			}
		}
	}
}";

			File.WriteAllText(exampleScriptPath, exampleCode);
		}

		/// <summary>
		/// Handles when compilation completes
		/// </summary>
		private void HandleCompilationComplete(CompilationResult result)
		{
			if (result.Success)
			{
				_currentScriptAssembly = result.Assembly;
				Core.LatestScriptAssembly = result.Assembly;

				// Force all [ModuleInitializer] methods in the new assembly to run now
				// by touching a type from it. This ensures ComponentDataAotDeserializer
				// has the new deserializers registered before ReloadScene fires.
				System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(
					result.Assembly.GetModules()[0].ModuleHandle);

				// Invalidate the component type cache so newly compiled script components
				// appear in the editor's "Add Component" dropdown
				EntityInspectorWindow.InvalidateComponentTypeCache();
			}
			else
			{
				Debug.Error("Script compilation failed with errors:");
				foreach (var error in result.Errors)
					Debug.Error($"  {error}");
			}

			bool shouldReloadScene = result.Success &&
			                         EnableHotReload &&
			                         AutoReloadSceneOnChange &&
			                         Core.IsEditMode;

			OnCompilationComplete?.Invoke(result, shouldReloadScene);

			if (shouldReloadScene)
				ReloadScene();
		}

		/// <summary>
		/// Handles when scripts have changed (file watcher detected changes)
		/// </summary>
		private void HandleScriptsChanged(List<string> changedFiles)
		{
			EditorDebug.Log($"Scripts changed: {string.Join(", ", changedFiles.Select(Path.GetFileName))}");
		}

		/// <summary>
		/// Reloads the current scene using SceneManager as the single source of truth
		/// rather than PersistentScene (which is project-agnostic).
		/// </summary>
		public void ReloadScene()
		{
			if (Core.Scene == null)
			{
				Debug.Warn("No active scene to reload");
				return;
			}

			try
			{
				OnBeforeSceneReload?.Invoke();

				var sceneManager = SceneManager.Instance;
				if (sceneManager.HasLoadedScene)
				{
					sceneManager.ReloadCurrentScene();
				}
				else
				{
					var currentSceneType = Core.Scene.GetType();
					var newScene = (Scene)Activator.CreateInstance(currentSceneType);
					Core.Scene = newScene;
				}

				OnAfterSceneReload?.Invoke();
			}
			catch (Exception ex)
			{
				Debug.Error($"Failed to reload scene: {ex.Message}\n{ex.StackTrace}");
			}
		}

		/// <summary>
		/// Manually compile all scripts
		/// </summary>
		/// <param name="reloadSceneOnSuccess">Whether to reload the scene if compilation succeeds</param>
		public void CompileScripts(bool reloadSceneOnSuccess = false)
		{
			// Ensure EngineLibs are up-to-date before compiling so Roslyn
			// references the latest engine DLLs, not stale on-disk builds.
			var projectPath = ProjectManager.Instance?.CurrentProject?.ProjectPath;
			if (!string.IsNullOrEmpty(projectPath))
				EngineLibsSync.SyncToProject(projectPath);

			// Store the reload preference temporarily
			var originalAutoReload = AutoReloadSceneOnChange;
			
			try
			{
				// Override auto reload setting for manual compilation
				if (!reloadSceneOnSuccess)
				{
					_autoReloadSceneOnChange.Value = false;
				}
				
				_scriptWatcher.CompileAllScripts(false);
			}
			finally
			{
				// Restore original setting after a short delay to ensure compilation event fires first
				Core.Schedule(0.1f, false, this, _ =>
				{
					_autoReloadSceneOnChange.Value = originalAutoReload;
				});
			}
		}

		/// <summary>
		/// Gets all types from the current script assembly
		/// </summary>
		public Type[] GetScriptTypes()
		{
			if (_currentScriptAssembly == null)
				return Array.Empty<Type>();

			try
			{
				return _currentScriptAssembly.GetTypes();
			}
			catch (ReflectionTypeLoadException ex)
			{
				Debug.Error($"Error loading script types: {ex.Message}");
				return ex.Types.Where(t => t != null).ToArray();
			}
		}

		public Type[] GetScriptComponentTypes()
		{
			return GetScriptTypes()
				.Where(t => t.IsSubclassOf(typeof(Component)) && !t.IsAbstract)
				.ToArray();
		}

		public Type[] GetScriptEntityTypes()
		{
			return GetScriptTypes()
				.Where(t => t.IsSubclassOf(typeof(Entity)) && !t.IsAbstract)
				.ToArray();
		}

		public void Dispose()
		{
			if (_scriptWatcher != null)
			{
				_scriptWatcher.OnCompilationComplete -= HandleCompilationComplete;
				_scriptWatcher.Dispose();
			}
		}
	}
}