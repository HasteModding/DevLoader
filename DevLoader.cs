using System.Collections;
using System.Reflection;
using Landfall.Haste; // Assuming Haste's namespace
using Landfall.Modding;
using UnityEngine;
using UnityEngine.Localization; // For LocalizedString
using Zorro.Settings; // Assuming SettingsHandler is here

namespace DevLoader
{
    /// <summary>
    /// Helper interface so that our generic mod toggle settings can be handled uniformly.
    /// </summary>
    public interface IModToggle
    {
        /// <summary>
        /// The plugin type associated with this setting.
        /// </summary>
        Type PluginType { get; }

        /// <summary>
        /// The current toggle value.
        /// </summary>
        bool Value { get; }
    }

    /// <summary>
    /// A generic Haste setting to toggle a mod on or off.
    /// Because this class is generic (wrapped around the mod's type parameter),
    /// each instantiation becomes a distinct type and hence Haste saves a unique pref.
    /// </summary>
    /// <typeparam name="TPlugin">The mod/plugin type being toggled.</typeparam>
    [HasteSetting]
    public class ModToggleSetting<TPlugin> : BoolSetting, IExposedSetting, IModToggle
    {
        // The plugin type is simply the generic type parameter.
        public Type PluginType => typeof(TPlugin);

        /// <summary>
        /// We override the SettingID so that it incorporates the plugin type’s full name.
        /// </summary>
        public string SettingID => $"DevLoader_Toggle_{PluginType.FullName}";

        // Required properties for BoolSetting.
        public override LocalizedString OnString => new UnlocalizedString("Enabled");
        public override LocalizedString OffString => new UnlocalizedString("Disabled");

        /// <summary>
        /// We default to mods enabled.
        /// </summary>
        protected override bool GetDefaultValue() => true;

        /// <summary>
        /// Called when the setting value is changed at runtime.
        /// A restart is required for this loader to actually load/unload mods.
        /// </summary>
        public override void ApplyValue()
        {
            Debug.Log(
                $"[DevLoader] Mod toggle for '{PluginType.FullName}' changed to: {Value}. Restart required to take effect."
            );
        }

        /// <summary>
        /// Returns a display name for the setting.
        /// If the mod’s type name is “Plugin” (a common pattern) then we use the assembly name.
        /// Otherwise we use the type’s name.
        /// </summary>
        public LocalizedString GetDisplayName()
        {
            string name =
                PluginType.Name == "Plugin" ? PluginType.Assembly.GetName().Name : PluginType.Name;
            return new UnlocalizedString($"Enable: {name}");
        }

        public string GetCategory() => "Mod Loader";
    }

    /// <summary>
    /// Handles the discovery, setting generation, and conditional loading of mods.
    /// This MonoBehaviour runs after initial game setup and Haste settings have loaded.
    /// </summary>
    public class ModLoaderController : MonoBehaviour
    {
        private static readonly string ModDirectoryName = "DevMods";
        private static bool _initializationAttempted = false;

        // We store our discovered mod toggles as IModToggle so we can retrieve their PluginType.
        private static readonly List<IModToggle> _modSettings = new();

        void Awake()
        {
            // In Awake, simply ensure the DevMods directory exists.
            Debug.Log("[DevLoader] Controller Awake: Ensuring mod directory exists...");
            EnsureModDirectoryExists();
        }

        IEnumerator Start()
        {
            if (_initializationAttempted)
            {
                yield break;
            }
            _initializationAttempted = true;

            Debug.Log("[DevLoader] Controller Start: Discovering mods and registering settings...");

            bool registrationSuccess = DiscoverAndRegisterSettings();
            if (!registrationSuccess)
            {
                Debug.LogWarning(
                    "[DevLoader] Controller Start: Mod settings could not be registered (SettingsHandler missing?). Mod initialization skipped."
                );
                yield break;
            }

            if (_modSettings.Count == 0)
            {
                Debug.Log("[DevLoader] Controller Start: No mods found to initialize.");
                yield break;
            }

            // Wait until the end of the frame to give Haste time to load stored setting values.
            Debug.Log(
                "[DevLoader] Controller Start: Waiting for end of frame before reading settings..."
            );
            yield return new WaitForEndOfFrame();
            Debug.Log("[DevLoader] Controller Start (Post-Wait): Initializing enabled mods...");
            InitializeEnabledMods();

            Debug.Log("[DevLoader] Controller Start phase complete.");
        }

        /// <summary>
        /// Ensures that the DevMods directory exists in the game’s root directory.
        /// In both Editor and builds, Path.GetDirectoryName(Application.dataPath)
        /// returns the parent folder (the folder containing Assets or the _Data folder).
        /// </summary>
        private static void EnsureModDirectoryExists()
        {
            try
            {
                string gameRootPath = Path.GetDirectoryName(Application.dataPath);
                string fullPath = Path.Combine(gameRootPath ?? ".", ModDirectoryName);

                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                    Debug.Log($"[DevLoader] Created mod directory: {fullPath}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[DevLoader] Failed to ensure mod directory exists: {e}");
            }
        }

