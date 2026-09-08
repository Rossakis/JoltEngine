using System;
using System.Collections.Generic;
using System.Linq;
using Voltage.Editor.ImGuiCore;

namespace Voltage.Editor.Gateway;

/// <summary>Per-request context handed to a handler.</summary>
public sealed class GatewayContext
{
	public GatewayClient Client { get; init; }

	public ImGuiManager ImGui { get; init; }

	public GatewayServer Server { get; init; }

	public GatewayCommandTable Commands { get; init; }

	public GatewayDispatcher Dispatcher { get; init; }
}

/// <summary>Handles one request on the main thread. Return a plain object, or a Task&lt;object&gt; to answer later.</summary>
public delegate object GatewayHandler(GatewayArgs args, GatewayContext ctx);

public sealed record GatewayCommand(string Name, string Help, GatewayHandler Handler);

/// <summary>Name-indexed registry of every gateway command.</summary>
public sealed class GatewayCommandTable
{
	private readonly Dictionary<string, GatewayCommand> _commands = new(StringComparer.OrdinalIgnoreCase);

	public IEnumerable<GatewayCommand> All => _commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal);

	public void Add(string name, string help, GatewayHandler handler)
	{
		_commands[name] = new GatewayCommand(name, help, handler);
	}

	public bool TryGet(string name, out GatewayCommand command) => _commands.TryGetValue(name ?? "", out command);
}
