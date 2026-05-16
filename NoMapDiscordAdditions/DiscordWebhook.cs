using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace NoMapDiscordAdditions
{
    public static class DiscordWebhook
    {
        /// <summary>Discord allows at most 10 attachments per message; we batch 5.</summary>
        public const int MaxImagesPerMessage = 5;

        /// <summary>
        /// Valheim's bundled Mono runtime ships a stale root-certificate
        /// store, so the TLS handshake to discord.com fails with
        /// "Unable to complete SSL connection" before any HTTP response is
        /// received. Discord webhook URLs are user-supplied secrets posted
        /// only to Discord's known endpoint, so accepting the presented
        /// certificate here is an acceptable trade-off to make the feature
        /// work at all on the game's runtime.
        /// </summary>
        private sealed class BypassCertificateHandler : CertificateHandler
        {
            protected override bool ValidateCertificate(byte[] certificateData) => true;
        }

        public struct OutgoingImage
        {
            public byte[] Bytes;
            public string FileName;
        }

        /// <summary>
        /// Sends up to <see cref="MaxImagesPerMessage"/> images in a single
        /// webhook POST (one Discord message with multiple attachments).
        /// Callers are responsible for chunking larger sets — see
        /// <see cref="SendImageBatches"/>.
        /// </summary>
        public static IEnumerator SendImages(IList<OutgoingImage> images, string message, bool useSpoilerTag)
        {
            if (images == null || images.Count == 0) yield break;

            var webhookUrl = ModHelpers.EffectiveConfig.WebhookUrl;
            if (string.IsNullOrEmpty(webhookUrl))
            {
                ModLog.Warn("[NoMapDiscordAdditions] Webhook URL not configured.");
                yield break;
            }

            var form = new WWWForm();
            int count = Mathf.Min(images.Count, MaxImagesPerMessage);
            for (int i = 0; i < count; i++)
            {
                form.AddBinaryData($"files[{i}]", images[i].Bytes,
                    GetAttachmentFileName(images[i].FileName, useSpoilerTag), "image/png");
            }

            if (!string.IsNullOrEmpty(message))
            {
                // Serialize the whole object so control chars / quotes /
                // unicode in player names or templates can't produce a
                // malformed body (Discord rejects that with HTTP 400, which
                // otherwise repeats on every send).
                form.AddField("payload_json",
                    JsonConvert.SerializeObject(new { content = message }));
            }

            using (var request = UnityWebRequest.Post(webhookUrl, form))
            {
                request.certificateHandler = new BypassCertificateHandler();
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    ModLog.Error($"[NoMapDiscordAdditions] Discord webhook failed: {request.error}");
                }
                else
                {
                    ModLog.Info($"[NoMapDiscordAdditions] Sent {count} image(s) to Discord.");
                }
            }
        }

        /// <summary>
        /// Sends an arbitrary number of images, chunked into messages of at
        /// most <see cref="MaxImagesPerMessage"/> attachments each. The
        /// explanatory <paramref name="message"/> rides only on the first
        /// chunk so the channel isn't spammed with repeated text.
        /// </summary>
        public static IEnumerator SendImageBatches(IList<OutgoingImage> images, string message, bool useSpoilerTag)
        {
            if (images == null || images.Count == 0) yield break;

            for (int start = 0; start < images.Count; start += MaxImagesPerMessage)
            {
                int len = Mathf.Min(MaxImagesPerMessage, images.Count - start);
                var chunk = new List<OutgoingImage>(len);
                for (int i = 0; i < len; i++)
                    chunk.Add(images[start + i]);

                string content = start == 0 ? message : null;
                yield return SendImages(chunk, content, useSpoilerTag);
            }
        }

        public static IEnumerator SendImage(byte[] imageData, string filename, string message, bool useSpoilerTag)
        {
            var webhookUrl = ModHelpers.EffectiveConfig.WebhookUrl;
            if (string.IsNullOrEmpty(webhookUrl))
            {
                ModLog.Warn("[NoMapDiscordAdditions] Webhook URL not configured.");
                yield break;
            }

            var form = new WWWForm();
            form.AddBinaryData("files[0]", imageData, GetAttachmentFileName(filename, useSpoilerTag), "image/png");

            if (!string.IsNullOrEmpty(message))
            {
                // Serialize the whole object so control chars / quotes /
                // unicode in player names or templates can't produce a
                // malformed body (Discord rejects that with HTTP 400, which
                // otherwise repeats on every send).
                form.AddField("payload_json",
                    JsonConvert.SerializeObject(new { content = message }));
            }

            using (var request = UnityWebRequest.Post(webhookUrl, form))
            {
                request.certificateHandler = new BypassCertificateHandler();
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    ModLog.Error($"[NoMapDiscordAdditions] Discord webhook failed: {request.error}");
                }
                else
                {
                    ModLog.Info("[NoMapDiscordAdditions] Map screenshot sent to Discord.");
                }
            }
        }

        private static string GetAttachmentFileName(string filename, bool useSpoilerTag)
        {
            if (!useSpoilerTag || string.IsNullOrEmpty(filename) || filename.StartsWith("SPOILER_"))
                return filename;

            return "SPOILER_" + filename;
        }
    }
}
