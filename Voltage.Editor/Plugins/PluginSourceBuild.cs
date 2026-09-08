using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Voltage.Persistence;

namespace Voltage.Editor.Plugins
{
	/// <summary>Builds a plugin added from its own source checkout, where the declared assemblies are gitignored release artifacts. Runs the same packaging target CI runs, against this editor's engine assemblies. Only ever triggered by files being absent, never by sources looking newer.</summary>
	public static class PluginSourceBuild
	{
		/// <summary>The MSBuild target a plugin repository exposes to stage its package layout.</summary>
		public const string PackageTarget = "PackagePlugin";

		public enum Outcome
		{
			/// <summary>Every declared assembly is already present - this is a package, not a source checkout.</summary>
			NotNeeded,

			/// <summary>The packaging target ran and produced what the manifest declares.</summary>
			Built,

			/// <summary>Files are missing and no project here knows how to produce them.</summary>
			NoPackagingProject,

			/// <summary>Buildable, but not here: MSBuild takes minutes, so only the install worker runs it.</summary>
			NotBuilt,

			/// <summary>The packaging target ran and failed, or produced less than the manifest declares.</summary>
			Failed,
		}

		public class Result
		{
			public Outcome Outcome;

			/// <summary>Manifest-declared payload files that were missing before the build.</summary>
			public List<string> MissingBefore = new();

			/// <summary>Still missing after the build, if one ran.</summary>
			public List<string> MissingAfter = new();

			/// <summary>The project the packaging target was found in, if any.</summary>
			public string ProjectPath;

			/// <summary>Build output worth showing when it failed.</summary>
			public string BuildLog;

			/// <summary>True when this folder looks like a plugin source checkout rather than a package.</summary>
			public bool IsSourceCheckout => Outcome != Outcome.NotNeeded;
		}

		/// <summary>Stages the package layout only when the manifest declares assemblies the folder lacks. Never throws. allowBuild is false on any path that must stay responsive.</summary>
		public static Result EnsureBuilt(string pluginFolder, bool allowBuild)
		{
			var result = new Result();

			var manifest = TryReadManifest(pluginFolder);
			if (manifest == null)
				return result; // No readable manifest: not ours to diagnose, LoadFrom will say so properly.

			result.MissingBefore = MissingPayloadFiles(pluginFolder, manifest);
			if (result.MissingBefore.Count == 0)
			{
				result.Outcome = Outcome.NotNeeded;
				return result;
			}

			result.ProjectPath = FindPackagingProject(pluginFolder);
			if (result.ProjectPath == null)
			{
				result.Outcome = Outcome.NoPackagingProject;
				result.MissingAfter = result.MissingBefore;
				return result;
			}

			if (!allowBuild)
			{
				result.Outcome = Outcome.NotBuilt;
				result.MissingAfter = result.MissingBefore;
				return result;
			}

			PluginLog.Log(
				$"'{manifest.Id}' is a source checkout - {result.MissingBefore.Count} declared file(s) are not " +
				$"built yet. Running {PackageTarget} in {Path.GetFileName(result.ProjectPath)}; this can take a " +
				"few minutes the first time.");

			var built = RunPackaging(pluginFolder, result.ProjectPath, out var log);
			result.BuildLog = log;
			result.MissingAfter = MissingPayloadFiles(pluginFolder, manifest);

			if (built && result.MissingAfter.Count == 0)
			{
				result.Outcome = Outcome.Built;
				PluginLog.Log($"Built '{manifest.Id}' from source.");
				return result;
			}

			result.Outcome = Outcome.Failed;
			PluginLog.Error($"Could not build '{manifest.Id}' from source. {Tail(log, 400)}");
			return result;
		}

		/// <summary>Rebuilds regardless of what exists, for picking up local edits. False when there is no packaging project or the build failed.</summary>
		public static bool Rebuild(string pluginFolder, out string log)
		{
			log = null;

			var project = FindPackagingProject(pluginFolder);
			if (project == null)
				return false;

			return RunPackaging(pluginFolder, project, out log);
		}

		/// <summary>What to do about it in terms of the checkout, instead of "file not found in the package". A cloned checkout is a throwaway folder, so advice pointing back at it is left out.</summary>
		public static string Explain(Result result, string pluginFolder, bool isClonedCheckout = false)
		{
			var missing = string.Join(", ", result.MissingAfter.Take(4))
			              + (result.MissingAfter.Count > 4 ? ", ..." : "");

			var buildCommand = isClonedCheckout
				? null
				: $"\n    dotnet build \"{result.ProjectPath}\" -t:{PackageTarget} " +
				  $"-p:VoltageEnginePath=\"{EngineAssembliesPath()}\"";

			switch (result.Outcome)
			{
				case Outcome.NotBuilt:
					return
						$"This is a plugin source checkout and its assemblies are not built yet: plugin.json " +
						$"declares {missing}, which a plugin repository gitignores because CI builds them for a " +
						"tagged release.\n\n" +
						(isClonedCheckout
							? "Install or update it from Plugin Manager - that clones and builds it in the " +
							  "background, with progress, instead of stalling the editor."
							: "Add it again from Plugin Manager > Add Plugin - that builds it in the background, " +
							  "with progress, instead of stalling the editor. Or build it yourself:" + buildCommand);

				case Outcome.NoPackagingProject:
					return
						$"This looks like a plugin source checkout rather than a built package: plugin.json declares " +
						$"{missing}, which {(isClonedCheckout ? "the repository at that commit" : pluginFolder)} does " +
						"not contain. Those are release artifacts - a plugin repository gitignores them and CI builds " +
						"them for a tagged release.\n\n" +
						$"There is no project there exposing a '{PackageTarget}' target, so the editor cannot build " +
						"them for you. Either build the plugin by hand and add the folder again, or install the " +
						"published release instead (Plugin Manager > Browse Plugins).";

				case Outcome.Failed:
					return
						$"This is a plugin source checkout, and building it did not produce {missing}.\n\n" +
						(isClonedCheckout
							? "The build output follows:\n\n"
							: "Build it yourself to see the full errors:" + buildCommand + "\n\n") +
						Tail(result.BuildLog, 800);

				default:
					return null;
			}
		}

