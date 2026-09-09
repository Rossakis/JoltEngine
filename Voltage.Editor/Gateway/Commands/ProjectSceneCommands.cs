using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.SceneFile;
using Voltage.Editor.Undo.Core;
using Voltage.Gateway;
using static Voltage.Editor.Gateway.GatewayValues;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Project and scene lifecycle.</summary>
internal static class ProjectSceneCommands
{
	public static void Register(GatewayCommandTable table)
	{
		table.Add("project.info", "The loaded project and its folders.", (_, _) => ProjectInfo()).ReadOnly();

		table.Add("project.recent", "Recently opened project files.", (_, _) => ProjectManager.Instance.GetRecentProjects()).ReadOnly();

		table.Add("project.load", "Open a .voltage project; answers once it has loaded.", (args, _) =>
		{
			var path = Path.GetFullPath(args.Require("path"));
			if (!File.Exists(path))
				throw new GatewayException($"project file not found: {path}");

			// The dispatcher runs before the ImGui frame begins, so the load can drive its own frames from here.
			if (!ProjectManager.Instance.LoadProject(path))
				throw new GatewayException("project failed to load; see log.tail");

			// Startup does the same two steps: the project alone leaves an empty placeholder scene behind.
			var loaded = SceneManager.Instance.LoadLastUsedScene();
			EditorChangeTracker.Clear();
			return WhenCurrent(loaded, () => new { project = ProjectInfo(), scene = SceneInfo() });
		}, P.Str("path", "Path to the .voltage file", required: true)).Destructive();

		table.Add("scene.list", "Scene files of the current project.", (_, _) =>
			SceneManager.Instance.GetAllSceneFiles().Select(p => new { name = Path.GetFileNameWithoutExtension(p), path = p }).ToList()).ReadOnly();

		table.Add("scene.info", "The open scene.", (_, _) => SceneInfo()).ReadOnly();

		table.Add("scene.load", "Open a scene by name or path. Refuses to drop unsaved changes unless force=true.", (args, _) =>
		{
			RequireProject();
			if (EditorChangeTracker.IsDirty && !args.Bool("force"))
				throw new GatewayException("scene has unsaved changes; call scene.save or pass force=true");

			var path = args.String("path");
			var name = args.String("name");
			Scene scene;
			if (!string.IsNullOrEmpty(path))
				scene = SceneManager.Instance.LoadScene(Path.GetFullPath(path));
			else if (!string.IsNullOrEmpty(name))
				scene = SceneManager.Instance.LoadSceneByName(name);
			else
				throw new GatewayException("pass 'name' or 'path'");

			if (scene == null)
				throw new GatewayException("scene failed to load; see log.tail");
			EditorChangeTracker.Clear();
			return WhenCurrent(scene, SceneInfo);
		}, P.Str("name", "Scene name without extension"), P.Str("path", "Scene file path; takes precedence over name"), P.Bool("force", "Discard unsaved changes", false)).Destructive();

		table.Add("scene.save", "Save the open scene to its file.", (_, _) =>
		{
			RequireProject();
			if (!SceneManager.Instance.SaveCurrentScene())
				throw new GatewayException("save failed; see log.tail");
			EditorChangeTracker.Clear();
			return SceneInfo();
		});

		table.Add("scene.reload", "Reload the open scene from disk, discarding unsaved changes.", (_, _) =>
		{
			RequireProject();
			if (!SceneManager.Instance.ReloadCurrentScene())
				throw new GatewayException("reload failed; see log.tail");
			EditorChangeTracker.Clear();
			return SceneInfo();
		}).Destructive();

		table.Add("scene.create", "Create a scene file in the project: 'empty' writes a fresh scene like the New Scene window, 'copy' saves the open scene under the new name.", (args, _) =>
		{
			RequireProject();
			var name = args.Require("name");
			if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
				throw new GatewayException($"invalid scene name '{name}'");
			var template = args.String("template", "empty").ToLowerInvariant();
			var load = args.Bool("load", true);
			var sm = SceneManager.Instance;

			if (template == "copy")
			{
				if (!sm.CreateSceneFile(name))
					throw new GatewayException("scene creation failed; see log.tail");
				return SceneInfo();
			}
			if (template != "empty")
				throw new GatewayException($"unknown template '{template}'; use empty or copy");
			if (load && EditorChangeTracker.IsDirty && !args.Bool("force"))
				throw new GatewayException("scene has unsaved changes; call scene.save, pass force=true, or load=false");

			var scenesFolder = ProjectManager.Instance.CurrentProject.ScenesFolder;
			Directory.CreateDirectory(scenesFolder);
			var path = Path.Combine(scenesFolder, $"{name}.vscene");
			if (File.Exists(path))
				throw new GatewayException($"scene file already exists: {path}");

			var data = new Voltage.Data.SceneData { Name = name, FilePath = path, CreatedAt = DateTime.Now, ModifiedAt = DateTime.Now };
			File.WriteAllText(path, Voltage.Persistence.Json.ToJson(data, new Voltage.Persistence.JsonSettings { PrettyPrint = true }), new System.Text.UTF8Encoding(false));
			sm.InvokeSceneCreated(path);
			if (!load)
				return new { path, loaded = false, scene = SceneInfo() };

			var created = sm.LoadScene(path) ?? throw new GatewayException("scene created but failed to load; see log.tail");
			EditorChangeTracker.Clear();
			return WhenCurrent(created, () => new { path, loaded = true, scene = SceneInfo() });
		}, P.Str("name", "Scene file name without extension", required: true), P.Enum("template", "empty: a new blank scene; copy: the open scene saved under the new name", new[] { "empty", "copy" }, "empty"), P.Bool("load", "Open the new scene", true), P.Bool("force", "Discard unsaved changes when loading", false));

		table.Add("project.create", "Create a new project like the New Project window and open it; answers once it has loaded.", (args, _) =>
		{
			var name = args.Require("name");
			var directory = Path.GetFullPath(args.Require("directory"));
			if (EditorChangeTracker.IsDirty && args.Bool("load", true) && !args.Bool("force"))
				throw new GatewayException("the open scene has unsaved changes; save it, pass force=true, or load=false");

			ProjectCreator.Result result;
			try
			{
				result = ProjectCreator.Create(name, directory, ProjectCreator.DefaultSettings(), load: false);
			}
			catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
			{
				throw new GatewayException(ex.Message);
			}

			if (!args.Bool("load", true))
				return new { path = result.ProjectPath, voltageFile = result.VoltageFile, scene = result.ScenePath, loaded = false };

			if (!ProjectManager.Instance.LoadProject(result.VoltageFile))
				throw new GatewayException($"project created at {result.ProjectPath} but failed to load; see log.tail");
			var opened = SceneManager.Instance.LoadLastUsedScene();
			EditorChangeTracker.Clear();
			return WhenCurrent(opened, () => new { path = result.ProjectPath, voltageFile = result.VoltageFile, loaded = true, project = ProjectInfo(), scene = SceneInfo() });
		}, P.Str("name", "Project and folder name", required: true), P.Str("directory", "Parent folder the project folder is created in", required: true), P.Bool("load", "Open the project after creating it", true), P.Bool("force", "Discard unsaved changes in the open scene", false)).Destructive().Unsafe();
	}

	/// <summary>Core swaps scenes on the next Update, so answer once the loaded one is current.</summary>
	private static object WhenCurrent(Scene scene, Func<object> result) =>
		scene == null ? result() : GatewayTasks.WhenReady(() => ReferenceEquals(Core.Scene, scene), result);

	private static object ProjectInfo()
	{
		var p = ProjectManager.Instance?.CurrentProject;
		if (p == null)
			return null;

		return new
		{
			name = p.ProjectName,
			path = p.ProjectPath,
			scenes = p.ScenesFolder,
			scripts = p.ScriptsFolder,
			prefabs = p.PrefabsFolder,
			content = p.ContentsFolder,
			data = p.DataFolder,
			effects = p.EffectsFolder
		};
	}

	private static object SceneInfo()
	{
		var sm = SceneManager.Instance;
		if (sm == null || !sm.HasLoadedScene)
			return null;

		return new
		{
			name = sm.CurrentSceneName,
			path = sm.CurrentScenePath,
			dirty = EditorChangeTracker.IsDirty,
			entityCount = Core.Scene?.Entities.Count ?? 0
		};
	}
}
