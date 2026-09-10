using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Voltage.Editor.Assets;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.Persistence;
using Voltage.Editor.SceneFile;
using Voltage.Editor.Utils;
using Voltage.Editor.Scripting;
using Voltage.Project;
using Voltage.Systems;
using Voltage.Utils;

namespace Voltage.Editor.ProjectFile;

/// <summary>
/// Global manager that tracks the current and last opened IGameProject.
/// Used by the Editor to manage project state across sessions.
/// </summary>
public class ProjectManager : GlobalManager
{
	private PersistentString _recentProjects = new("ProjectManager_RecentProjects", "");
	private const int MaxRecentProjects = 10;

	#region Singleton/Instance Access

	private static ProjectManager _instance;

	/// <summary>
	/// Gets the singleton instance of the ProjectManager.
	/// </summary>
	public static ProjectManager Instance
	{
		get
		{
			if (_instance == null)
			{
				_instance = Core.GetGlobalManager<ProjectManager>();
				if (_instance == null)
				{
					_instance = new ProjectManager();
					Core.RegisterGlobalManager(_instance);
				}
			}
			return _instance;
		}
	}

	#endregion

	#region Persistent Settings

	private PersistentString _lastProjectPath = new("ProjectManager_LastProjectPath", "");
	private static readonly string _editorBaseDirectory = AppContext.BaseDirectory;
	private const string _projectPathKeyPrefix = "LocalProjectPath_";
	
	#endregion

	#region Properties

	public IGameProject CurrentProject { get; private set; }

	public string LastProjectPath
	{
		get => _lastProjectPath.Value;
		private set => _lastProjectPath.Value = value;
	}

	/// <summary>
	/// Indicates whether a project is currently loaded.
	/// </summary>
	public bool HasActiveProject => CurrentProject != null;

	#endregion

	#region Events

	public event Action<IGameProject> OnProjectLoaded;
	public event Action<IGameProject, IGameProject> OnProjectChanged;
	public event Action OnProjectUnloaded;

	#endregion

	#region Initialization

	public ProjectManager()
	{
		_instance = this;
	}

	/// <summary>
	/// Attempts to load the last opened project if available.
	/// Should be called during editor startup.
	/// </summary>
	public void LoadLastProject()
	{
		if (string.IsNullOrWhiteSpace(LastProjectPath))
		{
			Debug.Log("No last project path found.");
			return;
		}

		if (!File.Exists(LastProjectPath))
		{
			Debug.Warn($"Last project file not found: {LastProjectPath}");
			LastProjectPath = ""; // Clear invalid path
			return;
		}

		try
		{
			LoadProject(LastProjectPath);
		}
		catch (Exception ex)
		{
			Debug.Error($"Failed to load last project from '{LastProjectPath}': {ex.Message}");
			LastProjectPath = ""; // Clear invalid path
		}
	}

	#endregion

	#region Project Management

	/// <summary>
	/// Loads a project from the specified .voltage file path.
	/// </summary>
	/// <param name="voltageFilePath">Path to the .voltage file</param>
	/// <returns>True if the project was loaded successfully</returns>
	private const int LoadPhaseCount = 4;

