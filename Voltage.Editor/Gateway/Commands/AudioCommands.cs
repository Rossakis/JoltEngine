using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Voltage.Audio;
using Voltage.Editor.Undo.Core;
using Voltage.Editor.Undo.EntityActions;
using Voltage.Gateway;
using static Voltage.Editor.Gateway.GatewayValues;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Audio zones in the scene, the mixer buses, and the audio engine's state.</summary>
internal static class AudioCommands
{
	private static readonly Dictionary<string, Type> ZoneTypes = new(StringComparer.OrdinalIgnoreCase)
	{
		["ambience"] = typeof(AmbienceZoneComponent),
		["music"] = typeof(MusicZoneComponent),
		["reverb"] = typeof(ReverbZoneComponent),
		["snapshot"] = typeof(MixerSnapshotZoneComponent),
		["crowd"] = typeof(CrowdEmitterComponent),
		["ducking"] = typeof(DialogueDuckingComponent)
	};

	public static void Register(GatewayCommandTable table)
	{
		table.Add("audio.zones", "Every audio zone and emitter in the scene with its trigger bounds and settings.", (_, _) =>
		{
			var scene = Core.Scene ?? throw new GatewayException("no scene loaded");
			var zones = new List<object>();
			foreach (var entity in scene.Entities)
				foreach (var component in entity.GetComponents<Component>())
				{
					var kind = ZoneTypes.FirstOrDefault(z => z.Value.IsInstanceOfType(component)).Key;
					if (kind == null)
						continue;
					var collider = entity.GetComponent<Collider>();
					zones.Add(new
					{
						entity = entity.Id,
						name = entity.Name,
						type = kind,
						component = component.GetType().Name,
						enabled = component.Enabled && entity.Enabled,
						position = new { x = entity.Transform.Position.X, y = entity.Transform.Position.Y },
						trigger = collider == null ? null : new { collider = collider.GetType().Name, isTrigger = collider.IsTrigger, bounds = Bounds(collider.Bounds) },
						values = Snapshot(component)
					});
				}
			return zones;
		}).ReadOnly();

		table.Add("audio.zone.types", "Zone types audio.zone.create accepts and the members each exposes.", (_, _) =>
			ZoneTypes.Select(z => new { type = z.Key, component = z.Value.FullName, members = Members(z.Value).Select(m => m.Name).ToList() }).ToList()).ReadOnly();

		table.Add("audio.zone.create", "Add an entity carrying an audio zone with a trigger box or circle collider; 'values' sets the zone's fields (undoable as one entity creation).", (args, ctx) =>
		{
			var scene = Core.Scene ?? throw new GatewayException("no scene loaded");
			var typeKey = args.Require("type");
			if (!ZoneTypes.TryGetValue(typeKey, out var type))
				throw new GatewayException($"unknown zone type '{typeKey}'; see audio.zone.types");

			var entity = new Entity("Zone", Entity.InstanceType.Serialized);
			entity.Name = scene.GetUniqueEntityName(args.String("name", $"{typeKey} Zone"), entity);
			entity.Transform.Position = new Vector2(args.Float("x"), args.Float("y"));
			scene.AddEntity(entity);

			var needsTrigger = type != typeof(CrowdEmitterComponent) && type != typeof(DialogueDuckingComponent);
			if (needsTrigger || args.Has("radius") || args.Has("width"))
			{
				Collider collider = args.Has("radius")
					? new CircleCollider(Math.Max(1f, args.Float("radius")))
					: new BoxCollider(Math.Max(1f, args.Float("width", 128)), Math.Max(1f, args.Float("height", 128)));
				collider.IsTrigger = true;
				collider.SetSerialized(true);
				entity.AddComponent(collider);
			}

			var zone = (Component)Activator.CreateInstance(type);
			zone.SetSerialized(true);
			entity.AddComponent(zone);
			if (args.TryGet("values", out var values))
			{
				if (values.ValueKind != JsonValueKind.Object)
					throw new GatewayException("'values' must be an object");
				foreach (var property in values.EnumerateObject())
					Set(zone, property.Name, property.Value, false, null);
			}

			var description = $"Create {typeKey} zone {entity.Name}";
			EditorChangeTracker.PushUndo(new EntityCreateDeleteUndoAction(scene, entity, wasCreated: true, description), entity, description);
			var pane = ctx.ImGui().SceneGraphWindow?.EntityPane;
			pane?.SetSelectedEntity(entity, ctrlDown: false);
			ctx.ImGui().MainEntityInspectorWindow?.DelayedSetEntity(entity);
			return EntityCommands.Detail(entity);
		}, P.Enum("type", "Zone kind", ZoneTypes.Keys.ToArray(), required: true), P.Str("name", "Entity name; default '<type> Zone'"), P.Float("x", "World position", 0f), P.Float("y", "World position", 0f), P.Float("width", "Trigger box width; default 128"), P.Float("height", "Trigger box height; default 128"), P.Float("radius", "Trigger circle radius instead of a box"), P.Obj("values", "Zone fields to set, e.g. {\"Volume\":0.5,\"Bus\":\"Ambience\"}"));

		table.Add("audio.mixer", "Mixer buses with volume, mute, solo and effective gain.", (_, _) =>
		{
			var audio = RequireAudio();
			return audio.Mixer.Buses.Select(b => Bus(audio, b)).ToList();
		}).ReadOnly();

		table.Add("audio.mixer.set", "Set a bus's volume, mute or solo. Runtime state only: it is not saved with the scene.", (args, _) =>
		{
			var audio = RequireAudio();
			var bus = audio.Mixer.GetBus(args.Require("bus")) ?? throw new GatewayException($"unknown bus '{args.Require("bus")}'; see audio.mixer");
			if (args.Has("volume"))
				bus.Volume = MathHelper.Clamp(args.Float("volume"), 0f, 1f);
			if (args.Has("mute"))
				bus.Mute = args.Bool("mute");
			if (args.Has("solo"))
				bus.Solo = args.Bool("solo");
			return Bus(audio, bus);
		}, P.Str("bus", "Bus name, e.g. Master, Music, SFX", required: true), P.Float("volume", "0..1"), P.Bool("mute"), P.Bool("solo"));

		table.Add("audio.state", "Backend, voice budget, music state, listener and software-mixer statistics.", (_, _) =>
		{
			var audio = RequireAudio();
			var listener = Core.Scene?.Entities.FirstOrDefault(e => e.GetComponent<AudioListenerComponent>() != null);
			object stats = null;
			if (audio.TryGetSoftwareAudioStats(out var s))
				stats = new { s.ActiveVoices, s.MixAvgMs, s.MixPeakMs, s.BudgetMs, s.LoadPercent, s.Underruns, s.SampleRate };
			return new
			{
				backend = audio.Backend?.GetType().Name,
				maxVoices = audio.MaxVoices,
				musicPlaying = audio.IsMusicPlaying,
				reverbSupported = audio.ReverbSupported,
				playMode = !Core.IsEditMode,
				listener = listener == null ? null : new { entity = listener.Id, name = listener.Name, x = listener.Transform.Position.X, y = listener.Transform.Position.Y },
				stats,
				note = Core.IsEditMode ? "zones and sources only play in play mode" : null
			};
		}).ReadOnly();
	}

	private static AudioManager RequireAudio() => Core.Audio ?? throw new GatewayException("the audio manager is not running");

	private static object Bus(AudioManager audio, AudioBus bus) => new
	{
		name = bus.Name,
		parent = bus.Parent?.Name,
		volume = bus.Volume,
		mute = bus.Mute,
		solo = bus.Solo,
		effectiveGain = audio.Mixer.EffectiveGain(bus)
	};

	private static object Bounds(RectangleF r) => new { x = r.X, y = r.Y, width = r.Width, height = r.Height };
}
