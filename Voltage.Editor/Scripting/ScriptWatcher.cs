	using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Timers;
using Voltage.Editor.DebugUtils;

namespace Voltage.Editor.Scripting
{
	/// <summary>
	/// Watches script files for changes and triggers hot reload
	/// </summary>
	public class ScriptWatcher : IDisposable
	{
		private readonly string _scriptsDirectory;
		private readonly FileSystemWatcher _fileWatcher;
		private readonly List<FileSystemWatcher> _devPluginWatchers = new();
		private readonly Timer _debounceTimer;
		private readonly HashSet<string> _changedFiles = new();
		private readonly object _lockObject = new();
		private bool _isCompiling = false;
		private ScriptCompilationProgressWindow _progressWindow;

		public event Action<CompilationResult> OnCompilationComplete;

		/// <summary>True while a change is debouncing or compiling, so callers can wait for the hot reload it will trigger.</summary>
		public bool IsBusy
		{
			get
			{
				lock (_lockObject)
					return _isCompiling || _changedFiles.Count > 0 || _debounceTimer.Enabled;
			}
		}

		/// <summary>
		/// When true, file changes will automatically trigger compilation.
		/// When false, file changes are still detected and logged but compilation is not triggered.
		/// </summary>
		public bool AutoCompileOnFileChange { get; set; } = true;

		/// <summary>
		/// Debounce delay in milliseconds to avoid multiple compilations
		/// </summary>
		public double DebounceDelay { get; set; } = 500;

		public ScriptWatcher(string scriptsDirectory)
		{
			_scriptsDirectory = scriptsDirectory;
			_progressWindow = new ScriptCompilationProgressWindow();

			if (!Directory.Exists(_scriptsDirectory))
			{
				Directory.CreateDirectory(_scriptsDirectory);
				EditorDebug.Log($"Created scripts directory: {_scriptsDirectory}");
			}

			_fileWatcher = new FileSystemWatcher(_scriptsDirectory)
			{
				Filter = "*.cs",
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
				IncludeSubdirectories = true,
				EnableRaisingEvents = true
			};

			_fileWatcher.Changed += OnFileChanged;
			_fileWatcher.Created += OnFileChanged;
			_fileWatcher.Deleted += OnFileChanged;
			_fileWatcher.Renamed += OnFileRenamed;

			_debounceTimer = new Timer(DebounceDelay);
			_debounceTimer.Elapsed += OnDebounceTimerElapsed;
			_debounceTimer.AutoReset = false;

			// Dev-mode plugin source folders hot-reload like project scripts. Cache-installed plugin
			// payloads are immutable, so they compile with the scripts but are never watched.
			foreach (var devRoot in Plugins.PluginManager.Instance.GetDevSourceRoots())
			{
				try
				{
					var watcher = new FileSystemWatcher(devRoot)
					{
						Filter = "*.cs",
						NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
						IncludeSubdirectories = true,
						EnableRaisingEvents = true
					};

					watcher.Changed += OnFileChanged;
					watcher.Created += OnFileChanged;
					watcher.Deleted += OnFileChanged;
					watcher.Renamed += OnFileRenamed;
					_devPluginWatchers.Add(watcher);

					EditorDebug.Log($"Watching dev plugin sources: {devRoot}", "Plugins");
				}
				catch (Exception ex)
				{
					EditorDebug.Warn($"Could not watch dev plugin source root '{devRoot}': {ex.Message}", "Plugins");
				}
			}
		}

		/// <summary>
		/// All .cs files that participate in a script compile: the project's Scripts folder plus every
		/// restored plugin source root (dev plugins from their working folder). obj/bin are excluded.
		/// </summary>
		private List<string> CollectAllCompileFiles()
		{
			var files = Directory.GetFiles(_scriptsDirectory, "*.cs", SearchOption.AllDirectories)
				.Where(f => !f.Contains("obj") && !f.Contains("bin"))
				.ToList();

			foreach (var root in Plugins.PluginManager.Instance.GetSourceRoots())
			{
				if (!Directory.Exists(root))
					continue;

				files.AddRange(Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
					.Where(f => !f.Contains("obj") && !f.Contains("bin")));
			}

			return files;
		}

