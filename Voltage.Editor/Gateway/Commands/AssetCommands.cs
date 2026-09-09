using System;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Voltage.Editor.Assets;
using Voltage.Editor.ProjectFile;
using Voltage.Gateway;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Importing files into the project, as dropping them onto the Asset Browser does.</summary>
internal static class AssetCommands
{
	public static void Register(GatewayCommandTable table)
	{
		table.Add("asset.import", "Copy a file into the project's content folder, create its .meta GUID and re-index.", (args, _) =>
		{
			var (path, guid) = Import(args);
			var item = AssetDatabase.Instance.Items.FirstOrDefault(i => string.Equals(i.AbsolutePath, path, StringComparison.OrdinalIgnoreCase));
			return new { path, guid, kind = item?.Descriptor.Kind.ToString(), droppable = item?.Descriptor.DropFactory != null };
		}, P.Str("source", "Absolute path of the file to import", required: true), P.Str("destination", "Folder relative to the Content folder; default its root"), P.Str("name", "File name to save as; default the source name"), P.Bool("overwrite", "Replace an existing file instead of failing", false));

		table.Add("asset.importAseprite", "Import an .aseprite/.ase file and, with entity=true, drop it into the scene as an animated sprite (undoable).", (args, ctx) =>
		{
			var source = args.Require("source");
			var extension = Path.GetExtension(source);
			if (!extension.Equals(".aseprite", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".ase", StringComparison.OrdinalIgnoreCase))
				throw new GatewayException("source must be an .aseprite or .ase file");

			var (path, guid) = Import(args);
			object created = null;
			if (args.Bool("entity"))
			{
				if (Core.Scene == null)
					throw new GatewayException("imported, but no scene is loaded to drop into");
				Vector2? position = args.Has("x") || args.Has("y") ? new Vector2(args.Float("x"), args.Float("y")) : null;
				var entity = DropHandlers.DropAsepriteAnimated(AssetDatabase.Instance.GetReference(path), position);
				created = entity?.GetComponent<Voltage.Sprites.SpriteAnimator>() != null ? EntityCommands.Detail(entity) : null;
			}
			return new { path, guid, entity = created };
		}, P.Str("source", "Absolute path of the Aseprite file", required: true), P.Str("destination", "Folder relative to the Content folder; default its root"), P.Str("name", "File name to save as"), P.Bool("overwrite", "Replace an existing file", false), P.Bool("entity", "Also place an animated sprite entity", false), P.Float("x", "World position for the entity"), P.Float("y"));

		table.Add("asset.meta", "GUID and kind of a project asset, creating the .meta sidecar if it is missing.", (args, _) =>
		{
			var db = AssetDatabase.Instance ?? throw new GatewayException("no project loaded; the asset database is empty");
			var key = args.Require("path");
			string path;
			try
			{
				path = GatewayValues.ResolveAsset(key).AbsolutePath;
			}
			catch (GatewayException)
			{
				var project = ProjectManager.Instance.CurrentProject ?? throw new GatewayException("no project loaded");
				path = GatewayPaths.RequireInside(Path.IsPathRooted(key) ? key : Path.Combine(project.ProjectPath, key), project.ProjectPath, "path");
				if (!File.Exists(path))
					throw new GatewayException($"file not found: {path}");
			}

			var guid = db.GetOrCreateGuid(path);
			var item = db.Items.FirstOrDefault(i => string.Equals(i.AbsolutePath, path, StringComparison.OrdinalIgnoreCase));
			return new
			{
				path,
				guid = guid == Guid.Empty ? (Guid?)null : guid,
				meta = File.Exists(path + ".meta") ? path + ".meta" : null,
				kind = item?.Descriptor.Kind.ToString(),
				dataAssetType = guid == Guid.Empty ? null : db.GetDataAssetTypeId(guid)
			};
		}, P.Str("path", "Asset GUID, project-relative or absolute path", required: true)).ReadOnly();
	}

	private static (string path, Guid guid) Import(GatewayArgs args)
	{
		var project = ProjectManager.Instance.CurrentProject ?? throw new GatewayException("no project loaded");
		var db = AssetDatabase.Instance ?? throw new GatewayException("the asset database is not ready");
		var source = Path.GetFullPath(args.Require("source"));
		if (!File.Exists(source))
			throw new GatewayException($"source file not found: {source}");

		var folder = args.Has("destination")
			? GatewayPaths.RequireInside(Path.Combine(project.ContentsFolder, args.Require("destination")), project.ContentsFolder, "destination")
			: project.ContentsFolder;
		Directory.CreateDirectory(folder);

		var name = args.String("name", Path.GetFileName(source));
		if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(".."))
			throw new GatewayException($"invalid file name '{name}'");
		var target = GatewayPaths.RequireInside(Path.Combine(folder, name), project.ContentsFolder, "destination");
		if (File.Exists(target) && !args.Bool("overwrite"))
			throw new GatewayException($"file already exists: {target}; pass overwrite=true");

		File.Copy(source, target, overwrite: true);
		var guid = db.GetOrCreateGuid(target);
		db.Refresh();
		return (target, guid);
	}
}
