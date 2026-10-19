using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Wttg2RememberSaveFix
{
    [BepInPlugin(PluginGuid, "WTTG2 Remember Save Fix", "1.0.0")]
    [BepInDependency("Epic_RememberNetworks", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class RememberSaveFix : BaseUnityPlugin
    {
        public const string PluginGuid = "local.wttg2.remember-save-fix";
        private static RememberSaveFix self;
        private Harmony harmony;
        private ConfigEntry<bool> enabledEntry;
        private ConfigEntry<bool> migrateEntry;
        private Type legacyType;
        private FieldInfo instanceField, saveField, persistedField, sessionField;
        private FieldInfo enabledLegacyField, separateField, rememberField;
        private FieldInfo credentialSaveField, credentialSecurityField, credentialBssidField, credentialPasswordField;
        private MethodInfo writeState, refreshRows;
        private bool failed;
        private bool pending;
        private float retryAt, retryUntil;

        private void Awake()
        {
            self = this;
            enabledEntry = Config.Bind("General", "EnableScopeHook", true,
                "Reconnect Epic Remember Networks to the loaded save's network identity. Restart after changing.");
            migrateEntry = Config.Bind("General", "RecoverPreviouslyRememberedNetworks", true,
                "Recover only existing successful-connection records with matching BSSID and security. No hidden game passwords are read.");
            if (!enabledEntry.Value) { Logger.LogInfo("Save scope hook disabled by configuration."); return; }
            try
            {
                ResolveLegacyBindings();
                harmony = new Harmony(PluginGuid);
                MethodInfo target = RequiredMethod(legacyType, "OnGameStageLoaded");
                harmony.Patch(target, new HarmonyMethod(typeof(RememberSaveFix).GetMethod("BeforeStage", BindingFlags.Static | BindingFlags.NonPublic)));
                HarmonyMethod beforeUse = new HarmonyMethod(typeof(RememberSaveFix).GetMethod("BeforeUse", BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(RequiredMethod(legacyType, "OnDialogPresent"), beforeUse);
                harmony.Patch(RequiredMethod(legacyType, "OnRowBuild"), beforeUse);
                harmony.Patch(RequiredMethod(legacyType, "OnGameplayTeardown"), new HarmonyMethod(typeof(RememberSaveFix).GetMethod("BeforeTeardown", BindingFlags.Static | BindingFlags.NonPublic)));
                Logger.LogInfo("Loaded-save scope hook installed. Only BSSID/security identities and existing remembered records are used.");
            }
            catch (Exception ex) { Fail("Could not bind the installed Epic Remember Networks; bridge disabled", ex); }
        }

        private void ResolveLegacyBindings()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidate = assembly.GetType("EpicMods.WTTG2.RememberNetworks.EpicRememberNetworksPlugin", false);
                if (candidate != null) { legacyType = candidate; break; }
            }
            if (legacyType == null) throw new MissingMemberException("Epic Remember Networks type");
            instanceField = RequiredField(legacyType, "Instance");
            saveField = RequiredField(legacyType, "currentSaveId");
            persistedField = RequiredField(legacyType, "persistedCredentials");
            sessionField = RequiredField(legacyType, "sessionCredentials");
            enabledLegacyField = RequiredField(legacyType, "enabledEntry");
            separateField = RequiredField(legacyType, "separateCredentialsBySaveEntry");
            rememberField = RequiredField(legacyType, "rememberAcrossSessionsEntry");
            writeState = RequiredMethod(legacyType, "WritePersistentState");
            refreshRows = RequiredMethod(legacyType, "RefreshRememberedRows");
            Type credential = legacyType.Assembly.GetType("EpicMods.WTTG2.RememberNetworks.CredentialRecord", true);
            credentialSaveField = RequiredField(credential, "SaveId");
            credentialSecurityField = RequiredField(credential, "Security");
            credentialBssidField = RequiredField(credential, "Bssid");
            credentialPasswordField = RequiredField(credential, "Password");
        }

        private static FieldInfo RequiredField(Type type, string name)
        {
            FieldInfo result = type.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (result == null) throw new MissingFieldException(type.FullName, name);
            return result;
        }
        private static MethodInfo RequiredMethod(Type type, string name)
        {
            MethodInfo result = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (result == null) throw new MissingMethodException(type.FullName, name);
            return result;
        }
        private static bool ConfigBool(FieldInfo field, object plugin)
        {
            ConfigEntry<bool> entry = field.GetValue(plugin) as ConfigEntry<bool>;
            return entry != null && entry.Value;
        }
        private static void BeforeStage(object __instance)
        {
            if (self == null || self.failed || !self.enabledEntry.Value) return;
            self.pending = true;
            self.retryAt = 0;
            self.retryUntil = Time.unscaledTime + 90f;
            self.TryRestore(__instance);
        }
        private static void BeforeUse(object __instance)
        {
            if (self == null || self.failed || !self.enabledEntry.Value) return;
            // This synchronous guard also covers unusually slow staging and a reopened menu.
            self.TryRestore(__instance);
        }
        private static void BeforeTeardown()
        {
            if (self != null) self.pending = false;
        }
        private void Update()
        {
            if (!pending || failed || !enabledEntry.Value || Time.unscaledTime < retryAt) return;
            retryAt = Time.unscaledTime + .25f;
            if (Time.unscaledTime > retryUntil)
            {
                pending = false;
                Logger.LogWarning("Network identities are still unavailable; recovery will retry when the Wi-Fi menu or password dialog opens.");
                return;
            }
            TryRestore(instanceField.GetValue(null));
        }
        private void TryRestore(object legacy)
        {
            try { if (RestoreScope(legacy)) pending = false; }
            catch (Exception ex) { self.Fail("Loaded-save scope recovery failed; original Remember Networks remains available", ex); }
        }

        private bool RestoreScope(object legacy)
        {
            if (legacy == null || !System.Object.ReferenceEquals(legacy, instanceField.GetValue(null))) return false;
            if (!ConfigBool(enabledLegacyField, legacy) || !ConfigBool(separateField, legacy)) return true;
            ManagerSlinger managers = GameManager.ManagerSlinger;
            if (managers == null) return false;
            WifiManager wifi = managers.WifiManager;
            if (wifi == null) return false;
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (WifiNetworkDefinition network in wifi.GetAllWifiNetworks())
            {
                if (network == null) continue;
                if ((int)network.networkSecurity < 1) continue;
                string key = RememberScopePolicy.NetworkKey((int)network.networkSecurity, network.networkBSSID);
                // Never fingerprint a partly initialized list: another hotspot may stage later.
                if (key == null) return false;
                keys.Add(key);
            }
            string scope = RememberScopePolicy.SaveScope(keys);
            if (scope == null) return false;
            IDictionary persisted = (IDictionary)persistedField.GetValue(legacy);
            IDictionary session = (IDictionary)sessionField.GetValue(legacy);
            int recovered = 0;
            if (migrateEntry.Value)
            {
                recovered += RecoverMatchingRecords(persisted, scope, keys);
                recovered += RecoverMatchingRecords(session, scope, keys);
            }
            string previous = saveField.GetValue(legacy) as string;
            saveField.SetValue(legacy, scope);
            if (!String.Equals(previous, scope, StringComparison.Ordinal) || recovered != 0)
            {
                // Use the original plugin's existing atomic writer/backup; this bridge has no credential file format.
                if (ConfigBool(rememberField, legacy)) writeState.Invoke(legacy, new object[] { true });
                Logger.LogInfo("Loaded-save network scope restored; " + recovered + " existing remembered record(s) recovered. No credentials logged.");
                refreshRows.Invoke(legacy, new object[] { "loaded-save scope recovery" });
            }
            return true;
        }

        private int RecoverMatchingRecords(IDictionary store, string targetScope, HashSet<string> activeKeys)
        {
            IDictionary target = store.Contains(targetScope) ? (IDictionary)store[targetScope] :
                (IDictionary)Activator.CreateInstance(store.GetType().GetGenericArguments()[1]);
            Dictionary<string, object> candidates = new Dictionary<string, object>(StringComparer.Ordinal);
            HashSet<string> ambiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (DictionaryEntry scope in store)
            {
                // Explicit global mode records must stay global and are never imported into isolated saves.
                if (String.Equals(scope.Key as string, "global", StringComparison.Ordinal)) continue;
                IDictionary records = scope.Value as IDictionary;
                if (records == null) continue;
                foreach (DictionaryEntry record in records)
                {
                    string key = record.Key as string;
                    if (key == null || !activeKeys.Contains(key) || record.Value == null) continue;
                    string bssid = credentialBssidField.GetValue(record.Value) as string;
                    string storedKey = RememberScopePolicy.NetworkKey((int)credentialSecurityField.GetValue(record.Value), bssid);
                    if (!String.Equals(key, storedKey, StringComparison.Ordinal)) continue;
                    object existing;
                    if (candidates.TryGetValue(key, out existing) &&
                        !String.Equals(credentialPasswordField.GetValue(existing) as string,
                            credentialPasswordField.GetValue(record.Value) as string, StringComparison.Ordinal)) ambiguous.Add(key);
                    else candidates[key] = record.Value;
                }
            }
            int recovered = 0;
            foreach (KeyValuePair<string, object> candidate in candidates)
            {
                if (!ambiguous.Contains(candidate.Key) && !target.Contains(candidate.Key))
                {
                    // The original file writer serializes CredentialRecord.SaveId, not its dictionary scope.
                    // Clone into the new scope so restarting the game preserves this migration too.
                    object clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(candidate.Value, null);
                    credentialSaveField.SetValue(clone, targetScope);
                    target.Add(candidate.Key, clone);
                    recovered++;
                }
            }
            if (recovered != 0 && !store.Contains(targetScope)) store.Add(targetScope, target);
            return recovered;
        }

        private void Fail(string message, Exception ex)
        {
            failed = true;
            // Exception messages can contain other mods' input. Log only the type.
            Logger.LogWarning(message + " (" + ex.GetType().Name + "). No passwords logged.");
        }
        private void OnDestroy()
        {
            if (harmony != null) harmony.UnpatchSelf();
            if (System.Object.ReferenceEquals(self, this)) self = null;
        }
    }
}
