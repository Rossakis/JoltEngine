using System;
using System.Collections.Generic;
using System.IO;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Undo.Core;
using Voltage.Editor.Undo.EntityActions;
using Voltage.Editor.Utils;
using Voltage.Sprites;
using Voltage.Audio;
using Voltage.Editor.Serialization;
using PrefabData = Voltage.Data.PrefabData;

namespace Voltage.Editor.Assets
{
    /// <summary>
    /// Drop-handler delegate: called with a stable <see cref="AssetReference"/> for the
    /// dragged asset.  Handlers resolve the reference to an absolute path at drop time via
    /// <see cref="AssetDatabase.Resolve"/>, so a file moved between drag-start and drop-end
    /// still resolves correctly.
    ///
    /// The optional <paramref name="worldPosition"/> parameter carries the world-space
    /// spawn location when the drop originates from the game viewport.  When <c>null</c>
    /// the handler falls back to spawning at the camera centre (Scene Graph behaviour).
    /// </summary>
    public delegate void AssetDropAction(AssetReference reference, Microsoft.Xna.Framework.Vector2? worldPosition = null);

    /// <summary>Opens an asset activated in the Asset Browser, given its absolute path.</summary>
    public delegate void AssetOpenAction(string absolutePath);

    /// <summary>
    /// Broad classification of a project asset.
    /// Used to select the icon shown in the Asset Browser and to gate drag-drop behaviour.
    /// </summary>
    public enum AssetKind
    {
        Texture,
        Prefab,
        Scene,
        Script,
        Effect,
        Tiled,
        Audio,
        Timeline,
        Tileset,
        /// <summary>A <c>.vasset</c> data container (see <c>Voltage.Data.DataAsset</c>).</summary>
        Data,
        /// <summary>A file type contributed by a plugin. Carries its own icon via the descriptor.</summary>
        Custom,
        Unsupported,
    }

    /// <summary>
    /// Immutable descriptor for one asset type family (e.g. all PNG / Aseprite files).
    /// </summary>
    public sealed record AssetTypeDescriptor(
        /// <summary>Lower-case extensions that map to this type, including the dot (e.g. ".png").</summary>
        string[] Extensions,

        /// <summary>Relative path used to load the icon texture via <c>Core.Content.LoadTexture</c>.</summary>
        string IconPath,

        /// <summary>Semantic kind — drives icon fallback and drop factory selection.</summary>
        AssetKind Kind,

        /// <summary>
        /// Drop factory invoked when the user drops this asset into the scene.
        /// Receives an <see cref="AssetReference"/> (GUID + hint path); the handler resolves
        /// it to an absolute path via <see cref="AssetDatabase.Resolve"/> at drop time.
        /// <c>null</c> for kinds that cannot be dropped (Script, Effect, Tiled, Unsupported).
        /// </summary>
        AssetDropAction DropFactory = null
    )
    {
        /// <summary>What double-clicking does. Without one, activation falls back to the behaviour for its Kind, which is wrong for a plugin file type. Init-only rather than a constructor parameter, so adding it does not break plugins already compiled against this assembly.</summary>
        public AssetOpenAction Open { get; init; }
    }

    /// <summary>Maps extensions to descriptors, falling back to Unsupported. Plugins call Register from an IEditorPlugin; Version bumps each time so the Asset Browser can re-resolve files it indexed first.</summary>
    public static class AssetTypeRegistry
    {
        /// <summary>Bumped on every registration.</summary>
        public static int Version { get; private set; }

        private const string IconDir         = "DefaultContent/UI/RemixIcon/FileTypes/";
        private const string IconTexture     = IconDir + "Voltage-Aseprite.png"; 
        private const string IconPrefab      = IconDir + "Voltage-Prefab.png";
        private const string IconScene       = IconDir + "Voltage-Scene.png";
        private const string IconScript      = IconDir + "Voltage-Script.png";
        private const string IconUnsupported = IconDir + "Voltage-Unsupported-File.png";
        private const string IconAudio       = IconDir + "Voltage-Audio.png";