		/// <summary>Manifest-declared payload files missing from disk, read without validating.</summary>
		private static List<string> MissingPayloadFiles(string pluginFolder, PluginManifest manifest)
		{
			var declared = new List<string>();

			if (manifest.Gameplay != null)
			{
				declared.AddRange(manifest.Gameplay.ManagedAssemblies ?? new List<string>());
				declared.AddRange(manifest.Gameplay.EditorManagedAssemblies ?? new List<string>());
			}

			if (manifest.Editor != null)
				declared.AddRange(manifest.Editor.Assemblies ?? new List<string>());

			return declared
				.Where(rel => !string.IsNullOrWhiteSpace(rel))
				.Where(rel => !File.Exists(Path.Combine(pluginFolder, PluginManifest.NormalizeRelative(rel))))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		private static PluginManifest TryReadManifest(string pluginFolder)
		{
			try
			{
				var path = Path.Combine(pluginFolder, PluginManifest.FileName);
				return File.Exists(path) ? Json.FromJson<PluginManifest>(File.ReadAllText(path)) : null;
			}
			catch
			{
				return null;
			}
		}

		/// <summary>The packaging project at the plugin root, by convention next to plugin.json.</summary>
		public static string FindPackagingProject(string pluginFolder)
		{
			IEnumerable<string> candidates;
			try
			{
				candidates = Directory.EnumerateFiles(pluginFolder, "*.*proj", SearchOption.TopDirectoryOnly);
			}
			catch
			{
				return null;
			}

			foreach (var project in candidates.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
			{
				try
				{
					var text = File.ReadAllText(project);
					if (text.Contains($"\"{PackageTarget}\"", StringComparison.Ordinal))
						return project;
				}
				catch
				{
				}
			}

			return null;
		}

		/// <summary>Restores, then runs the packaging target; msbuild does not restore on its own.</summary>
		private static bool RunPackaging(string pluginFolder, string projectPath, out string log)
		{
			var output = new StringBuilder();
			var engine = EngineAssembliesPath();

			// Both properties point at this editor's own folder: it is self-contained, so the engine
			// assemblies, the editor assembly and ImGui all sit together there. A plugin built against
			// anything else could load into an editor whose Voltage.dll disagrees with it.
			var properties = $"-p:VoltageEnginePath=\"{engine}\" -p:VoltageEditorPath=\"{engine}\"";

			foreach (var project in Directory.EnumerateFiles(pluginFolder, "*.*proj", SearchOption.TopDirectoryOnly)
				         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
			{
				if (!RunDotnet(pluginFolder, $"restore \"{project}\" {properties}", output))
				{
					log = output.ToString();
					return false;
				}
			}

			var ok = RunDotnet(pluginFolder,
				$"msbuild \"{projectPath}\" -t:{PackageTarget} {properties} -nologo -verbosity:minimal", output);

			log = output.ToString();
			return ok;
		}

		/// <summary>Folder holding the engine/editor assemblies a plugin must bind to: this editor's own.</summary>
		public static string EngineAssembliesPath() =>
			AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

		private static bool RunDotnet(string workingDirectory, string arguments, StringBuilder output)
		{
			try
			{
				var info = new ProcessStartInfo
				{
					FileName = "dotnet",
					Arguments = arguments,
					WorkingDirectory = workingDirectory,
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true,
				};

				info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
				info.Environment["DOTNET_NOLOGO"] = "1";

				using var process = Process.Start(info);
				if (process == null)
				{
					output.AppendLine("Could not start dotnet. Is the .NET SDK installed and on PATH?");
					return false;
				}

				var stdout = process.StandardOutput.ReadToEndAsync();
				var stderr = process.StandardError.ReadToEndAsync();

				if (!process.WaitForExit(10 * 60 * 1000))
				{
					try { process.Kill(true); } catch { /* already gone */ }
					output.AppendLine($"dotnet {arguments.Split(' ')[0]} timed out after 10 minutes.");
					return false;
				}

				output.Append(stdout.Result);
				output.Append(stderr.Result);
				return process.ExitCode == 0;
			}
			catch (Exception ex)
			{
				output.AppendLine($"Could not run dotnet: {ex.Message}");
				return false;
			}
		}

		/// <summary>The end of a build log - where MSBuild puts the errors and the summary.</summary>
		private static string Tail(string log, int characters)
		{
			if (string.IsNullOrWhiteSpace(log))
				return "";

			var trimmed = log.Trim();
			return trimmed.Length <= characters ? trimmed : "..." + trimmed.Substring(trimmed.Length - characters);
		}
	}
}
