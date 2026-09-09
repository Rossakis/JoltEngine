using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Voltage.Editor.Assets;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Tools.Tilemap;
using Voltage.Editor.Undo.ComponentActions;
using Voltage.Editor.Undo.Core;
using Voltage.Gateway;
using Voltage.Tilesets;
using static Voltage.Editor.Gateway.GatewayValues;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Tilemap painting and tileset queries, recorded in undo the way the tile palette's strokes are.</summary>
internal static class TilemapCommands
{
	private const int MaxFillCells = 20000;

	public static void Register(GatewayCommandTable table)
	{
		table.Add("tilemap.list", "Tilemap layers in the open scene.", (_, _) =>
			TilemapSceneUtils.FindTilemaps().Select(Info).ToList()).ReadOnly();

		table.Add("tilemap.info", "One tilemap layer: tileset, cell size, painted extents and collision counts.", (args, ctx) =>
			Info(Resolve(args, ctx)), P.Str("entity", "Entity id, GUID or name; default the paint tool's target or the only layer")).ReadOnly();

		table.Add("tilemap.create", "Add a tilemap layer bound to a tileset asset, as dropping the tileset into the scene does (undoable).", (args, _) =>
		{
			if (Core.Scene == null)
				throw new GatewayException("no scene loaded");
			var item = ResolveAsset(args.Require("tileset"));
			if (!item.Extension.Equals(TilesetAssetIO.FileExtension, StringComparison.OrdinalIgnoreCase))
				throw new GatewayException($"{item.FileName} is not a {TilesetAssetIO.FileExtension} asset");
			var map = TilemapSceneUtils.CreateTilemapLayer(TilemapSceneUtils.ReferenceFor(item.AbsolutePath), null, args.String("name"))
				?? throw new GatewayException("layer creation failed; see log.tail");
			return Info(map);
		}, P.Str("tileset", "Tileset GUID, path or file name", required: true), P.Str("name", "Entity name; default '<tileset> Layer'"));

		table.Add("tilemap.get", "Tiles in one cell or a rectangle: base tile, stack, orientation and collision.", (args, ctx) =>
		{
			var map = Resolve(args, ctx);
			var x = args.Int("x");
			var y = args.Int("y");
			var w = Math.Max(1, args.Int("width", 1));
			var h = Math.Max(1, args.Int("height", 1));
			if (w * h > MaxFillCells)
				throw new GatewayException($"rectangle too large; at most {MaxFillCells} cells");

			var cells = new List<object>();
			for (var cy = y; cy < y + h; cy++)
				for (var cx = x; cx < x + w; cx++)
				{
					var stack = map.GetCellStack(cx, cy);
					var solid = map.GetCollision(cx, cy);
					if (stack.Length == 0 && !solid && !args.Bool("includeEmpty"))
						continue;
					cells.Add(Cell(map, cx, cy, stack, solid));
				}
			return w == 1 && h == 1 ? (cells.Count == 1 ? cells[0] : Cell(map, x, y, Array.Empty<int>(), false)) : cells;
		}, P.Str("entity", "Tilemap entity"), P.Int("x", "Cell column", required: true), P.Int("y", "Cell row", required: true), P.Int("width", "Rectangle width in cells", 1), P.Int("height", "Rectangle height in cells", 1), P.Bool("includeEmpty", "List empty cells of a rectangle too", false)).ReadOnly();

		table.Add("tilemap.paint", "Set tiles in cells (undoable). A cell is replaced unless stack=true pushes on top of it; tile -1 erases.", (args, ctx) =>
		{
			var map = Resolve(args, ctx);
			var stack = args.Bool("stack");
			var changes = new List<TilePaintUndoAction.CellChange>();
			foreach (var (x, y, tile) in Cells(args, map))
				Apply(map, x, y, tile, stack, changes);
			return Commit(map, changes, "Paint");
		}, P.Str("entity", "Tilemap entity"), P.Int("x", "Cell column, with y and tile for a single cell"), P.Int("y"), P.Int("tile", "Tile index in the tileset; -1 erases"), P.List("cells", "[{x, y, tile}] for many cells", "object"), P.Bool("stack", "Push onto existing tiles instead of replacing", false));

		table.Add("tilemap.erase", "Clear cells, or a rectangle with width and height (undoable).", (args, ctx) =>
		{
			var map = Resolve(args, ctx);
			var changes = new List<TilePaintUndoAction.CellChange>();
			if (args.Has("cells"))
				foreach (var (x, y, _) in Cells(args, map, requireTile: false))
					Apply(map, x, y, -1, false, changes);
			else
				foreach (var (x, y) in Rect(args))
					Apply(map, x, y, -1, false, changes);
			return Commit(map, changes, "Erase");
		}, P.Str("entity", "Tilemap entity"), P.Int("x"), P.Int("y"), P.Int("width", "Rectangle width in cells", 1), P.Int("height", "Rectangle height in cells", 1), P.List("cells", "[{x, y}] for many cells", "object")).Destructive();

		table.Add("tilemap.fill", "Fill a rectangle with a tile, or with flood=true the contiguous run of the tile under x,y (undoable).", (args, ctx) =>
		{
			var map = Resolve(args, ctx);
			var tile = args.Int("tile", int.MinValue);
			if (tile == int.MinValue)
				throw new GatewayException("missing parameter 'tile'");
			CheckTile(map, tile);
			var changes = new List<TilePaintUndoAction.CellChange>();

			if (args.Bool("flood"))
			{
				var origin = new Point(args.Int("x"), args.Int("y"));
				var target = map.GetTile(origin.X, origin.Y);
				if (target == tile)
					return Commit(map, changes, "Fill");
				foreach (var cell in Flood(map, origin, c => map.GetTile(c.X, c.Y) == target))
					Apply(map, cell.X, cell.Y, tile, false, changes);
			}
			else
				foreach (var (x, y) in Rect(args))
					Apply(map, x, y, tile, false, changes);
			return Commit(map, changes, "Fill");
		}, P.Str("entity", "Tilemap entity"), P.Int("x", "Rectangle origin, or the flood seed", required: true), P.Int("y", required: true), P.Int("width", "Rectangle width in cells", 1), P.Int("height", "Rectangle height in cells", 1), P.Int("tile", "Tile index; -1 erases", required: true), P.Bool("flood", "Replace the contiguous run of the seed cell's tile", false));

		table.Add("tilemap.collision", "Set or clear the per-cell collision mask for cells or a rectangle, with flood=true for the contiguous run (undoable); colliders are rebuilt.", (args, ctx) =>
		{
			var map = Resolve(args, ctx);
			var solid = args.Bool("solid", true);
			var changes = new List<TileCollisionUndoAction.CellChange>();
			IEnumerable<(int x, int y)> cells;
			if (args.Has("cells"))
				cells = Cells(args, map, requireTile: false).Select(c => (c.x, c.y));
			else if (args.Bool("flood"))
			{
				var origin = new Point(args.Int("x"), args.Int("y"));
				var target = map.GetCollision(origin.X, origin.Y);
				cells = target == solid ? Array.Empty<(int, int)>() : Flood(map, origin, c => map.GetCollision(c.X, c.Y) == target).Select(c => (c.X, c.Y));
			}
			else
				cells = Rect(args);

			foreach (var (x, y) in cells)
			{
				var old = map.GetCollision(x, y);
				if (old == solid)
					continue;
				map.SetCollision(x, y, solid);
				changes.Add(new TileCollisionUndoAction.CellChange(x, y, old, solid));
			}

			if (changes.Count > 0)
			{
				var description = $"{(solid ? "Paint" : "Clear")} {changes.Count} collision cell{(changes.Count == 1 ? "" : "s")}";
				EditorChangeTracker.PushUndo(new TileCollisionUndoAction(map, changes, description), map.Entity, description);
				map.RebuildColliders();
			}
			return new { changed = changes.Count, collisionCells = map.CollisionCellCount };
		}, P.Str("entity", "Tilemap entity"), P.Bool("solid", "true marks solid, false clears", true), P.Int("x"), P.Int("y"), P.Int("width", "Rectangle width in cells", 1), P.Int("height", "Rectangle height in cells", 1), P.List("cells", "[{x, y}] for many cells", "object"), P.Bool("flood", "Apply to the contiguous run of the seed cell", false));

		table.Add("tilemap.generateCollision", "Derive the collision mask from the painted tiles, optionally only from tiles flagged solid in the tileset.", (args, ctx) =>
		{
			var map = Resolve(args, ctx);
			var before = map.EnumerateCollision().ToHashSet();
			var count = map.GenerateCollisionFromTiles(args.Bool("solidOnly", true));
			var changes = new List<TileCollisionUndoAction.CellChange>();
			foreach (var (x, y) in map.EnumerateCollision())
				if (!before.Contains((x, y)))
					changes.Add(new TileCollisionUndoAction.CellChange(x, y, false, true));
			foreach (var (x, y) in before)
				if (!map.GetCollision(x, y))
					changes.Add(new TileCollisionUndoAction.CellChange(x, y, true, false));
			if (changes.Count > 0)
				EditorChangeTracker.PushUndo(new TileCollisionUndoAction(map, changes, "Generate collision from tiles"), map.Entity, "Generate collision from tiles");
			map.RebuildColliders();
			return new { generated = count, changed = changes.Count, collisionCells = map.CollisionCellCount };
		}, P.Str("entity", "Tilemap entity"), P.Bool("solidOnly", "Only tiles marked solid in the tileset", true));

		table.Add("tileset.list", "Tileset assets of the project.", (_, _) =>
			RequireAssets().Items
				.Where(i => i.Extension.Equals(TilesetAssetIO.FileExtension, StringComparison.OrdinalIgnoreCase))
				.DistinctBy(i => i.AbsolutePath, StringComparer.OrdinalIgnoreCase)
				.Select(i => new { name = Path.GetFileNameWithoutExtension(i.FileName), path = i.AbsolutePath, guid = AssetDatabase.Instance.GetReference(i.AbsolutePath).Guid })
				.ToList()).ReadOnly();

		table.Add("tileset.info", "A tileset asset: grid, texture, and every tile that carries a name, solid flag, collision shape, terrain or animation.", (args, _) =>
		{
			var item = ResolveTileset(args.Require("tileset"));
			var asset = TilesetAssetIO.Load(item.AbsolutePath);
			var runtime = TilesetRuntime.Get(item.AbsolutePath);
			return new
			{
				name = asset.Name,
				path = item.AbsolutePath,
				guid = AssetDatabase.Instance.GetReference(item.AbsolutePath).Guid,
				tileWidth = asset.TileWidth,
				tileHeight = asset.TileHeight,
				spacing = asset.Spacing,
				margin = asset.Margin,
				columns = runtime?.Columns ?? asset.Columns,
				rows = runtime?.Rows ?? asset.Rows,
				tileCount = runtime?.TileCount ?? asset.TileCount,
				texture = asset.Texture.IsValid ? asset.Texture.AssetPath : null,
				textureSource = asset.TextureSource.ToString(),
				normalMap = asset.HasNormalMap ? asset.NormalMap.AssetPath : null,
				issues = runtime?.Issues,
				terrains = asset.Terrains.Select(t => new { t.Id, t.Name }).ToList(),
				customColliders = asset.CustomColliders.Select(c => new { c.Name, points = c.Points.Count }).ToList(),
				tiles = asset.Tiles.Select(t => new
				{
					index = t.Index,
					name = t.Name,
					solid = t.Solid,
					shape = t.CollisionShape.ToString(),
					oneWay = t.OneWay,
					customCollider = t.CustomColliderName,
					terrain = t.TerrainId >= 0 ? t.TerrainId : (int?)null,
					animated = t.IsAnimated,
					frames = t.IsAnimated ? t.AnimationFrames : null,
					blank = runtime != null && t.Index >= 0 && t.Index < runtime.TileCount && runtime.IsBlank(t.Index)
				}).ToList()
			};
		}, P.Str("tileset", "Tileset GUID, path or file name", required: true)).ReadOnly();

		table.Add("tileset.create", "Write a new .vtileset for a texture in the project; the grid is derived from the image size.", (args, _) =>
		{
			var project = ProjectManager.Instance.CurrentProject ?? throw new GatewayException("no project loaded");
			var texture = ResolveAsset(args.Require("texture"));
			if (texture.Descriptor.Kind != AssetKind.Texture)
				throw new GatewayException($"{texture.FileName} is not a texture");

			var name = args.String("name", Path.GetFileNameWithoutExtension(texture.FileName)).Trim();
			if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
				throw new GatewayException($"invalid tileset name '{name}'");
			var folder = args.Has("folder") ? GatewayPaths.RequireInside(Path.Combine(project.ProjectPath, args.Require("folder")), project.ProjectPath, "folder") : TilesetPaths.DefaultTilesetFolder();
			Directory.CreateDirectory(folder);
			var path = GatewayPaths.RequireInside(TilesetPaths.UniquePath(folder, name, TilesetAssetIO.FileExtension), project.ProjectPath, "path");

			var asset = TilesetAssetIO.CreateDefault(name);
			asset.TileWidth = Math.Max(1, args.Int("tileWidth", 16));
			asset.TileHeight = Math.Max(1, args.Int("tileHeight", 16));
			asset.Spacing = Math.Max(0, args.Int("spacing"));
			asset.Margin = Math.Max(0, args.Int("margin"));
			asset.Texture = TilemapSceneUtils.ReferenceFor(texture.AbsolutePath);
			asset.TextureSource = texture.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? TilesetImageSource.Png : TilesetImageSource.Aseprite;
			TilesetAssetIO.Save(asset, path);
			AssetDatabase.Instance?.Refresh();
			TilesetRuntime.Invalidate(path);

			var runtime = TilesetRuntime.Get(path);
			return new { path, guid = AssetDatabase.Instance?.GetReference(path).Guid, columns = runtime?.Columns, rows = runtime?.Rows, tileCount = runtime?.TileCount, issues = runtime?.Issues };
		}, P.Str("texture", "Texture GUID, path or file name", required: true), P.Str("name", "Tileset name; default the texture name"), P.Str("folder", "Project-relative folder; default the tilesets folder"), P.Int("tileWidth", "Cell width in pixels", 16), P.Int("tileHeight", "Cell height in pixels", 16), P.Int("spacing", "Pixels between cells", 0), P.Int("margin", "Pixels around the grid", 0));
	}