	public bool LoadProject(string voltageFilePath)
	{
		if (string.IsNullOrWhiteSpace(voltageFilePath))
		{
			Debug.Error("Project path cannot be null or empty.");
			return false;
		}

		if (!File.Exists(voltageFilePath))
		{
			Debug.Error($"Project file not found: {voltageFilePath}");
			return false;
		}

		try
		{
			var jsonContent = File.ReadAllText(voltageFilePath);
			var metadata = Voltage.Persistence.Json.FromJson<ProjectCreatorWindow.ProjectMetadata>(jsonContent);

			if (metadata == null)
			{
				Debug.Error($"Failed to deserialize project metadata from: {voltageFilePath}");
				return false;
			}

			// Before anything loads: an editor older than the project cannot be trusted to round-trip it.
			ProjectEngineVersion.Evaluate(metadata);

			// Create a RuntimeGameProject from the metadata
			metadata.ProjectPath = ResolveAndCacheProjectPath(voltageFilePath);
			var project = new RuntimeGameProject(metadata);
			
			// Unload current project if exists
			var oldProject = CurrentProject;
			if (CurrentProject != null)
			{
				UnloadCurrentProject();
			}

			// Set as current project
			CurrentProject = project;
			LastProjectPath = voltageFilePath;

			AddToRecentProjects(voltageFilePath);

			using var progress = Utils.ProjectLoadProgress.Begin(project.ProjectName, LoadPhaseCount);

			Utils.ProjectLoadProgress.BeginPhase("Opening project");
			Utils.ProjectLoadProgress.Report(voltageFilePath);
			project.Initialize();

			Utils.ProjectLoadProgress.BeginPhase("Syncing engine libraries");
			EngineLibsSync.SyncToProject(project.ProjectPath);
			ProjectStructureGenerator.EnsureDefaultFontExists(project.ProjectPath);
			ProjectSettings.Instance = project.Settings;

			Utils.EditorBackgroundColor.Apply();

			Utils.ProjectLoadProgress.BeginPhase("Restoring plugins");
			Plugins.PluginManager.Instance.RestoreForProject(project);

			// Point the content resolver at the game project root so that relative
			// paths like "Content/..." resolve against the project, not the editor binary.
			VoltageContentManager.ContentRoot = project.ProjectPath;

			// Point Scene.LoadLevel(name) at the project's Scenes folder so it resolves correctly
			// in play mode (where AppContext.BaseDirectory is the editor binary, not the game project).
			Scene.ScenesDirectory = project.ScenesFolder;

			// Same for Scene.LoadPrefab(path) — resolve relative prefab paths against the project's
			// Prefabs folder in play mode rather than the editor binary directory.
			Scene.PrefabsDirectory = project.PrefabsFolder;

			// Phase 4b: wire the GUID-based prefab path resolver so the engine's overlay-load
			// path can find source prefabs by GUID via AssetDatabase (survives renames).
			var prefabsRoot = project.PrefabsFolder;
			Scene.PrefabPathResolver = (guid, name) =>
			{
				// 1. GUID lookup via AssetDatabase.
				if (guid != Guid.Empty && AssetDatabase.Instance != null)
				{
					var byGuid = AssetDatabase.Instance.GetPath(guid);
					if (!string.IsNullOrEmpty(byGuid) && File.Exists(byGuid))
						return byGuid;
				}

				// 2. Name-based fallback — scan PrefabsFolder subdirectories.
				if (!string.IsNullOrEmpty(name) && Directory.Exists(prefabsRoot))
				{
					var direct = Path.Combine(prefabsRoot, name + ".vprefab");
					if (File.Exists(direct))
						return direct;

					foreach (var sub in Directory.GetDirectories(prefabsRoot))
					{
						var candidate = Path.Combine(sub, name + ".vprefab");
						if (File.Exists(candidate)) return candidate;
						candidate = Path.Combine(sub, name + ".prefab");
						if (File.Exists(candidate)) return candidate;
					}
				}

				return null;
			};

			// General asset GUID -> path resolver (for AssetReference), backed by the AssetDatabase so
			// play mode resolves by GUID; published builds use the baked manifest instead.
			Scene.AssetPathResolver = guid =>
			{
				if (guid == Guid.Empty || AssetDatabase.Instance == null)
					return null;
				var p = AssetDatabase.Instance.GetPath(guid);
				return !string.IsNullOrEmpty(p) && File.Exists(p) ? p : null;
			};

			// Reverse resolver (stored path -> GUID + fresh relative hint), backed by the AssetDatabase. Lets
			// components that persisted only a path recover their stable GUID and refresh it after a rename/move.
			Scene.AssetReferenceResolver = storedPath =>
			{
				if (string.IsNullOrEmpty(storedPath) || AssetDatabase.Instance == null)
					return default;
				var abs = Path.IsPathRooted(storedPath)
					? storedPath
					: Path.GetFullPath(storedPath, project.ProjectPath);
				if (!File.Exists(abs))
					return default;
				var editorRef = AssetDatabase.Instance.GetReference(abs);
				return new Voltage.Serialization.AssetReference
				{
					AssetGuid = editorRef.Guid,
					AssetPath = editorRef.HintPath,
					AssetName = Path.GetFileNameWithoutExtension(editorRef.HintPath),
				};
			};

			Utils.ProjectLoadProgress.BeginPhase("Indexing assets and opening the scene");
			OnProjectLoaded?.Invoke(project);
			if (oldProject != null)
			{
				OnProjectChanged?.Invoke(oldProject, project);
			}

			return true;
		}
		catch (Exception ex)
		{
			Debug.Error($"Failed to load project from '{voltageFilePath}': {ex.Message}");
			Debug.Error($"Stack trace: {ex.StackTrace}");
			return false;
		}
	}

