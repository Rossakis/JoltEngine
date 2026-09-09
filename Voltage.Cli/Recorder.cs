using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Voltage.Cli;

/// <summary>Records a session through input.record and saves it in the shape voltage run replays.</summary>
public static class Recorder
{
	public static int Record(GatewayConnection connection, string path, bool mouse, bool keyboard, bool text)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliException("pass the file to write, e.g. voltage record steps.json");

		var start = new JsonObject { ["action"] = "start", ["mouse"] = mouse, ["keyboard"] = keyboard, ["text"] = text };
		connection.Call("input.record", JsonSerializer.SerializeToElement(start));
		Console.Error.WriteLine("Recording; press Enter to stop");
		Console.In.ReadLine();

		var stop = new JsonObject { ["action"] = "stop" };
		var result = connection.Call("input.record", JsonSerializer.SerializeToElement(stop));
		var steps = result.GetProperty("steps");
		var file = new JsonObject { ["steps"] = JsonNode.Parse(steps.GetRawText()) };
		File.WriteAllText(Path.GetFullPath(path), file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

		var seconds = result.TryGetProperty("seconds", out var s) ? s.GetDouble() : 0;
		var truncated = result.TryGetProperty("truncated", out var t) && t.GetBoolean();
		Console.WriteLine($"{steps.GetArrayLength()} steps over {seconds:0.0}s written to {Path.GetFullPath(path)}{(truncated ? " (truncated)" : "")}");
		return 0;
	}
}