		private const string IconEffect = IconUnsupported;
        private const string IconTiled  = IconUnsupported;
        private const string IconData   = IconUnsupported;

        private const string IconTileset = "DefaultContent/UI/Custom/CursorSelection-UI-TileBrush.png";

        private static readonly AssetTypeDescriptor _fallback = new(
            Extensions: Array.Empty<string>(),
            IconPath: IconUnsupported,
            Kind: AssetKind.Unsupported
        );

        private static readonly Dictionary<string, AssetTypeDescriptor> _map =
            new(StringComparer.OrdinalIgnoreCase);

        static AssetTypeRegistry()
        {
            Register(new AssetTypeDescriptor(
                Extensions: new[] { ".png" },
                IconPath: IconTexture,
                Kind: AssetKind.Texture,
                DropFactory: DropHandlers.DropTexture
            ));

            // Aseprite drops as an ANIMATED sprite (SpriteAnimator with every tag loaded), not a flat sprite.
            Register(new AssetTypeDescriptor(
                Extensions: new[] { ".aseprite", ".ase" },
                IconPath: IconTexture,
                Kind: AssetKind.Texture,
                DropFactory: (reference, position) => DropHandlers.DropAsepriteAnimated(reference, position)
            ));

            Register(new AssetTypeDescriptor(
                // .vprefab is the engine's native format; .prefab kept for legacy compatibility.
                Extensions: new[] { ".vprefab", ".prefab" },
                IconPath: IconPrefab,
                Kind: AssetKind.Prefab,
                DropFactory: DropHandlers.DropPrefab
            ));

            Register(new AssetTypeDescriptor(
                Extensions: new[] { ".vscene" },
                IconPath: IconScene,
                Kind: AssetKind.Scene,
                DropFactory: DropHandlers.DropScene
            ));

            // Script / Effect / Tiled — not droppable in Phase 2.
            Register(new AssetTypeDescriptor(
                Extensions: new[] { ".cs" },
                IconPath: IconScript,
                Kind: AssetKind.Script
            ));

            Register(new AssetTypeDescriptor(
                Extensions: new[] { ".fx", ".fxc", ".fxb" },
                IconPath: IconEffect,
                Kind: AssetKind.Effect
            ));

            Register(new AssetTypeDescriptor(
                Extensions: new[] { ".tmx", ".tsx" },
                IconPath: IconTiled,
                Kind: AssetKind.Tiled
            ));

            Register(new AssetTypeDescriptor(
                Extensions: new[] { ".wav", ".ogg", ".mp3" },
                IconPath: IconAudio,
                Kind: AssetKind.Audio,
                DropFactory: DropHandlers.DropAudio
            ));

            // Dropping a ".vtimeline" asset into the scene spawns an entity with a
            // TimelineDirector component bound to it.
            Register(new AssetTypeDescriptor(
                Extensions: new[] { ".vtimeline" },
                IconPath: IconScene,
                Kind: AssetKind.Timeline,
                DropFactory: DropHandlers.DropTimeline
            ));

            Register(new AssetTypeDescriptor(
                Extensions: new[] { Voltage.Tilesets.TilesetAssetIO.FileExtension },
                IconPath: IconTileset,
                Kind: AssetKind.Tileset,
                DropFactory: DropHandlers.DropTileset
            ));

            Register(new AssetTypeDescriptor(
                Extensions: new[] { Voltage.Data.DataAssetIO.FileExtension },
                IconPath: IconData,
                Kind: AssetKind.Data
            ));
        }

        /// <summary>Registers a file-type family, replacing any previous descriptor for the same extensions.</summary>
        public static void Register(AssetTypeDescriptor descriptor)
        {
            if (descriptor?.Extensions == null)
                return;

            foreach (var ext in descriptor.Extensions)
            {
                if (!string.IsNullOrEmpty(ext))
                    _map[ext.StartsWith(".") ? ext : "." + ext] = descriptor;
            }

            Version++;
        }

