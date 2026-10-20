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
    [BepInPlugin(PluginGuid, "WTTG2 Remember Save Fix", "1.0.1")]
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
        private bool stageLoaded;
        private bool restoring;
        private WifiManager restoredWifi;
        private string restoredScope;
        private float retryAt, retryUntil;

        private sealed class RejectedCredential
        {
            public IDictionary Store;
            public string Scope, Key, Password;
        }

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
                harmony.Patch(RequiredMethod(legacyType, "OnGameDataCleared"), new HarmonyMethod(typeof(RememberSaveFix).GetMethod("BeforeTeardown", BindingFlags.Static | BindingFlags.NonPublic)));
                harmony.Patch(RequiredMethod(legacyType, "RemoveCredential"),
                    new HarmonyMethod(typeof(RememberSaveFix).GetMethod("BeforeRemove", BindingFlags.Static | BindingFlags.NonPublic)),
                    new HarmonyMethod(typeof(RememberSaveFix).GetMethod("AfterRemove", BindingFlags.Static | BindingFlags.NonPublic)));
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
            self.ClearRestoreState();
            self.stageLoaded = true;
            self.pending = true;
            self.retryAt = 0;
            self.retryUntil = Time.unscaledTime + 90f;
            self.TryRestore(__instance);
        }
        private static void BeforeUse(object __instance)
        {
            if (self == null || self.failed || !self.enabledEntry.Value || !self.stageLoaded) return;
            // This synchronous guard also covers unusually slow staging and a reopened menu.
            self.TryRestore(__instance);
        }
        private static void BeforeTeardown()
        {
            if (self != null) self.ClearRestoreState();
        }
        private void ClearRestoreState()
        {
            pending = false;
            stageLoaded = false;
            restoredWifi = null;
            restoredScope = null;
        }
        private static void BeforeRemove(object __instance, string __0, string __1, out object __state)
        {
            __state = null;
            if (self == null || self.failed || !self.enabledEntry.Value) return;
            try { __state = self.CaptureRejectedCredential(__instance, __0, __1); }
            catch (Exception ex) { self.Fail("Could not bind credential invalidation; bridge disabled", ex); }
        }
        private static void AfterRemove(object __instance, bool __result, object __state)
        {
            if (!__result || __state == null || self == null || self.failed) return;
            try
            {
                int removed = self.RemoveRejectedAliases((RejectedCredential)__state);
                if (removed != 0 && ConfigBool(self.rememberField, __instance))
                    self.writeState.Invoke(__instance, new object[] { true });
            }
            catch (Exception ex) { self.Fail("Could not finish credential invalidation; bridge disabled", ex); }
        }
        private RejectedCredential CaptureRejectedCredential(object legacy, string scope, string key)
        {
            if (!System.Object.ReferenceEquals(legacy, instanceField.GetValue(null)) ||
                !ConfigBool(enabledLegacyField, legacy) || !ConfigBool(separateField, legacy) ||
                String.Equals(scope, "global", StringComparison.Ordinal) ||
                !String.Equals(scope, restoredScope, StringComparison.Ordinal)) return null;
            IDictionary store = (IDictionary)(ConfigBool(rememberField, legacy) ? persistedField : sessionField).GetValue(legacy);
            IDictionary records = store.Contains(scope) ? store[scope] as IDictionary : null;
            object record = records != null && records.Contains(key) ? records[key] : null;
            if (!RecordMatches(record, scope, key)) return null;
            return new RejectedCredential { Store = store, Scope = scope, Key = key,
                Password = credentialPasswordField.GetValue(record) as string };
        }
        private int RemoveRejectedAliases(RejectedCredential rejected)
        {
            // Epic calls RemoveCredential only after vanilla password rejection. Forget matching
            // migration sources too; otherwise they reappear on the next load/menu refresh.
            int removed = 0;
            List<object> emptyScopes = new List<object>();
            foreach (DictionaryEntry scope in rejected.Store)
            {
                string scopeId = scope.Key as string;
                if (String.Equals(scopeId, "global", StringComparison.Ordinal) ||
                    String.Equals(scopeId, rejected.Scope, StringComparison.Ordinal)) continue;
                IDictionary records = scope.Value as IDictionary;
                object record = records != null && records.Contains(rejected.Key) ? records[rejected.Key] : null;
                if (!RecordMatches(record, scopeId, rejected.Key) ||
                    !String.Equals(credentialPasswordField.GetValue(record) as string, rejected.Password, StringComparison.Ordinal)) continue;
                records.Remove(rejected.Key);
                if (records.Count == 0) emptyScopes.Add(scope.Key);
                removed++;
            }
            foreach (object scope in emptyScopes) rejected.Store.Remove(scope);
            return removed;
        }
        private bool RecordMatches(object record, string scope, string key)
        {
            if (record == null || key == null || scope == null ||
                !String.Equals(credentialSaveField.GetValue(record) as string, scope, StringComparison.Ordinal)) return false;
            string bssid = credentialBssidField.GetValue(record) as string;
            string storedKey = RememberScopePolicy.NetworkKey((int)credentialSecurityField.GetValue(record), bssid);
            return String.Equals(key, storedKey, StringComparison.Ordinal);
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
            if (restoring) return;
            restoring = true;
            try { if (RestoreScope(legacy)) pending = false; }
            catch (Exception ex) { Fail("Loaded-save scope recovery failed; original Remember Networks remains available", ex); }
            finally { restoring = false; }
        }

        private bool RestoreScope(object legacy)
        {
            if (legacy == null || !System.Object.ReferenceEquals(legacy, instanceField.GetValue(null))) return false;
            if (!ConfigBool(enabledLegacyField, legacy) || !ConfigBool(separateField, legacy)) return true;
            ManagerSlinger managers = GameManager.ManagerSlinger;
            if (managers == null) return false;
            WifiManager wifi = managers.WifiManager;
            if (wifi == null) return false;
            string previous = saveField.GetValue(legacy) as string;
            if (System.Object.ReferenceEquals(restoredWifi, wifi) &&
                String.Equals(previous, restoredScope, StringComparison.Ordinal)) return true;
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (WifiNetworkDefinition network in wifi.GetAllWifiNetworks())
            {
                if (network == null) return false;
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
            saveField.SetValue(legacy, scope);
            restoredWifi = wifi;
            restoredScope = scope;
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
                    if (key == null || !activeKeys.Contains(key) || !RecordMatches(record.Value, scope.Key as string, key)) continue;
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
