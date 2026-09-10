using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Voltage.Editor.ProjectFile;
using Voltage.Project;

namespace Voltage.Editor.Builders;

/// <summary>What one asset build did, updated while it runs; read it under its lock from the UI.</summary>
internal sealed class AssetBuildReport
{
	public readonly object Lock = new();
	public string Platform;
	public string OutputDir;
	public string MgcbPath;
	public string IndexPath;
	public string ToolVersion;
	public List<AssetBuildItem> Items = new();
	public List<string> Log = new();
	public List<string> Errors = new();
	public string Status = "starting";
	public bool Running = true;
	public bool Success;
	public TimeSpan Elapsed;

	public int Count(string outcome) { lock (Lock) return Items.Count(i => i.Outcome == outcome); }

	public object Describe()
	{
		lock (Lock)
		{
			return new
			{
				success = Success,
				running = Running,
				status = Status,
				platform = Platform,
				output = OutputDir,
				mgcb = MgcbPath,
				index = IndexPath,
				toolVersion = ToolVersion,
				elapsedSeconds = Elapsed.TotalSeconds,
				compiled = Items.Count(i => i.Outcome == "compiled"),
				copied = Items.Count(i => i.Outcome == "copied"),
				skipped = Items.Count(i => i.Outcome == "skipped"),
				failed = Items.Count(i => i.Outcome == "failed"),
				items = Items.Select(i => new { path = i.RelativePath, outcome = i.Outcome, asset = i.AssetName, importer = i.Importer, processor = i.Processor, reason = i.Reason, error = i.Error }).ToList(),
				errors = Errors.ToList()
			};
		}
	}
}

/// <summary>Plans, runs and copies one asset build; shared by the menu, the game build and the gateway.</summary>
internal static class AssetBuildService
{
	private static int _running;

	public static bool IsRunning => Volatile.Read(ref _running) == 1;

	public static AssetBuildReport LastReport { get; private set; }

	/// <summary>Compiles into <paramref name="outputDir"/>; a standalone run (<paramref name="copyRaw"/>) starts from an empty folder and also copies every raw file the contract keeps.</summary>
	public static Task<AssetBuildReport> RunAsync(IGameProject project, ProjectSettings.AssetBuildSettings settings, string outputDir, string platformOverride, bool clean, bool copyRaw, CancellationToken cancel)
	{
		if (project == null)
			throw new InvalidOperationException("no project loaded");
		if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
			throw new InvalidOperationException("an asset build is already running");

		var report = new AssetBuildReport { ToolVersion = MgcbRunner.ToolVersion };
		LastReport = report;
		return Task.Run(() =>
		{
			var watch = Stopwatch.StartNew();
			try
			{
				Execute(project, settings, outputDir, platformOverride, clean, copyRaw, report, cancel);
			}
			catch (OperationCanceledException)
			{
				Fail(report, "cancelled");
			}
			catch (Exception ex)
			{
				Fail(report, ex.Message);
			}
			finally
			{
				lock (report.Lock)
				{
					report.Elapsed = watch.Elapsed;
					report.Running = false;
				}
				Volatile.Write(ref _running, 0);
				Debug.Info($"[AssetBuild] {(report.Success ? "finished" : "failed")}: {report.Count("compiled")} compiled, {report.Count("copied")} copied, {report.Count("skipped")} skipped, {report.Count("failed")} failed in {report.Elapsed.TotalSeconds:0.0}s");
			}
			return report;
		}, cancel);
	}