	private static AssetItem ResolveTileset(string key)
	{
		var item = ResolveAsset(key);
		if (!item.Extension.Equals(TilesetAssetIO.FileExtension, StringComparison.OrdinalIgnoreCase))
			throw new GatewayException($"{item.FileName} is not a {TilesetAssetIO.FileExtension} asset");
		return item;
	}

	/// <summary>Named entity, else the palette's target, else the scene's only layer.</summary>
	private static TilemapRenderer Resolve(GatewayArgs args, GatewayContext ctx)
	{
		if (Core.Scene == null)
			throw new GatewayException("no scene loaded");

		if (args.Has("entity"))
		{
			var entity = ResolveEntity(args.Require("entity"));
			return entity.GetComponent<TilemapRenderer>() ?? throw new GatewayException($"{entity.Name} has no TilemapRenderer");
		}

		var target = ctx.ImGui().TilePaintTool.Target;
		if (target?.Entity != null && target.Entity.Scene == Core.Scene)
			return target;

		var maps = TilemapSceneUtils.FindTilemaps();
		return maps.Count switch
		{
			1 => maps[0],
			0 => throw new GatewayException("no tilemap in the scene; call tilemap.create"),
			_ => throw new GatewayException("several tilemaps in the scene; pass 'entity'")
		};
	}

