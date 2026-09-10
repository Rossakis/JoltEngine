using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;

namespace Voltage.Aseprite
{
	/// <summary>The compiled Aseprite payload: parsed cels stored uncompressed, so a build skips zlib and colour conversion. Shared by the pipeline writer and the runtime reader.</summary>
	public static class AsepriteBinary
	{
		public const uint Magic = 0x45534156;
		public const byte Version = 1;

		private const byte LayerImage = 0, LayerGroup = 1, LayerTilemap = 2;
		private const byte CelImage = 0, CelLinked = 1, CelTilemap = 2;

		public static void Write(BinaryWriter w, AsepriteFile file)
		{
			w.Write(Magic);
			w.Write(Version);
			w.Write(file.Name ?? "");
			w.Write(file.CanvasWidth);
			w.Write(file.CanvasHeight);
			w.Write((ushort)file.ColorDepth);

			w.Write(file.Palette.TransparentIndex);
			WritePixels(w, file.Palette.Colors);

			w.Write(file.Warnings.Count);
			foreach (var warning in file.Warnings)
				w.Write(warning ?? "");

			var tilesets = new List<AsepriteTileset>();
			foreach (var layer in file.Layers)
				if (layer is AsepriteTilemapLayer tilemap && !tilesets.Contains(tilemap.Tileset))
					tilesets.Add(tilemap.Tileset);
			w.Write(tilesets.Count);
			foreach (var tileset in tilesets)
			{
				w.Write(tileset.ID);
				w.Write(tileset.TileCount);
				w.Write(tileset.TileWidth);
				w.Write(tileset.TileHeight);
				w.Write(tileset.Name ?? "");
				WritePixels(w, tileset.Pixels);
			}

			w.Write(file.Layers.Count);
			foreach (var layer in file.Layers)
			{
				w.Write(layer is AsepriteGroupLayer ? LayerGroup : layer is AsepriteTilemapLayer ? LayerTilemap : LayerImage);
				w.Write(layer.IsVisible);
				w.Write(layer.IsBackgroundLayer);
				w.Write(layer.IsReferenceLayer);
				w.Write(layer.ChildLevel);
				w.Write((int)layer.BlendMode);
				w.Write(layer.Opacity);
				w.Write(layer.Name ?? "");
				w.Write(layer is AsepriteTilemapLayer t ? tilesets.IndexOf(t.Tileset) : -1);
				var children = (layer as AsepriteGroupLayer)?.Children;
				w.Write(children?.Count ?? 0);
				if (children != null)
					foreach (var child in children)
						w.Write(file.Layers.IndexOf(child));
			}

			w.Write(file.Frames.Count);
			for (var f = 0; f < file.Frames.Count; f++)
			{
				var frame = file.Frames[f];
				w.Write(frame.Name ?? "");
				w.Write(frame.Duration);
				w.Write(frame.Cels.Count);
				for (var c = 0; c < frame.Cels.Count; c++)
				{
					var cel = frame.Cels[c];
					w.Write(cel is AsepriteLinkedCel ? CelLinked : cel is AsepriteTilemapCel ? CelTilemap : CelImage);
					w.Write(file.Layers.IndexOf(cel.Layer));
					w.Write(cel.Position.X);
					w.Write(cel.Position.Y);
					w.Write(cel.Opacity);
					WriteUserData(w, cel.UserData);
					switch (cel)
					{
						case AsepriteImageCel image:
							w.Write(image.Width);
							w.Write(image.Height);
							WritePixels(w, image.Pixels);
							break;
						case AsepriteLinkedCel linked:
							w.Write(FindLinkedFrame(file, f, c, linked));
							break;
						case AsepriteTilemapCel tilemap:
							w.Write(tilemap.Width);
							w.Write(tilemap.Height);
							w.Write(tilemap.BitsPerTile);
							w.Write(tilemap.TileIDBitmask);
							w.Write(tilemap.XFlipBitmask);
							w.Write(tilemap.YFlipBitmask);
							w.Write(tilemap.RotationBitmask);
							w.Write(tilemap.Tiles.Length);
							foreach (var tile in tilemap.Tiles)
							{
								w.Write(tile.ID);
								w.Write(tile.XFlip);
								w.Write(tile.YFlip);
								w.Write(tile.Rotate90);
							}
							break;
					}
				}
			}

			w.Write(file.Tags.Count);
			foreach (var tag in file.Tags)
			{
				w.Write(tag.From);
				w.Write(tag.To);
				w.Write((byte)tag.LoopDirection);
				w.Write(tag.Color.PackedValue);
				w.Write(tag.Name ?? "");
				WriteUserData(w, tag.UserData);
			}

			w.Write(file.Slices.Count);
			foreach (var slice in file.Slices)
			{
				w.Write(slice.IsNinePatch);
				w.Write(slice.HasPivot);
				w.Write(slice.Name ?? "");
				WriteUserData(w, slice.UserData);
				var keys = DistinctKeys(slice);
				w.Write(keys.Count);
				foreach (var key in keys)
				{
					w.Write(key.FrameIndex);
					WriteRect(w, key.Bounds);
					w.Write(key.CenterBounds.HasValue);
					if (key.CenterBounds.HasValue)
						WriteRect(w, key.CenterBounds.Value);
					w.Write(key.Pivot.HasValue);
					if (key.Pivot.HasValue)
					{
						w.Write(key.Pivot.Value.X);
						w.Write(key.Pivot.Value.Y);
					}
				}
			}
		}

