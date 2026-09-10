using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Utils;

namespace Voltage.Editor.Builders;

/// <summary>Runs the dotnet-mgcb tool from a manifest inside the project, restoring it at the engine's MonoGame version.</summary>
public static class MgcbRunner
{
	public const string ToolId = "dotnet-mgcb";

	public static string ToolVersion => MonoGameVersionResolver.GetVersion();

	public static string ManifestPath(IGameProject project) => Path.Combine(project.ProjectPath, ".config", "dotnet-tools.json");

	/// <summary>True when the manifest names the tool at the current version and a restore has run since.</summary>
	public static bool IsRestored(IGameProject project)
	{
		var manifest = ManifestPath(project);
		return File.Exists(manifest) && File.ReadAllText(manifest).Contains($"\"{ToolVersion}\"", StringComparison.Ordinal) && File.Exists(RestoreStamp(project));
	}

	private static string RestoreStamp(IGameProject project) => Path.Combine(project.ProjectPath, "obj", "AssetBuild", "tool-" + ToolVersion + ".restored");

	/// <summary>Adds or updates only the mgcb entry of the manifest, keeping other tools, then restores; the first restore downloads the tool.</summary>
	public static bool EnsureTool(IGameProject project, Action<string> log, CancellationToken cancel)
	{
		var manifest = ManifestPath(project);
		var changed = UpdateManifest(manifest);
		if (changed)
			log($"wrote {manifest} for {ToolId} {ToolVersion}");

		if (!changed && File.Exists(RestoreStamp(project)))
			return true;

		log($"dotnet tool restore ({ToolId} {ToolVersion}); the first run downloads the tool");
		var code = Run(project.ProjectPath, new[] { "tool", "restore" }, log, log, cancel);
		if (code != 0)
			return false;

		Directory.CreateDirectory(Path.GetDirectoryName(RestoreStamp(project)) ?? ".");
		File.WriteAllText(RestoreStamp(project), DateTime.UtcNow.ToString("O"));
		return true;
	}

	/// <summary>True when the file was written; an existing manifest keeps its other tools and isRoot flag.</summary>
	private static bool UpdateManifest(string manifest)
	{
		JsonObject root = null;
		if (File.Exists(manifest))
		{
			try
			{
				root = JsonNode.Parse(File.ReadAllText(manifest)) as JsonObject;
			}
			catch (JsonException)
			{
			}
		}
		root ??= new JsonObject { ["version"] = 1, ["isRoot"] = true };
		if (root["tools"] is not JsonObject tools)
			root["tools"] = tools = new JsonObject();

		if (tools[ToolId] is JsonObject existing && existing["version"]?.GetValue<string>() == ToolVersion)
			return false;

		tools[ToolId] = new JsonObject { ["version"] = ToolVersion, ["commands"] = new JsonArray("mgcb") };
		Directory.CreateDirectory(Path.GetDirectoryName(manifest) ?? ".");
		File.WriteAllText(manifest, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
		return true;
	}

	/// <summary>Runs MGCB on a response file from the project directory, streaming both output pipes.</summary>
	public static int RunMgcb(IGameProject project, string mgcbPath, Action<string> stdout, Action<string> stderr, CancellationToken cancel) =>
		Run(project.ProjectPath, new[] { "mgcb", "/@:" + mgcbPath }, stdout, stderr, cancel);

	/// <summary>Runs any dotnet command with fixed arguments, for extension project builds.</summary>
	public static int RunDotnet(string workingDirectory, string[] arguments, Action<string> stdout, Action<string> stderr, CancellationToken cancel) =>
		Run(workingDirectory, arguments, stdout, stderr, cancel);

	private static int Run(string workingDirectory, string[] arguments, Action<string> stdout, Action<string> stderr, CancellationToken cancel)
	{
		var startInfo = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			WorkingDirectory = workingDirectory
		};
		foreach (var arg in arguments)
			startInfo.ArgumentList.Add(arg);

		Process process;
		try
		{
			process = Process.Start(startInfo) ?? throw new InvalidOperationException("could not start dotnet");
		}
		catch (Win32Exception ex)
		{
			throw new InvalidOperationException($"the .NET SDK ('dotnet') was not found on the path: {ex.Message}");
		}

		using (process)
		{
			process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout(e.Data); };
			process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr(e.Data); };
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();

			while (!process.WaitForExit(200))
			{
				if (!cancel.IsCancellationRequested)
					continue;
				try { process.Kill(true); } catch (Exception) { }
				cancel.ThrowIfCancellationRequested();
			}
			process.WaitForExit();
			return process.ExitCode;
		}
	}
}