	/// <summary>Refuses an output that would let clean or strip delete the project's own sources.</summary>
	private static void RequireSafeOutput(IGameProject project, string outputDir)
	{
		var output = Full(outputDir);
		foreach (var protectedDir in new[] { project.ProjectPath, project.ContentsFolder, project.DataFolder, project.ScriptsFolder, project.EffectsFolder, Path.Combine(project.ProjectPath, ".config") })
		{
			if (string.IsNullOrEmpty(protectedDir))
				continue;
			var guarded = Full(protectedDir);
			if (guarded.StartsWith(output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
				throw new InvalidOperationException($"asset build output {outputDir} would overwrite {protectedDir}");
		}
	}

	private static string Full(string path) =>
		Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

	private static void Execute(IGameProject project, ProjectSettings.AssetBuildSettings settings, string outputDir, string platformOverride, bool clean, bool copyRaw, AssetBuildReport report, CancellationToken cancel)
	{
		RequireSafeOutput(project, outputDir);
		var plan = AssetBuildPipeline.CreatePlan(project, settings, outputDir, platformOverride);
		lock (report.Lock)
		{
			report.Platform = plan.Platform;
			report.OutputDir = plan.OutputDir;
			report.MgcbPath = plan.MgcbPath;
			report.Items = plan.Items;
			foreach (var item in plan.Items)
				item.Outcome = item.Action == AssetBuildAction.Skip ? "skipped" : item.Action == AssetBuildAction.Fail ? "failed" : "pending";
		}
		if (!plan.PipelineAvailable)
			Warn(report, $"Voltage.Pipeline.dll not found at {plan.PipelineDll}; Aseprite and .fnt files are copied instead of compiled");

		// A standalone output is rebuilt from nothing every time, so renamed or deleted sources leave nothing behind; a game build's folder was emptied by the publish.
		if (copyRaw && Directory.Exists(plan.OutputDir))
		{
			Info(report, "emptying " + plan.OutputDir);
			Directory.Delete(plan.OutputDir, true);
		}
		if (clean && Directory.Exists(plan.IntermediateDir))
		{
			Info(report, "cleaning " + plan.IntermediateDir);
			Directory.Delete(plan.IntermediateDir, true);
		}
		Directory.CreateDirectory(plan.OutputDir);
		cancel.ThrowIfCancellationRequested();

		if (plan.Compiled.Any())
		{
			SetStatus(report, "building extensions");
			BuildExtensions(project, plan, report, cancel);

			SetStatus(report, "restoring dotnet-mgcb");
			if (!MgcbRunner.EnsureTool(project, line => Info(report, line), cancel))
				throw new InvalidOperationException("dotnet tool restore failed; is NuGet reachable?");

			AssetBuildPipeline.WriteMgcb(plan);
			Info(report, "wrote " + plan.MgcbPath);
			SetStatus(report, "running mgcb");
			var started = DateTime.UtcNow.AddSeconds(-2);
			var code = MgcbRunner.RunMgcb(project, plan.MgcbPath, line => OnMgcbLine(report, plan, line, false), line => OnMgcbLine(report, plan, line, true), cancel);
			Info(report, $"mgcb exited with code {code}");
			foreach (var item in plan.Compiled)
			{
				// An incremental run leaves untouched outputs alone, so an .xnb older than the run is fine only when mgcb reported nothing for it.
				var xnb = item.OutputXnb(plan.OutputDir);
				var built = File.Exists(xnb) && (File.GetLastWriteTimeUtc(xnb) >= started || code == 0 && !clean);
				lock (report.Lock)
				{
					if (item.Outcome == "failed")
						continue;
					item.Outcome = built ? "compiled" : "failed";
					if (!built && item.Error == null)
						item.Error = code == 0 ? "no output produced" : $"mgcb exited with code {code}";
				}
			}
		}
		else
			Info(report, "nothing to compile; every file is copied or skipped");

		if (copyRaw)
			PruneStaleOutputs(plan, report);
		SetStatus(report, "writing index");
		var index = AssetBuildPipeline.WriteIndex(plan);
		lock (report.Lock) report.IndexPath = index;

		if (copyRaw)
		{
			SetStatus(report, "copying raw files");
			CopyRaw(project, plan, report);
		}
		else
		{
			lock (report.Lock)
				foreach (var item in plan.Items.Where(i => i.Action == AssetBuildAction.Copy))
					item.Outcome = "copied";
		}

		lock (report.Lock)
		{
			report.Success = report.Items.All(i => i.Outcome != "failed");
			report.Status = report.Success ? "done" : "failed";
		}
	}

	/// <summary>Copies the files the contract keeps raw; a compiled source never ships beside its .xnb.</summary>
	public static void CopyRaw(IGameProject project, AssetBuildPlan plan, AssetBuildReport report)
	{
		var contentRoot = project.ContentsFolder;
		var stripped = 0;
		foreach (var item in plan.Items)
		{
			if (item.Action == AssetBuildAction.Skip || item.Action == AssetBuildAction.Fail)
				continue;
			var dest = Path.Combine(plan.OutputDir, Path.GetRelativePath(contentRoot, item.SourcePath));
			if (item.Action == AssetBuildAction.Compile && item.Outcome == "compiled")
			{
				// dotnet publish copies Content/** on its own, so a compiled source must be removed, not just not copied.
				if (File.Exists(dest))
				{
					File.Delete(dest);
					stripped++;
				}
				continue;
			}
			Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
			File.Copy(item.SourcePath, dest, true);
			if (item.Action == AssetBuildAction.Copy)
				lock (report.Lock) item.Outcome = "copied";
		}
		if (stripped > 0)
			Info(report, $"stripped {stripped} raw sources");
	}

	/// <summary>Deletes .xnb files the plan did not produce, so a renamed or removed source cannot linger as a compiled orphan.</summary>
	/// <summary>Standalone outputs only: a game build's Content folder also holds engine and plugin content this plan never produced.</summary>
	private static void PruneStaleOutputs(AssetBuildPlan plan, AssetBuildReport report)
	{
		if (!Directory.Exists(plan.OutputDir))
			return;
		var expected = new HashSet<string>(plan.Compiled.Select(i => Path.GetFullPath(i.OutputXnb(plan.OutputDir))), StringComparer.OrdinalIgnoreCase);
		var engineContent = Path.Combine(Path.GetFullPath(plan.OutputDir), "Voltage") + Path.DirectorySeparatorChar;
		var pruned = 0;
		foreach (var xnb in Directory.EnumerateFiles(plan.OutputDir, "*.xnb", SearchOption.AllDirectories).ToList())
		{
			var full = Path.GetFullPath(xnb);
			if (expected.Contains(full) || full.StartsWith(engineContent, StringComparison.OrdinalIgnoreCase))
				continue;
			File.Delete(xnb);
			pruned++;
		}
		if (pruned > 0)
			Info(report, $"pruned {pruned} stale .xnb files");
	}

	/// <summary>Rebuilds every rule assembly that lives in a one-csproj folder when its sources are newer than the DLL.</summary>
	private static void BuildExtensions(IGameProject project, AssetBuildPlan plan, AssetBuildReport report, CancellationToken cancel)
	{
		foreach (var reference in plan.References)
		{
			var csproj = AssetBuildRules.FindExtensionProject(project, reference);
			if (csproj == null || !AssetBuildRules.IsStale(reference, csproj))
				continue;

			var name = Path.GetFileNameWithoutExtension(csproj);
			Info(report, $"building extension {name}");
			// The scaffolded csproj references Voltage.Pipeline through this property instead of an absolute editor path.
			var pipelineDir = Path.GetDirectoryName(AssetBuildPipeline.PipelineDllPath) + Path.DirectorySeparatorChar;
			var code = MgcbRunner.RunDotnet(Path.GetDirectoryName(csproj), new[] { "build", csproj, "-c", "Release", "--nologo", $"-p:VoltagePipelineDir={pipelineDir}" }, line => Info(report, line), line => Info(report, line), cancel);
			if (code != 0 || !File.Exists(reference))
				throw new InvalidOperationException($"extension {name} failed to build (exit code {code}); see the report log");
		}
	}

	public static void Clean(IGameProject project, string platform)
	{
		var output = AssetBuildSettingsStore.DefaultOutputDirectory(project, platform);
		var intermediate = AssetBuildSettingsStore.IntermediateDirectory(project, platform);
		if (Directory.Exists(output)) Directory.Delete(output, true);
		if (Directory.Exists(intermediate)) Directory.Delete(intermediate, true);
		Debug.Info($"[AssetBuild] cleaned {output}");
	}

	private static readonly Regex ErrorLine = new(@"^(?<path>.+?)(?:\(\d+,\d+\))?:\s*error\s*:?\s*(?<message>.*)$", RegexOptions.IgnoreCase);

	private static void OnMgcbLine(AssetBuildReport report, AssetBuildPlan plan, string line, bool isError)
	{
		if (string.IsNullOrWhiteSpace(line))
			return;
		var match = ErrorLine.Match(line.Trim());
		if (match.Success || isError && line.Contains("error", StringComparison.OrdinalIgnoreCase))
		{
			var path = match.Success ? match.Groups["path"].Value.Trim() : null;
			var item = path == null ? null : plan.Compiled.FirstOrDefault(i => string.Equals(Path.GetFullPath(i.SourcePath), SafeFullPath(path), StringComparison.OrdinalIgnoreCase) || line.Contains(i.SourcePath, StringComparison.OrdinalIgnoreCase));
			lock (report.Lock)
			{
				report.Errors.Add(line);
				report.Log.Add(line);
				if (item != null)
				{
					item.Outcome = "failed";
					item.Error = match.Success ? match.Groups["message"].Value : line;
				}
			}
			Debug.Error("[AssetBuild] " + line);
			return;
		}

		lock (report.Lock) report.Log.Add(line);
		Debug.Info("[AssetBuild] " + line);
	}

	private static string SafeFullPath(string path)
	{
		try { return Path.GetFullPath(path); } catch (Exception) { return path; }
	}

	private static void SetStatus(AssetBuildReport report, string status)
	{
		lock (report.Lock) report.Status = status;
	}

	private static void Info(AssetBuildReport report, string line)
	{
		lock (report.Lock) report.Log.Add(line);
		Debug.Info("[AssetBuild] " + line);
	}

	private static void Warn(AssetBuildReport report, string line)
	{
		lock (report.Lock) report.Log.Add(line);
		Debug.Warn("[AssetBuild] " + line);
	}

	private static void Fail(AssetBuildReport report, string message)
	{
		lock (report.Lock)
		{
			report.Success = false;
			report.Status = "failed";
			report.Errors.Add(message);
			report.Log.Add(message);
			foreach (var item in report.Items.Where(i => i.Outcome == "pending"))
			{
				item.Outcome = "failed";
				item.Error = message;
			}
		}
		Debug.Error("[AssetBuild] " + message);
	}
}
