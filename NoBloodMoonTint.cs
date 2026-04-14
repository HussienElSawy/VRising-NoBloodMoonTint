using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NoBloodMoonTint
{
	[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
	public class Plugin : BasePlugin
	{
		internal static ManualLogSource PluginLog = null!;
		internal static Harmony? HarmonyInstance;
		private static volatile bool _suppressHueEnabled = false;

		internal static KeyCode ToggleKey = KeyCode.F7;
		internal static KeyCode ScanKey = KeyCode.F6;

		internal static bool IsSuppressionEnabled => _suppressHueEnabled;

		internal static void SetSuppression(bool value)
		{
			_suppressHueEnabled = value;
			VolumeSuppress.OnSuppressionChanged(value);
			LogInfo($"[KEYBIND] Blood moon tint suppression set to: {value}");
		}

		public override void Load()
		{
			PluginLog = Log;
			SessionFileLogger.Initialize("NoBloodMoonTint");
			_suppressHueEnabled = false;

			HarmonyInstance = new Harmony(MyPluginInfo.PLUGIN_GUID);

			var toggleCfg = Config.Bind("Keybinds", "Toggle", "F7", "Key to toggle blood moon tint suppression (Unity KeyCode name)");
			var scanCfg = Config.Bind("Keybinds", "ScanVolumes", "F6", "Key to scan and diff current scene volume profiles");

			if (Enum.TryParse<KeyCode>(toggleCfg.Value, true, out var tk)) ToggleKey = tk;
			if (Enum.TryParse<KeyCode>(scanCfg.Value, true, out var sk)) ScanKey = sk;

			ShaderColorHook.Install(HarmonyInstance);

			ClassInjector.RegisterTypeInIl2Cpp<KeybindListener>();
			AddComponent<KeybindListener>();
			LogInfo($"[KEYBIND] Keybinds active — Scan:{ScanKey} Toggle:{ToggleKey}");
			LogInfo("[VOLUME] LateUpdate will zero blood moon HDRP volumes each frame when suppression is on.");
			LogInfo("[VOLUME] Press F6 to scan and diff volume profiles while toggling blood moon on/off.");
		}

		internal static void LogInfo(string message)
		{
			PluginLog.LogInfo(message);
			SessionFileLogger.Info(message);
		}
	}

	public class KeybindListener : MonoBehaviour
	{
		public KeybindListener(IntPtr ptr) : base(ptr) { }

		private GUIStyle? _hudStyle;

		void Update()
		{
			if (Plugin.ScanKey != KeyCode.None && Input.GetKeyDown(Plugin.ScanKey))
			{
				VolumeSuppress.ScanAndLog("manual-scan");
			}
			else
			if (Plugin.ToggleKey != KeyCode.None && Input.GetKeyDown(Plugin.ToggleKey))
			{
				Plugin.SetSuppression(!Plugin.IsSuppressionEnabled);
			}
		}

		void LateUpdate()
		{
			if (Plugin.IsSuppressionEnabled)
				VolumeSuppress.ZeroBloodMoonVolumes();
		}

		void OnGUI()
		{
			if (_hudStyle == null)
			{
				_hudStyle = new GUIStyle();
				_hudStyle.fontSize = 11;
				_hudStyle.fontStyle = FontStyle.Bold;
			}

			var label   = Plugin.IsSuppressionEnabled ? "NoBloodMoonTint: ON" : "NoBloodMoonTint: OFF";
			var fgColor = Plugin.IsSuppressionEnabled
				? new Color(0.2f, 1f, 0.2f, 1f)      // green  — suppression active
				: new Color(1f, 0.35f, 0.1f, 1f);    // orange — blood moon visible

			const float w = 200f, h = 20f, x = 10f;
			var y = Screen.height - h - 10f;

			// Drop shadow (1 px offset)
			_hudStyle.normal.textColor = new Color(0f, 0f, 0f, 0.9f);
			GUI.Label(new Rect(x + 1f, y + 1f, w, h), label, _hudStyle);

			// Foreground text
			_hudStyle.normal.textColor = fgColor;
			GUI.Label(new Rect(x, y, w, h), label, _hudStyle);
		}
	}

	// Suppresses blood moon screen tint by enforcing target volume state and neutralizing
	// related color grading/material tints while suppression is enabled.
	internal static class VolumeSuppress
	{
		private sealed class VolumeState
		{
			internal float Weight;
			internal bool Enabled;
		}

		private const float TargetScenePostProcessWeight = 1.000f;
		private const float BrightnessLiftStops = 0.50f;
		private static readonly List<Volume> _cached = new();
		private static readonly Dictionary<Volume, float> _originalWeights = new();
		private static readonly Dictionary<Volume, bool> _originalEnabled = new();
		private static readonly Dictionary<Volume, float> _originalPriorities = new();
		private static readonly Dictionary<string, VolumeState> _lastScan = new();
		private static Color? _originalAmbientLight = null;
		private static bool _loggedGenericTargets = false;

		// Profile component override backups — keyed by "profileName|ComponentType" for dynamic restore.
		private static readonly Dictionary<string, Color> _origColorFilters = new();
		private static readonly Dictionary<string, float> _origPostExposures = new();
		private static readonly Dictionary<string, Color> _origSplitShadows = new();
		private static readonly Dictionary<string, Color> _origSplitHighlights = new();
		private static readonly Dictionary<string, float> _origTemperatures = new();
		private static readonly Dictionary<string, float> _origTints = new();
		private static readonly Dictionary<string, bool> _origBloodMoonFogActive = new();
		private static readonly Dictionary<Material, Color> _origCustomVignetteTint = new();
		private static readonly List<Material> _customVignetteMats = new();
		private static int _customVignetteScanCooldown = 0;

		internal static void OnSuppressionChanged(bool enabled)
		{
			if (!enabled)
			{
				// Restore everything to pre-suppression state.
				RestoreVolumes();
			}
			else
			{
				ApplySuppressedStateImmediate();
			}
		}

		private static void ApplySuppressedStateImmediate()
		{
			RebuildCache();
			ZeroBloodMoonVolumes();
		}

		internal static void ScanAndLog(string reason)
		{
			Plugin.LogInfo($"[VOLUME] Scanning all scene volumes (reason={reason})...");
			try
			{
				var all = UnityEngine.Object.FindObjectsOfType<Volume>();
				var loggedCount = 0;
				var current = new Dictionary<string, VolumeState>();

				foreach (var v in all)
				{
					var profileName = v.sharedProfile != null ? v.sharedProfile.name : "<no profile>";
					if (ShouldSkipVolumeInLogs(profileName)) continue;
					var goName = v.gameObject != null ? v.gameObject.name : "<null go>";
					var key = $"{goName}|{profileName}|{v.priority:F3}";
					current[key] = new VolumeState
					{
						Weight = v.weight,
						Enabled = v.enabled
					};
					loggedCount++;

					Plugin.LogInfo($"[VOLUME] '{goName}' profile='{profileName}' enabled={v.enabled} weight={v.weight:F3} isGlobal={v.isGlobal} priority={v.priority}");
				}

				Plugin.LogInfo($"[VOLUME] Found {loggedCount} non-sound volumes total.");

				if (_lastScan.Count > 0)
				{
					var changes = 0;
					foreach (var kv in current)
					{
						if (!_lastScan.TryGetValue(kv.Key, out var previous))
						{
							Plugin.LogInfo($"[VOLUME_DIFF] Added: {kv.Key} enabled={kv.Value.Enabled} weight={kv.Value.Weight:F3}");
							changes++;
							continue;
						}

						if (Math.Abs(previous.Weight - kv.Value.Weight) > 0.0001f || previous.Enabled != kv.Value.Enabled)
						{
							Plugin.LogInfo($"[VOLUME_DIFF] Changed: {kv.Key} enabled {previous.Enabled}->{kv.Value.Enabled}, weight {previous.Weight:F3}->{kv.Value.Weight:F3}");
							changes++;
						}
					}

					foreach (var kv in _lastScan)
					{
						if (!current.ContainsKey(kv.Key))
						{
							Plugin.LogInfo($"[VOLUME_DIFF] Removed: {kv.Key}");
							changes++;
						}
					}

					if (changes == 0)
					{
						Plugin.LogInfo("[VOLUME_DIFF] No differences from previous scan.");
					}
				}

				_lastScan.Clear();
				foreach (var kv in current)
				{
					_lastScan[kv.Key] = kv.Value;
				}
			}
			catch (Exception ex)
			{
				Plugin.LogInfo($"[VOLUME] ScanAndLog failed: {ex.GetType().Name} {ex.Message}");
			}

			// Detailed scans removed to minimize logging. Only log volume state and basic info.
		}

		private static bool ShouldSkipVolumeInLogs(string profileName)
		{
			var p = (profileName ?? string.Empty).ToLowerInvariant();
			return p.Contains("sound_profile") || p.StartsWith("mus ");
		}

		// Called every LateUpdate when suppression is on.
		internal static void ZeroBloodMoonVolumes()
		{
			try
			{
				// Cache is rebuilt only on first suppression or manual scan (F6), not per-frame for performance.

				foreach (var v in _cached)
				{
					if (v == null) continue;
					if (!_originalWeights.ContainsKey(v)) _originalWeights[v] = v.weight;
					if (!_originalEnabled.ContainsKey(v)) _originalEnabled[v] = v.enabled;
					if (!_originalPriorities.ContainsKey(v)) _originalPriorities[v] = v.priority;

					var originalPriority = _originalPriorities.TryGetValue(v, out var p) ? p : v.priority;

					// Generic targeting rules (no profile-name matching):
					// - Priority 0 baseline post-process stays at 1.0
					// - Higher-priority global mood volumes are disabled and zeroed
					if (Math.Abs(originalPriority) < 0.001f)
					{
						v.enabled = true;
						v.weight = TargetScenePostProcessWeight;
						ApplyBaselineBrightnessLift(v);
					}
					else if (v.isGlobal && originalPriority >= 9.5f)
					{
						v.enabled = false;
						v.weight = 0f;
						v.priority = 10f;
					}
				}

				if (!_loggedGenericTargets)
				{
					_loggedGenericTargets = true;
					Plugin.LogInfo($"[SUPPRESS] Blood moon effect suppression active (brightness +{BrightnessLiftStops:F2} EV).");
				}

				// Also directly neutralize profile-level color grade components for high-priority volumes.
				// This catches any code path that may still sample the sharedProfile even when the volume
				// is disabled/zeroed — e.g. ColorAdjustments colorFilter and SplitToning warm cast.
				foreach (var v in _cached)
				{
					if (v == null || v.sharedProfile == null) continue;
					var origPri2 = _originalPriorities.TryGetValue(v, out var p2) ? p2 : v.priority;
					if (!(v.isGlobal && origPri2 >= 9.5f)) continue;

					try
					{
						var profName = v.sharedProfile.name;
						foreach (var comp in v.sharedProfile.components)
						{
							if (comp == null) continue;

							// BloodMoon local fog can remain visible even after volume disable in some scenes.
							// Force this specific custom fog override off while suppression is enabled.
							var typeName = comp.GetIl2CppType()?.Name ?? comp.GetType().Name;
							if (profName.IndexOf("bloodmoon", StringComparison.OrdinalIgnoreCase) >= 0
								&& typeName.IndexOf("StunlockFogVolumeComponent", StringComparison.OrdinalIgnoreCase) >= 0)
							{
								var fogKey = $"{profName}|{typeName}";
								if (!_origBloodMoonFogActive.ContainsKey(fogKey)) _origBloodMoonFogActive[fogKey] = comp.active;
								comp.active = false;
							}

							var ca = comp.TryCast<ColorAdjustments>();
							if (ca != null)
							{
								var caKey = $"{profName}|ColorAdjustments";
								if (!_origColorFilters.ContainsKey(caKey)) _origColorFilters[caKey] = ca.colorFilter.value;
								ca.colorFilter.value = Color.white;
								// DO NOT touch saturation, contrast, or other adjustments — only neutralize the warm color filter.
								continue;
							}

							var st = comp.TryCast<SplitToning>();
							if (st != null)
							{
								var stKey = $"{profName}|SplitToning";
								if (!_origSplitShadows.ContainsKey(stKey))    _origSplitShadows[stKey]    = st.shadows.value;
								if (!_origSplitHighlights.ContainsKey(stKey)) _origSplitHighlights[stKey] = st.highlights.value;
								var neutral = new Color(0.5f, 0.5f, 0.5f, 0f);
								st.shadows.value    = neutral;
								st.highlights.value = neutral;
								continue;
							}

							var wb = comp.TryCast<WhiteBalance>();
							if (wb != null)
							{
								var wbKey = $"{profName}|WhiteBalance";
								if (!_origTemperatures.ContainsKey(wbKey)) _origTemperatures[wbKey] = wb.temperature.value;
								if (!_origTints.ContainsKey(wbKey))        _origTints[wbKey]        = wb.tint.value;
								wb.temperature.value = 0f;
								wb.tint.value        = 0f;
							}
						}
					}
					catch { }
				}

				// Suppress ambient light if it looks warm/reddish (r significantly exceeds g/b).
				// Store the first value seen so we can restore on suppress-off.
				try
				{
					var ambient = RenderSettings.ambientLight;
					if (!_originalAmbientLight.HasValue)
						_originalAmbientLight = ambient;

					if (IsWarmColor(ambient))
					{
						// Fully neutralize to gray using the green channel so blue matches green.
						// Previously only clamped red, leaving blue below green and making blues appear darker.
						var neutralized = new Color(ambient.g, ambient.g, ambient.g, ambient.a);
						RenderSettings.ambientLight = neutralized;
					}
				}
				catch { }

				// CustomVignette is a separate full-screen pass that can keep a stale red tint.
				// Explicitly neutralize its _ColorTint while suppression is on.
				NeutralizeCustomVignetteTint();
			}
			catch { }
		}

		private static void ApplyBaselineBrightnessLift(Volume v)
		{
			if (v?.sharedProfile == null) return;

			try
			{
				var profName = v.sharedProfile.name;
				foreach (var comp in v.sharedProfile.components)
				{
					if (comp == null) continue;
					var ca = comp.TryCast<ColorAdjustments>();
					if (ca == null) continue;

					var key = $"{profName}|ColorAdjustments.postExposure";
					if (!_origPostExposures.ContainsKey(key)) _origPostExposures[key] = ca.postExposure.value;
					ca.postExposure.value = _origPostExposures[key] + BrightnessLiftStops;
					break;
				}
			}
			catch { }
		}

		private static void NeutralizeCustomVignetteTint()
		{
			try
			{
				if (--_customVignetteScanCooldown <= 0)
				{
					_customVignetteScanCooldown = 120;
					_customVignetteMats.Clear();
					var allMats = Resources.FindObjectsOfTypeAll<Material>();
					foreach (var m in allMats)
					{
						if (m == null || m.shader == null) continue;
						if (!m.shader.name.Equals("Hidden/Shader/CustomVignette", StringComparison.OrdinalIgnoreCase)) continue;
						if (!m.HasProperty("_ColorTint")) continue;
						_customVignetteMats.Add(m);
					}
				}

				foreach (var m in _customVignetteMats)
				{
					if (m == null || !m.HasProperty("_ColorTint")) continue;
					if (!_origCustomVignetteTint.ContainsKey(m)) _origCustomVignetteTint[m] = m.GetColor("_ColorTint");
					m.SetColor("_ColorTint", new Color(0f, 0f, 0f, 0f));
				}
			}
			catch { }
		}

		// Returns true if the color has a noticeably warm/reddish bias (r exceeds g by more than 12%).
		private static bool IsWarmColor(Color c)
			=> c.r > 0.02f && c.r > c.g * 1.12f;

		private static void RestoreVolumes()
		{
			foreach (var kvp in _originalWeights)
			{
				if (kvp.Key != null)
				{
					kvp.Key.weight = kvp.Value;
				}
			}

			foreach (var kvp in _originalEnabled)
			{
				if (kvp.Key != null)
				{
					kvp.Key.enabled = kvp.Value;
				}
			}

			foreach (var kvp in _originalPriorities)
			{
				if (kvp.Key != null)
				{
					kvp.Key.priority = kvp.Value;
				}
			}

			// Restore profile component overrides dynamically by searching for components in profiles.
			try
			{
				var allVols = UnityEngine.Object.FindObjectsOfType<Volume>();
				foreach (var v in allVols)
				{
					if (v?.sharedProfile == null) continue;
					var profName = v.sharedProfile.name;

					foreach (var comp in v.sharedProfile.components)
					{
						if (comp == null) continue;

						var typeName = comp.GetIl2CppType()?.Name ?? comp.GetType().Name;
						var fogKey = $"{profName}|{typeName}";
						if (_origBloodMoonFogActive.TryGetValue(fogKey, out var fogActive))
						{
							comp.active = fogActive;
						}

						var ca = comp.TryCast<ColorAdjustments>();
						if (ca != null)
						{
							var caKey = $"{profName}|ColorAdjustments";
							if (_origColorFilters.TryGetValue(caKey, out var origCF))
								ca.colorFilter.value = origCF;

							var peKey = $"{profName}|ColorAdjustments.postExposure";
							if (_origPostExposures.TryGetValue(peKey, out var origPE))
								ca.postExposure.value = origPE;
							continue;
						}

						var st = comp.TryCast<SplitToning>();
						if (st != null)
						{
							var stKey = $"{profName}|SplitToning";
							if (_origSplitShadows.TryGetValue(stKey, out var origSh))
								st.shadows.value = origSh;
							if (_origSplitHighlights.TryGetValue(stKey, out var origHi))
								st.highlights.value = origHi;
							continue;
						}

						var wb = comp.TryCast<WhiteBalance>();
						if (wb != null)
						{
							var wbKey = $"{profName}|WhiteBalance";
							if (_origTemperatures.TryGetValue(wbKey, out var origT))
								wb.temperature.value = origT;
							if (_origTints.TryGetValue(wbKey, out var origTint))
								wb.tint.value = origTint;
						}
					}
				}
			}
			catch { }

			_origColorFilters.Clear();
			_origPostExposures.Clear();
			_origSplitShadows.Clear();
			_origSplitHighlights.Clear();
			_origTemperatures.Clear();
			_origTints.Clear();
			_origBloodMoonFogActive.Clear();

			foreach (var kvp in _origCustomVignetteTint)
			{
				try
				{
					if (kvp.Key != null && kvp.Key.HasProperty("_ColorTint"))
					{
						kvp.Key.SetColor("_ColorTint", kvp.Value);
					}
				}
				catch { }
			}
			_origCustomVignetteTint.Clear();
			_customVignetteMats.Clear();
			_customVignetteScanCooldown = 0;

			// Restore ambient light if we suppressed it.
			if (_originalAmbientLight.HasValue)
			{
				RenderSettings.ambientLight = _originalAmbientLight.Value;
				_originalAmbientLight = null;
			}

			// Clear volume backup dictionaries so next toggle captures fresh state.
			_originalWeights.Clear();
			_originalEnabled.Clear();
			_originalPriorities.Clear();

			_loggedGenericTargets = false;
		}

		private static void RebuildCache()
		{
			_cached.Clear();
			var all = UnityEngine.Object.FindObjectsOfType<Volume>();
			foreach (var v in all)
			{
				if (v == null) continue;
				_cached.Add(v);
			}
		}
	}

	// Hooks Shader/Material color APIs so we catch both global and material-level tint paths.
	internal static class ShaderColorHook
	{
		private static readonly HashSet<string> Logged = new();
		private static readonly HashSet<string> LoggedForcedNeutral = new();

		internal static void Install(Harmony harmony)
		{
			TryPatch(harmony, typeof(Shader), "SetGlobalColor", new[] { typeof(string), typeof(Color) }, nameof(SetGlobalColorStringPrefix), "Shader.SetGlobalColor(string,Color)");
			TryPatch(harmony, typeof(Shader), "SetGlobalColor", new[] { typeof(int), typeof(Color) }, nameof(SetGlobalColorIntPrefix), "Shader.SetGlobalColor(int,Color)");
			TryPatch(harmony, typeof(Shader), "SetGlobalVector", new[] { typeof(string), typeof(Vector4) }, nameof(SetGlobalVectorStringPrefix), "Shader.SetGlobalVector(string,Vector4)");
			TryPatch(harmony, typeof(Shader), "SetGlobalVector", new[] { typeof(int), typeof(Vector4) }, nameof(SetGlobalVectorIntPrefix), "Shader.SetGlobalVector(int,Vector4)");

			TryPatch(harmony, typeof(Material), "SetColor", new[] { typeof(string), typeof(Color) }, nameof(SetMaterialColorStringPrefix), "Material.SetColor(string,Color)");
			TryPatch(harmony, typeof(Material), "SetColor", new[] { typeof(int), typeof(Color) }, nameof(SetMaterialColorIntPrefix), "Material.SetColor(int,Color)");
			TryPatch(harmony, typeof(Material), "SetVector", new[] { typeof(string), typeof(Vector4) }, nameof(SetMaterialVectorStringPrefix), "Material.SetVector(string,Vector4)");
			TryPatch(harmony, typeof(Material), "SetVector", new[] { typeof(int), typeof(Vector4) }, nameof(SetMaterialVectorIntPrefix), "Material.SetVector(int,Vector4)");
		}

		private static void TryPatch(Harmony harmony, Type type, string methodName, Type[] args, string prefixName, string label)
		{
			try
			{
				var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | (type == typeof(Shader) ? BindingFlags.Static : BindingFlags.Instance), null, args, null);
				if (method == null)
				{
					Plugin.LogInfo($"[HOOK] Method not found: {label}");
					return;
				}
				var prefix = new HarmonyMethod(typeof(ShaderColorHook).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic));
				harmony.Patch(method, prefix: prefix);
				Plugin.LogInfo($"[HOOK] Hooked {label}");
			}
			catch (Exception ex)
			{
				Plugin.LogInfo($"[HOOK] Failed {label}: {ex.GetType().Name} {ex.Message}");
			}
		}

		private static bool SetGlobalColorStringPrefix(string name, Color value)
		{
			if (Logged.Add($"global_color:{name}"))
			{
				Plugin.LogInfo($"[SHADER] SetGlobalColor name='{name}' r={value.r:F3} g={value.g:F3} b={value.b:F3} a={value.a:F3}");
			}

			if (Plugin.IsSuppressionEnabled && IsBloodMoonColor(value) && IsLikelyMoonContext(name, null, null))
			{
				Plugin.LogInfo($"[SHADER] SUPPRESSED SetGlobalColor '{name}'");
				return false;
			}
			return true;
		}

		private static bool SetGlobalColorIntPrefix(int nameID, Color value)
		{
			if (Logged.Add($"global_color_id:{nameID}"))
			{
				Plugin.LogInfo($"[SHADER] SetGlobalColor nameID={nameID} r={value.r:F3} g={value.g:F3} b={value.b:F3} a={value.a:F3}");
			}
			return true;
		}

		private static bool SetGlobalVectorStringPrefix(string name, Vector4 value)
		{
			if (Logged.Add($"global_vector:{name}"))
			{
				Plugin.LogInfo($"[SHADER] SetGlobalVector name='{name}' x={value.x:F3} y={value.y:F3} z={value.z:F3} w={value.w:F3}");
			}
			return true;
		}

		private static bool SetGlobalVectorIntPrefix(int nameID, Vector4 value)
		{
			if (Logged.Add($"global_vector_id:{nameID}"))
			{
				Plugin.LogInfo($"[SHADER] SetGlobalVector nameID={nameID} x={value.x:F3} y={value.y:F3} z={value.z:F3} w={value.w:F3}");
			}
			return true;
		}

		private static bool SetMaterialColorStringPrefix(Material __instance, string name, ref Color value)
		{
			var materialName = SafeName(__instance?.name);
			var shaderName = SafeName(__instance?.shader != null ? __instance.shader.name : null);
			var key = $"mat_color:{shaderName}:{materialName}:{name}";
			if (Logged.Add(key) && (IsBloodMoonColor(value) || IsLikelyMoonContext(name, materialName, shaderName)))
			{
				Plugin.LogInfo($"[MATERIAL] SetColor name='{name}' shader='{shaderName}' material='{materialName}' r={value.r:F3} g={value.g:F3} b={value.b:F3} a={value.a:F3}");
			}

			if (Plugin.IsSuppressionEnabled && IsDarkForegroundPass(name, materialName, shaderName))
			{
				value = new Color(0f, 0f, 0f, 0f);
				var lk = $"forced_df:{shaderName}:{materialName}:{name}";
				if (LoggedForcedNeutral.Add(lk))
				{
					Plugin.LogInfo($"[MATERIAL] FORCED NEUTRAL SetColor name='{name}' shader='{shaderName}' material='{materialName}'");
				}
				return true;
			}

			if (Plugin.IsSuppressionEnabled && IsBloodMoonColor(value) && IsLikelyMoonContext(name, materialName, shaderName))
			{
				// Mutate toward transparent instead of skipping to avoid stale red values.
				value = new Color(0f, 0f, 0f, 0f);
				var lk = $"forced_generic:{shaderName}:{materialName}:{name}";
				if (LoggedForcedNeutral.Add(lk))
				{
					Plugin.LogInfo($"[MATERIAL] FORCED NEUTRAL (generic) SetColor name='{name}' shader='{shaderName}' material='{materialName}'");
				}
			}
			return true;
		}

		private static bool SetMaterialColorIntPrefix(Material __instance, int nameID, ref Color value)
		{
			var materialName = SafeName(__instance?.name);
			var shaderName = SafeName(__instance?.shader != null ? __instance.shader.name : null);
			var key = $"mat_color_id:{shaderName}:{materialName}:{nameID}";
			if (Logged.Add(key) && IsBloodMoonColor(value))
			{
				Plugin.LogInfo($"[MATERIAL] SetColor nameID={nameID} shader='{shaderName}' material='{materialName}' r={value.r:F3} g={value.g:F3} b={value.b:F3} a={value.a:F3}");
			}

			if (Plugin.IsSuppressionEnabled && IsDarkForegroundPass(null, materialName, shaderName) && IsBloodMoonColor(value))
			{
				value = new Color(0f, 0f, 0f, 0f);
				var lk = $"forced_df_id:{shaderName}:{materialName}:{nameID}";
				if (LoggedForcedNeutral.Add(lk))
				{
					Plugin.LogInfo($"[MATERIAL] FORCED NEUTRAL SetColor nameID={nameID} shader='{shaderName}' material='{materialName}'");
				}
			}
			return true;
		}

		private static bool SetMaterialVectorStringPrefix(Material __instance, string name, Vector4 value)
		{
			var materialName = SafeName(__instance?.name);
			var shaderName = SafeName(__instance?.shader != null ? __instance.shader.name : null);
			var key = $"mat_vector:{shaderName}:{materialName}:{name}";
			if (Logged.Add(key) && (IsBloodMoonVector(value) || IsLikelyMoonContext(name, materialName, shaderName)))
			{
				Plugin.LogInfo($"[MATERIAL] SetVector name='{name}' shader='{shaderName}' material='{materialName}' x={value.x:F3} y={value.y:F3} z={value.z:F3} w={value.w:F3}");
			}
			return true;
		}

		private static bool SetMaterialVectorIntPrefix(Material __instance, int nameID, Vector4 value)
		{
			var materialName = SafeName(__instance?.name);
			var shaderName = SafeName(__instance?.shader != null ? __instance.shader.name : null);
			var key = $"mat_vector_id:{shaderName}:{materialName}:{nameID}";
			if (Logged.Add(key) && IsBloodMoonVector(value))
			{
				Plugin.LogInfo($"[MATERIAL] SetVector nameID={nameID} shader='{shaderName}' material='{materialName}' x={value.x:F3} y={value.y:F3} z={value.z:F3} w={value.w:F3}");
			}
			return true;
		}

		private static bool IsBloodMoonColor(Color c)
			=> c.a > 0.02f && c.r > 0.1f && c.r > c.g * 1.4f && c.r > c.b * 1.4f;

		private static bool IsBloodMoonVector(Vector4 v)
			=> v.x > 0.1f && v.x > v.y * 1.4f && v.x > v.z * 1.4f;

		private static bool IsLikelyMoonContext(string? propertyName, string? materialName, string? shaderName)
		{
			var text = $"{propertyName} {materialName} {shaderName}".ToLowerInvariant();
			return text.Contains("blood")
				|| text.Contains("moon")
				|| text.Contains("tint")
				|| text.Contains("hue")
				|| text.Contains("overlay")
				|| text.Contains("post")
				|| text.Contains("fog")
				|| text.Contains("sky")
				|| text.Contains("atmo")
				|| text.Contains("vignette")
				|| text.Contains("grade");
		}

		private static bool IsDarkForegroundPass(string? propertyName, string materialName, string shaderName)
		{
			if (!shaderName.Equals("Hidden/Shader/DarkForeground", StringComparison.OrdinalIgnoreCase)
				&& !materialName.Equals("Hidden/Shader/DarkForeground", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			if (propertyName == null)
			{
				return true;
			}

			return propertyName.Equals("_ForegroundColor", StringComparison.OrdinalIgnoreCase)
				|| propertyName.Equals("_TintColor", StringComparison.OrdinalIgnoreCase)
				|| propertyName.Equals("_OverlayColor", StringComparison.OrdinalIgnoreCase);
		}

		private static string SafeName(string? value)
			=> string.IsNullOrWhiteSpace(value) ? "<null>" : value;
	}

	internal static class SessionFileLogger
	{
		private static readonly object Sync = new();
		private static StreamWriter? _writer;
		private static string? _logPath;

		internal static void Initialize(string pluginName)
		{
			var logsDir = Path.Combine(Paths.BepInExRootPath, "logs");
			Directory.CreateDirectory(logsDir);

			_logPath = Path.Combine(logsDir, $"{pluginName}.log");
			RotateExistingFile(_logPath);

			_writer = new StreamWriter(_logPath, false)
			{
				AutoFlush = true
			};

			Write("INFO", "Session logging started.");
		}

		internal static void Info(string message) => Write("INFO", message);

		private static void Write(string level, string message)
		{
			lock (Sync)
			{
				if (_writer == null)
				{
					return;
				}

				var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
				_writer.WriteLine($"[{timestamp}] [{level}] {message}");
			}
		}

		private static void RotateExistingFile(string currentPath)
		{
			if (!File.Exists(currentPath))
			{
				return;
			}

			var dir = Path.GetDirectoryName(currentPath)!;
			var file = Path.GetFileNameWithoutExtension(currentPath);
			var ext = Path.GetExtension(currentPath);
			var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
			var rotatedPath = Path.Combine(dir, $"{file}.{stamp}{ext}");

			File.Move(currentPath, rotatedPath, true);
		}
	}


}
