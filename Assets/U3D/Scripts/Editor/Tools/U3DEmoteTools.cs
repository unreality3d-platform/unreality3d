using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;

namespace U3D.Editor
{
    /// <summary>
    /// Builds the emote assets the first time Add Emotes runs in a project that lacks them: the
    /// swirl particle prefab, the emote set, and U3DEmote on both player prefabs. Runs once in the
    /// template source project; creators receive the saved assets and prefabs. The particle
    /// material is made by hand, because a transparent URP particle material depends on settings
    /// URP's material Inspector applies.
    /// </summary>
    internal static class U3DEmoteTools
    {
        private const string EmotesFolder = "Assets/U3D/U3D_Assets/Emotes";
        private const string AtlasPath = EmotesFolder + "/U3D_EmoteAtlas.png";
        private const string SoundPath = EmotesFolder + "/U3D_EmoteSound.wav";
        private const string MaterialPath = EmotesFolder + "/U3D_EmoteParticle.mat";
        private const string SwirlPath = EmotesFolder + "/U3D_EmoteSwirl.prefab";
        private const string SetPath = EmotesFolder + "/U3D_EmoteSet.asset";
        private const string MixerPath = "Assets/U3D/Prefabs/U3D_AudioMixer.mixer";
        private const string PlayerPrefabPath = "Assets/U3D/Prefabs/U3D_PlayerController.prefab";
        private const string RemotePrefabPath = "Assets/U3D/Prefabs/U3D_RemoteAvatar.prefab";

        private static readonly string[] EmoteNames =
        {
            "Wave", "Thumbs up", "Laugh", "Heart", "Clap",
            "Smile", "Wow", "Question", "Broken heart", "Sparkles"
        };

        /// <summary>
        /// Makes sure the emote set exists and both player prefabs carry U3DEmote using it.
        /// Returns false, after telling the creator why, when something it needs is missing.
        /// </summary>
        internal static bool EnsureEmoteAssets()
        {
            U3DEmoteSet set = AssetDatabase.LoadAssetAtPath<U3DEmoteSet>(SetPath);
            if (set == null)
            {
                set = BuildEmoteSet();
                if (set == null) return false;
            }

            bool player = EnsureOnPrefab(PlayerPrefabPath, set);
            bool remote = EnsureOnPrefab(RemotePrefabPath, set);
            return player && remote;
        }

        private static U3DEmoteSet BuildEmoteSet()
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
            {
                EditorUtility.DisplayDialog("Add Emotes",
                    "Could not find the emote particle material at:\n" + MaterialPath +
                    "\n\nMake sure the U3D template folders have not been moved or renamed.",
                    "OK");
                return null;
            }

            Sprite[] sprites = LoadAtlasSprites();
            if (sprites.Length < EmoteNames.Length)
            {
                EditorUtility.DisplayDialog("Add Emotes",
                    "The emote atlas at:\n" + AtlasPath + "\n\nhas " + sprites.Length + " sprites and needs " +
                    EmoteNames.Length + ". Set its Sprite Mode to Multiple and slice it into 256 px cells.",
                    "OK");
                return null;
            }

            ParticleSystem swirl = AssetDatabase.LoadAssetAtPath<ParticleSystem>(SwirlPath);
            if (swirl == null)
                swirl = BuildSwirlPrefab(material, sprites[0]);
            if (swirl == null)
            {
                EditorUtility.DisplayDialog("Add Emotes",
                    "Could not create the emote swirl prefab at:\n" + SwirlPath, "OK");
                return null;
            }

            U3DEmoteSet set = ScriptableObject.CreateInstance<U3DEmoteSet>();
            AssetDatabase.CreateAsset(set, SetPath);

