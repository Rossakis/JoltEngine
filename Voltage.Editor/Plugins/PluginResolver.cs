using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Voltage.Editor.DebugUtils;

namespace Voltage.Editor.Plugins
{
	/// <summary>A validated manifest, the immutable directory to sync the payload from, and the pin data for the lockfile.</summary>
	public class ResolvedPlugin
	{
		public PluginManifest Manifest;

		/// <summary>Directory the payload is synced into PluginLibs from (cache entry, bundled folder, or dev folder).</summary>
		public string PayloadDir;

		/// <summary>"sha256:..." content hash; null for dev-mode path plugins.</summary>
		public string ContentHash;

		/// <summary>Resolved git commit SHA (git sources only).</summary>
		public string Commit;

		/// <summary>Dev-mode path plugin: unpinned, re-synced every open.</summary>
		public bool IsDev;

		/// <summary>False for anything built on this machine (bundled, or a local source checkout): .NET embeds absolute source paths, so the hash differs per machine and pinning it would churn the lockfile or hard-fail a teammate. Those pin on version; the hash is still used locally as PluginSync's skip-marker.</summary>
		public bool IsPinnable = true;
	}

	/// <summary>Thrown when a plugin cannot be acquired or fails pin verification. Message is user-facing.</summary>
	public class PluginResolveException : Exception
	{
		public PluginResolveException(string message) : base(message)
		{
		}
	}

	/// <summary>Acquires plugin packages into the immutable per-user cache and verifies them against the lockfile. Sources: bundled, local path, git URL (pinned ref), https zip.</summary>
	public static class PluginResolver
	{
		public const string BundledPluginsFolderName = "BundledPlugins";

		/// <summary>Resolves one plugins.json entry. With a lockEntry the payload must match its pins; allowRepin accepts new content for a user-driven update. allowSourceBuild lets a local source checkout be built here, which only the install worker does because it takes minutes.</summary>
		public static ResolvedPlugin Resolve(ProjectPluginEntry entry, PluginLockEntry lockEntry, string projectPath,
			bool allowRepin = false, bool allowSourceBuild = false)
		{
			if (entry.Source != null && entry.Source.IsUnset)
			{
				throw new PluginResolveException(
					$"This project uses '{entry.Id}', but no one has published it anywhere this editor can fetch " +
					"from - it exists as a local folder on whoever added it. Ask them to publish it (Plugin " +
					"Manager > Publish New Version, then \"Declare for the team\") or to vendor it into the " +
					"repository. If it is yours, add your copy from Plugin Manager > Add Plugin.");
			}

			if (entry.Source == null || !entry.Source.IsValid())
				throw new PluginResolveException($"Plugin '{entry.Id}' has an invalid source in plugins.json - exactly one of Bundled/Git/Zip/Path must be set.");

			if (entry.Source.Bundled)
				return ResolveBundled(entry, lockEntry);

			if (!string.IsNullOrWhiteSpace(entry.Source.Path))
				return ResolvePath(entry, lockEntry, projectPath, allowRepin, allowSourceBuild);

			if (!string.IsNullOrWhiteSpace(entry.Source.Git))
				return ResolveGit(entry, lockEntry, allowRepin, allowSourceBuild);

			return ResolveZip(entry, lockEntry, allowRepin);
		}

		#region Bundled

		/// <summary>Bundled plugins are versioned by the editor, so a hash change after an editor update refreshes the lock instead of failing. Their payload is compiled per machine and never pinned.</summary>
		private static ResolvedPlugin ResolveBundled(ProjectPluginEntry entry, PluginLockEntry lockEntry)
		{
			var packageRoot = FindBundledPackage(entry.Id);
			if (packageRoot == null)
			{
				var listing = PluginRegistryIndex.Entries.FirstOrDefault(
					e => string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase));

				if (listing != null && !string.IsNullOrWhiteSpace(listing.Git))
				{
					throw new PluginResolveException(
						$"'{entry.Id}' is no longer bundled with the editor - it now ships separately. Remove the " +
						$"entry and reinstall it from Plugin Manager > Browse Plugins, or change its source in " +
						$"plugins.json to {{ \"Git\": \"{listing.Git}\" }}.");
				}

				throw new PluginResolveException(
					$"Bundled plugin '{entry.Id}' not found under {GetBundledPluginsRoot()}. The editor install " +
					"may be incomplete, or the plugin may have moved to its own repository - check Plugin " +
					"Manager > Browse Plugins.");
			}