		public static AsepriteFile Read(BinaryReader r)
		{
			if (r.ReadUInt32() != Magic)
				throw new InvalidDataException("not a compiled Aseprite asset");
			var version = r.ReadByte();
			if (version != Version)
				throw new InvalidDataException($"compiled Aseprite version {version} is not supported");

			var name = r.ReadString();
			var width = r.ReadInt32();
			var height = r.ReadInt32();
			var depth = (AsepriteColorDepth)r.ReadUInt16();

			var palette = new AsepritePalette(r.ReadInt32()) { Colors = ReadPixels(r) };

			var warnings = new List<string>(r.ReadInt32());
			for (var i = 0; i < warnings.Capacity; i++)
				warnings.Add(r.ReadString());

			var tilesetCount = r.ReadInt32();
			var tilesets = new AsepriteTileset[tilesetCount];
			for (var i = 0; i < tilesetCount; i++)
			{
				var id = r.ReadInt32();
				var tileCount = r.ReadInt32();
				var tileWidth = r.ReadInt32();
				var tileHeight = r.ReadInt32();
				var tilesetName = r.ReadString();
				tilesets[i] = new AsepriteTileset(id, tileCount, tileWidth, tileHeight, tilesetName, ReadPixels(r));
			}

			var layerCount = r.ReadInt32();
			var layers = new List<AsepriteLayer>(layerCount);
			var childIndices = new int[layerCount][];
			for (var i = 0; i < layerCount; i++)
			{
				var kind = r.ReadByte();
				var visible = r.ReadBoolean();
				var background = r.ReadBoolean();
				var reference = r.ReadBoolean();
				var childLevel = r.ReadInt32();
				var blend = (AsepriteBlendMode)r.ReadInt32();
				var opacity = r.ReadInt32();
				var layerName = r.ReadString();
				var tilesetIndex = r.ReadInt32();
				var childCount = r.ReadInt32();
				childIndices[i] = new int[childCount];
				for (var c = 0; c < childCount; c++)
					childIndices[i][c] = r.ReadInt32();

				layers.Add(kind switch
				{
					LayerGroup => new AsepriteGroupLayer(visible, background, reference, childLevel, blend, opacity, layerName),
					LayerTilemap => new AsepriteTilemapLayer(tilesets[tilesetIndex], visible, background, reference, childLevel, blend, opacity, layerName),
					_ => new AsepriteImageLayer(visible, background, reference, childLevel, blend, opacity, layerName)
				});
			}
			for (var i = 0; i < layerCount; i++)
				if (layers[i] is AsepriteGroupLayer group)
					foreach (var index in childIndices[i])
						group.Children.Add(layers[index]);

			var frameCount = r.ReadInt32();
			var frames = new List<AsepriteFrame>(frameCount);
			for (var f = 0; f < frameCount; f++)
			{
				var frameName = r.ReadString();
				var duration = r.ReadInt32();
				var celCount = r.ReadInt32();
				var cels = new List<AsepriteCel>(celCount);
				for (var c = 0; c < celCount; c++)
				{
					var kind = r.ReadByte();
					var layer = layers[r.ReadInt32()];
					var position = new Point(r.ReadInt32(), r.ReadInt32());
					var opacity = r.ReadInt32();
					var text = r.ReadString();
					var hasColor = r.ReadBoolean();
					var color = hasColor ? new Color(r.ReadUInt32()) : (Color?)null;

					AsepriteCel cel;
					switch (kind)
					{
						case CelLinked:
							cel = new AsepriteLinkedCel(frames[r.ReadInt32()].Cels[c], layer, position, opacity);
							break;
						case CelTilemap:
						{
							var cw = r.ReadInt32();
							var ch = r.ReadInt32();
							var bits = r.ReadInt32();
							var idMask = r.ReadUInt32();
							var xMask = r.ReadUInt32();
							var yMask = r.ReadUInt32();
							var rotMask = r.ReadUInt32();
							var tiles = new AsepriteTile[r.ReadInt32()];
							for (var t = 0; t < tiles.Length; t++)
								tiles[t] = new AsepriteTile(r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32());
							cel = new AsepriteTilemapCel(cw, ch, bits, idMask, xMask, yMask, rotMask, tiles, layer, position, opacity);
							break;
						}
						default:
						{
							var cw = r.ReadInt32();
							var ch = r.ReadInt32();
							cel = new AsepriteImageCel(cw, ch, ReadPixels(r), layer, position, opacity);
							break;
						}
					}

					cel.UserData.Text = text;
					cel.UserData.Color = color;
					cels.Add(cel);
				}
				frames.Add(new AsepriteFrame(frameName, duration, cels, width, height));
			}

			var tagCount = r.ReadInt32();
			var tags = new List<AsepriteTag>(tagCount);
			for (var i = 0; i < tagCount; i++)
			{
				var from = r.ReadInt32();
				var to = r.ReadInt32();
				var direction = (AsepriteLoopDirection)r.ReadByte();
				var color = new Color(r.ReadUInt32());
				var tagName = r.ReadString();
				var tag = new AsepriteTag(from, to, direction, color, tagName);
				ReadUserData(r, tag.UserData);
				tags.Add(tag);
			}

			var sliceCount = r.ReadInt32();
			var slices = new List<AsepriteSlice>(sliceCount);
			for (var i = 0; i < sliceCount; i++)
			{
				var ninePatch = r.ReadBoolean();
				var hasPivot = r.ReadBoolean();
				var sliceName = r.ReadString();
				var slice = new AsepriteSlice(ninePatch, hasPivot, sliceName);
				ReadUserData(r, slice.UserData);
				var keyCount = r.ReadInt32();
				for (var k = 0; k < keyCount; k++)
				{
					var frameIndex = r.ReadInt32();
					var bounds = ReadRect(r);
					Rectangle? center = r.ReadBoolean() ? ReadRect(r) : null;
					Point? pivot = r.ReadBoolean() ? new Point(r.ReadInt32(), r.ReadInt32()) : null;
					_ = new AsepriteSliceKey(slice, frameIndex, bounds, center, pivot);
				}
				slices.Add(slice);
			}

			return new AsepriteFile(name, palette, width, height, depth, frames, layers, tags, slices, warnings);
		}

