using System;
using System.Collections.Generic;
using System.IO;

namespace Voltage.Assets
{
	/// <summary>Maps source content paths to the compiled asset names an asset build wrote to Content/content.index; empty when no build ran.</summary>
	public static class CompiledContentIndex
	{
		private static Dictionary<string, string> _entries;
		private static readonly object Lock = new();

		public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "Content", "content.index");

		public static int Count
		{
			get
			{
				Load();
				return _entries.Count;
			}
		}

		/// <summary>True when the build compiled this source path; the query may be relative, absolute under the base directory, or use backslashes.</summary>
		public static bool TryGetAssetName(string sourcePath, out string assetName)
		{
			assetName = null;
			if (string.IsNullOrEmpty(sourcePath))
				return false;

			Load();
			return _entries.Count > 0 && _entries.TryGetValue(Normalize(sourcePath), out assetName);
		}

		/// <summary>Raised by <see cref="Reset"/> so loaders can forget what failed against the old index.</summary>
		public static event Action OnReset;

		/// <summary>Forgets the loaded index so the next lookup re-reads the file.</summary>
		public static void Reset()
		{
			lock (Lock)
				_entries = null;
			OnReset?.Invoke();
		}

		private static void Load()
		{
			if (_entries != null)
				return;

			lock (Lock)
			{
				if (_entries != null)
					return;

				var entries = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
				try
				{
					if (File.Exists(Path))
					{
						foreach (var line in File.ReadLines(Path))
						{
							var tab = line.IndexOf('\t');
							if (tab <= 0 || tab == line.Length - 1)
								continue;
							entries[Normalize(line.Substring(0, tab))] = line.Substring(tab + 1).Trim();
						}
					}
				}
				catch (Exception ex)
				{
					Debug.Warn($"[Content] could not read {Path}: {ex.Message}");
				}

				_entries = entries;
			}
		}

		private static string Normalize(string path)
		{
			var normalized = path.Replace('\\', '/').Trim();
			if (normalized.StartsWith("./", StringComparison.Ordinal))
				normalized = normalized.Substring(2);

			if (System.IO.Path.IsPathRooted(normalized))
			{
				var root = AppContext.BaseDirectory.Replace('\\', '/').TrimEnd('/') + "/";
				if (normalized.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
					normalized = normalized.Substring(root.Length);
			}

			return normalized;
		}
	}
}