        /// <summary>
        /// Returns the descriptor for the given file extension (with or without leading dot).
        /// Falls back to the <c>Unsupported</c> descriptor for unknown extensions.
        /// </summary>
        public static AssetTypeDescriptor Resolve(string extension)
        {
            if (string.IsNullOrEmpty(extension))
                return _fallback;

            if (extension[0] != '.')
                extension = '.' + extension;

            return _map.TryGetValue(extension, out var descriptor) ? descriptor : _fallback;
        }

        /// <summary>All registered descriptors (one per family, not per extension).</summary>
        public static IReadOnlyCollection<AssetTypeDescriptor> AllDescriptors => _map.Values;

        /// <summary>The fallback descriptor used for unknown extensions.</summary>
        public static AssetTypeDescriptor FallbackDescriptor => _fallback;
    }

    /// <summary>
    /// Static implementations of the per-kind drop factories.
    ///
    /// Each handler receives an <see cref="AssetReference"/> (GUID + hint path) and resolves
    /// it to an absolute path at drop time via <see cref="AssetDatabase.Resolve"/>.
    /// This means a file renamed or moved between drag-start and drop-end still resolves
    /// correctly as long as its <c>.meta</c> sidecar moved with it.
    ///
    /// All editor dependencies are resolved lazily at call-time via
    /// <c>Core.GetGlobalManager</c> and <c>Core.Scene</c>.
    /// </summary>
    internal static class DropHandlers
    {
        internal static void DropTexture(AssetReference reference, Microsoft.Xna.Framework.Vector2? worldPosition = null)
        {
            var absolutePath = ResolveOrLog(reference, "DropTexture");
            if (absolutePath == null) return;

            var scene = Core.Scene;
            if (scene == null)
            {
                EditorDebug.Log("DropTexture: no active scene.", "AssetBrowser");
                return;
            }

            // Convert absolute path to project-relative for cross-machine portability.
            string relativePath = ToProjectRelativePath(absolutePath);

            // Create the entity (mirrors SceneGraphWindow.CreateEmptyEntity).
            string baseName = Path.GetFileNameWithoutExtension(absolutePath);
            string entityName = scene.GetUniqueEntityName(baseName, null);
            var entity = new Entity(entityName, Entity.InstanceType.Serialized);
            // Spawn at the supplied world position (game-view drop) or camera centre (scene-graph drop).
            entity.Transform.Position = worldPosition ?? scene.Camera.Transform.Position;

            scene.AddEntity(entity);

            // Add a SpriteRenderer and load the texture.
            var sr = entity.AddComponent<SpriteRenderer>();

            // AddComponent leaves IsSerialized false and the scene save skips those, so without this the
            // dropped sprite comes back as an empty entity.
            sr.SetSerialized(true);

            var ext = Path.GetExtension(absolutePath).ToLowerInvariant();
            try
            {
                if (ext == ".png")
                    sr.LoadPngFile(relativePath);
                else if (ext == ".aseprite" || ext == ".ase")
                    sr.LoadAsepriteFile(relativePath, layerName: null, frameNumber: 0);
            }
            catch (Exception ex)
            {
                EditorDebug.Log($"DropTexture: failed to load '{relativePath}': {ex.Message}", "AssetBrowser");
            }

            EditorChangeTracker.PushUndo(
                new EntityCreateDeleteUndoAction(scene, entity, wasCreated: true,
                    $"Create Sprite Entity '{entityName}'"),
                entity,
                $"Create Sprite Entity '{entityName}'"
            );

            // Select the new entity in the editor.
            var imgr = Core.GetGlobalManager<ImGuiManager>();
            imgr?.SceneGraphWindow.EntityPane.SetSelectedEntity(entity, false);
            imgr?.MainEntityInspectorWindow.DelayedSetEntity(entity);
        }

