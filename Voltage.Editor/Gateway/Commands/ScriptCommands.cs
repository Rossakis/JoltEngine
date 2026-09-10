using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Scripting;
using Voltage.Gateway;
using static Voltage.Editor.Gateway.GatewayValues;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Script files in the project's Scripts folder: list, read, create from a template, write and delete, compiling afterwards.</summary>
internal static class ScriptCommands
{
	private static readonly Regex ClassPattern = new(@"\b(?:class|struct|record)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);

	private static readonly GatewayParam PathParam = P.Str("path", "File path relative to the Scripts folder", required: true);

	private static readonly GatewayParam CompileParam = P.Bool("compile", "Compile the scripts afterwards and include the result", true);

	public static void Register(GatewayCommandTable table)
	{
		table.Add("script.templates", "Templates script.create accepts.", (_, _) =>
			ScriptTemplates.Kinds.Select(k => new { id = k.Id, label = k.Label, defaultName = k.DefaultName }).ToList()).ReadOnly();

		table.Add("script.list", "Script files with the types they declare and whether the compiled assembly has them.", (_, ctx) =>
		{
			var folder = ScriptsFolder();
			var compiled = new HashSet<string>(ctx.ImGui().ScriptManager?.GetScriptTypes().Select(t => t.Name) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
			if (!Directory.Exists(folder))
				return new List<object>();
			return Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
				.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
				.Select(f =>
				{
					var info = new FileInfo(f);
					var classes = ClassPattern.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value).Distinct().ToList();
					return new
					{
						path = Path.GetRelativePath(folder, f).Replace('\\', '/'),
						size = info.Length,
						modified = info.LastWriteTimeUtc,
						types = classes,
						compiled = classes.Any(compiled.Contains)
					};
				})
				.ToList<object>();
		}).ReadOnly();

		table.Add("script.read", "The text of a script file.", (args, _) =>
		{
			var path = Resolve(args.Require("path"), mustExist: true);
			return new { path = Relative(path), content = File.ReadAllText(path) };
		}, PathParam).ReadOnly();

		table.Add("script.create", "Write a new script from a template into the Scripts folder and compile it.", (args, ctx) =>
		{
			var name = args.Require("name");
			if (!ScriptTemplates.IsIdentifier(name))
				throw new GatewayException($"'{name}' is not a valid C# class name");
			var kind = args.String("template", "component");
			if (!ScriptTemplates.IsKind(kind))
				throw new GatewayException($"unknown template '{kind}'; see script.templates");

			var folder = ScriptsFolder();
			var target = args.Has("folder") ? GatewayPaths.RequireInside(Path.Combine(folder, args.Require("folder")), folder, "folder") : folder;
			Directory.CreateDirectory(target);
			var path = Path.Combine(target, name + ".cs");
			if (File.Exists(path))
				throw new GatewayException($"script already exists: {Relative(path)}");

			var segments = Path.GetRelativePath(folder, target).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Where(s => s != ".").ToList();
			var ns = ScriptTemplates.Namespace(RequireProject().ProjectName, segments);
			File.WriteAllText(path, ScriptTemplates.Render(kind, name, ns), new System.Text.UTF8Encoding(false));
			return Finish(ctx, args, new { path = Relative(path), template = kind, @namespace = ns, created = true });
		}, P.Str("name", "Class and file name", required: true), P.Enum("template", "Template id", ScriptTemplates.Kinds.Select(k => k.Id).ToArray(), "component"), P.Str("folder", "Subfolder under Scripts; created when missing"), CompileParam).Destructive();

		table.Add("script.write", "Replace or create a script file with the given text and compile it.", (args, ctx) =>
		{
			var path = Resolve(args.Require("path"), mustExist: false);
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			var existed = File.Exists(path);
			File.WriteAllText(path, args.RequireProperty("content").GetString() ?? "", new System.Text.UTF8Encoding(false));
			return Finish(ctx, args, new { path = Relative(path), created = !existed });
		}, PathParam, P.Str("content", "Full file text", required: true), CompileParam).Destructive();

		table.Add("script.delete", "Delete a script file and compile without it.", (args, ctx) =>
		{
			var path = Resolve(args.Require("path"), mustExist: true);
			File.Delete(path);
			return Finish(ctx, args, new { path = Relative(path), deleted = true });
		}, PathParam, CompileParam).Destructive();
	}

	private static string ScriptsFolder()
	{
		RequireProject();
		var folder = ProjectManager.Instance.GetScriptsFolder();
		if (string.IsNullOrEmpty(folder))
			throw new GatewayException("the project has no Scripts folder");
		return folder;
	}

	private static string Resolve(string relative, bool mustExist)
	{
		var folder = ScriptsFolder();
		var path = GatewayPaths.RequireInside(Path.Combine(folder, relative), folder, "path");
		if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			throw new GatewayException("script paths end with .cs");
		if (mustExist && !File.Exists(path))
			throw new GatewayException($"script not found: {relative}");
		return path;
	}

	private static string Relative(string path) => Path.GetRelativePath(ScriptsFolder(), path).Replace('\\', '/');

	/// <summary>Answers straight away, or after the compile the caller asked for, folding its result in.</summary>
	private static object Finish(GatewayContext ctx, GatewayArgs args, object result)
	{
		if (!args.Bool("compile", true))
			return result;
		var compile = PlayScriptCommands.CompileAsync(ctx, false);
		return GatewayTasks.FromTask(compile, r => new { file = result, compile = r }, 150f);
	}
}