		private void OnFileChanged(object sender, FileSystemEventArgs e)
		{
			lock (_lockObject)
			{
				if (_isCompiling)
					return;

				_changedFiles.Add(e.FullPath);
				_debounceTimer.Stop();
				_debounceTimer.Start();

				EditorDebug.Log($"Script file changed: {Path.GetFileName(e.FullPath)}");
			}
		}

		private void OnFileRenamed(object sender, RenamedEventArgs e)
		{
			lock (_lockObject)
			{
				if (_isCompiling)
					return;

				_changedFiles.Add(e.FullPath);
				_debounceTimer.Stop();
				_debounceTimer.Start();

				EditorDebug.Log($"Script file renamed: {Path.GetFileName(e.OldFullPath)} -> {Path.GetFileName(e.FullPath)}");
			}
		}

		private void OnDebounceTimerElapsed(object sender, ElapsedEventArgs e)
		{
			lock (_lockObject)
			{
				if (_isCompiling || _changedFiles.Count == 0)
					return;

				if (!AutoCompileOnFileChange)
				{
					EditorDebug.Log($"Script files changed but auto-compile is disabled. Use 'Compile Scripts' to apply changes.");
					_changedFiles.Clear();
					return;
				}

				_isCompiling = true;
				var changedFilesCopy = new List<string>(_changedFiles);
				_changedFiles.Clear();

				// Trigger compilation on main thread
				Core.Schedule(0.01f, false, this, _ =>
				{
					CompileScripts(changedFilesCopy);
				});
			}
		}

		private void CompileScripts(List<string> changedFiles, bool reloadScene = true)
		{
			try
			{
				EditorDebug.Log($"Hot reloading scripts: {string.Join(", ", changedFiles.Select(Path.GetFileName))}");

				var allScriptFiles = CollectAllCompileFiles();

				if (allScriptFiles.Count == 0)
				{
					Debug.Warn("No script files found to compile");
					return;
				}

				_progressWindow.Show(allScriptFiles.Count);
				_progressWindow.UpdateProgress($"Compiling {allScriptFiles.Count} file(s)...");

				var result = ScriptCompiler.Compile(allScriptFiles, "DynamicScripts_" + DateTime.Now.Ticks);
				_progressWindow.Complete(result.Success, allScriptFiles.Count);

				if (result.Success)
				{
					EditorDebug.Success($"Successfully compiled {allScriptFiles.Count} script file(s)");
				}

				OnCompilationComplete?.Invoke(result);
			}
			catch (Exception ex)
			{
				Debug.Error($"Error during script compilation: {ex.Message}\n{ex.StackTrace}");
				_progressWindow?.Complete(false, 0);

				// Fire the event with a failure result so ScriptManager is notified
				OnCompilationComplete?.Invoke(new CompilationResult
				{
					Success = false,
					Errors = new List<string> { $"Internal error: {ex.Message}" },
					Assembly = null
				});
			}
			finally
			{
				lock (_lockObject)
				{
					_isCompiling = false;
				}
			}
		}

		/// <summary>
		/// Manually trigger compilation of all scripts
		/// </summary>
		public void CompileAllScripts(bool reloadScene)
		{
			var allScriptFiles = CollectAllCompileFiles();

			if (allScriptFiles.Count > 0)
			{
				CompileScripts(allScriptFiles);
			}
		}

		/// <summary>
		/// Draws the progress window if visible
		/// </summary>
		public void DrawProgress()
		{
			_progressWindow?.Draw();
		}

		public void Dispose()
		{
			_fileWatcher?.Dispose();
			foreach (var watcher in _devPluginWatchers)
				watcher.Dispose();
			_devPluginWatchers.Clear();
			_debounceTimer?.Dispose();
		}
	}
}