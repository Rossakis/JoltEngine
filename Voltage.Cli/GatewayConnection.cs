using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Voltage.Cli;

public sealed class CliException : Exception
{
	public CliException(string message) : base(message)
	{
	}
}

/// <summary>A single authenticated socket to the editor gateway.</summary>
public sealed class GatewayConnection : IDisposable
{
	private readonly TcpClient _tcp;
	private readonly StreamReader _reader;
	private readonly StreamWriter _writer;
	private int _nextId = 1;

	public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>Called for every event line that arrives while waiting for a response.</summary>
	public Action<JsonElement> OnEvent { get; set; }

	public GatewayConnection(int port, string token, TimeSpan? timeout = null)
	{
		if (timeout.HasValue)
			Timeout = timeout.Value;

		_tcp = new TcpClient();
		try
		{
			_tcp.Connect("127.0.0.1", port);
		}
		catch (SocketException ex)
		{
			throw new CliException($"could not connect to the editor on port {port}: {ex.Message}");
		}

		_tcp.NoDelay = true;
		var stream = _tcp.GetStream();
		_reader = new StreamReader(stream, new UTF8Encoding(false));
		_writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

		_writer.WriteLine(JsonSerializer.Serialize(new { auth = token }));
		var hello = ReadLine(Timeout);
		using var doc = JsonDocument.Parse(hello);
		if (!doc.RootElement.TryGetProperty("event", out var evt) || evt.GetString() != "hello")
			throw new CliException("authentication rejected; the editor's gateway.json may be stale");
	}

	/// <summary>Sends a request and returns the parsed response; throws on an error response.</summary>
	public JsonElement Call(string method, JsonElement? parameters)
	{
		var id = _nextId++;
		var request = parameters.HasValue
			? JsonSerializer.Serialize(new { id, method, @params = parameters.Value })
			: JsonSerializer.Serialize(new { id, method });

		return SendRaw(request, id);
	}

	/// <summary>Sends a pre-built JSON request line. Waits for its response when it carries an id.</summary>
	public JsonElement SendRaw(string requestJson, object expectedId)
	{
		_writer.WriteLine(requestJson);
		if (expectedId == null)
			return default;

		var expected = JsonSerializer.SerializeToElement(expectedId).GetRawText();
		var deadline = DateTime.UtcNow + Timeout;

		while (true)
		{
			var remaining = deadline - DateTime.UtcNow;
			if (remaining <= TimeSpan.Zero)
				throw new CliException($"timed out after {Timeout.TotalSeconds:0}s waiting for '{requestJson}'");

			var line = ReadLine(remaining);
			var doc = JsonDocument.Parse(line);
			var root = doc.RootElement.Clone();
			doc.Dispose();

			if (root.TryGetProperty("event", out _))
			{
				OnEvent?.Invoke(root);
				continue;
			}

			if (root.TryGetProperty("id", out var id) && id.GetRawText() == expected)
			{
				if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
					return root.TryGetProperty("result", out var result) ? result : default;

				throw new CliException(root.TryGetProperty("error", out var err) ? err.GetString() : "request failed");
			}
		}
	}

	/// <summary>Blocks on the socket and hands every event to <see cref="OnEvent"/> until the socket closes.</summary>
	public void PumpEvents()
	{
		_tcp.ReceiveTimeout = 0;
		while (true)
		{
			var line = _reader.ReadLine();
			if (line == null)
				return;

			using var doc = JsonDocument.Parse(line);
			if (doc.RootElement.TryGetProperty("event", out _))
				OnEvent?.Invoke(doc.RootElement.Clone());
		}
	}

	private string ReadLine(TimeSpan timeout)
	{
		_tcp.ReceiveTimeout = (int)Math.Max(1, timeout.TotalMilliseconds);
		string line;
		try
		{
			line = _reader.ReadLine();
		}
		catch (IOException)
		{
			throw new CliException("the editor stopped responding");
		}

		return line ?? throw new CliException("the editor closed the connection");
	}

	public void Dispose()
	{
		_tcp.Close();
	}
}