	private static void CheckTile(TilemapRenderer map, int tile)
	{
		if (tile < -1)
			throw new GatewayException($"tile {tile} is out of range");
		var tileset = map.ResolvedTileset;
		if (tile >= 0 && tileset != null && tile >= tileset.TileCount)
			throw new GatewayException($"tile {tile} is out of range; the tileset has {tileset.TileCount} tiles");
	}

	private static IEnumerable<(int x, int y, int tile)> Cells(GatewayArgs args, TilemapRenderer map, bool requireTile = true)
	{
		if (args.TryGet("cells", out var cells))
		{
			if (cells.ValueKind != JsonValueKind.Array)
				throw new GatewayException("'cells' must be an array of {x, y, tile}");
			foreach (var element in cells.EnumerateArray())
			{
				var cell = new GatewayArgs(element);
				var tile = cell.Int("tile", requireTile ? int.MinValue : -1);
				if (tile == int.MinValue)
					tile = args.Int("tile", int.MinValue);
				if (requireTile && tile == int.MinValue)
					throw new GatewayException("each cell needs a 'tile', or pass a default 'tile'");
				CheckTile(map, tile);
				yield return (cell.Int("x"), cell.Int("y"), tile);
			}
			yield break;
		}

		if (!args.Has("x") || !args.Has("y"))
			throw new GatewayException("pass 'x' and 'y', or 'cells'");
		var single = args.Int("tile", requireTile ? int.MinValue : -1);
		if (single == int.MinValue)
			throw new GatewayException("missing parameter 'tile'");
		CheckTile(map, single);
		yield return (args.Int("x"), args.Int("y"), single);
	}

