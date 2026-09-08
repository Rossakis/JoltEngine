using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.SceneFile;
using Voltage.Editor.Undo.Core;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Project and scene lifecycle.</summary>
internal static class ProjectSceneCommands
{
	public static void Register(GatewayCommandTable table)
	{
		table.Add("project.info", "The loaded project and its folders.", (_, _) => ProjectInfo());

		table.Add("project.recent", "Recently opened project files.", (_, _) => ProjectManager.Instance.GetRecentProjects());

		table.Add("project.load", "Open a .voltage project; answers once it has loaded. params: path", (args, _) =>
		{
			var path = Path.GetFullPath(args.Require("path"));
			if (!File.Exists(path))
				throw new GatewayException($"project file not found: {path}");

			// The dispatcher runs before the ImGui frame begins, so the load can drive its own frames from here.
			if (!ProjectManager.Instance.LoadProject(path))
				throw new GatewayException("project failed to load; see log.tail");

			// Startup does the same two steps: the project alone leaves an empty placeholder scene behind.
			SceneManager.Instance.LoadLastUsedScene();
			EditorChangeTracker.Clear();
			return new { project = ProjectInfo(), scene = SceneInfo() };
		});

		table.Add("scene.list", "Scene files of the current project.", (_, _) =>
			SceneManager.Instance.GetAllSceneFiles().Select(p => new { name = Path.GetFileNameWithoutExtension(p), path = p }).ToList());

		table.Add("scene.info", "The open scene.", (_, _) => SceneInfo());

		table.Add("scene.load", "Open a scene by name or path. Refuses to drop unsaved changes unless force=true. params: name|path, force", (args, _) =>
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
			return SceneInfo();
		});

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
		});

		table.Add("scene.create", "Create a new scene file in the project. params: name", (args, _) =>
		{
			RequireProject();
			var name = args.Require("name");
			if (!SceneManager.Instance.CreateSceneFile(name))
				throw new GatewayException("scene creation failed; see log.tail");
			return SceneInfo();
		});
	}

	private static void RequireProject()
	{
		if (!ProjectManager.Instance.HasActiveProject)
			throw new GatewayException("no project loaded; call project.load first");
	}

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

/// <summary>Helpers for handlers that answer from an event.</summary>
internal static class GatewayTasks
{
	/// <summary>Completes with the source or fails after the timeout; the cleanup runs on timeout to detach handlers.</summary>
	public static Task<object> WithTimeout(TaskCompletionSource<object> source, TimeSpan timeout, Action cleanup, string timeoutMessage)
	{
		Core.Schedule((float)timeout.TotalSeconds, false, null, _ =>
		{
			if (source.TrySetException(new GatewayException(timeoutMessage)))
				cleanup?.Invoke();
		});
		return source.Task;
	}

	/// <summary>Fails a task that has not finished by the deadline; the original keeps running unobserved.</summary>
	public static Task<object> WithTimeout(Task<object> task, TimeSpan timeout, string timeoutMessage)
	{
		var source = new TaskCompletionSource<object>();
		task.ContinueWith(t =>
		{
			if (t.IsCompletedSuccessfully)
				source.TrySetResult(t.Result);
			else
				source.TrySetException(t.Exception?.GetBaseException() ?? new GatewayException("cancelled"));
		}, TaskContinuationOptions.ExecuteSynchronously);
		Core.Schedule((float)timeout.TotalSeconds, false, null, _ => source.TrySetException(new GatewayException(timeoutMessage)));
		return source.Task;
	}
}
