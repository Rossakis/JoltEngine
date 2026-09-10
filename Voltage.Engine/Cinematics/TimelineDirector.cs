using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Voltage.Serialization;

namespace Voltage.Cinematics
{
	/// <summary>
	/// Plays a reusable <see cref="TimelineAsset"/> in a scene. The core split is between <b>evaluable</b> state (parameter tracks + spawnable reconciliation
	/// - pure functions of time, so seek/scrub are deterministic) and <b>imperative</b> events (fired once
	/// when the playhead crosses them forward).
	/// </summary>
	[ComponentId("timeline_director")]
	public partial class TimelineDirector : Component, IUpdatable, ITimelineContext
	{
		/// <summary>The reusable .vtimeline asset this director plays.</summary>
		[AssetType(typeof(TimelineAsset))]
		public AssetReference Timeline;

		/// <summary>Role → scene entity for pre-existing actors. Spawnables bind themselves at runtime.</summary>
		public List<RoleBinding> Bindings = new();

		public WrapMode Wrap = WrapMode.Hold;

		/// <summary>Playback rate multiplier (1 = normal, 0.5 = slow-mo, 2 = double).</summary>
		public float Speed = 1f;

		/// <summary>Use unscaled time so the cutscene plays even when the game is time-scaled/paused.</summary>
		public bool UseUnscaledTime;

		public bool PlayOnStart;

		// Runtime only (private → not serialized by the ComponentData generator).

		private TimelineAsset _asset;
		private DirectorState _state = DirectorState.Stopped;
		private float _playhead;
		private int _direction = 1; // for PingPong
		private readonly Dictionary<string, Entity> _resolvedRoles = new();
		private readonly Dictionary<TimelineSpawnClip, Entity> _spawns = new();
		private readonly HashSet<TimelineEventClip> _begun = new();
		private readonly HashSet<TimelineEventClip> _ended = new();
		private readonly StateSnapshot _snapshot = new();
		private readonly HashSet<string> _warned = new();

		public DirectorState State => _state;
		public float PlayheadTime => _playhead;
		public float Duration => _asset?.Duration ?? 0f;
		public TimelineAsset Asset => _asset;

		/// <summary>Fires once when playback ends normally or via <see cref="Skip"/>/<see cref="Stop"/>.</summary>
		public event Action OnFinished;
		/// <summary>Fires when playback is aborted via <see cref="Cancel"/> (pre-cutscene state restored).</summary>
		public event Action OnCancelled;
		/// <summary>Fires for broadcast events (name + args) — the decoupled hook for game code.</summary>
		public event Action<string, TimelineArg[]> OnSignal;

		#region ITimelineContext

		Scene ITimelineContext.Scene => Entity?.Scene;
		Entity ITimelineContext.ResolveRole(string role) => ResolveRole(role);

		#endregion

		#region Lifecycle

		public override void OnStart()
		{
			if (PlayOnStart)
				Play();
		}

		public override void OnRemovedFromEntity()
		{
			// Don't leak spawnables if the director's entity is destroyed mid-cutscene.
			DespawnAll(includePersistent: true);
		}

		#endregion

		#region Public control API

		public void Play()
		{
			EnsureAsset();
			if (_asset == null)
			{
				Debug.Warn("[TimelineDirector] Play() with no resolvable timeline asset.");
				return;
			}

			if (_state == DirectorState.Paused)
			{
				_state = DirectorState.Playing;
				return;
			}

			ResolveBindings();
			ValidateOnPlay();
			CaptureSnapshot();
			_begun.Clear();
			_ended.Clear();
			_playhead = 0f;
			_direction = 1;
			_state = DirectorState.Playing;

			EvaluateState(0f);
			FireEventsAtStart();          // events sitting exactly at t=0
		}

		public void Pause()
		{
			if (_state == DirectorState.Playing)
				_state = DirectorState.Paused;
		}

		public void Resume()
		{
			if (_state == DirectorState.Paused)
				_state = DirectorState.Playing;
		}

