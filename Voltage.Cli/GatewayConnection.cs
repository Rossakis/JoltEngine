using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voltage.Cli;

public sealed class CliException : Exception
{
	public CliException(string message) : base(message)
	{
	}
}

/// <summary>A single authenticated socket to the editor gateway. A reader thread routes replies to their callers and hands events to <see cref="OnEvent"/>.</summary>
public sealed class GatewayConnection : IDisposable
{
	private readonly TcpClient _tcp;
	private readonly StreamReader _reader;
	private readonly StreamWriter _writer;
	private readonly object _writeLock = new();
	private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _waiters = new();
	private readonly ManualResetEventSlim _closed = new(false);
	private volatile string _closeReason;
	private int _nextId;

	public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>Called on the reader thread for every event line; never call back into this connection synchronously from it.</summary>
	public Action<JsonElement> OnEvent { get; set; }

	public bool IsOpen => !_closed.IsSet;

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
		_tcp.ReceiveTimeout = (int)Math.Max(1, Timeout.TotalMilliseconds);
		string hello;
		try
		{
			hello = _reader.ReadLine();
		}
		catch (IOException)
		{
			throw new CliException("the editor stopped responding");
		}
		if (hello == null)
			throw new CliException("the editor closed the connection");
		using (var doc = JsonDocument.Parse(hello))
			if (!doc.RootElement.TryGetProperty("event", out var evt) || evt.GetString() != "hello")
				throw new CliException("authentication rejected; the editor's gateway.json may be stale");

		_tcp.ReceiveTimeout = 0;
		new Thread(ReadLoop) { IsBackground = true, Name = "Gateway connection reader" }.Start();
	}

	/// <summary>Sends a request and returns the parsed response; throws on an error response.</summary>
	public JsonElement Call(string method, JsonElement? parameters)
	{
		var id = Interlocked.Increment(ref _nextId);
		var request = parameters.HasValue
			? JsonSerializer.Serialize(new { id, method, @params = parameters.Value })
			: JsonSerializer.Serialize(new { id, method });

		return SendRaw(request, id);
	}

	/// <summary>Sends a pre-built JSON request line. Waits for its response when it carries an id.</summary>
	public JsonElement SendRaw(string requestJson, object expectedId)
	{
		if (expectedId == null)
		{
			Write(requestJson);
			return default;
		}

		var key = JsonSerializer.SerializeToElement(expectedId).GetRawText();
		var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
		_waiters[key] = waiter;
		JsonElement root;
		try
		{
			Write(requestJson);
			if (!waiter.Task.Wait(Timeout))
				throw new CliException($"timed out after {Timeout.TotalSeconds:0}s waiting for '{requestJson}'");
			root = waiter.Task.Result;
		}
		catch (AggregateException ex) when (ex.InnerException is CliException inner)
		{
			throw inner;
		}
		finally
		{
			_waiters.TryRemove(key, out _);
		}

		if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
			return root.TryGetProperty("result", out var result) ? result : default;

		throw new CliException(root.TryGetProperty("error", out var err) ? err.GetString() : "request failed");
	}

	/// <summary>Blocks until the socket closes; events keep flowing to <see cref="OnEvent"/> meanwhile.</summary>
	public void PumpEvents()
	{
		_closed.Wait();
	}

	private void Write(string line)
	{
		if (_closed.IsSet)
			throw new CliException(_closeReason ?? "the editor closed the connection");

		try
		{
			lock (_writeLock)
				_writer.WriteLine(line);
		}
		catch (IOException)
		{
			throw new CliException("the editor stopped responding");
		}
		catch (ObjectDisposedException)
		{
			throw new CliException("the editor closed the connection");
		}
	}

	private void ReadLoop()
	{
		var reason = "the editor closed the connection";
		try
		{
			string line;
			while ((line = _reader.ReadLine()) != null)
			{
				if (line.Length == 0)
					continue;

				JsonElement root;
				try
				{
					using var doc = JsonDocument.Parse(line);
					root = doc.RootElement.Clone();
				}
				catch (JsonException)
				{
					continue;
				}

				if (root.TryGetProperty("event", out _))
				{
					try { OnEvent?.Invoke(root); } catch (Exception) { }
					continue;
				}

				if (root.TryGetProperty("id", out var id) && _waiters.TryRemove(id.GetRawText(), out var waiter))
					waiter.TrySetResult(root);
			}
		}
		catch (IOException)
		{
			reason = "the editor stopped responding";
		}
		catch (ObjectDisposedException)
		{
		}
		finally
		{
			_closeReason = reason;
			_closed.Set();
			foreach (var key in _waiters.Keys)
				if (_waiters.TryRemove(key, out var waiter))
					waiter.TrySetException(new CliException(reason));
		}
	}

	public void Dispose()
	{
		_tcp.Close();
		_closed.Set();
	}
}
