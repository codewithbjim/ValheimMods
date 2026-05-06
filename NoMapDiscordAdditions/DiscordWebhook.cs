using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace NoMapDiscordAdditions
{
    public static class DiscordWebhook
    {
        public static IEnumerator SendImage(byte[] imageData, string filename, string message, bool useSpoilerTag)
        {
            var webhookUrl = ModHelpers.EffectiveConfig.WebhookUrl;
            if (string.IsNullOrEmpty(webhookUrl))
            {
                Debug.LogWarning("[NoMapDiscordAdditions] Webhook URL not configured.");
                yield break;
            }

            var form = new WWWForm();
            form.AddBinaryData("files[0]", imageData, GetAttachmentFileName(filename, useSpoilerTag), "image/jpeg");

            if (!string.IsNullOrEmpty(message))
            {
                form.AddField("payload_json",
                    "{\"content\":\"" + EscapeJson(message) + "\"}");
            }

            using (var request = UnityWebRequest.Post(webhookUrl, form))
            {
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[NoMapDiscordAdditions] Discord webhook failed: {request.error}");
                }
                else
                {
                    Debug.Log("[NoMapDiscordAdditions] Map screenshot sent to Discord.");
                }
            }
        }

        private static string EscapeJson(string text)
        {
            return text
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
        }

        private static string GetAttachmentFileName(string filename, bool useSpoilerTag)
        {
            if (!useSpoilerTag || string.IsNullOrEmpty(filename) || filename.StartsWith("SPOILER_"))
                return filename;

            return "SPOILER_" + filename;
        }
    }
}