		/// <summary>
		/// Jumps to a named marker.
		/// </summary>
		public bool SeekToMarker(string markerName)
		{
			EnsureAsset();
			var marker = _asset?.FindMarker(markerName);
			if (marker == null)
			{
				Debug.Warn($"[TimelineDirector] No marker named '{markerName}' on '{Entity?.Name}'.");
				return false;
			}

			Seek(marker.Time);
			return true;
		}

		/// <summary>Programmatic time jump (rewind, checkpoint). Pure state — fires NO events.</summary>
		public void Seek(float time)
		{
			EnsureAsset();
			if (_asset == null)
				return;
			if (_resolvedRoles.Count == 0)
				ResolveBindings();

			_playhead = Math.Clamp(time, 0f, Duration);
			NotifyInterrupted();
			EvaluateState(_playhead);
		}

		/// <summary>
		/// Editor scrubbing / preview: applies pure state at <paramref name="time"/> without advancing or
		/// firing events. Re-resolves bindings each call since the designer may be rebinding roles.
		/// </summary>
		public void Evaluate(float time)
		{
			EnsureAsset();
			if (_asset == null)
				return;

			ResolveBindings();
			_playhead = Math.Clamp(time, 0f, Duration);
			EvaluateState(_playhead);
		}

		/// <summary>
		/// Player skipped the cutscene: fire the pending <see cref="SkipBehavior.FireImmediately"/> events
		/// in order (so consequential state applies), snap parameters + spawnables to the end, then finish.
		/// </summary>
		public void Skip()
		{
			if (_state == DirectorState.Stopped || _asset == null)
				return;

			foreach (var e in _asset.EventsInOrder())
			{
				if (e.OnSkip != SkipBehavior.FireImmediately)
					continue;

				if (!_begun.Contains(e) && e.Time > _playhead)
					FireBegin(e);
				if (e.Duration > 0f && _begun.Contains(e) && !_ended.Contains(e))
					FireEnd(e);   // ranged consequential event → apply its net effect
			}

			EvaluateState(Duration);
			_playhead = Duration;
			Finish();
		}

		/// <summary>End normally: snap to the final frame, clean up temporaries, fire <see cref="OnFinished"/>.</summary>
		public void Stop()
		{
			if (_state == DirectorState.Stopped)
				return;

			if (_asset != null)
			{
				EvaluateState(Duration);
				_playhead = Duration;
			}
			Finish();
		}

		/// <summary>Abort (e.g. the player died mid-cutscene): restore pre-cutscene state, despawn everything.</summary>
		public void Cancel()
		{
			if (_state == DirectorState.Stopped)
				return;

			NotifyInterrupted();
			_snapshot.Restore();
			DespawnAll(includePersistent: true);
			_state = DirectorState.Stopped;
			OnCancelled?.Invoke();
		}

		/// <summary>Coroutine helper: <c>yield return director.PlayAndWait();</c> resumes when playback ends.</summary>
		public IEnumerator PlayAndWait()
		{
			Play();
			while (_state != DirectorState.Stopped)
				yield return null;
		}

		/// <summary>Directly assigns the asset (editor authoring / tests), bypassing file resolution.</summary>
		public void SetAsset(TimelineAsset asset) => _asset = asset;

		#endregion

		#region Core loop

		public void Update()
		{
			if (_state != DirectorState.Playing || _asset == null)
				return;

			var delta = (UseUnscaledTime ? Utils.Time.UnscaledDeltaTime : Utils.Time.DeltaTime) * Speed;
			if (delta == 0f)
				return;

			var prev = _playhead;
			var next = Advance(prev, delta, out var wrapped, out var finished);

			if (wrapped) // Loop/PingPong cycle boundary — let events fire again next cycle.
			{
				_begun.Clear();
				_ended.Clear();
			}

			EvaluateState(next);
			FireEventsBetween(prev, next);
			_playhead = next;

			if (finished)
				Finish();
		}