            var so = new SerializedObject(set);
            SerializedProperty emotes = so.FindProperty("emotes");
            emotes.arraySize = EmoteNames.Length;
            for (int i = 0; i < EmoteNames.Length; i++)
            {
                SerializedProperty entry = emotes.GetArrayElementAtIndex(i);
                entry.FindPropertyRelative("displayName").stringValue = EmoteNames[i];
                entry.FindPropertyRelative("sprite").objectReferenceValue = sprites[i];
                entry.FindPropertyRelative("sound").objectReferenceValue = null;
            }
            so.FindProperty("defaultSound").objectReferenceValue = AssetDatabase.LoadAssetAtPath<AudioClip>(SoundPath);
            so.FindProperty("output").objectReferenceValue = FindEffectsGroup();
            so.FindProperty("swirlPrefab").objectReferenceValue = swirl;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            Debug.Log("Built the emote set at " + SetPath, set);
            return set;
        }

        /// <summary>Sprites in reading order: top row first, left to right.</summary>
        private static Sprite[] LoadAtlasSprites()
        {
            return AssetDatabase.LoadAllAssetsAtPath(AtlasPath)
                .OfType<Sprite>()
                .OrderByDescending(s => Mathf.RoundToInt(s.rect.y))
                .ThenBy(s => s.rect.x)
                .ToArray();
        }

        private static AudioMixerGroup FindEffectsGroup()
        {
            AudioMixer mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(MixerPath);
            if (mixer == null) return null;
            AudioMixerGroup[] groups = mixer.FindMatchingGroups("Effects");
            return groups != null && groups.Length > 0 ? groups[0] : null;
        }

        private static ParticleSystem BuildSwirlPrefab(Material material, Sprite firstSprite)
        {
            GameObject go = new GameObject("U3D_EmoteSwirl");
            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.duration = 1f;
            main.loop = true;
            main.playOnAwake = true;
            main.startDelay = 0f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.6f, 2.0f);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.14f, 0.2f);
            main.startColor = Color.white;
            main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.maxParticles = 24;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = false;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = false;

            ParticleSystem.VelocityOverLifetimeModule velocity = ps.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.Local;
            velocity.x = new ParticleSystem.MinMaxCurve(0f);
            velocity.y = new ParticleSystem.MinMaxCurve(0.45f);
            velocity.z = new ParticleSystem.MinMaxCurve(0f);
            velocity.orbitalX = new ParticleSystem.MinMaxCurve(0f);
            velocity.orbitalY = new ParticleSystem.MinMaxCurve(3f);
            velocity.orbitalZ = new ParticleSystem.MinMaxCurve(0f);
            velocity.radial = new ParticleSystem.MinMaxCurve(0.1f);

            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            var sizeCurve = new AnimationCurve(
                new Keyframe(0f, 0f),
                new Keyframe(0.12f, 1f),
                new Keyframe(1f, 0.6f));
            size.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            ParticleSystem.ColorOverLifetimeModule color = ps.colorOverLifetime;
            color.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.65f), new GradientAlphaKey(0f, 1f) });
            color.color = gradient;

            ParticleSystem.TextureSheetAnimationModule sheet = ps.textureSheetAnimation;
            sheet.enabled = true;
            sheet.mode = ParticleSystemAnimationMode.Sprites;
            sheet.AddSprite(firstSprite);
            sheet.frameOverTime = new ParticleSystem.MinMaxCurve(0f);
            sheet.cycleCount = 1;

            ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.View;
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(go, SwirlPath, out bool success);
            Object.DestroyImmediate(go);

            if (!success || saved == null) return null;
            Debug.Log("Built the emote swirl prefab at " + SwirlPath, saved);
            return saved.GetComponent<ParticleSystem>();
        }

        private static bool EnsureOnPrefab(string path, U3DEmoteSet set)
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null)
            {
                EditorUtility.DisplayDialog("Add Emotes",
                    "Could not find the player prefab at:\n" + path +
                    "\n\nMake sure the U3D template prefab has not been moved or renamed.",
                    "OK");
                return false;
            }

            U3DEmote existing = asset.GetComponent<U3DEmote>();
            if (existing != null && existing.EmoteSet == set) return true;

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                U3DEmote emote = root.GetComponent<U3DEmote>();
                if (emote == null) emote = root.AddComponent<U3DEmote>();

                var so = new SerializedObject(emote);
                so.FindProperty("emoteSet").objectReferenceValue = set;
                so.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            Debug.Log("Added U3DEmote to " + path);
            return true;
        }
    }
}