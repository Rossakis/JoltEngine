using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Voltage.Gateway;

/// <summary>Loopback TCP listener speaking newline-delimited JSON. Sockets are serviced on background threads; requests are handed to the main thread through <see cref="TryDequeue"/>.</summary>
public sealed class GatewayServer : IDisposable
{
	private readonly TcpListener _listener;
	private readonly ConcurrentQueue<(GatewayClient client, string line)> _inbox = new();
	private readonly List<GatewayClient> _clients = new();
	private readonly object _clientsLock = new();
	private volatile bool _running;
	private int _nextClientId;

	private readonly GatewayOptions _options;

	/// <summary>Explicit resolvers: games built with PublishAot disable reflection serialization by default.</summary>
	private static readonly JsonSerializerOptions WireJson = new() { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };

	private static readonly JsonSerializerOptions InfoJson = new() { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };

	public int Port { get; private set; }

	public string Token { get; }

	/// <summary>Discovery file the CLI reads to find the port and token.</summary>
	public string InfoFilePath => _options.InfoFilePath;

	public GatewayServer(GatewayOptions options)
	{
		_options = options;
		Port = options.Port;
		Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
		_listener = new TcpListener(IPAddress.Loopback, options.Port);
	}

	public void Start()
	{
		_listener.Start();
		Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
		_running = true;

		WriteInfoFile();

		var accept = new Thread(AcceptLoop) { IsBackground = true, Name = "Gateway accept" };
		accept.Start();
	}

	/// <summary>Pops the next request line; call from the main thread only.</summary>
	public bool TryDequeue(out GatewayClient client, out string line)
	{
		if (_inbox.TryDequeue(out var item))
		{
			client = item.client;
			line = item.line;
			return true;
		}

		client = null;
		line = null;
		return false;
	}

	public void Broadcast(string json, Func<GatewayClient, bool> filter = null)
	{
		foreach (var client in Snapshot())
			if (client.Authenticated && (filter == null || filter(client)))
				client.Send(json);
	}

	public int ClientCount
	{
		get
		{
			lock (_clientsLock)
				return _clients.Count;
		}
	}

	private GatewayClient[] Snapshot()
	{
		lock (_clientsLock)
			return _clients.ToArray();
	}

	private void AcceptLoop()
	{
		while (_running)
		{
			TcpClient tcp;
			try
			{
				tcp = _listener.AcceptTcpClient();
			}
			catch (Exception)
			{
				break;
			}

			var client = new GatewayClient(Interlocked.Increment(ref _nextClientId), tcp);
			lock (_clientsLock)
				_clients.Add(client);

			var reader = new Thread(() => ReadLoop(client)) { IsBackground = true, Name = $"Gateway reader {client.Id}" };
			reader.Start();
		}
	}

	private void ReadLoop(GatewayClient client)
	{
		try
		{
			var first = client.ReadLine();
			if (first == null || !IsValidAuth(first))
			{
				client.Send("{\"event\":\"error\",\"data\":\"authentication failed\"}");
				return;
			}

			client.Authenticated = true;
			UpdateKeepAlive();
			client.Send(JsonSerializer.Serialize(new
			{
				@event = "hello",
				data = new { editor = "Voltage", host = _options.Host, pid = Environment.ProcessId, clientId = client.Id }
			}, WireJson));

			while (_running)
			{
				var line = client.ReadLine();
				if (line == null)
					break;
				if (line.Length > 0)
					_inbox.Enqueue((client, line));
			}
		}
		finally
		{
			Remove(client);
		}
	}

	/// <summary>Anything but a well-formed auth object fails closed; the compare does not leak by timing.</summary>
	private bool IsValidAuth(string line)
	{
		try
		{
			using var doc = JsonDocument.Parse(line);
			if (doc.RootElement.ValueKind != JsonValueKind.Object
				|| !doc.RootElement.TryGetProperty("auth", out var auth)
				|| auth.ValueKind != JsonValueKind.String)
				return false;

			var offered = Encoding.UTF8.GetBytes(auth.GetString() ?? "");
			var expected = Encoding.UTF8.GetBytes(Token);
			return CryptographicOperations.FixedTimeEquals(offered, expected);
		}
		catch (Exception)
		{
			return false;
		}
	}

	private void Remove(GatewayClient client)
	{
		lock (_clientsLock)
			_clients.Remove(client);
		client.Dispose();
		UpdateKeepAlive();
	}

	/// <summary>Only authenticated clients keep the host running unfocused, so an unauthenticated connect changes nothing; a headless host never pauses.</summary>
	private void UpdateKeepAlive()
	{
		lock (_clientsLock)
			Core.KeepRunningWhenUnfocused = _options.Headless || _clients.Any(c => c.Authenticated);
	}

	/// <summary>The token is the only barrier on a loopback port every local account can reach, so the file is owner-only from the moment it exists.</summary>
	private void WriteInfoFile()
	{
		Directory.CreateDirectory(Path.GetDirectoryName(InfoFilePath) ?? ".");
		var info = new
		{
			port = Port,
			token = Token,
			pid = Environment.ProcessId,
			started = DateTime.UtcNow,
			exe = Environment.ProcessPath,
			args = _options.Args ?? Array.Empty<string>(),
			logs = _options.LogsDirectory,
			host = _options.Host,
			game = _options.Name
		};
		var json = JsonSerializer.Serialize(info, InfoJson);
		Directory.CreateDirectory(Path.GetDirectoryName(InfoFilePath)!);

		var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
		if (!OperatingSystem.IsWindows())
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

		using (var stream = new FileStream(InfoFilePath, options))
		using (var writer = new StreamWriter(stream))
			writer.Write(json);

		if (!OperatingSystem.IsWindows())
			File.SetUnixFileMode(InfoFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
	}

	public void Dispose()
	{
		if (!_running)
			return;

		_running = false;
		try { _listener.Stop(); } catch (Exception) { }

		// The info file stays behind on purpose: its pid tells the CLI the editor is gone, and its exe path
		// is what 'voltage start' relaunches.
		foreach (var client in Snapshot())
			Remove(client);
	}
}
