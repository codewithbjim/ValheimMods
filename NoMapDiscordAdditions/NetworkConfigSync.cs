using System;
using UnityEngine;

namespace NoMapDiscordAdditions
{
    internal static class NetworkConfigSync
    {
        private const string RpcRequestConfig = "NMDA_RequestConfig";
        private const string RpcReceiveConfig = "NMDA_ReceiveConfig";
        private const int ProtocolVersion = 5;

        private static bool _initialized;
        private static bool _requestedFromServer;

        private static Plugin.CaptureMethodMode? _serverCaptureMethod;
        private static int? _serverCaptureSuperSize;
        private static bool? _serverSpoilerImageData;
        private static bool? _serverHideClouds;
        private static bool? _serverEnableCartographyTableLabels;
        // Stored in memory only — never written to the client's config file.
        private static string _serverWebhookUrl;

        public static bool EffectiveUseTextureCapture =>
            (_serverCaptureMethod ?? Plugin.CaptureMethod.Value) == Plugin.CaptureMethodMode.TextureCapture;

        public static int EffectiveCaptureSuperSize =>
            _serverCaptureSuperSize ?? Plugin.CaptureSuperSize.Value;

        public static bool EffectiveSpoilerImageData =>
            _serverSpoilerImageData ?? Plugin.SpoilerImageData.Value;

        public static bool EffectiveHideClouds =>
            _serverHideClouds ?? Plugin.HideClouds.Value;

        public static bool EffectiveEnableCartographyTableLabels =>
            _serverEnableCartographyTableLabels ?? (Plugin.EnableCartographyTableLabels?.Value ?? true);

        public static string EffectiveWebhookUrl =>
            !string.IsNullOrEmpty(_serverWebhookUrl) ? _serverWebhookUrl : Plugin.WebhookUrl.Value;

        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;

            // Keep it simple/robust: poll until ZRoutedRpc is alive, then register RPCs.
            // This works for both server and client, and is safe across hot-reload.
            if (Plugin.Instance != null)
                Plugin.Instance.StartCoroutine(RegisterWhenReady());
        }

        private static System.Collections.IEnumerator RegisterWhenReady()
        {
            while (ZRoutedRpc.instance == null)
                yield return null;

            // Use ZPackage to avoid generic overload/version differences.
            if (!TryRegisterRpc(RpcRequestConfig, RPC_RequestConfig) ||
                !TryRegisterRpc(RpcReceiveConfig, RPC_ReceiveConfig))
                yield break;

            // Client: request server config once we have networking.
            if (ZNet.instance != null && !ZNet.instance.IsServer())
                RequestFromServer();
        }

        private static bool TryRegisterRpc(string name, Action<long, ZPackage> handler)
        {
            try
            {
                ZRoutedRpc.instance.Register(name, handler);
                return true;
            }
            catch (ArgumentException)
            {
                // Hot-reload can leave prior handlers registered in ZRoutedRpc.
                // If already registered, treat as success and keep using the existing hook.
                Debug.Log($"[NoMapDiscordAdditions] RPC already registered (hot-reload): {name}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NoMapDiscordAdditions] Failed to register RPC {name}: {e}");
                return false;
            }
        }

        public static void Tick()
        {
            // Called from Plugin.Update(): on clients, request once after connection.
            if (_requestedFromServer) return;
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            if (ZRoutedRpc.instance == null) return;

            RequestFromServer();
        }

        private static void RequestFromServer()
        {
            if (_requestedFromServer) return;
            _requestedFromServer = true;

            long serverPeerId = TryGetServerPeerId();
            var pkg = new ZPackage();
            pkg.Write(ProtocolVersion);

            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(serverPeerId, RpcRequestConfig, pkg);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NoMapDiscordAdditions] Config request failed (no server override?): {e.Message}");
            }
        }

        private static void RPC_RequestConfig(long sender, ZPackage pkg)
        {
            // Only the server should answer; if a client receives this, ignore it.
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var reply = new ZPackage();
            reply.Write(ProtocolVersion);
            reply.Write((int)Plugin.CaptureMethod.Value);
            reply.Write(Plugin.CaptureSuperSize.Value);
            reply.Write(Plugin.SpoilerImageData.Value);
            reply.Write(Plugin.HideClouds.Value);
            reply.Write(Plugin.EnableCartographyTableLabels?.Value ?? true);
            reply.Write(Plugin.WebhookUrl.Value ?? "");

            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcReceiveConfig, reply);
        }

        private static void RPC_ReceiveConfig(long sender, ZPackage pkg)
        {
            try
            {
                int ver = pkg.ReadInt();
                if (ver != ProtocolVersion)
                    return;

                int captureMethodRaw = pkg.ReadInt();
                _serverCaptureMethod = Enum.IsDefined(typeof(Plugin.CaptureMethodMode), captureMethodRaw)
                    ? (Plugin.CaptureMethodMode)captureMethodRaw
                    : Plugin.CaptureMethodMode.ScreenCapture;
                _serverCaptureSuperSize = pkg.ReadInt();
                _serverSpoilerImageData = pkg.ReadBool();
                _serverHideClouds = pkg.ReadBool();
                _serverEnableCartographyTableLabels = pkg.ReadBool();
                string url = pkg.ReadString();
                _serverWebhookUrl = string.IsNullOrEmpty(url) ? null : url;

                CaptureButton.RefreshEnabledState();

                Debug.Log(
                    $"[NoMapDiscordAdditions] Server-authoritative config applied: " +
                    $"CaptureMethod={_serverCaptureMethod}, CaptureSuperSize={_serverCaptureSuperSize}, " +
                    $"SpoilerImageData={_serverSpoilerImageData}, HideClouds={_serverHideClouds}, " +
                    $"EnableCartographyTableLabels={_serverEnableCartographyTableLabels}, " +
                    $"HasWebhookUrl={_serverWebhookUrl != null}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NoMapDiscordAdditions] Failed to read server config: {e}");
            }
        }

        private static long TryGetServerPeerId()
        {
            try
            {
                // Prefer an actual API if it exists in this Valheim build.
                var mi = typeof(ZRoutedRpc).GetMethod("GetServerPeerID",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (mi != null && ZRoutedRpc.instance != null)
                    return (long)mi.Invoke(ZRoutedRpc.instance, null);
            }
            catch
            {
                // ignore and fall back
            }

            // Commonly the server is peer 1 in routed RPC, but this fallback is only used
            // if we can't reflect an official method.
            return 1L;
        }
    }
}

