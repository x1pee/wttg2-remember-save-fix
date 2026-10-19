using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using Wttg2RememberSaveFix;

class RememberFixTests
{
    static int passed;
    static void Check(bool condition, string name)
    { if (!condition) throw new Exception("FAILED: " + name); passed++; Console.WriteLine("PASS " + name); }
    static void Set(object instance, string name, object value)
    { instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(instance, value); }
    static void Main(string[] args)
    {
        string game = args[0];
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs request)
        {
            string name = new AssemblyName(request.Name).Name + ".dll";
            foreach (string folder in new string[] { Path.Combine(game, "BepInEx/core"), Path.Combine(game, "WTTG2_Data/Managed"), Path.Combine(game, "BepInEx/plugins") })
            { string candidate = Path.Combine(folder, name); if (File.Exists(candidate)) return Assembly.LoadFrom(candidate); }
            return null;
        };
        Run(game);
    }
    static void Run(string game)
    {
        string a = RememberScopePolicy.NetworkKey(1, "ab:cd:ef:12:34");
        string b = RememberScopePolicy.NetworkKey(3, "aa:bb:cc:dd:ee");
        string c = RememberScopePolicy.NetworkKey(2, "AA:BB:CC:DD:EF");
        Check(a == "1|AB:CD:EF:12:34", "normalized five-octet game BSSID");
        Check(RememberScopePolicy.NetworkKey(1, "zz:yy:wx:12:34") == "1|ZZ:YY:WX:12:34", "game alphanumeric BSSID accepted beyond hexadecimal");
        Check(RememberScopePolicy.NetworkKey(1, "AA:BB:CC:D!:EF") == null, "non-game punctuation rejected");
        Check(RememberScopePolicy.NetworkKey(0, "ab:cd:ef:12:34") == null, "open network excluded");
        Check(RememberScopePolicy.NetworkKey(4, "ab:cd:ef:12:34") == null, "unknown security excluded");
        Check(RememberScopePolicy.NetworkKey(1, "not-ready") == null, "uninitialized BSSID excluded");
        string scope = RememberScopePolicy.SaveScope(new string[] { a, b });
        Check(scope == RememberScopePolicy.SaveScope(new string[] { b, a, a }), "scope survives ordering and duplicates");
        Check(scope != RememberScopePolicy.SaveScope(new string[] { a, c }), "new save BSSIDs yield isolated scope");
        Check(RememberScopePolicy.SaveScope(new string[] { a }) == null, "partial initialization does not create scope");
        Check(new Guid(scope).ToString("N") == scope, "scope accepted by legacy GUID format");

        Assembly legacy = Assembly.LoadFrom(Path.Combine(game, "BepInEx/plugins/Epic_RememberNetworks.dll"));
        Assembly bridge = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WTTG2RememberSaveFix.dll"));
        Type recordType = legacy.GetType("EpicMods.WTTG2.RememberNetworks.CredentialRecord", true);
        Type bridgeType = bridge.GetType("Wttg2RememberSaveFix.RememberSaveFix", true);
        object fix = FormatterServices.GetUninitializedObject(bridgeType);
        bridgeType.GetMethod("ResolveLegacyBindings", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(fix, null);
        Check(true, "all installed legacy reflection bindings resolve");
        Type recordMap = typeof(Dictionary<,>).MakeGenericType(typeof(string), recordType);
        Type storeType = typeof(Dictionary<,>).MakeGenericType(typeof(string), recordMap);
        IDictionary store = (IDictionary)Activator.CreateInstance(storeType);
        IDictionary oldRecords = (IDictionary)Activator.CreateInstance(recordMap);
        object record = Activator.CreateInstance(recordType);
        Set(record, "SaveId", new string('1', 32)); Set(record, "Bssid", "AB:CD:EF:12:34"); Set(record, "Security", 1);
        Set(record, "Essid", "Synthetic known network"); Set(record, "Password", "synthetic-learned-A");
        oldRecords.Add(a, record); store.Add(new string('1', 32), oldRecords);
        MethodInfo recover = bridgeType.GetMethod("RecoverMatchingRecords", BindingFlags.Instance | BindingFlags.NonPublic);
        HashSet<string> keys = new HashSet<string>(new string[] { a, b }, StringComparer.Ordinal);
        int count = (int)recover.Invoke(fix, new object[] { store, scope, keys });
        Check(count == 1, "known record recovered from old save lineage");
        object recovered = ((IDictionary)store[scope])[a];
        FieldInfo saveId = recordType.GetField("SaveId", BindingFlags.Instance | BindingFlags.NonPublic);
        Check((string)saveId.GetValue(recovered) == scope, "record SaveId survives legacy writer serialization");
        Check((string)saveId.GetValue(record) == new string('1', 32), "legacy source record remains unchanged");
        Check((int)recover.Invoke(fix, new object[] { store, scope, keys }) == 0, "recovery is idempotent");
        Check(!((IDictionary)store[scope]).Contains(b), "unlearned network is not invented");

        IDictionary other = (IDictionary)Activator.CreateInstance(recordMap);
        object conflict = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(record, null);
        Set(conflict, "Password", "synthetic-learned-B"); other.Add(a, conflict); store.Add(new string('2', 32), other);
        string anotherScope = new string('3', 32);
        Check((int)recover.Invoke(fix, new object[] { store, anotherScope, keys }) == 0 && !store.Contains(anotherScope), "ambiguous known passwords skipped");
        IDictionary globalOnly = (IDictionary)Activator.CreateInstance(storeType); globalOnly.Add("global", oldRecords);
        Check((int)recover.Invoke(fix, new object[] { globalOnly, scope, keys }) == 0, "global-mode records do not leak into isolated saves");
        IDictionary unrelated = (IDictionary)Activator.CreateInstance(storeType); unrelated.Add(new string('1',32), oldRecords);
        Check((int)recover.Invoke(fix, new object[] { unrelated, scope, new HashSet<string>(new string[] { b, c }) }) == 0, "different-save BSSID records excluded");
        Type legacyType = legacy.GetType("EpicMods.WTTG2.RememberNetworks.EpicRememberNetworksPlugin", true);
        object legacyWriter = FormatterServices.GetUninitializedObject(legacyType);
        Type loggerType = Assembly.LoadFrom(Path.Combine(game, "BepInEx/core/BepInEx.dll")).GetType("BepInEx.Logging.ManualLogSource", true);
        legacyType.BaseType.GetField("<Logger>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(legacyWriter, Activator.CreateInstance(loggerType, new object[] { "RememberFixSyntheticTest" }));
        string testDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-state");
        Directory.CreateDirectory(testDirectory);
        string statePath = Path.Combine(testDirectory, "synthetic.state");
        Set(legacyWriter, "stateDirectory", testDirectory); Set(legacyWriter, "statePath", statePath);
        Set(legacyWriter, "stateBackupPath", statePath + ".bak"); Set(legacyWriter, "currentSaveId", scope);
        Set(legacyWriter, "persistedCredentials", store);
        MethodInfo writer = legacyType.GetMethod("WritePersistentState", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo reader = legacyType.GetMethod("TryReadStateFile", BindingFlags.Instance | BindingFlags.NonPublic);
        writer.Invoke(legacyWriter, new object[] { true });
        object[] readArgs = new object[] { statePath, null, null, 0 };
        Check((bool)reader.Invoke(legacyWriter, readArgs), "original legacy writer and parser roundtrip succeeds");
        Check((string)readArgs[1] == scope && ((IDictionary)readArgs[2]).Contains(scope), "recovered scope persists through real state writer/parser");
        writer.Invoke(legacyWriter, new object[] { true });
        Check(File.Exists(statePath + ".bak"), "legacy atomic replacement backup survives recovery write");
        Console.WriteLine("TOTAL " + passed + " passed; synthetic credentials only; no live game or save modified.");
    }
}