        /// <summary>
        /// Scans the DevMods directory for DLLs, loads each assembly,
        /// and for each non-abstract class marked with [LandfallPlugin] (and not part of our loader),
        /// creates a generic ModToggleSetting for that mod type and registers it with Haste.
        /// </summary>
        private bool DiscoverAndRegisterSettings()
        {
            _modSettings.Clear();

            string gameRootPath = Path.GetDirectoryName(Application.dataPath);
            string fullModPath = Path.Combine(gameRootPath ?? ".", ModDirectoryName);

            if (!Directory.Exists(fullModPath))
            {
                Debug.Log($"[DevLoader] Mod directory not found during discovery: {fullModPath}");
                return true;
            }

            var settingsHandler = GameHandler.Instance?.SettingsHandler;
            if (settingsHandler == null)
            {
                Debug.LogError(
                    "[DevLoader] Could not get SettingsHandler instance in Start! Cannot register mod toggles."
                );
                return false;
            }

            string[] dllFiles;
            try
            {
                dllFiles = Directory.GetFiles(fullModPath, "*.dll");
            }
            catch (Exception e)
            {
                Debug.LogError($"[DevLoader] Error accessing mod directory {fullModPath}: {e}");
                return true;
            }

            if (dllFiles.Length == 0)
            {
                Debug.Log($"[DevLoader] No DLLs found in {fullModPath}.");
                return true;
            }

            foreach (string dllFile in dllFiles)
            {
                try
                {
                    Assembly assembly = Assembly.LoadFrom(dllFile);

                    foreach (Type type in assembly.GetTypes())
                    {
                        // Check for the LandfallPlugin attribute.
                        if (!Attribute.IsDefined(type, typeof(LandfallPlugin)))
                        {
                            continue;
                        }

                        // Skip abstract types and types coming from our own loader.
                        if (
                            type.IsAbstract
                            || !type.IsClass
                            || type.Assembly == typeof(DevLoaderPlugin).Assembly
                        )
                        {
                            continue;
                        }

                        // Ensure we haven't already registered this mod.
                        if (_modSettings.Any(s => s.PluginType.FullName == type.FullName))
                        {
                            continue;
                        }

                        Debug.Log($"[DevLoader] Found LandfallPlugin: {type.FullName}");

                        // Create a generic instance of ModToggleSetting<T> for this mod type.
                        Type genericSettingType = typeof(ModToggleSetting<>).MakeGenericType(type);
                        object instance = Activator.CreateInstance(genericSettingType);
                        if (instance is Setting setting)
                        {
                            // Add the setting to Haste's settings.
                            settingsHandler.AddSetting(setting);
                            // Also store it as an IModToggle for later initialization.
                            if (setting is IModToggle modToggle)
                            {
                                _modSettings.Add(modToggle);
                            }
                        }
                    }
                }
                catch (ReflectionTypeLoadException rtle)
                {
                    Debug.LogError(
                        $"[DevLoader] Failed to load types from assembly {dllFile}. This might be due to missing dependencies for that mod. Errors:"
                    );
                    foreach (var loaderException in rtle.LoaderExceptions.Take(5))
                    {
                        Debug.LogError($"- {loaderException?.Message ?? "Unknown Error"}");
                    }
                    if (rtle.LoaderExceptions.Length > 5)
                    {
                        Debug.LogError("... (additional errors truncated)");
                    }
                }
                catch (BadImageFormatException)
                {
                    Debug.LogWarning(
                        $"[DevLoader] Skipped file {Path.GetFileName(dllFile)} as it's not a valid .NET assembly."
                    );
                }
                catch (Exception e)
                {
                    Debug.LogError($"[DevLoader] Failed to process assembly {dllFile}: {e}");
                }
            }
            Debug.Log(
                $"[DevLoader] Mod discovery complete. Registered {_modSettings.Count} mod toggles."
            );
            return true;
        }

        /// <summary>
        /// Iterates through each discovered mod toggle and, if its value is enabled,
        /// attempts to initialize the mod by running its static constructor.
        /// </summary>
        private void InitializeEnabledMods()
        {
            if (_modSettings.Count == 0)
            {
                return;
            }

            int initializedCount = 0;
            foreach (var setting in _modSettings)
            {
                if (setting.Value)
                {
                    Type pluginType = setting.PluginType;
                    Debug.Log($"[DevLoader] Initializing enabled mod: {pluginType.FullName}");
                    try
                    {
                        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
                            pluginType.TypeHandle
                        );
                        initializedCount++;
                    }
                    catch (Exception e)
                    {
                        Debug.LogError(
                            $"[DevLoader] Failed to initialize mod {pluginType.FullName} via static constructor: {e}"
                        );
                    }
                }
            }
            Debug.Log(
                $"[DevLoader] Finished initializing mods. Enabled: {initializedCount}/{_modSettings.Count}."
            );
        }
    }

    /// <summary>
    /// The main entry point for the mod loader.
    /// Its sole purpose is to create a persistent controller GameObject.
    /// </summary>
    [LandfallPlugin]
    public class DevLoaderPlugin
    {
        // Use a nullable field to quiet the static analysis warning.
        private static GameObject? _controllerObject;

        static DevLoaderPlugin()
        {
            Debug.Log("[DevLoader] Plugin Initializing - Creating Controller...");

            if (GameObject.Find("DevLoaderController") != null)
            {
                Debug.LogWarning(
                    "[DevLoader] Controller GameObject already exists. Skipping creation."
                );
                return;
            }

            if (_controllerObject == null)
            {
                _controllerObject = new GameObject("DevLoaderController");
                _controllerObject.AddComponent<ModLoaderController>();
                GameObject.DontDestroyOnLoad(_controllerObject);
                Debug.Log("[DevLoader] Controller GameObject created.");
            }
        }
    }
}