        /// <summary>
        /// Spawns an entity with a <see cref="SpriteAnimator"/> bound to the dropped Aseprite. Every tag loads as
        /// its own animation; the first tag becomes the serialized default that plays on scene load.
        /// </summary>
        internal static Entity DropAsepriteAnimated(AssetReference reference, Microsoft.Xna.Framework.Vector2? worldPosition = null)
        {
            var absolutePath = ResolveOrLog(reference, "DropAsepriteAnimated");
            if (absolutePath == null) return null;

            var scene = Core.Scene;
            if (scene == null)
            {
                EditorDebug.Log("DropAsepriteAnimated: no active scene.", "AssetBrowser");
                return null;
            }

            var relativePath = ToProjectRelativePath(absolutePath);
            var baseName = Path.GetFileNameWithoutExtension(absolutePath);
            var entityName = scene.GetUniqueEntityName(baseName, null);
            var entity = new Entity(entityName, Entity.InstanceType.Serialized);
            entity.Transform.Position = worldPosition ?? scene.Camera.Transform.Position;

            scene.AddEntity(entity);

            var animator = entity.AddComponent<SpriteAnimator>();
            animator.SetSerialized(true);
            animator.TextureFilePath = relativePath;

            // Persist the GUID so the animation survives renaming/moving the Aseprite source later on.
            animator.AsepriteAsset = new Voltage.Serialization.AssetReference
            {
                AssetGuid = reference.Guid,
                AssetPath = relativePath,
                AssetName = Path.GetFileNameWithoutExtension(relativePath),
            };

            try
            {
                var loaded = animator.LoadAllAnimations();

                // Serialize the first animation as the default so it plays on the next scene load.
                if (loaded > 0)
                {
                    string first = null;
                    foreach (var key in animator.Animations.Keys) { first = key; break; }
                    if (first != null)
                    {
                        animator.LoadedTag = first;
                        animator.Play(first);
                    }
                }
                else
                {
                    EditorDebug.Log($"DropAsepriteAnimated: '{relativePath}' has no frames/tags to animate.", "AssetBrowser");
                }
            }
            catch (Exception ex)
            {
                EditorDebug.Log($"DropAsepriteAnimated: failed to load '{relativePath}': {ex.Message}", "AssetBrowser");
            }

            EditorChangeTracker.PushUndo(
                new EntityCreateDeleteUndoAction(scene, entity, wasCreated: true, $"Create Animated Sprite '{entityName}'"),
                entity,
                $"Create Animated Sprite '{entityName}'");

            var imgr = Core.GetGlobalManager<ImGuiManager>();
            imgr?.SceneGraphWindow.EntityPane.SetSelectedEntity(entity, false);
            imgr?.MainEntityInspectorWindow.DelayedSetEntity(entity);
            return entity;
        }

        internal static void DropAudio(AssetReference reference, Microsoft.Xna.Framework.Vector2? worldPosition = null)
        {
            var absolutePath = ResolveOrLog(reference, "DropAudio");
            if (absolutePath == null) return;

            var scene = Core.Scene;
            if (scene == null)
            {
                EditorDebug.Log("DropAudio: no active scene.", "AssetBrowser");
                return;
            }

            // Spawn an entity with an AudioSourceComponent bound to the dropped clip.
            string baseName = Path.GetFileNameWithoutExtension(absolutePath);
            string entityName = scene.GetUniqueEntityName(baseName, null);
            var entity = new Entity(entityName, Entity.InstanceType.Serialized);
            entity.Transform.Position = worldPosition ?? scene.Camera.Transform.Position;

            scene.AddEntity(entity);

            var audio = entity.AddComponent<AudioSourceComponent>();
            audio.SetSerialized(true);

            // Convert the editor-side AssetReference to the engine's serializable one the field expects.
            audio.Clip = new Voltage.Serialization.AssetReference
            {
                AssetGuid = reference.Guid,
                AssetPath = reference.HintPath,
                AssetName = Path.GetFileNameWithoutExtension(reference.HintPath),
            };

            EditorChangeTracker.PushUndo(
                new EntityCreateDeleteUndoAction(scene, entity, wasCreated: true,
                    $"Create Audio Entity '{entityName}'"),
                entity,
                $"Create Audio Entity '{entityName}'"
            );

            var imgr = Core.GetGlobalManager<ImGuiManager>();
            imgr?.SceneGraphWindow.EntityPane.SetSelectedEntity(entity, false);
            imgr?.MainEntityInspectorWindow.DelayedSetEntity(entity);
        }

