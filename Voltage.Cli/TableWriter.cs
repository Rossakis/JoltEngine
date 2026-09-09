using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Voltage.Cli;

/// <summary>Aligned text tables for results that are lists of flat objects or objects of scalars; anything else falls back to JSON.</summary>
public static class TableWriter
{
	private const int MaxCell = 60;

	public static bool TryWrite(JsonElement result, StringBuilder output)
	{
		if (result.ValueKind == JsonValueKind.Array)
		{
			var rows = result.EnumerateArray().ToList();
			if (rows.Count == 0)
				return false;
			if (rows.All(r => r.ValueKind == JsonValueKind.Object))
				return WriteRows(rows, output);
			if (rows.All(r => r.ValueKind != JsonValueKind.Object && r.ValueKind != JsonValueKind.Array))
			{
				foreach (var row in rows)
					output.AppendLine(Cell(row));
				return true;
			}
			return false;
		}

		if (result.ValueKind == JsonValueKind.Object)
		{
			var pairs = result.EnumerateObject().ToList();
			if (pairs.Count == 0)
				return false;
			var width = pairs.Max(p => p.Name.Length);
			foreach (var pair in pairs)
				output.AppendLine($"{pair.Name.PadRight(width)}  {Cell(pair.Value)}");
			return true;
		}

		return false;
	}

	private static bool WriteRows(List<JsonElement> rows, StringBuilder output)
	{
		var columns = new List<string>();
		foreach (var row in rows)
			foreach (var property in row.EnumerateObject())
				if (!columns.Contains(property.Name))
					columns.Add(property.Name);

		var cells = rows.Select(r => columns.Select(c => r.TryGetProperty(c, out var v) ? Cell(v) : "").ToArray()).ToList();
		var widths = columns.Select((c, i) => Math.Max(c.Length, cells.Max(row => row[i].Length))).ToArray();

		output.AppendLine(string.Join("  ", columns.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
		output.AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));
		foreach (var row in cells)
			output.AppendLine(string.Join("  ", row.Select((v, i) => v.PadRight(widths[i]))).TrimEnd());
		return true;
	}

	/// <summary>Scalars print raw, nested values as compact JSON, everything clipped to one line.</summary>
	public static string Cell(JsonElement value)
	{
		var text = value.ValueKind switch
		{
			JsonValueKind.Null or JsonValueKind.Undefined => "",
			JsonValueKind.String => value.GetString() ?? "",
			JsonValueKind.Number => value.GetRawText(),
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			_ => value.GetRawText()
		};
		text = text.Replace("\r", "").Replace('\n', ' ');
		return text.Length > MaxCell ? text.Substring(0, MaxCell - 3) + "..." : text;
	}

	/// <summary>Follows a dotted path such as "scene.name" or "items.0.name"; arrays are indexed by number.</summary>
	public static JsonElement? Select(JsonElement root, string path)
	{
		var current = root;
		foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
		{
			if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(part, out var child))
				current = child;
			else if (current.ValueKind == JsonValueKind.Array && int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < current.GetArrayLength())
				current = current[index];
			else
				return null;
		}
		return current;
	}
}
