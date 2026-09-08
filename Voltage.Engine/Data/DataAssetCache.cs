using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Voltage.Persistence;
using Voltage.Serialization;

namespace Voltage.Data
{
	/// <summary>
	/// The shared-instance store behind <see cref="DataAssets"/>.
	/// </summary>
	public static class DataAssetCache
	{
		private static readonly object _lock = new();
		private static readonly Dictionary<Guid, DataAsset> _byGuid = new();
		private static readonly Dictionary<string, DataAsset> _byPath = new(StringComparer.OrdinalIgnoreCase);

		private static Dictionary<DataAsset, string> _playModeSnapshot;

		/// <summary>
		/// Incremented on every in-place reload.
		/// </summary>
		public static int ReloadVersion { get; private set; }

		/// <summary>
		/// Null when the reference is empty, unresolvable, or the wrong type.
		/// </summary>
		public static T Get<T>(AssetReference reference) where T : DataAsset
		{
			var asset = Get(reference);
			if (asset == null)
				return null;

			if (asset is T typed)
				return typed;

			Debug.Warn(
				$"[DataAssetCache] '{asset.SourcePath ?? reference.ToString()}' is a " +
				$"{asset.GetType().Name}, not a {typeof(T).Name}. Check the inspector slot's assignment.");
			return null;
		}

		/// <summary>Resolves and loads <paramref name="reference"/>, or null when it cannot be resolved.</summary>
		public static DataAsset Get(AssetReference reference)
		{
			if (!reference.IsValid)
				return null;

			if (reference.AssetGuid != Guid.Empty)
			{
				lock (_lock)
				{
					if (_byGuid.TryGetValue(reference.AssetGuid, out var cached))
						return cached;
				}
			}

			var path = reference.ResolvePath();
			if (string.IsNullOrEmpty(path))
			{
				Debug.Warn(
					$"[DataAssetCache] Could not resolve {reference}. Open the project in the editor to " +
					"regenerate the asset manifest.");
				return null;
			}

			return Load(path, reference.AssetGuid);
		}

		/// <summary>Loads by absolute path, bypassing GUID resolution. For tooling and tests.</summary>
		public static DataAsset GetByPath(string absolutePath) => Load(absolutePath, Guid.Empty);

		private static DataAsset Load(string absolutePath, Guid guid)
		{
			if (string.IsNullOrEmpty(absolutePath))
				return null;

			lock (_lock)
			{
				if (_byPath.TryGetValue(absolutePath, out var cached))
				{
					if (guid != Guid.Empty && !_byGuid.ContainsKey(guid))
					{
						_byGuid[guid] = cached;
						cached.SourceGuid = guid;
					}
					return cached;
				}
			}

			var asset = DataAssetIO.Load(absolutePath);
			if (asset == null)
				return null;

			asset.SourceGuid = guid;

			// Never cache a clone-on-load type: doing so would hand out a shared instance after all.
			if (DataAssetRegistry.TryGet(asset.GetType(), out var entry) && entry.CloneOnLoad)
				return asset;

			lock (_lock)
			{
				if (_byPath.TryGetValue(absolutePath, out var raced))
					return raced;

				_byPath[absolutePath] = asset;
				if (guid != Guid.Empty)
					_byGuid[guid] = asset;

				if (_playModeSnapshot != null)
					_playModeSnapshot[asset] = SafeSnapshot(asset);
			}

			return asset;
		}

		/// <summary>Drops every cached instance. Call on project close, not on scene change.</summary>
		public static void Clear()
		{
			lock (_lock)
			{
				_byGuid.Clear();
				_byPath.Clear();
				_playModeSnapshot?.Clear();
			}
		}

		/// <summary>Re-reads the file <b>in place</b>, so every existing reference sees the new values.</summary>
		public static void Reload(Guid guid)
		{
			DataAsset existing;
			lock (_lock)
			{
				if (!_byGuid.TryGetValue(guid, out existing))
					return;
			}

			ReloadInstance(existing);
		}

		/// <summary>Re-reads the file behind <paramref name="absolutePath"/> in place.</summary>
		public static void ReloadPath(string absolutePath)
		{
			if (string.IsNullOrEmpty(absolutePath))
				return;

			DataAsset existing;
			lock (_lock)
			{
				if (!_byPath.TryGetValue(absolutePath, out existing))
					return;
			}

			ReloadInstance(existing);
		}

		// Per instance: ReloadVersion is global, so a window keyed on it refreshes when any .vasset changes.
		private static readonly ConditionalWeakTable<DataAsset, StrongBox<int>> _reloadCounts = new();

		/// <summary>How many times this instance has been re-read from disk.</summary>
		public static int ReloadCountFor(DataAsset asset) =>
			asset != null && _reloadCounts.TryGetValue(asset, out var box) ? box.Value : 0;

		private static void ReloadInstance(DataAsset existing)
		{
			var path = existing.SourcePath;
			if (string.IsNullOrEmpty(path))
				return;

			var fresh = DataAssetIO.Load(path);
			if (fresh == null || fresh.GetType() != existing.GetType())
				return;   // DataAssetIO already logged; keep the old values rather than half-applying

			// In place: callers hold this reference, so swapping the dictionary entry for a new object
			// would leave them reading stale data forever.
			CopyInto(fresh, existing);
			existing.LoadedVersion = fresh.LoadedVersion;
			existing.OnLoaded();

			lock (_lock)
			{
				ReloadVersion++;
				_reloadCounts.GetOrCreateValue(existing).Value++;
			}
		}

		/// <summary>
		/// <b>Editor only.</b> Snapshots every cached asset before play mode, so runtime mutation of a shared instance cannot leak into the authored file.
		/// </summary>
		public static void SnapshotForPlayMode()
		{
			lock (_lock)
			{
				_playModeSnapshot = new Dictionary<DataAsset, string>(_byPath.Count);
				foreach (var asset in _byPath.Values)
					_playModeSnapshot[asset] = SafeSnapshot(asset);
			}
		}

		/// <summary><b>Editor only.</b> Restores pre-play-mode values in place. No-op without a snapshot.</summary>
		public static void RestoreAfterPlayMode()
		{
			Dictionary<DataAsset, string> snapshot;
			lock (_lock)
			{
				snapshot = _playModeSnapshot;
				_playModeSnapshot = null;
			}

			if (snapshot == null)
				return;

			foreach (var pair in snapshot)
			{
				if (pair.Value == null)
					continue;

				try
				{
					Json.FromJsonOverwrite(pair.Value, pair.Key);
				}
				catch (Exception ex)
				{
					Debug.Warn(
						$"[DataAssetCache] Could not restore '{pair.Key.SourcePath}' after play mode: {ex.Message}");
				}
			}
		}

		private static string SafeSnapshot(DataAsset asset)
		{
			try
			{
				return Json.ToJson(asset, new JsonSettings { TypeNameHandling = TypeNameHandling.None });
			}
			catch (Exception ex)
			{
				Debug.Warn($"[DataAssetCache] Could not snapshot '{asset.SourcePath}': {ex.Message}");
				return null;
			}
		}

		private static void CopyInto(DataAsset source, DataAsset destination)
		{
			try
			{
				var json = Json.ToJson(source, new JsonSettings { TypeNameHandling = TypeNameHandling.None });
				Json.FromJsonOverwrite(json, destination);
			}
			catch (Exception ex)
			{
				Debug.Warn($"[DataAssetCache] Could not apply reload for '{source.SourcePath}': {ex.Message}");
			}
		}
	}
}
