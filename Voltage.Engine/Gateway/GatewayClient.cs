using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Voltage.Gateway;

/// <summary>One connected gateway socket. Reads happen on the server's reader thread, writes on a private writer thread.</summary>
public sealed class GatewayClient : IDisposable
{
	private readonly TcpClient _tcp;
	private readonly StreamReader _reader;
	private readonly StreamWriter _writer;
	private readonly BlockingCollection<string> _outbox = new();
	private readonly Thread _writerThread;
	private volatile bool _closed;

	/// <summary>Longest request line accepted, in characters; anything longer closes the connection.</summary>
	public const int MaxLineLength = 4 * 1024 * 1024;

	public int Id { get; }

	public bool Authenticated { get; internal set; }

	/// <summary>True once this client asked for live log events.</summary>
	public bool LogSubscribed { get; set; }

	/// <summary>True once this client asked for lifecycle events.</summary>
	public bool EventsSubscribed { get; set; }

	internal GatewayClient(int id, TcpClient tcp)
	{
		Id = id;
		_tcp = tcp;
		_tcp.NoDelay = true;

		var stream = tcp.GetStream();
		_reader = new StreamReader(stream, new UTF8Encoding(false));
		_writer = new StreamWriter(stream, new UTF8Encoding(false));

		_writerThread = new Thread(WriteLoop) { IsBackground = true, Name = $"Gateway writer {id}" };
		_writerThread.Start();
	}

	public bool IsConnected => !_closed && _tcp.Connected;

	/// <summary>Queues one JSON line for delivery without blocking the caller.</summary>
	public void Send(string json)
	{
		if (_closed)
			return;

		try
		{
			_outbox.TryAdd(json);
		}
		catch (InvalidOperationException)
		{
		}
	}

	/// <summary>Blocks until the next line arrives; null once the socket closes or a line outgrows <see cref="MaxLineLength"/> before it ends.</summary>
	internal string ReadLine()
	{
		if (_closed)
			return null;

		var line = new StringBuilder();
		try
		{
			while (true)
			{
				var c = _reader.Read();
				if (c < 0)
					return line.Length > 0 ? line.ToString() : null;
				if (c == '\n')
					return line.ToString().TrimEnd('\r');

				line.Append((char)c);
				if (line.Length > MaxLineLength)
				{
					Send("{\"event\":\"error\",\"data\":\"request too large\"}");
					Dispose();
					return null;
				}
			}
		}
		catch (Exception)
		{
			return null;
		}
	}

	private void WriteLoop()
	{
		try
		{
			foreach (var line in _outbox.GetConsumingEnumerable())
			{
				_writer.WriteLine(line);
				_writer.Flush();
			}
		}
		catch (Exception)
		{
			Dispose();
		}
	}

	public void Dispose()
	{
		if (_closed)
			return;

		_closed = true;
		try { _outbox.CompleteAdding(); } catch (Exception) { }

		// Let a final line (such as the auth rejection) reach the peer before the socket goes.
		if (_writerThread != null && _writerThread != Thread.CurrentThread)
			_writerThread.Join(TimeSpan.FromMilliseconds(250));
		try { _tcp.Close(); } catch (Exception) { }
	}
}