	private static IEnumerable<(int x, int y)> Rect(GatewayArgs args)
	{
		if (!args.Has("x") || !args.Has("y"))
			throw new GatewayException("pass 'x' and 'y' (with width and height for a rectangle), or 'cells'");
		var x0 = args.Int("x");
		var y0 = args.Int("y");
		var w = Math.Max(1, args.Int("width", 1));
		var h = Math.Max(1, args.Int("height", 1));
		if (w * h > MaxFillCells)
			throw new GatewayException($"rectangle too large; at most {MaxFillCells} cells");
		for (var y = y0; y < y0 + h; y++)
			for (var x = x0; x < x0 + w; x++)
				yield return (x, y);
	}

	/// <summary>Contiguous four-connected run from the origin, bounded like the palette's fill tool.</summary>
	private static List<Point> Flood(TilemapRenderer map, Point origin, Func<Point, bool> matches)
	{
		var extents = map.TileExtents;
		var bounds = extents.IsEmpty
			? new Rectangle(origin.X - 32, origin.Y - 32, 64, 64)
			: new Rectangle(extents.X - 32, extents.Y - 32, extents.Width + 64, extents.Height + 64);
		var result = new List<Point>();
		if (!bounds.Contains(origin))
			return result;

		var visited = new HashSet<Point> { origin };
		var queue = new Queue<Point>();
		queue.Enqueue(origin);
		while (queue.Count > 0 && result.Count < MaxFillCells)
		{
			var cell = queue.Dequeue();
			if (!matches(cell))
				continue;
			result.Add(cell);
			foreach (var next in new[] { new Point(cell.X + 1, cell.Y), new Point(cell.X - 1, cell.Y), new Point(cell.X, cell.Y + 1), new Point(cell.X, cell.Y - 1) })
				if (bounds.Contains(next) && visited.Add(next))
					queue.Enqueue(next);
		}
		return result;
	}