	/// <summary>
	/// Unloads the current project and resets all dependent subsystems.
	/// </summary>
	public void UnloadCurrentProject()
	{
		if (CurrentProject == null)
		{
			Debug.Warn("No project to unload.");
			return;
		}

		var project = CurrentProject;

		// Clear scene state BEFORE nulling the project so subsystems can still
		// read CurrentProject if needed during cleanup.
		SceneManager.Instance.ClearCurrentScene();
		PersistentScene.Clear();

		project.UnloadContent();
		EditorDebug.Log($"Unloaded project: {project.ProjectName}");

		Plugins.PluginManager.Instance.OnProjectUnloaded();

		CurrentProject = null;

		// Restore the content resolver to the editor's base directory so
		// editor-relative assets continue to load correctly with no project active.
		VoltageContentManager.ContentRoot = _editorBaseDirectory;

		OnProjectUnloaded?.Invoke();
	}

	#endregion

	#region Project Context Validation

	/// <summary>
	/// Validates that a given file path belongs to the currently loaded project.
	/// Use this before any save/load operation to prevent cross-project contamination.
	/// </summary>
	/// <param name="filePath">Absolute file path to validate</param>
	/// <returns>True if the path is inside the current project's directory tree</returns>
	public bool IsPathInCurrentProject(string filePath)
	{
		if (!HasActiveProject || string.IsNullOrWhiteSpace(filePath))
			return false;

		return CrossPlatformPath.IsPathUnder(CurrentProject.ProjectPath, filePath);
	}

	/// <summary>
	/// Returns the scenes folder of the current project, or null if no project is loaded.
	/// All scene operations should use this to resolve paths.
	/// </summary>
	public string GetScenesFolder()
	{
		return CurrentProject?.ScenesFolder;
	}

	/// <summary>
	/// Returns the scripts folder of the current project, or null if no project is loaded.
	/// All script operations should use this to resolve paths.
	/// </summary>
	public string GetScriptsFolder()
	{
		return CurrentProject?.ScriptsFolder;
	}

	/// <summary>
	/// Returns the prefabs folder of the current project, or null if no project is loaded.
	/// All prefab operations should use this to resolve paths.
	/// </summary>
	public string GetPrefabsFolder()
	{
		return CurrentProject?.PrefabsFolder;
	}
	
	/// <summary>
	/// Returns a stable, machine-independent key for a .voltage file,
	/// based on the project name embedded in the file name.
	/// </summary>
	private static string GetLocalPathKey(string voltageFilePath)
	{
		// Key is based on filename (e.g. "Jolt.voltage" → "LocalProjectPath_Jolt")
		var fileNameNoExt = Path.GetFileNameWithoutExtension(voltageFilePath);
		return $"{_projectPathKeyPrefix}{fileNameNoExt}";
	}
	
	/// <summary>The .voltage file's own folder wins when it holds a project, so a copy or clone opens itself; the stored path only covers a file kept elsewhere.</summary>
	private static string ResolveAndCacheProjectPath(string voltageFilePath)
	{
		var key = GetLocalPathKey(voltageFilePath);
		var stored = EditorSettingsLoader.LoadSetting(key, "");
		var voltageDir = Path.GetDirectoryName(Path.GetFullPath(voltageFilePath))!;

		var ownFolderIsProject = File.Exists(Path.Combine(voltageDir, "ProjectSettings.json")) || Directory.Exists(Path.Combine(voltageDir, "Scripts"));
		if (!ownFolderIsProject && !string.IsNullOrWhiteSpace(stored) && Directory.Exists(stored))
		{
			Debug.Log($"Loaded local project path for key '{key}': {stored}");
			return stored;
		}

		if (!string.Equals(stored, voltageDir, StringComparison.OrdinalIgnoreCase))
			EditorSettingsLoader.SaveSetting(key, voltageDir);
		return voltageDir;
	}

	#endregion

	#region Recent Projects

	/// <summary>
	/// Gets the list of recent project paths.
	/// </summary>
	public List<string> GetRecentProjects()
	{
		if (string.IsNullOrWhiteSpace(_recentProjects.Value))
		{
			return new List<string>();
		}

		return _recentProjects.Value
			.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
			.Where(path => File.Exists(path))
			.ToList();
	}

	/// <summary>
	/// Adds a project to the recent projects list.
	/// </summary>
	private void AddToRecentProjects(string projectPath)
	{
		var recentProjects = GetRecentProjects();

		recentProjects.Remove(projectPath);
		recentProjects.Insert(0, projectPath);

		if (recentProjects.Count > MaxRecentProjects)
		{
			recentProjects = recentProjects.Take(MaxRecentProjects).ToList();
		}

		_recentProjects.Value = string.Join("|", recentProjects);
	}

	/// <summary>
	/// Clears the recent projects list.
	/// </summary>
	public void ClearRecentProjects()
	{
		_recentProjects.Value = "";
	}

	#endregion
}
