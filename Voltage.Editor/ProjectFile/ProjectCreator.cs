using System;
using System.IO;
using Voltage.Data;
using Voltage.Editor.Persistence;
using Voltage.Project;

namespace Voltage.Editor.ProjectFile
{
	/// <summary>Creates a project on disk the way the New Project window does; shared by the window and the gateway.</summary>
	public static class ProjectCreator
	{
		public const string ScriptsFolder = "Scripts";
		public const string EffectsFolder = "Effects";
		public const string ContentsFolder = "Content";
		public const string DataFolder = "Data";
		public const string ScenesFolder = "Scenes";
		public const string PrefabsFolder = "Prefabs";
		public const string DefaultSceneName = "MainScene";

		public sealed record Result(string ProjectPath, string VoltageFile, string ScenePath);

		/// <summary>Writes the folders, solution, metadata, settings and default scene, and queues the load when asked. Throws on failure.</summary>
		public static Result Create(string name, string parentDirectory, ProjectSettings settings, Version version = null, bool load = true)
		{
			if (string.IsNullOrWhiteSpace(name))
				throw new ArgumentException("Project name cannot be empty.", nameof(name));
			if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
				throw new ArgumentException($"Project name '{name}' contains invalid characters.", nameof(name));
			if (string.IsNullOrWhiteSpace(parentDirectory))
				throw new ArgumentException("Project location cannot be empty.", nameof(parentDirectory));

			var projectPath = Path.Combine(Path.GetFullPath(parentDirectory), name);
			if (Directory.Exists(projectPath))
				throw new InvalidOperationException($"A project named '{name}' already exists at {projectPath}.");

			Directory.CreateDirectory(projectPath);
			var dataPath = Path.Combine(projectPath, DataFolder);
			var scenesPath = Path.Combine(dataPath, ScenesFolder);
			Directory.CreateDirectory(Path.Combine(projectPath, ScriptsFolder));
			Directory.CreateDirectory(Path.Combine(projectPath, EffectsFolder));
			Directory.CreateDirectory(Path.Combine(projectPath, ContentsFolder));
			Directory.CreateDirectory(dataPath);
			Directory.CreateDirectory(scenesPath);
			Directory.CreateDirectory(Path.Combine(dataPath, PrefabsFolder));

			if (!ProjectStructureGenerator.CreateProjectStructure(name, projectPath, version ?? new Version(1, 0, 0)))
				throw new InvalidOperationException("Failed to create the project structure; see the log.");

			var metadata = new ProjectCreatorWindow.ProjectMetadata
			{
				ProjectName = name,
				ProjectPath = projectPath,
				ScriptsFolder = ScriptsFolder,
				EffectsFolder = EffectsFolder,
				ContentsFolder = ContentsFolder,
				DataFolder = DataFolder,
				ScenesFolder = DataFolder + "/" + ScenesFolder,
				PrefabsFolder = DataFolder + "/" + PrefabsFolder,
				CreatedDate = DateTime.Now,
				EngineVersion = VoltageVersion.Engine,
			};

			var utf8 = new System.Text.UTF8Encoding(false);
			var pretty = new Voltage.Persistence.JsonSettings { PrettyPrint = true };
			var voltageFile = Path.Combine(projectPath, $"{name}.voltage");
			File.WriteAllText(voltageFile, Voltage.Persistence.Json.ToJson(metadata, pretty), utf8);
			EditorSettingsLoader.SaveSetting($"LocalProjectPath_{name}", projectPath);
			File.WriteAllText(Path.Combine(projectPath, "ProjectSettings.json"), Voltage.Persistence.Json.ToJson(settings ?? DefaultSettings(), pretty), utf8);

			CopyEngineEffects(projectPath);
			var scenePath = Path.Combine(scenesPath, $"{DefaultSceneName}.vscene");
			File.WriteAllText(scenePath, Voltage.Persistence.Json.ToJson(new SceneData { Name = DefaultSceneName }, pretty), utf8);

			if (load)
				ImGuiCore.ImGuiManager.RequestProjectLoad(voltageFile);

			return new Result(projectPath, voltageFile, scenePath);
		}

		/// <summary>Renderables need the compiled engine effects; without them the first sprite kills the frame, so a new project gets the editor's copies.</summary>
		public static int CopyEngineEffects(string projectPath)
		{
			var source = Path.Combine(AppContext.BaseDirectory, "Content", "Voltage", "Effects");
			if (!Directory.Exists(source))
				return 0;

			var target = Path.Combine(projectPath, ContentsFolder, "Voltage", "Effects");
			Directory.CreateDirectory(target);
			var copied = 0;
			foreach (var file in Directory.GetFiles(source, "*.mgfxo", SearchOption.AllDirectories))
			{
				var destination = Path.Combine(target, Path.GetRelativePath(source, file));
				Directory.CreateDirectory(Path.GetDirectoryName(destination));
				if (File.Exists(destination))
					continue;
				File.Copy(file, destination);
				copied++;
			}
			return copied;
		}

		/// <summary>The settings the New Project window starts from.</summary>
		public static ProjectSettings DefaultSettings() => ProjectCreatorWindow.CreateSettings();
	}
}
