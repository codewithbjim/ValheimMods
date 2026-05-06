using System;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace NoMapDiscordAdditions
{
    internal static class ServerSyncCompat
    {
        public static bool IsActive { get; private set; }

        private static object _configSync;
        private static PropertyInfo _syncedProp;

        public static void Init(ConfigFile config, string modGuid, string displayName, string version)
        {
            if (IsActive) return;

            try
            {
                // ServerSync.ConfigSync lives in the external "ServerSync" assembly.
                // We use reflection so this mod can run without a hard dependency.
                var configSyncType = Type.GetType("ServerSync.ConfigSync, ServerSync");
                if (configSyncType == null)
                    return;

                _configSync = Activator.CreateInstance(configSyncType, modGuid);
                if (_configSync == null)
                    return;

                // Set metadata like AzuCraftyBoxes does.
                SetProp(_configSync, "DisplayName", displayName);
                SetProp(_configSync, "CurrentVersion", version);
                SetProp(_configSync, "MinimumRequiredVersion", version);

                // Optional: a "Lock Configuration" entry is the common ServerSync pattern.
                var lockEntry = config.Bind("Discord", "Lock Configuration", false,
                    "If true, configuration can only be changed by the server/admins.");

                InvokeGeneric(_configSync, "AddLockingConfigEntry", lockEntry);

                // Mark specific entries as server-synced.
                AddSynced(lockEntry, false); // lock entry itself is not marked synced in many mods; it is used for locking.
                AddSynced(Plugin.CaptureMethod, true);
                AddSynced(Plugin.CaptureSuperSize, true);
                AddSynced(Plugin.SpoilerImageData, true);
                AddSynced(Plugin.HideClouds, true);
                AddSynced(Plugin.EnableCartographyTableLabels, true);

                IsActive = true;
                Debug.Log("[NoMapDiscordAdditions] ServerSync detected; using server-authoritative config sync.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NoMapDiscordAdditions] ServerSync init failed; falling back to RPC sync. {e.Message}");
                IsActive = false;
                _configSync = null;
                _syncedProp = null;
            }
        }

        private static void AddSynced<T>(ConfigEntry<T> entry, bool synchronizedSetting)
        {
            if (_configSync == null) return;

            object syncedEntry = InvokeGeneric(_configSync, "AddConfigEntry", entry);
            if (syncedEntry == null) return;

            _syncedProp ??= syncedEntry.GetType().GetProperty("SynchronizedConfig",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            _syncedProp?.SetValue(syncedEntry, synchronizedSetting, null);
        }

        private static void SetProp(object obj, string propName, object value)
        {
            var p = obj.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            p?.SetValue(obj, value, null);
        }

        private static object InvokeGeneric<T>(object obj, string methodName, ConfigEntry<T> arg)
        {
            var methods = obj.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var m in methods)
            {
                if (m.Name != methodName) continue;
                if (!m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;

                var gm = m.MakeGenericMethod(typeof(T));
                return gm.Invoke(obj, new object[] { arg });
            }
            return null;
        }
    }
}

