using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Voltage.Cli;

/// <summary>Replays a recorded script file: {"steps": [...]} goes to input.script, a list of {method, params} runs call by call or as one batch.</summary>
public static class ScriptRunner
{
	public static int Run(GatewayConnection connection, string path, bool batch, bool stopOnError, Action<JsonElement> print)
	{
		if (string.IsNullOrEmpty(path) || !File.Exists(path))
			throw new CliException($"script file not found: {path}");

		var root = JsonNode.Parse(File.ReadAllText(path));
		if (root is JsonObject obj && obj["steps"] is JsonArray)
		{
			print(connection.Call("input.script", JsonSerializer.SerializeToElement(obj)));
			return 0;
		}

		if (root is not JsonArray calls)
			throw new CliException("a script is either {\"steps\": [...]} or a list of {\"method\": ..., \"params\": {...}}");

		var requests = calls.Select((node, i) =>
		{
			if (node is not JsonObject call || call["method"]?.GetValue<string>() is not { } method)
				throw new CliException($"script entry {i} has no \"method\"");
			return (method, parameters: call["params"]?.DeepClone());
		}).ToList();

		if (batch)
		{
			var payload = new JsonObject
			{
				["requests"] = new JsonArray(requests.Select(r => (JsonNode)new JsonObject { ["method"] = r.method, ["params"] = r.parameters }).ToArray()),
				["stopOnError"] = stopOnError
			};
			var result = connection.Call("batch", JsonSerializer.SerializeToElement(payload));
			print(result);
			return result.TryGetProperty("failed", out var failed) && failed.ValueKind == JsonValueKind.Number ? 1 : 0;
		}

		var failures = 0;
		foreach (var (method, parameters) in requests)
		{
			Console.WriteLine($"> {method}{(parameters != null ? " " + parameters.ToJsonString() : "")}");
			try
			{
				print(connection.Call(method, parameters != null ? JsonSerializer.SerializeToElement(parameters) : null));
			}
			catch (CliException ex)
			{
				failures++;
				Console.Error.WriteLine($"error: {ex.Message}");
				if (stopOnError)
					return 1;
			}
		}
		return failures > 0 ? 1 : 0;
	}
}