		/// <summary>Logs what <see cref="Validate"/> finds, once per distinct problem set, at <see cref="Play"/>.</summary>
		private void ValidateOnPlay()
		{
			var problems = Validate();
			if (problems.Count == 0)
				return;

			WarnOnce($"validate:{_asset.GetHashCode()}:{problems.Count}",
				$"[TimelineDirector] '{Entity?.Name}' has {problems.Count} unresolved timeline reference(s):" +
				Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", problems));
		}

		/// <summary>Everything this timeline references that will not resolve with the current bindings; empty when it would play cleanly.</summary>
		public List<string> Validate()
		{
			var problems = new List<string>();
			EnsureAsset();
			if (_asset == null)
				return problems;
			ResolveBindings();

			foreach (var role in _asset.Roles)
			{
				if (role == null || string.IsNullOrEmpty(role.Name))
					continue;

				if (_resolvedRoles.ContainsKey(role.Name))
					continue;

				var spawned = false;
				foreach (var spawn in _asset.SpawnClips)
				{
					if (spawn != null && spawn.SpawnRole == role.Name)
					{
						spawned = true;
						break;
					}
				}

				if (!spawned)
					problems.Add($"role '{role.Name}' is not bound to an entity.");
			}

			foreach (var binding in Bindings)
			{
				if (binding == null || string.IsNullOrEmpty(binding.Role))
					continue;

				if (ResolveBoundEntity(binding.Entity) == null)
					problems.Add($"role '{binding.Role}' is bound to an entity that no longer exists.");
				else if (_asset.Roles.TrueForAll(r => r?.Name != binding.Role))
					problems.Add($"binding for role '{binding.Role}' matches no role on the asset — it was probably renamed.");
			}

			foreach (var spawn in _asset.SpawnClips)
			{
				if (spawn != null && !spawn.Prefab.IsValid)
					problems.Add($"spawn '{spawn.SpawnRole}' has no prefab assigned.");
			}

			foreach (var track in _asset.ParameterTracks)
				track?.Validate(this, problems);

			var contentEnd = _asset.ContentEndTime();
			if (contentEnd > _asset.Duration + 0.001f)
				problems.Add($"content runs to {contentEnd:0.00}s but Length is {_asset.Duration:0.00}s — the rest never plays.");

			return problems;
		}

		/// <summary>
		/// Logs a warning the first time a given key is seen.
		/// </summary>
		private void WarnOnce(string key, string message)
		{
			if (!_warned.Add(key))
				return;

			Debug.Warn(message);
		}

		/// <summary>Advances the playhead honoring the wrap mode; reports cycle wraps and end-of-timeline.</summary>
		private float Advance(float from, float delta, out bool wrapped, out bool finished)
		{
			wrapped = false;
			finished = false;

			var duration = Duration;
			if (duration <= 0f)
			{
				finished = true;
				return 0f;
			}

			var t = from + delta * _direction;

			switch (Wrap)
			{
				case WrapMode.Loop:
					if (t >= duration) { t -= duration; wrapped = true; }
					else if (t < 0f) { t += duration; wrapped = true; }
					return Math.Clamp(t, 0f, duration);

				case WrapMode.PingPong:
					if (t >= duration) { t = duration - (t - duration); _direction = -1; wrapped = true; }
					else if (t <= 0f) { t = -t; _direction = 1; wrapped = true; }
					return Math.Clamp(t, 0f, duration);

				case WrapMode.Hold:
				case WrapMode.Stop:
				default:
					if (t >= duration) { finished = true; return duration; }
					return t < 0f ? 0f : t;
			}
		}

		/// <summary>Tells every track to silence or reset whatever it started on a forward crossing.</summary>
		private void NotifyInterrupted()
		{
			var tracks = _asset?.ParameterTracks;
			if (tracks == null)
				return;

			for (var i = 0; i < tracks.Count; i++)
				tracks[i]?.OnPlaybackInterrupted(this);
		}

		/// <summary>Pure function of time: reconcile spawnables, then sample + apply every parameter track.</summary>
		private void EvaluateState(float time)
		{
			ReconcileSpawnables(time);

			var tracks = _asset.ParameterTracks;
			if (tracks != null)
			{
				for (var i = 0; i < tracks.Count; i++)
					tracks[i]?.Evaluate(time, this);
			}
		}

		private void FireEventsBetween(float prev, float next)
		{
			if (next <= prev) // events only fire on forward motion
				return;

			var tracks = _asset.ParameterTracks;
			if (tracks != null)
			{
				for (var i = 0; i < tracks.Count; i++)
					tracks[i]?.OnCrossForward(prev, next, this);
			}

			foreach (var e in _asset.EventsInOrder())
			{
				if (!_begun.Contains(e) && e.Time > prev && e.Time <= next)
					FireBegin(e);
				if (e.Duration > 0f && _begun.Contains(e) && !_ended.Contains(e) && e.Time + e.Duration <= next)
					FireEnd(e);
			}
		}

		private void FireEventsAtStart()
		{
			foreach (var e in _asset.EventsInOrder())
			{
				if (e.Time <= 0f && !_begun.Contains(e))
					FireBegin(e);
			}
		}

		private void FireBegin(TimelineEventClip e)
		{
			_begun.Add(e);
			if (!string.IsNullOrEmpty(e.BroadcastMessage))
				OnSignal?.Invoke(e.BroadcastMessage, e.Args);
			if (!string.IsNullOrEmpty(e.BeginMethod))
				Dispatch(e, e.BeginMethod);
		}

		private void FireEnd(TimelineEventClip e)
		{
			_ended.Add(e);
			if (!string.IsNullOrEmpty(e.EndMethod))
				Dispatch(e, e.EndMethod);
		}

		private void Dispatch(TimelineEventClip e, string method)
		{
			if (string.IsNullOrEmpty(e.TargetRole) || string.IsNullOrEmpty(e.TargetComponentId))
				return;

			var entity = ResolveRole(e.TargetRole);
			if (entity == null)
			{
				WarnOnce($"event-role:{e.TargetRole}:{method}",
					$"[TimelineDirector] Event '{e.Name ?? method}' targets role '{e.TargetRole}', which is not " +
					"bound to any entity — the event did nothing.");
				return;
			}

			var component = ResolveComponent(entity, e.TargetComponentId);
			if (component == null)
			{
				WarnOnce($"event-component:{e.TargetComponentId}:{method}",
					$"[TimelineDirector] Event '{e.Name ?? method}': entity '{entity.Name}' has no component " +
					$"with id '{e.TargetComponentId}'.");
				return;
			}

			if (!TimelineDispatch.TryInvoke(e.TargetComponentId, method, component, e.Args))
			{
				WarnOnce($"event-method:{e.TargetComponentId}.{method}",
					$"[TimelineDirector] Event '{e.Name ?? method}': no [TimelineEvent] method " +
					$"'{method}' on component '{e.TargetComponentId}'. It may have been renamed or removed.");
			}
		}

		private void Finish()
		{
			DespawnAll(includePersistent: false);   // temporaries go; KeepAfterTimeline spawns stay
			_state = DirectorState.Stopped;
			OnFinished?.Invoke();
		}

		#endregion

		#region Roles, spawnables, snapshot

		private Entity ResolveRole(string role)
		{
			if (string.IsNullOrEmpty(role))
				return null;
			return _resolvedRoles.TryGetValue(role, out var e) ? e : null;
		}

		Component ITimelineContext.ResolveComponent(string role, string componentId) =>
			ResolveComponent(ResolveRole(role), componentId);

		void ITimelineContext.RaiseSignal(string name, TimelineArg[] args) => OnSignal?.Invoke(name, args);

		private Component ResolveComponent(Entity entity, string componentId)
		{
			if (entity == null || string.IsNullOrEmpty(componentId))
				return null;
			return ComponentIdRegistry.TryGetType(componentId, out var type) ? entity.GetComponent(type) : null;
		}

		private void ResolveBindings()
		{
			// Keep spawnable-provided roles; only rebuild the pre-bound actor entries.
			foreach (var b in Bindings)
			{
				if (b == null || string.IsNullOrEmpty(b.Role))
					continue;
				var e = ResolveBoundEntity(b.Entity);
				if (e != null)
					_resolvedRoles[b.Role] = e;
			}
		}

		private Entity ResolveBoundEntity(EntityReference reference)
		{
			var scene = Entity?.Scene;
			if (scene == null || !reference.IsValid)
				return null;

			var id = reference.GetPersistentId();
			var found = id != Guid.Empty ? scene.FindEntityByPersistentId(id) : null;
			if (found == null && !string.IsNullOrEmpty(reference.EntityName))
				found = scene.FindEntity(reference.EntityName);   // fallback by name
			return found;
		}

		private void ReconcileSpawnables(float time)
		{
			var clips = _asset.SpawnClips;
			if (clips == null || clips.Count == 0)
				return;

			var scene = Entity?.Scene;
			if (scene == null)
				return;

			for (var i = 0; i < clips.Count; i++)
			{
				var clip = clips[i];
				if (clip == null || string.IsNullOrEmpty(clip.SpawnRole))
					continue;

				var shouldExist = time >= clip.Time && time < clip.Time + clip.Duration;
				var live = _spawns.TryGetValue(clip, out var inst) && inst != null;

				if (shouldExist && !live)
				{
					var spawned = clip.Prefab.Instantiate(scene);
					if (spawned != null)
					{
						_spawns[clip] = spawned;
						_resolvedRoles[clip.SpawnRole] = spawned;   // now targetable by other tracks
					}
				}
				else if (!shouldExist && live)
				{
					inst.Destroy();
					_spawns.Remove(clip);
					_resolvedRoles.Remove(clip.SpawnRole);
				}
			}
		}

		private void DespawnAll(bool includePersistent)
		{
			if (_spawns.Count == 0)
				return;

			var toRemove = new List<TimelineSpawnClip>();
			foreach (var kv in _spawns)
			{
				if (!includePersistent && kv.Key.KeepAfterTimeline)
					continue;
				kv.Value?.Destroy();
				_resolvedRoles.Remove(kv.Key.SpawnRole);
				toRemove.Add(kv.Key);
			}

			for (var i = 0; i < toRemove.Count; i++)
				_spawns.Remove(toRemove[i]);
		}

		private void CaptureSnapshot()
		{
			_snapshot.Clear();

			// Snapshot each pre-bound entity's transform + enabled state — the common things a timeline
			// moves. Parameter tracks add any extra fields they touch (alpha, sprite frame, …).
			foreach (var e in _resolvedRoles.Values)
			{
				if (e == null)
					continue;
				var entity = e;
				var pos = entity.Position;
				var rot = entity.Rotation;
				var scale = entity.Scale;
				var enabled = entity.Enabled;
				_snapshot.Add(() =>
				{
					entity.Position = pos;
					entity.Rotation = rot;
					entity.Scale = scale;
					entity.Enabled = enabled;
				});
			}

			var tracks = _asset.ParameterTracks;
			if (tracks != null)
			{
				for (var i = 0; i < tracks.Count; i++)
					tracks[i]?.CaptureRestoreState(_snapshot, this);
			}
		}

		#endregion

		#region Asset resolution

		private void EnsureAsset()
		{
			_asset ??= TryLoadAsset();
		}

		/// <summary>
		/// Best-effort load of the .vtimeline JSON from the resolved asset path. The full content-pipeline
		/// integration (baked manifest, polymorphic track deserialization) lands in a later milestone; for
		/// now this covers editor + direct-path cases.
		/// </summary>
		private TimelineAsset TryLoadAsset()
		{
			if (!Timeline.IsValid)
				return null;   // no asset assigned yet — not an error

			var path = Timeline.ResolvePath();
			if (string.IsNullOrEmpty(path))
			{
				WarnOnce($"timeline-unresolved:{Timeline.AssetGuid}",
					$"[TimelineDirector] '{Entity?.Name}' references timeline {Timeline} but it could not be " +
					"resolved. The file may have been deleted, or the asset manifest may be stale — reopen the " +
					"project in the editor to regenerate it.");
				return null;
			}

			if (!File.Exists(path))
			{
				WarnOnce($"timeline-missing:{path}",
					$"[TimelineDirector] '{Entity?.Name}': timeline file '{path}' does not exist.");
				return null;
			}

			try
			{
				return TimelineAssetIO.Load(path);
			}
			catch (Exception ex)
			{
				Debug.Warn($"[TimelineDirector] Failed to load timeline '{path}': {ex.Message}");
				return null;
			}
		}

		#endregion
	}
}