        internal static void DropTimeline(AssetReference reference, Microsoft.Xna.Framework.Vector2? worldPosition = null)
        {
            var absolutePath = ResolveOrLog(reference, "DropTimeline");
            if (absolutePath == null) return;

            var scene = Core.Scene;
            if (scene == null)
            {
                EditorDebug.Log("DropTimeline: no active scene.", "AssetBrowser");
                return;
            }

            // Spawn an entity with a TimelineDirector bound to the dropped .vtimeline asset.
            string baseName = Path.GetFileNameWithoutExtension(absolutePath);
            string entityName = scene.GetUniqueEntityName(baseName + "_Director", null);
            var entity = new Entity(entityName, Entity.InstanceType.Serialized);
            entity.Transform.Position = worldPosition ?? scene.Camera.Transform.Position;

            scene.AddEntity(entity);

            var director = entity.AddComponent<Voltage.Cinematics.TimelineDirector>();
            director.SetSerialized(true);

            director.Timeline = new Voltage.Serialization.AssetReference
            {
                AssetGuid = reference.Guid,
                AssetPath = reference.HintPath,
                AssetName = Path.GetFileNameWithoutExtension(reference.HintPath),
            };

            EditorChangeTracker.PushUndo(
                new EntityCreateDeleteUndoAction(scene, entity, wasCreated: true,
                    $"Create Timeline Director '{entityName}'"),
                entity,
                $"Create Timeline Director '{entityName}'"
            );

            var imgr = Core.GetGlobalManager<ImGuiManager>();
            imgr?.SceneGraphWindow.EntityPane.SetSelectedEntity(entity, false);
            imgr?.MainEntityInspectorWindow.DelayedSetEntity(entity);
        }

        /// <summary>Creates a tilemap layer bound to the dropped tileset, then arms the tile brush.</summary>
        internal static void DropTileset(AssetReference reference, Microsoft.Xna.Framework.Vector2? worldPosition = null)
        {
            var absolutePath = ResolveOrLog(reference, "DropTileset");
            if (absolutePath == null) return;

            if (Core.Scene == null)
            {
                EditorDebug.Log("DropTileset: no active scene.", "AssetBrowser");
                return;
            }

            var engineReference = new Voltage.Serialization.AssetReference
            {
                AssetGuid = reference.Guid,
                AssetPath = reference.HintPath,
                AssetName = Path.GetFileNameWithoutExtension(reference.HintPath),
            };

            var map = Voltage.Editor.Tools.Tilemap.TilemapSceneUtils
                .CreateTilemapLayer(engineReference, worldPosition);

            if (map == null)
                return;

            var imgr = Core.GetGlobalManager<ImGuiManager>();
            if (imgr == null)
                return;

            imgr.TilePaintTool.Target = map;
            imgr.CursorSelectionManager.SelectionMode = Voltage.Editor.Gizmos.CursorSelectionMode.TilePaint;
            imgr.ShowTilePaletteWindow = true;
        }

        internal static void DropPrefab(AssetReference reference, Microsoft.Xna.Framework.Vector2? worldPosition = null)
        {
            var absolutePath = ResolveOrLog(reference, "DropPrefab");
            if (absolutePath == null) return;

            var imgr = Core.GetGlobalManager<ImGuiManager>();
            if (imgr == null)
            {
                EditorDebug.Log("DropPrefab: ImGuiManager unavailable.", "AssetBrowser");
                return;
            }

            // Load directly from the resolved absolute path rather than re-searching by name.
            // The name-based scan in SerializationManager.LoadPrefabData only looks one level
            // deep under PrefabsFolder, so prefabs at other nesting depths (or in arbitrary
            // locations from an OS file-drop) would throw and produce nothing.
            PrefabData prefabData;
            try
            {
                prefabData = SerializationManager.Instance.LoadPrefabDataFromPath(absolutePath)
                             ?? throw new Exception("LoadPrefabDataFromPath returned null.");
            }
            catch (Exception ex)
            {
                EditorDebug.Log($"DropPrefab: failed to load prefab from '{absolutePath}': {ex.Message}", "AssetBrowser");
                return;
            }

            string prefabName = Path.GetFileNameWithoutExtension(absolutePath);
            // Forward the stable GUID (from the .meta sidecar) and the resolved data.
            imgr.SceneGraphWindow.CreateEntityFromPrefabData(prefabData, prefabName, reference.Guid, worldPosition);
        }