		/// <summary>The loader links a cel to the same cel index of an earlier frame; find that frame again for the writer.</summary>
		private static int FindLinkedFrame(AsepriteFile file, int frameIndex, int celIndex, AsepriteLinkedCel linked)
		{
			for (var f = 0; f < frameIndex; f++)
				if (celIndex < file.Frames[f].Cels.Count && ReferenceEquals(file.Frames[f].Cels[celIndex], linked.Cel))
					return f;
			throw new InvalidDataException($"linked cel in frame {frameIndex} points outside the file");
		}

		/// <summary>The parser registers each slice key twice (constructor and explicit add); compiled files carry each once.</summary>
		private static List<AsepriteSliceKey> DistinctKeys(AsepriteSlice slice)
		{
			var keys = new List<AsepriteSliceKey>(slice.Keys.Count);
			foreach (var key in slice.Keys)
				if (!keys.Contains(key))
					keys.Add(key);
			return keys;
		}

		private static void WriteUserData(BinaryWriter w, AsepriteUserData data)
		{
			w.Write(data?.Text ?? "");
			w.Write(data?.Color != null);
			if (data?.Color != null)
				w.Write(data.Color.Value.PackedValue);
		}

		private static void ReadUserData(BinaryReader r, AsepriteUserData data)
		{
			data.Text = r.ReadString();
			if (r.ReadBoolean())
				data.Color = new Color(r.ReadUInt32());
		}

		private static void WritePixels(BinaryWriter w, Color[] pixels)
		{
			w.Write(pixels?.Length ?? 0);
			if (pixels == null)
				return;
			for (var i = 0; i < pixels.Length; i++)
				w.Write(pixels[i].PackedValue);
		}

		private static Color[] ReadPixels(BinaryReader r)
		{
			var pixels = new Color[r.ReadInt32()];
			for (var i = 0; i < pixels.Length; i++)
				pixels[i] = new Color(r.ReadUInt32());
			return pixels;
		}

		private static void WriteRect(BinaryWriter w, Rectangle rect)
		{
			w.Write(rect.X);
			w.Write(rect.Y);
			w.Write(rect.Width);
			w.Write(rect.Height);
		}

		private static Rectangle ReadRect(BinaryReader r) => new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
	}
}
