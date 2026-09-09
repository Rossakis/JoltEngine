using System;
using System.IO;
using System.Threading.Tasks;

namespace Voltage.Gateway;

/// <summary>Timeouts for handlers that answer from a later frame or event.</summary>
public static class GatewayTasks
{
	/// <summary>Completes with the source or fails after the timeout; the cleanup runs on timeout to detach handlers.</summary>
	public static Task<object> WithTimeout(TaskCompletionSource<object> source, TimeSpan timeout, Action cleanup, string timeoutMessage)
	{
		Core.Schedule((float)timeout.TotalSeconds, false, null, _ =>
		{
			if (source.TrySetException(new GatewayException(timeoutMessage)))
				cleanup?.Invoke();
		});
		return source.Task;
	}

	/// <summary>Answers on the first frame <paramref name="ready"/> holds, or at the deadline regardless, for state that settles a frame after the handler ran.</summary>
	public static Task<object> WhenReady(Func<bool> ready, Func<object> result, float timeoutSeconds = 5f)
	{
		var source = new TaskCompletionSource<object>();
		var deadline = Voltage.Utils.Time.TotalTime + timeoutSeconds;
		Core.Schedule(0f, true, null, timer =>
		{
			if (!ready() && Voltage.Utils.Time.TotalTime < deadline)
				return;
			timer.Stop();
			try
			{
				source.TrySetResult(result());
			}
			catch (Exception ex)
			{
				source.TrySetException(ex);
			}
		});
		return source.Task;
	}

	/// <summary>Fails a task that has not finished by the deadline; the original keeps running unobserved.</summary>
	public static Task<object> WithTimeout(Task<object> task, TimeSpan timeout, string timeoutMessage)
	{
		var source = new TaskCompletionSource<object>();
		var timer = Core.Schedule((float)timeout.TotalSeconds, false, null, _ => source.TrySetException(new GatewayException(timeoutMessage)));
		task.ContinueWith(t =>
		{
			timer.Stop();
			if (t.IsCompletedSuccessfully)
				source.TrySetResult(t.Result);
			else
				source.TrySetException(t.Exception?.GetBaseException() ?? new GatewayException("cancelled"));
		}, TaskContinuationOptions.ExecuteSynchronously);
		return source.Task;
	}

	/// <summary>Answers on the main thread once a background task finishes, mapping its value; fails with the task's error or at the deadline.</summary>
	public static Task<object> FromTask<T>(Task<T> task, Func<T, object> result, float timeoutSeconds = 60f)
	{
		return WhenReady(() => task.IsCompleted, () =>
		{
			if (!task.IsCompleted)
				throw new GatewayException("timed out waiting for the operation to finish");
			if (task.IsFaulted)
				throw task.Exception?.GetBaseException() ?? new GatewayException("operation failed");
			if (task.IsCanceled)
				throw new GatewayException("operation was cancelled");
			return result(task.Result);
		}, timeoutSeconds);
	}
}

/// <summary>Path checks for commands that write files.</summary>
public static class GatewayPaths
{
	/// <summary>Returns the full path, or throws when it escapes the root.</summary>
	public static string RequireInside(string path, string root, string what = "path")
	{
		var full = Path.GetFullPath(path);
		var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		if (!full.StartsWith(rootFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
			throw new GatewayException($"{what} must be inside {rootFull}");
		return full;
	}
}
