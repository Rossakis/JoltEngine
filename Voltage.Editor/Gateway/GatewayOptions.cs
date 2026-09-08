using System;

namespace Voltage.Editor.Gateway;

/// <summary>Gateway settings read from the editor command line.</summary>
public sealed class GatewayOptions
{
	public const int DefaultPort = 47800;

	public bool Enabled { get; init; } = true;

	public int Port { get; init; } = DefaultPort;

	/// <summary>Understands --no-gateway, --gateway-port N and --gateway-port=N.</summary>
	public static GatewayOptions FromArgs(string[] args)
	{
		var enabled = true;
		var port = DefaultPort;

		if (args == null)
			return new GatewayOptions();

		for (var i = 0; i < args.Length; i++)
		{
			var arg = args[i];

			if (arg.Equals("--no-gateway", StringComparison.OrdinalIgnoreCase))
			{
				enabled = false;
			}
			else if (arg.StartsWith("--gateway-port=", StringComparison.OrdinalIgnoreCase))
			{
				int.TryParse(arg.Substring("--gateway-port=".Length), out port);
			}
			else if (arg.Equals("--gateway-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
			{
				int.TryParse(args[++i], out port);
			}
		}

		return new GatewayOptions { Enabled = enabled, Port = port };
	}
}