			var manifest = PluginManifest.LoadFrom(packageRoot);
			EnsureManifestIdMatches(entry, manifest);

			if (lockEntry != null && lockEntry.Version != manifest.Version)
				PluginLog.Log($"Bundled plugin '{entry.Id}' changed with the editor ({lockEntry.Version} -> {manifest.Version}); lock updated.");

			return new ResolvedPlugin
			{
				Manifest = manifest,
				PayloadDir = packageRoot,
				ContentHash = PluginCache.ComputeContentHash(packageRoot),
				IsPinnable = false,
			};
		}

		public static string GetBundledPluginsRoot()
		{
			return Path.Combine(AppContext.BaseDirectory, BundledPluginsFolderName);
		}

		/// <summary>Ids of all plugins bundled with this editor build, for the Add-bundled dropdown.</summary>
		public static IReadOnlyList<string> GetAvailableBundledPluginIds()
		{
			var ids = new List<string>();
			var root = GetBundledPluginsRoot();
			if (!Directory.Exists(root))
				return ids;

			foreach (var dir in Directory.GetDirectories(root))
			{
				var manifestPath = Path.Combine(dir, PluginManifest.FileName);
				if (!File.Exists(manifestPath))
					continue;

				try
				{
					var manifest = Voltage.Persistence.Json.FromJson<PluginManifest>(File.ReadAllText(manifestPath));
					if (!string.IsNullOrWhiteSpace(manifest?.Id))
						ids.Add(manifest.Id);
				}
				catch (Exception ex)
				{
					PluginLog.Warn($"Skipping bundled plugin folder '{dir}': {ex.Message}");
				}
			}

			return ids;
		}

		/// <summary>Finds the bundled package folder whose plugin.json declares the given id.</summary>
		private static string FindBundledPackage(string id)
		{
			var root = GetBundledPluginsRoot();
			if (!Directory.Exists(root))
				return null;

			foreach (var dir in Directory.GetDirectories(root))
			{
				var manifestPath = Path.Combine(dir, PluginManifest.FileName);
				if (!File.Exists(manifestPath))
					continue;

				try
				{
					var manifest = Voltage.Persistence.Json.FromJson<PluginManifest>(File.ReadAllText(manifestPath));
					if (manifest != null && string.Equals(manifest.Id, id, StringComparison.OrdinalIgnoreCase))
						return dir;
				}
				catch (Exception ex)
				{
					PluginLog.Warn($"Skipping bundled plugin folder '{dir}': {ex.Message}");
				}
			}

			return null;
		}

		#endregion

		#region Local path