	/// <summary>Writes one cell the way the brush does and records the stack change for undo.</summary>
	private static void Apply(TilemapRenderer map, int x, int y, int tile, bool stack, List<TilePaintUndoAction.CellChange> changes)
	{
		var tileset = map.ResolvedTileset;
		if (tile >= 0 && tileset != null && tile < tileset.TileCount && tileset.IsBlank(tile))
			return;

		var old = map.GetCellStack(x, y);
		if (tile < 0)
		{
			if (old.Length == 0)
				return;
			map.SetTile(x, y, -1);
		}
		else if (stack && old.Length > 0)
		{
			if (old.Length >= TilemapRenderer.MaxStackHeight)
				return;
			map.PushTile(x, y, tile);
		}
		else
		{
			if (old.Length == 1 && old[0] == tile)
				return;
			map.SetTile(x, y, tile);
		}
		changes.Add(new TilePaintUndoAction.CellChange(x, y, old, map.GetCellStack(x, y)));
	}

	private static object Commit(TilemapRenderer map, List<TilePaintUndoAction.CellChange> changes, string verb)
	{
		if (changes.Count > 0)
		{
			var description = $"{verb} {changes.Count} tile{(changes.Count == 1 ? "" : "s")}";
			EditorChangeTracker.PushUndo(new TilePaintUndoAction(map, changes, description), map.Entity, description);
			if (map.AutoBuildColliders)
				map.RebuildColliders();
		}
		var extents = map.TileExtents;
		return new { changed = changes.Count, tileCount = map.TileCount, extents = new { extents.X, extents.Y, extents.Width, extents.Height } };
	}

	private static object Cell(TilemapRenderer map, int x, int y, int[] stack, bool solid) => new
	{
		x,
		y,
		tile = stack.Length > 0 ? stack[^1] : -1,
		baseTile = stack.Length > 0 ? stack[0] : -1,
		stack,
		orientation = map.GetOrientation(x, y),
		solid
	};

	private static object Info(TilemapRenderer map)
	{
		var extents = map.TileExtents;
		var tileset = map.ResolvedTileset;
		return new
		{
			entity = map.Entity?.Id,
			name = map.Entity?.Name,
			tileset = map.Tileset.IsValid ? new { path = map.Tileset.AssetPath, guid = map.Tileset.AssetGuid, name = map.Tileset.AssetName } : null,
			tilesetTiles = tileset?.TileCount,
			tileWidth = map.TileWidth,
			tileHeight = map.TileHeight,
			tileCount = map.TileCount,
			extents = new { extents.X, extents.Y, extents.Width, extents.Height },
			collisionCells = map.CollisionCellCount,
			physicsLayer = map.PhysicsLayer,
			collidesWithLayers = map.CollidesWithLayers,
			isTrigger = map.IsTrigger,
			autoBuildColliders = map.AutoBuildColliders,
			renderLayer = map.RenderLayer,
			layerDepth = map.LayerDepth,
			issues = tileset?.Issues
		};
	}
}
