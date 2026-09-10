using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Voltage.Cli;

/// <summary>Shell completion: the scripts call back into 'voltage __complete', which answers from the live command list or its cache.</summary>
public static class Completion
{
	public static readonly string[] Subcommands = { "help", "logs", "watch", "pipe", "json", "mcp", "start", "stop", "restart", "doctor", "run", "record", "completion" };

	private static readonly string[] GlobalOptions = { "--config", "--port", "--token", "--timeout", "--compact", "--table", "--field", "--game" };

	private static readonly Dictionary<string, string[]> SubcommandOptions = new()
	{
		["logs"] = new[] { "--follow", "--level", "--count", "--grep", "--since" },
		["watch"] = new[] { "--logs", "--filter", "--json" },
		["start"] = new[] { "--exe", "--wait", "--game", "--headless", "--safe", "--no-prompts", "--gateway-port" },
		["stop"] = new[] { "--wait", "--game" },
		["restart"] = new[] { "--rebuild", "--wait", "--game", "--headless", "--safe", "--no-prompts", "--gateway-port" },
		["doctor"] = new[] { "--json" },
		["run"] = new[] { "--batch", "--continue" },
		["record"] = new[] { "--no-mouse", "--no-keyboard", "--no-text" },
	};

	private static readonly string[] OptionsWithValue = { "--config", "--port", "--token", "--timeout", "--field", "--level", "--count", "--filter", "--exe", "--wait", "--grep", "--since", "--gateway-port" };

	private static readonly TimeSpan CacheMaxAge = TimeSpan.FromSeconds(30);

	/// <summary>Prints the script for bash, zsh or pwsh.</summary>
	public static int Script(string shell, string configOverride)
	{
		var invocation = Invocation(configOverride);
		switch ((shell ?? "").ToLowerInvariant())
		{
			case "bash":
				Console.WriteLine(
"# voltage completion for bash. Install: voltage completion bash > ~/.voltage-completion.bash && echo 'source ~/.voltage-completion.bash' >> ~/.bashrc\n" +
"_voltage_complete() {\n" +
"  local IFS=$'\\n'\n" +
"  COMP_WORDBREAKS=${COMP_WORDBREAKS//=/}\n" +
$"  COMPREPLY=($({invocation} __complete $((COMP_CWORD - 1)) \"${{COMP_WORDS[@]:1}}\" 2>/dev/null))\n" +
"  local i\n" +
"  for i in \"${!COMPREPLY[@]}\"; do\n" +
"    [[ ${COMPREPLY[$i]} == *= ]] || COMPREPLY[$i]+=\" \"\n" +
"  done\n" +
"}\n" +
"complete -o nospace -F _voltage_complete voltage");
				return 0;
			case "zsh":
				Console.WriteLine(
"#compdef voltage\n" +
"# voltage completion for zsh. Install: voltage completion zsh > ~/.zsh/completions/_voltage (a directory on your fpath), then rm -f ~/.zcompdump && compinit\n" +
"_voltage() {\n" +
"  local -a completions\n" +
$"  completions=(${{(f)\"$({invocation} __complete $((CURRENT - 2)) \"${{words[@]:1}}\" 2>/dev/null)\"}})\n" +
"  compadd -S '' -- ${(M)completions:#*=}\n" +
"  compadd -- ${completions:#*=}\n" +
"}\n" +
"compdef _voltage voltage");
				return 0;
			case "pwsh":
			case "powershell":
				Console.WriteLine(
"# voltage completion for PowerShell. Install: voltage completion pwsh >> $PROFILE (or dot-source the saved script from your profile)\n" +
"Register-ArgumentCompleter -Native -CommandName voltage -ScriptBlock {\n" +
"  param($wordToComplete, $commandAst, $cursorPosition)\n" +
"  $words = @($commandAst.CommandElements | Select-Object -Skip 1 | ForEach-Object { $_.Extent.Text })\n" +
"  if ([string]::IsNullOrEmpty($wordToComplete)) { $words += '' }\n" +
"  $index = $words.Count - 1\n" +
$"  & {invocation} __complete $index @words 2>$null | ForEach-Object {{\n" +
"    [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)\n" +
"  }\n" +
"}");
				return 0;
			default:
				throw new CliException("usage: voltage completion bash|zsh|pwsh");
		}
	}