		private static ResolvedPlugin ResolvePath(ProjectPluginEntry entry, PluginLockEntry lockEntry, string projectPath,
			bool allowRepin, bool allowSourceBuild)
		{
			var sourceDir = entry.Source.Path;
			if (!System.IO.Path.IsPathRooted(sourceDir))
				sourceDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectPath, sourceDir));

			if (!Directory.Exists(sourceDir))
				throw new PluginResolveException($"Source folder not found: {sourceDir}");

			var build = PluginSourceBuild.EnsureBuilt(sourceDir, allowSourceBuild);

			PluginManifest manifest;
			try
			{
				manifest = PluginManifest.LoadFrom(sourceDir);
			}
			catch (PluginManifestException ex) when (build.IsSourceCheckout)
			{
				// "file 'lib/X.dll' not found in the package" is true and useless when the reason is that
				// nobody has built it yet. Say which it is, and what to run.
				var explanation = PluginSourceBuild.Explain(build, sourceDir);
				throw new PluginResolveException(
					explanation == null ? ex.Message : ex.Message + "\n\n" + explanation);
			}

			EnsureManifestIdMatches(entry, manifest);

			if (entry.Dev)
			{
				return new ResolvedPlugin
				{
					Manifest = manifest,
					PayloadDir = sourceDir,
					ContentHash = null,
					IsDev = true,
				};
			}

			// A source checkout carries assemblies this machine compiled, and .NET embeds absolute source
			// paths in them, so its hash is not reproducible anywhere else - exactly the reason bundled
			// plugins are not content-pinned either. Pinning it would hard-fail every teammate who opened
			// the project, on nothing worse than having built the same commit themselves.
			var isSourceCheckout = PluginSourceBuild.FindPackagingProject(sourceDir) != null;

			var hash = PluginCache.ComputeContentHash(sourceDir);
			if (!isSourceCheckout)
			{
				VerifyAgainstLock(entry, lockEntry, hash, allowRepin,
					$"Update the plugin via the Plugin Manager, or mark the entry \"Dev\": true in plugins.json to iterate unpinned.");
			}

			// Immutable copy into the cache so later restores don't depend on the folder still existing.
			// Cache is keyed by the MANIFEST id (authoritative), so this also works in discovery mode
			// where entry.Id is not known yet (the Add-plugin flow).
			if (!PluginCache.HasEntry(manifest.Id, manifest.Version, hash))
			{
				var staging = CreateStagingDir();
				PluginCache.CopyDirectory(sourceDir, staging);
				PluginCache.CommitToCache(staging, manifest.Id, manifest.Version, hash);
			}

			return new ResolvedPlugin
			{
				Manifest = manifest,
				PayloadDir = PluginCache.GetEntryPath(manifest.Id, manifest.Version, hash),
				ContentHash = hash,
				IsPinnable = !isSourceCheckout,
			};
		}

		#endregion

		#region Git / Zip (remote acquisition)

		/// <summary>Git acquisition through the user's git CLI and ambient credentials, so private repos work without the editor handling credentials. The ref is pinned to a commit SHA, so a force-pushed tag cannot silently change what teammates get.</summary>
		private static ResolvedPlugin ResolveGit(ProjectPluginEntry entry, PluginLockEntry lockEntry, bool allowRepin,
			bool allowSourceBuild)
		{
			var url = entry.Source.Git;

			if (TryUseCachedLockedPayload(entry, lockEntry, allowRepin, out var cached))
				return cached;

			string commit;
			if (!allowRepin && lockEntry != null && lockEntry.Source.Matches(entry.Source) && !string.IsNullOrEmpty(lockEntry.Commit))
				commit = lockEntry.Commit;
			else
				commit = ResolveGitRefToSha(entry.Id, url, entry.Source.Ref);

			// Source-built plugins are cached by commit; reuse that before cloning and building again.
			if (TryUseCachedSourceBuild(entry, lockEntry, commit, out var prebuilt))
				return prebuilt;

			var staging = CreateStagingDir();
			try
			{
				RunGit(entry.Id, staging, "init --quiet");
				RunGit(entry.Id, staging, $"remote add origin \"{url}\"");
				RunGit(entry.Id, staging, $"fetch --quiet --depth 1 origin {commit}");
				RunGit(entry.Id, staging, "checkout --quiet FETCH_HEAD");

				// Source checkouts gitignore their assemblies; build them so the checkout validates as a package.
				var build = PluginSourceBuild.EnsureBuilt(staging, allowSourceBuild);

				PluginManifest manifest;
				try
				{
					manifest = PluginManifest.LoadFrom(staging);
				}
				catch (PluginManifestException ex) when (build.IsSourceCheckout)
				{
					var explanation = PluginSourceBuild.Explain(build, staging, isClonedCheckout: true);
					throw new PluginResolveException(
						explanation == null ? ex.Message : ex.Message + "\n\n" + explanation);
				}

				EnsureManifestIdMatches(entry, manifest);

				// Locally built assemblies hash differently per machine, so key on the commit and leave the content unpinned.
				if (build.IsSourceCheckout)
				{
					return new ResolvedPlugin
					{
						Manifest = manifest,
						PayloadDir = PluginCache.CommitToCache(staging, manifest.Id, manifest.Version, commit),
						ContentHash = null,
						Commit = commit,
						IsPinnable = false,
					};
				}

				var hash = PluginCache.ComputeContentHash(staging);
				VerifyAgainstLock(entry, lockEntry, hash, allowRepin,
					"The pinned commit's content changed unexpectedly - this should not happen for an immutable SHA; delete the lock entry to re-resolve.");

				var cachePath = PluginCache.CommitToCache(staging, manifest.Id, manifest.Version, hash);

				return new ResolvedPlugin
				{
					Manifest = manifest,
					PayloadDir = cachePath,
					ContentHash = hash,
					Commit = commit,
				};
			}
			catch
			{
				TryDeleteStaging(staging);
				throw;
			}
		}

		/// <summary>Reuses a payload this machine already built for the same commit, so reopening a project does not rebuild it.</summary>
		private static bool TryUseCachedSourceBuild(ProjectPluginEntry entry, PluginLockEntry lockEntry, string commit,
			out ResolvedPlugin resolved)
		{
			resolved = null;

			// Only an entry pinned by commit alone was source-built; anything with a content hash is a package.
			if (string.IsNullOrEmpty(commit) || lockEntry?.ContentHash != null || string.IsNullOrEmpty(lockEntry?.Version))
				return false;

			if (!lockEntry.Source.Matches(entry.Source) || !PluginCache.HasEntry(entry.Id, lockEntry.Version, commit))
				return false;

			var cachePath = PluginCache.GetEntryPath(entry.Id, lockEntry.Version, commit);
			var manifest = PluginManifest.LoadFrom(cachePath);
			EnsureManifestIdMatches(entry, manifest);

			resolved = new ResolvedPlugin
			{
				Manifest = manifest,
				PayloadDir = cachePath,
				ContentHash = null,
				Commit = commit,
				IsPinnable = false,
			};
			return true;
		}

		/// <summary>Https zip acquisition, verified by sha256 content hash against the lockfile.</summary>
		private static ResolvedPlugin ResolveZip(ProjectPluginEntry entry, PluginLockEntry lockEntry, bool allowRepin)
		{
			var url = entry.Source.Zip;

			if (TryUseCachedLockedPayload(entry, lockEntry, allowRepin, out var cached))
				return cached;

			var staging = CreateStagingDir();
			var zipPath = staging + ".zip";
			try
			{
				DownloadZip(entry, url, zipPath);
				PluginDownloadContext.EnterWorking();

				System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, staging);

				// Accept both layouts: plugin.json at the archive root, or inside a single top-level
				// folder (the shape GitHub's "download as zip" produces).
				var packageRoot = staging;
				if (!File.Exists(Path.Combine(packageRoot, PluginManifest.FileName)))
				{
					var subDirs = Directory.GetDirectories(staging);
					if (subDirs.Length == 1 && File.Exists(Path.Combine(subDirs[0], PluginManifest.FileName)))
						packageRoot = subDirs[0];
				}

				var manifest = PluginManifest.LoadFrom(packageRoot);
				EnsureManifestIdMatches(entry, manifest);

				var hash = PluginCache.ComputeContentHash(packageRoot);
				VerifyAgainstLock(entry, lockEntry, hash, allowRepin,
					"The file served at the zip URL changed since it was locked. Update the plugin via the Plugin Manager if this is intentional.");

				var cachePath = PluginCache.CommitToCache(packageRoot, manifest.Id, manifest.Version, hash);
				if (packageRoot != staging)
					TryDeleteStaging(staging);

				return new ResolvedPlugin
				{
					Manifest = manifest,
					PayloadDir = cachePath,
					ContentHash = hash,
				};
			}
			catch
			{
				TryDeleteStaging(staging);
				throw;
			}
			finally
			{
				if (File.Exists(zipPath))
					File.Delete(zipPath);
			}
		}

		/// <summary>Uses the cached payload when the lock pins this exact source, without touching the network.</summary>
		private static bool TryUseCachedLockedPayload(ProjectPluginEntry entry, PluginLockEntry lockEntry, bool allowRepin, out ResolvedPlugin resolved)
		{
			resolved = null;

			if (allowRepin || lockEntry?.ContentHash == null || !lockEntry.Source.Matches(entry.Source))
				return false;

			if (!PluginCache.HasEntry(entry.Id, lockEntry.Version, lockEntry.ContentHash))
				return false;

			var cachePath = PluginCache.GetEntryPath(entry.Id, lockEntry.Version, lockEntry.ContentHash);
			var manifest = PluginManifest.LoadFrom(cachePath);
			EnsureManifestIdMatches(entry, manifest);

			resolved = new ResolvedPlugin
			{
				Manifest = manifest,
				PayloadDir = cachePath,
				ContentHash = lockEntry.ContentHash,
				Commit = lockEntry.Commit,
			};
			return true;
		}

		/// <summary>Resolves a git ref to a full commit SHA via git ls-remote. Tags beat branches; annotated tags prefer the peeled commit.</summary>
		private static string ResolveGitRefToSha(string pluginId, string url, string gitRef)
		{
			if (string.IsNullOrWhiteSpace(gitRef))
				throw new PluginResolveException($"Plugin '{pluginId}': git source needs a \"Ref\" (tag, branch, or commit SHA) to pin.");

			if (gitRef.Length == 40 && gitRef.All(Uri.IsHexDigit))
				return gitRef.ToLowerInvariant();

			var output = RunGitCapture(pluginId, null, $"ls-remote \"{url}\" \"{gitRef}\" \"{gitRef}^{{}}\" \"refs/tags/{gitRef}\" \"refs/tags/{gitRef}^{{}}\" \"refs/heads/{gitRef}\"");

			string best = null;
			foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			{
				var parts = line.Split('\t');
				if (parts.Length < 2 || parts[0].Length != 40)
					continue;

				var sha = parts[0];
				var refName = parts[1].Trim();

				if (refName.EndsWith("^{}", StringComparison.Ordinal))
					return sha.ToLowerInvariant();

				best ??= sha;
			}

			if (best == null)
				throw new PluginResolveException($"Plugin '{pluginId}': ref '{gitRef}' not found at {url} (checked tags, branches, and direct refs).");

			return best.ToLowerInvariant();
		}

		/// <summary>Runs git, surfacing stderr verbatim on failure (auth errors must reach the user unedited).</summary>
		private static void RunGit(string pluginId, string workingDir, string arguments)
		{
			RunGitCapture(pluginId, workingDir, arguments);
		}

		private static string RunGitCapture(string pluginId, string workingDir, string arguments)
		{
			var processInfo = new System.Diagnostics.ProcessStartInfo
			{
				FileName = "git",
				Arguments = arguments,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};
			if (workingDir != null)
				processInfo.WorkingDirectory = workingDir;

			using var process = System.Diagnostics.Process.Start(processInfo);
			if (process == null)
				throw new PluginResolveException($"Plugin '{pluginId}': could not start git. Is git installed and on PATH?");

			var stdout = process.StandardOutput.ReadToEndAsync();
			var stderr = process.StandardError.ReadToEndAsync();
			process.WaitForExit();

			if (process.ExitCode != 0)
			{
				throw new PluginResolveException(
					$"Plugin '{pluginId}': git {arguments.Split(' ')[0]} failed (exit {process.ExitCode}).\n{stderr.Result.Trim()}");
			}

			return stdout.Result;
		}

		private static void TryDeleteStaging(string staging)
		{
			try
			{
				if (Directory.Exists(staging))
					Directory.Delete(staging, recursive: true);
			}
			catch
			{
			}
		}

		#endregion

		#region Shared helpers

		private static void EnsureManifestIdMatches(ProjectPluginEntry entry, PluginManifest manifest)
		{
			if (string.IsNullOrEmpty(entry.Id))
				return;

			if (!string.Equals(entry.Id, manifest.Id, StringComparison.OrdinalIgnoreCase))
				throw new PluginResolveException(
					$"plugins.json entry '{entry.Id}' resolved to a package whose plugin.json declares id '{manifest.Id}'. The entry id must match the manifest.");
		}

		/// <summary>Acquired content must hash to what the lock recorded, so teammates and CI restore identical bytes. allowRepin accepts new content instead.</summary>
		private static void VerifyAgainstLock(ProjectPluginEntry entry, PluginLockEntry lockEntry, string actualHash, bool allowRepin, string remedyHint)
		{
			if (lockEntry?.ContentHash == null || allowRepin)
				return;

			if (!lockEntry.Source.Matches(entry.Source))
				return; // Source itself changed in plugins.json - treat as a new resolution, re-pin below.

			if (lockEntry.ContentHash != actualHash)
				throw new PluginResolveException(
					$"Plugin '{entry.Id}': content does not match plugins.lock.json (expected {lockEntry.ContentHash}, got {actualHash}). " + remedyHint);
		}

		/// <summary>Streams the archive to disk with progress and cancellation. The timeout is per read, not per transfer: a slow line is fine, a server that goes quiet is not.</summary>
		private static void DownloadZip(ProjectPluginEntry entry, string url, string zipPath)
		{
			var token = PluginDownloadContext.Token;

			using var http = new System.Net.Http.HttpClient
			{
				Timeout = TimeSpan.FromSeconds(60),
			};

			using var response = http
				.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, token)
				.GetAwaiter().GetResult();

			if (!response.IsSuccessStatusCode)
			{
				throw new PluginResolveException(
					$"Plugin '{entry.Id}': download failed ({(int)response.StatusCode} {response.ReasonPhrase}) from {url}");
			}

			var total = response.Content.Headers.ContentLength;
			PluginDownloadContext.Report(0, total);

			using var source = response.Content.ReadAsStreamAsync(token).GetAwaiter().GetResult();
			using var file = File.Create(zipPath);

			var buffer = new byte[81920];
			long read = 0;

			while (true)
			{
				token.ThrowIfCancellationRequested();

				var readTask = source.ReadAsync(buffer, 0, buffer.Length, token);
				if (!readTask.Wait(TimeSpan.FromSeconds(30), token))
				{
					throw new PluginResolveException(
						$"Plugin '{entry.Id}': the download stopped responding after {read:N0} bytes. " +
						"The server may be unreachable - try again.");
				}

				var count = readTask.Result;
				if (count <= 0)
					break;

				file.Write(buffer, 0, count);
				read += count;
				PluginDownloadContext.Report(read, total);
			}

			// A truncated transfer is otherwise only caught later as a corrupt archive, which reads as the
			// plugin being broken rather than the download being cut short.
			if (total.HasValue && read != total.Value)
			{
				throw new PluginResolveException(
					$"Plugin '{entry.Id}': download ended early ({read:N0} of {total.Value:N0} bytes). Try again.");
			}
		}

		internal static string CreateStagingDir()
		{
			var staging = Path.Combine(Path.GetTempPath(), "VoltagePluginStaging", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(staging);
			return staging;
		}

		#endregion
	}
}