        internal static void DropScene(AssetReference reference, Microsoft.Xna.Framework.Vector2? worldPosition = null)
        {
            // worldPosition is intentionally ignored for scene drops — scene loading has no spawn point.
            var absolutePath = ResolveOrLog(reference, "DropScene");
            if (absolutePath == null) return;

            var imgr = Core.GetGlobalManager<ImGuiManager>();
            if (imgr == null)
            {
                EditorDebug.Log("DropScene: ImGuiManager unavailable.", "AssetBrowser");
                return;
            }

            string sceneName = Path.GetFileNameWithoutExtension(absolutePath);
            imgr.RequestSceneChange(sceneName);
        }

        /// <summary>
        /// Opens a prefab in an isolated, in-memory edit scene (one SerializedPrefab instance, nothing else)
        /// so it can be viewed/edited in isolation. Triggered by double-clicking a prefab in the browser.
        /// </summary>
        internal static void OpenPrefabIsolated(AssetReference reference)
        {
            var absolutePath = ResolveOrLog(reference, "OpenPrefabIsolated");
            if (absolutePath == null) return;

            var imgr = Core.GetGlobalManager<ImGuiManager>();
            if (imgr == null)
            {
                EditorDebug.Log("OpenPrefabIsolated: ImGuiManager unavailable.", "AssetBrowser");
                return;
            }

            PrefabData prefabData;
            try
            {
                prefabData = SerializationManager.Instance.LoadPrefabDataFromPath(absolutePath)
                             ?? throw new Exception("LoadPrefabDataFromPath returned null.");
            }
            catch (Exception ex)
            {
                EditorDebug.Log($"OpenPrefabIsolated: failed to load prefab from '{absolutePath}': {ex.Message}", "AssetBrowser");
                return;
            }

            string prefabName = Path.GetFileNameWithoutExtension(absolutePath);
            imgr.OpenPrefabIsolated(prefabData, prefabName, reference.Guid);
        }

        /// <summary>
        /// Resolves <paramref name="reference"/> via the <see cref="AssetDatabase"/> singleton
        /// and logs a warning if resolution fails.
        /// Returns the absolute path, or null on failure.
        /// </summary>
        private static string ResolveOrLog(AssetReference reference, string callerName)
        {
            var db = AssetDatabase.Instance;
            var path = db?.Resolve(reference);
            if (string.IsNullOrEmpty(path))
            {
                EditorDebug.Log(
                    $"{callerName}: could not resolve asset reference {reference}. " +
                    "The file may have been deleted or is outside the project.", "AssetBrowser");
                return null;
            }
            return path;
        }

        /// <summary>
        /// Converts an absolute path to a path relative to the current project root,
        /// using forward slashes for cross-platform portability.
        /// </summary>
        private static string ToProjectRelativePath(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
                return absolutePath;

            if (!Path.IsPathRooted(absolutePath))
                return CrossPlatformPath.GetRelativePathForStorage(Environment.CurrentDirectory, absolutePath);

            try
            {
                var project = ProjectManager.Instance?.CurrentProject;
                if (project == null)
                    return CrossPlatformPath.Normalize(absolutePath).Replace(CrossPlatformPath.Sep, '/');

                return CrossPlatformPath.GetRelativePathForStorage(project.ProjectPath, absolutePath);
            }
            catch
            {
                return CrossPlatformPath.Normalize(absolutePath).Replace(CrossPlatformPath.Sep, '/');
            }
        }
    }
}
