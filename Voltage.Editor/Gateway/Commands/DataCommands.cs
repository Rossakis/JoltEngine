using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Voltage.Data;
using Voltage.Editor.Assets;
using static Voltage.Editor.Gateway.GatewayValues;
using Voltage.Gateway;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Data assets (.vasset): the typed JSON files that hold game data.</summary>
internal static class DataCommands
{
	public static void Register(GatewayCommandTable table)
	{
		table.Add("data.types", "Registered data asset types.", (_, _) =>
			DataAssetRegistry.All.Select(e => new { id = e.Id, type = e.Type?.FullName }).OrderBy(e => e.id).ToList()).ReadOnly();

		table.Add("data.get", "Read a data asset: its members and the JSON on disk.", (args, _) =>
		{
			var (asset, path) = Load(args.Require("asset"));
			return Detail(asset, path);
		}, AssetParam).ReadOnly();

		table.Add("data.set", "Set one member of a data asset and save it to disk.", (args, _) =>
		{
			var (asset, path) = Load(args.Require("asset"));
			var memberName = args.Require("member");
			var value = args.RequireProperty("value");

			var result = Set(asset, memberName, value, undo: false, null, args.Bool("remove"));
			DataAssetIO.Save(asset, path);
			return new { member = memberName, value = result, path };
		}, AssetParam, P.Str("member", "Field or property name; dotted paths reach nested members, Items[2] a list element, Items[+] appends", required: true), P.Any("value", "Value in the shared language: number, string, boolean, enum name, {x,y} vector, {r,g,b,a} or #RRGGBB colour, asset path or GUID for asset and prefab references, entity key for entity references, Entity/Type for component references, or a JSON array for a whole list", required: true), P.Bool("remove", "Remove the indexed list element instead of setting it", false));

		table.Add("data.create", "Create a data asset file of a registered type.", (args, ctx) =>
		{
			var id = args.Require("type");
			if (!DataAssetRegistry.IsRegistered(id))
				throw new GatewayException($"unknown data asset type '{id}'; see data.types");

			var path = args.Require("path");
			var project = ProjectFile.ProjectManager.Instance.CurrentProject ?? throw new GatewayException("no project loaded");
			if (!Path.IsPathRooted(path))
				path = Path.Combine(project.ProjectPath, path);
			path = ctx.Dispatcher.Options.Safe ? GatewayPaths.RequireInside(path, project.ProjectPath) : Path.GetFullPath(path);
			if (!path.EndsWith(".vasset", StringComparison.OrdinalIgnoreCase))
				path += ".vasset";
			if (File.Exists(path))
				throw new GatewayException($"{path} already exists");

			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			var asset = DataAssetIO.CreateDefault(id);
			DataAssetIO.Save(asset, path);
			AssetDatabase.Instance?.Refresh();
			return Detail(DataAssetCache.GetByPath(path) ?? asset, path);
		}, P.Str("type", "Type id from data.types", required: true), P.Str("path", "Absolute or project-relative .vasset path", required: true));
	}

	private static readonly GatewayParam AssetParam = P.Str("asset", "Data asset path, GUID or file name", required: true);

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
