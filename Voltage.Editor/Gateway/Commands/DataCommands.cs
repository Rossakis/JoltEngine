using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Voltage.Data;
using Voltage.Editor.Assets;
using static Voltage.Editor.Gateway.GatewayValues;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Data assets (.vasset): the typed JSON files that hold game data.</summary>
internal static class DataCommands
{
	public static void Register(GatewayCommandTable table)
	{
		table.Add("data.types", "Registered data asset types.", (_, _) =>
			DataAssetRegistry.All.Select(e => new { id = e.Id, type = e.Type?.FullName }).OrderBy(e => e.id).ToList());

		table.Add("data.get", "Read a data asset: its members and the JSON on disk. params: asset (path, GUID or file name)", (args, _) =>
		{
			var (asset, path) = Load(args.Require("asset"));
			return Detail(asset, path);
		});

		table.Add("data.set", "Set one member of a data asset and save it to disk. params: asset, member, value", (args, _) =>
		{
			var (asset, path) = Load(args.Require("asset"));
			var memberName = args.Require("member");
			var value = args.RequireProperty("value");

			var result = Set(asset, memberName, value, undo: false, null);
			DataAssetIO.Save(asset, path);
			return new { member = memberName, value = result, path };
		});

		table.Add("data.create", "Create a data asset file of a registered type. params: type (id from data.types), path (absolute or project-relative, .vasset)", (args, _) =>
		{
			var id = args.Require("type");
			if (!DataAssetRegistry.IsRegistered(id))
				throw new GatewayException($"unknown data asset type '{id}'; see data.types");

			var path = args.Require("path");
			if (!Path.IsPathRooted(path))
			{
				var project = ProjectFile.ProjectManager.Instance.CurrentProject ?? throw new GatewayException("no project loaded");
				path = Path.Combine(project.ProjectPath, path);
			}
			path = Path.GetFullPath(path);
			if (!path.EndsWith(".vasset", StringComparison.OrdinalIgnoreCase))
				path += ".vasset";
			if (File.Exists(path))
				throw new GatewayException($"{path} already exists");

			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			var asset = DataAssetIO.CreateDefault(id);
			DataAssetIO.Save(asset, path);
			AssetDatabase.Instance?.Refresh();
			return Detail(DataAssetCache.GetByPath(path) ?? asset, path);
		});
	}

	private static (DataAsset asset, string path) Load(string key)
	{
		var item = ResolveAsset(key);
		if (!item.Extension.Equals(".vasset", StringComparison.OrdinalIgnoreCase))
			throw new GatewayException($"{item.FileName} is not a .vasset data asset");
		var asset = DataAssetCache.GetByPath(item.AbsolutePath) ?? throw new GatewayException($"could not load {item.FileName}; see log.tail");
		return (asset, item.AbsolutePath);
	}

	private static object Detail(DataAsset asset, string path)
	{
		JsonElement json;
		using (var doc = JsonDocument.Parse(DataAssetIO.ToJson(asset)))
			json = doc.RootElement.Clone();

		return new
		{
			path,
			type = asset.GetType().FullName,
			typeId = DataAssetRegistry.TryGetId(asset.GetType()),
			values = Snapshot(asset),
			json
		};
	}
}