	/// <summary>Prints the candidates for the word at index within words, one per line; never fails.</summary>
	public static int Complete(List<string> args, string infoPath)
	{
		if (args.Count == 0 || !int.TryParse(args[0], out var index))
			return 0;
		var words = args.Skip(1).ToList();
		index = Math.Max(0, index);
		while (words.Count <= index)
			words.Add("");
		var current = words[index];

		var previous = index > 0 ? words[index - 1] : null;
		if (previous != null && OptionsWithValue.Contains(previous))
		{
			if (previous == "--level")
				Print(new[] { "Error", "Warn", "Log", "Info", "Trace", "Success" }, current);
			return 0;
		}

		var command = FirstCommand(words, index);

		if (current.StartsWith('-'))
		{
			var options = GlobalOptions.AsEnumerable();
			if (command != null && SubcommandOptions.TryGetValue(command, out var extra))
				options = options.Concat(extra);
			Print(options, current);
			return 0;
		}

		if (command == null)
		{
			Print(Subcommands.Concat(Methods(infoPath).Select(m => m.name)), current);
			return 0;
		}

		switch (command)
		{
			case "completion":
				Print(new[] { "bash", "zsh", "pwsh" }, current);
				break;
			case "help":
				Print(Methods(infoPath).Select(m => m.name), current);
				break;
			default:
				var method = Methods(infoPath).FirstOrDefault(m => m.name.Equals(command, StringComparison.OrdinalIgnoreCase));
				if (method.name != null)
				{
					var used = words.Where((w, i) => i != index && w.Contains('=')).Select(w => w.Substring(0, w.IndexOf('='))).ToHashSet(StringComparer.OrdinalIgnoreCase);
					Print(method.parameters.Where(p => !used.Contains(p)).Select(p => p + "="), current);
				}
				break;
		}
		return 0;
	}

	/// <summary>The first word that is neither an option nor an option's value, or null while the command itself is being typed.</summary>
	private static string FirstCommand(List<string> words, int index)
	{
		for (var i = 0; i < index; i++)
		{
			var word = words[i];
			if (word.StartsWith('-'))
			{
				if (OptionsWithValue.Contains(word))
					i++;
				continue;
			}
			return word;
		}
		return null;
	}

	private static void Print(IEnumerable<string> candidates, string prefix)
	{
		foreach (var candidate in candidates.Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Distinct().OrderBy(c => c, StringComparer.Ordinal))
			Console.WriteLine(candidate);
	}

	/// <summary>Live method list when the host answers within two seconds, else the cache beside gateway.json.</summary>
	private static List<(string name, List<string> parameters)> Methods(string infoPath)
	{
		var cachePath = Path.Combine(Path.GetDirectoryName(infoPath) ?? ".", "gateway-commands.txt");
		var fresh = File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < CacheMaxAge;

		if (!fresh)
		{
			try
			{
				var info = GatewayInfo.Load(infoPath);
				using var connection = new GatewayConnection(info.Port, info.Token, TimeSpan.FromSeconds(2));
				var commands = connection.Call("commands", null);
				var lines = commands.EnumerateArray().Select(c =>
				{
					var name = c.GetProperty("name").GetString();
					var parameters = c.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > 0
						? p.EnumerateArray().Select(x => x.GetProperty("name").GetString())
						: ParamsFromHelp(c.TryGetProperty("help", out var h) ? h.GetString() : null);
					return name + " " + string.Join(" ", parameters);
				});
				File.WriteAllLines(cachePath, lines);
			}
			catch (Exception)
			{
			}
		}

		try
		{
			if (File.Exists(cachePath))
				return File.ReadAllLines(cachePath)
					.Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
					.Where(parts => parts.Length > 0)
					.Select(parts => (parts[0], parts.Skip(1).ToList()))
					.ToList();
		}
		catch (Exception)
		{
		}

		return new List<(string, List<string>)>();
	}

	/// <summary>Names from a "params: a, b=1 (note)" help suffix, for commands without declared parameters.</summary>
	private static IEnumerable<string> ParamsFromHelp(string help)
	{
		var index = help?.IndexOf("params:", StringComparison.OrdinalIgnoreCase) ?? -1;
		if (index < 0)
			yield break;

		var depth = 0;
		var current = new System.Text.StringBuilder();
		foreach (var c in help.Substring(index + "params:".Length) + ",")
		{
			if (c is '(' or '[' or '{') depth++;
			else if (c is ')' or ']' or '}') depth--;
			if (c == ',' && depth == 0)
			{
				var token = current.ToString().Trim();
				current.Clear();
				var end = token.IndexOfAny(new[] { ' ', '=', '(' });
				var name = end > 0 ? token.Substring(0, end) : token;
				foreach (var part in name.Split('|', StringSplitOptions.RemoveEmptyEntries))
					if (part.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
						yield return part;
			}
			else
				current.Append(c);
		}
	}

	/// <summary>How to call this CLI back from a shell: the exe itself, or dotnet plus the dll, plus a non-default --config.</summary>
	private static string Invocation(string configOverride)
	{
		var process = Environment.ProcessPath ?? "voltage";
		var name = Path.GetFileNameWithoutExtension(process);
		var invocation = name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
			? $"\"{process}\" \"{typeof(Completion).Assembly.Location}\""
			: $"\"{process}\"";
		return configOverride != null ? $"{invocation} --config \"{configOverride}\"" : invocation;
	}
}
