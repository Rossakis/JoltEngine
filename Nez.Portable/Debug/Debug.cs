using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Nez
{
    public static partial class Debug
    {
        public enum LogType
        {
            Error,
            Warn,
            Log,
            Info,
            Trace
        }

        public struct LogEntry
        {
            public LogType Type;
            public string Message;
            public DateTime Timestamp;
            public string CallerClass;
            public int CallerLine;

            public LogEntry(LogType type, string message, DateTime timestamp, string callerClass, int callerLine)
            {
                Type = type;
                Message = message;
                Timestamp = timestamp;
                CallerClass = callerClass;
                CallerLine = callerLine;
            }
        }

        private static readonly List<LogEntry> _logEntries = new();
        private static readonly object _logLock = new();

        // Track repeated messages
        private static readonly Dictionary<(LogType, string, string, int), int> _messageCounts = new();
        private const int MaxRepeatedMessages = 100;

        public static IReadOnlyList<LogEntry> GetLogEntries()
        {
            lock (_logLock)
                return _logEntries.AsReadOnly();
        }

        public static void ClearLogEntries()
        {
            lock (_logLock)
            {
                _logEntries.Clear();
                _messageCounts.Clear();
            }
        }

        #region Logging

        [DebuggerHidden]
        private static void Log(
            LogType type,
            string format,
            object[] args,
            string callerFile,
            int callerLine)
        {
            string msg = args != null && args.Length > 0 ? string.Format(format, args) : format;
            string callerClass = System.IO.Path.GetFileNameWithoutExtension(callerFile);
            
            var key = (type, msg, callerClass, callerLine);

            lock (_logLock)
            {
                if (_messageCounts.TryGetValue(key, out int count))
                {
                    if (count < MaxRepeatedMessages)
                    {
                        _logEntries.Add(new LogEntry(type, msg, DateTime.Now, callerClass, callerLine));
                        _messageCounts[key] = count + 1;
                    }
                    else if (count == MaxRepeatedMessages)
                    {
                        string summary = $"\"{msg}\" was logged more than {MaxRepeatedMessages} times. [at {callerClass}:{callerLine}]";
                        _logEntries.Add(new LogEntry(LogType.Warn, summary, DateTime.Now, callerClass, callerLine));
                        _messageCounts[key] = count + 1;
                    }
                }
                else
                {
                    _logEntries.Add(new LogEntry(type, msg, DateTime.Now, callerClass, callerLine));
                    _messageCounts[key] = 1;
                }

                if (_logEntries.Count > 500)
                    _logEntries.RemoveAt(0);
            }
        }

        [DebuggerHidden]
        public static void Error(
            string format,
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0,
            params object[] args)
        {
            Log(LogType.Error, format, args, callerFile, callerLine);
        }

        [DebuggerHidden]
        public static void ErrorIf(
            bool condition,
            string format,
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0,
            params object[] args)
        {
            if (condition)
                Log(LogType.Error, format, args, callerFile, callerLine);
        }

        [DebuggerHidden]
        public static void Warn(
            string format,
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0,
            params object[] args)
        {
            Log(LogType.Warn, format, args, callerFile, callerLine);
        }

        [DebuggerHidden]
        public static void WarnIf(
            bool condition,
            string format,
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0,
            params object[] args)
        {
            if (condition)
                Log(LogType.Warn, format, args, callerFile, callerLine);
        }

        [Conditional("DEBUG")]
        [DebuggerHidden]
        public static void Log(
            object obj,
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0)
        {
            Log(LogType.Log, "{0}", new object[] { obj }, callerFile, callerLine);
        }

        [Conditional("DEBUG")]
        [DebuggerHidden]
        public static void Log(
            string format,
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0,
            params object[] args)
        {
            Log(LogType.Log, format, args, callerFile, callerLine);
        }

        [Conditional("DEBUG")]
        [DebuggerHidden]
        public static void LogIf(
            bool condition,
            string format,
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0,
            params object[] args)
        {
            if (condition)
                Log(LogType.Log, format, args, callerFile, callerLine);
        }

        [Conditional("DEBUG")]
        [DebuggerHidden]
        public static void Info(
            string format,
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0,
            params object[] args)
        {
            Log(LogType.Info, format, args, callerFile, callerLine);
        }

        [Conditional("DEBUG")]
        [DebuggerHidden]
        public static void Trace(
            string format,
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0,
            params object[] args)
        {
            Log(LogType.Trace, format, args, callerFile, callerLine);
        }

        #endregion

        [Conditional("DEBUG")]
        public static void BreakIf(bool condition)
        {
            if (condition)
                Debugger.Break();
        }

        [Conditional("DEBUG")]
        public static void Break_()
        {
            Debugger.Break();
        }

        /// <summary>
        /// Times how long an Action takes to run and returns the TimeSpan
        /// </summary>
        /// <returns>The action.</returns>
        /// <param name="action">Action.</param>
        public static TimeSpan TimeAction(Action action, uint numberOfIterations = 1)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            for (var i = 0; i < numberOfIterations; i++)
                action();
            stopwatch.Stop();

            if (numberOfIterations > 1)
                return TimeSpan.FromTicks(stopwatch.Elapsed.Ticks / numberOfIterations);

            return stopwatch.Elapsed;
        }
    }
}