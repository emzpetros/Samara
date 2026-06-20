using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ImpossibleRobert.Common;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UnityEngine;

namespace Brain
{
    /// <summary>
    /// Result of a caption operation.
    /// </summary>
    [Serializable]
    public class CaptionResult
    {
        public string path;
        public string caption;
    }

    /// <summary>
    /// Engine for generating AI captions from images using various backends.
    /// </summary>
    public static class CaptionEngine
    {
        /// <summary>
        /// Processes an image file for captioning - handles resizing and format conversion.
        /// </summary>
        /// <param name="filePath">Path to the image file</param>
        /// <param name="minSize">Minimum size in pixels (will upscale if smaller)</param>
        /// <returns>Tuple of image bytes and MIME type</returns>
        public static async Task<(byte[] imageBytes, string mimeType)> ProcessImageForCaption(string filePath, int minSize = 32, CancellationToken cancellationToken = default, bool preferJpeg = false)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            bool isPng = ext == ".png";
            bool isJpeg = ext == ".jpg" || ext == ".jpeg";
            bool forceReencode = preferJpeg && isPng;
            string mime = (!isPng || forceReencode) ? "image/jpeg" : "image/png";

            // Fast path: if the file is already a format Ollama accepts (PNG / JPEG)
            // and the dimensions are >= minSize, skip the decode/re-encode round trip
            // entirely. ImageSharp's full Load decodes every pixel into Rgba32 and
            // a re-save reencodes — for a 530 KB PNG that's ~500 ms of pure waste
            // when we're just going to send the bytes upstream anyway.
            // Image.IdentifyAsync only reads the file header (a few KB) so it's
            // typically <10 ms regardless of image size.
            if ((isPng || isJpeg) && !forceReencode)
            {
                try
                {
                    // ImageUtils.GetDimensions reads only the PNG IHDR / JPEG SOF marker
                    // (a few bytes) — much cheaper than Image.Identify which scans further.
                    Tuple<int, int> dims = ImageUtils.GetDimensions(filePath, true, ext);
                    if (dims != null && dims.Item1 > 0 && dims.Item2 >= 2 &&
                        dims.Item1 >= minSize && dims.Item2 >= minSize)
                    {
                        byte[] raw;
                        using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                        using (MemoryStream ms = new MemoryStream(fs.CanSeek ? (int)fs.Length : 0))
                        {
                            await fs.CopyToAsync(ms, 81920, cancellationToken);
                            raw = ms.ToArray();
                        }
                        return (raw, mime);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // Header read failed (corrupt, exotic variant, etc.) — fall through
                    // to the slow path which is more permissive.
                }
            }

            // Slow path: format we don't pass through, or image is below minSize and
            // needs upscaling. Decode, optionally resize, re-encode.
            using (Image<Rgba32> img = await Image.LoadAsync<Rgba32>(filePath, cancellationToken))
            {
                int w = img.Width;
                int h = img.Height;

                if (h < 2) throw new InvalidOperationException("Image height is too small");

                double scale = Math.Max((float)minSize / w, (float)minSize / h);
                if (scale > 1.0)
                {
                    int newW = (int)Math.Ceiling(w * scale);
                    int newH = (int)Math.Ceiling(h * scale);
                    img.Mutate(x => x.Resize(newW, newH));
                }

                using (MemoryStream ms = new MemoryStream())
                {
                    IImageEncoder encoder = (isPng && !forceReencode) ? new PngEncoder() : (IImageEncoder)new JpegEncoder();
                    await img.SaveAsync(ms, encoder, cancellationToken);
                    byte[] imgBytes = ms.ToArray();
                    return (imgBytes, mime);
                }
            }
        }

        /// <summary>
        /// Generates captions for one or more images.
        /// </summary>
        /// <param name="filenames">List of image file paths</param>
        /// <param name="prompts">List of prompts corresponding to each file (must match filenames count)</param>
        /// <param name="modelName">Optional model name override</param>
        /// <param name="progressCallback">Optional callback for progress updates (progress 0-1, message)</param>
        /// <returns>List of caption results</returns>
        public static async Task<List<CaptionResult>> CaptionImages(
            List<string> filenames,
            List<string> prompts,
            string modelName = null,
            Action<float, string> progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            if (filenames == null || filenames.Count == 0)
                return new List<CaptionResult>();

            if (prompts == null || prompts.Count != filenames.Count)
                throw new ArgumentException("Prompts list must match filenames count");

            IBrainSettings settings = Intelligence.Settings;
            List<CaptionResult> resultList = null;

            switch (settings.AIBackend)
            {
                case 0: // BLIP
                    resultList = await CaptionWithBlip(filenames, settings);
                    break;

                case 1: // Ollama
                    resultList = await CaptionWithOllama(filenames, prompts, modelName ?? settings.OllamaModel, settings, progressCallback, cancellationToken);
                    break;

                case 2: // LM Studio
                    resultList = await CaptionWithLMStudio(filenames, prompts, modelName ?? settings.LMStudioModel, settings, progressCallback, cancellationToken);
                    break;
            }

            // Clean up results
            resultList?.ForEach(r =>
            {
                if (r.caption != null)
                {
                    r.caption = StringUtils.StripTags(r.caption, true)
                        .Trim()
                        .TrimStart('"')
                        .TrimEnd('"');
                    r.caption = StringUtils.StripTags(r.caption); // remove any left-over tags
                }
            });

            return resultList ?? new List<CaptionResult>();
        }

        /// <summary>
        /// Simplified caption method for a single image.
        /// </summary>
        public static async Task<string> CaptionImage(string filename, string prompt, string modelName = null, CancellationToken cancellationToken = default)
        {
            List<CaptionResult> results = await CaptionImages(
                new List<string> {filename},
                new List<string> {prompt},
                modelName,
                null,
                cancellationToken);
            return results?.FirstOrDefault()?.caption;
        }

        private static Task<List<CaptionResult>> CaptionWithBlip(List<string> filenames, IBrainSettings settings)
        {
            string blipType = settings.BlipType == 1 ? "--large" : "";
            string gpuUsage = settings.BlipUseGPU ? "--gpu" : "";
            string nameList = "\"" + string.Join("\" \"", filenames.Select(IOUtils.ToShortPath)) + "\"";
            string command = settings.BlipPath != null ? Path.Combine(settings.BlipPath, "blip-caption") : "blip-caption";
            string result = IOUtils.ExecuteCommand(command, $"{blipType} {gpuUsage} --json {nameList}");

            if (string.IsNullOrWhiteSpace(result)) return Task.FromResult<List<CaptionResult>>(null);

            try
            {
                return Task.FromResult(JsonConvert.DeserializeObject<List<CaptionResult>>(result));
            }
            catch (Exception e)
            {
                Debug.LogError($"Could not parse BLIP result '{result}': {e.Message}");
                return Task.FromResult<List<CaptionResult>>(null);
            }
        }

        public static bool DebugLogOllamaTraffic = false;

        private static async Task<List<CaptionResult>> CaptionWithOllama(
            List<string> filenames,
            List<string> prompts,
            string modelName,
            IBrainSettings settings,
            Action<float, string> progressCallback,
            CancellationToken cancellationToken)
        {
            OllamaClient client = new OllamaClient(
                OllamaClient.CreateHttpClient(Intelligence.OllamaServiceUrl, DebugLogOllamaTraffic), true);
            List<CaptionResult> resultList = new List<CaptionResult>();

            int parallelCount = Math.Max(1, settings.OllamaParallelRequests);

            try
            {
                for (int batchStart = 0; batchStart < filenames.Count; batchStart += parallelCount)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    int batchSize = Math.Min(parallelCount, filenames.Count - batchStart);
                    List<Task<CaptionResult>> batchTasks = new List<Task<CaptionResult>>(batchSize);

                    for (int i = 0; i < batchSize; i++)
                    {
                        int idx = batchStart + i;
                        string file = filenames[idx];
                        string prompt = prompts[idx];
                        progressCallback?.Invoke((float)idx / filenames.Count, Path.GetFileName(file));
                        batchTasks.Add(ProcessOllamaRequest(client, file, prompt, modelName, settings, cancellationToken));
                    }

                    CaptionResult[] batchResults;
                    try
                    {
                        batchResults = await Task.WhenAll(batchTasks).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch
                    {
                        batchResults = batchTasks.Select(t => t.Status == TaskStatus.RanToCompletion ? t.Result : null).ToArray();
                    }

                    if (batchResults != null)
                    {
                        resultList.AddRange(batchResults.Where(r => r != null));
                    }
                }
            }
            finally
            {
                client.Dispose();
            }
            return resultList;
        }

        private static async Task<CaptionResult> ProcessOllamaRequest(
            OllamaClient client,
            string file,
            string prompt,
            string modelName,
            IBrainSettings settings,
            CancellationToken cancellationToken)
        {
            using (CancellationTokenSource linked = CreateRequestCts(cancellationToken, settings.AITimeout))
            {
                try
                {
                    System.Diagnostics.Stopwatch phaseSw = System.Diagnostics.Stopwatch.StartNew();
                    long tImageStart = phaseSw.ElapsedMilliseconds;

                    (byte[] imgBytes, string _mime) = await ProcessImageForCaption(file, settings.AIMinSize, linked.Token, preferJpeg: true).ConfigureAwait(false);

                    long tImageDone = phaseSw.ElapsedMilliseconds;

                    string base64Image = Convert.ToBase64String(imgBytes);
                    long tBase64Done = phaseSw.ElapsedMilliseconds;

                    // Build JSON directly with JsonTextWriter — bypasses reflection-based
                    // serialization and avoids the large intermediate UTF-16 string that
                    // JsonConvert.SerializeObject would create from the ~700 KB base64 payload.
                    MemoryStream bodyMs = new MemoryStream(base64Image.Length + 2048);
                    using (StreamWriter sw = new StreamWriter(bodyMs, new UTF8Encoding(false), 8192, true))
                    using (JsonTextWriter jw = new JsonTextWriter(sw))
                    {
                        jw.WriteStartObject();
                        jw.WritePropertyName("model"); jw.WriteValue(modelName);
                        jw.WritePropertyName("prompt"); jw.WriteValue(prompt);
                        jw.WritePropertyName("stream"); jw.WriteValue(false);
                        jw.WritePropertyName("think"); jw.WriteValue(false);
                        jw.WritePropertyName("keep_alive"); jw.WriteValue("30m");
                        jw.WritePropertyName("images");
                        jw.WriteStartArray();
                        jw.WriteValue(base64Image);
                        jw.WriteEndArray();
                        jw.WritePropertyName("options");
                        jw.WriteStartObject();
                        jw.WritePropertyName("num_predict"); jw.WriteValue(settings.AIMaxCaptionLength + 100);
                        jw.WritePropertyName("num_ctx"); jw.WriteValue(4096);
                        jw.WritePropertyName("temperature"); jw.WriteValue(0.2);
                        jw.WritePropertyName("stop");
                        jw.WriteStartArray();
                        jw.WriteValue("\n\n\n");
                        jw.WriteValue("<|im_end|>");
                        jw.WriteValue("<|endoftext|>");
                        jw.WriteEndArray();
                        jw.WriteEndObject();
                        jw.WriteEndObject();
                    }
                    bodyMs.Position = 0;

                    using (StreamContent content = new StreamContent(bodyMs))
                    {
                        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                        content.Headers.ContentLength = bodyMs.Length;
                        using (HttpResponseMessage resp = await client.Http.PostAsync("api/generate", content, linked.Token).ConfigureAwait(false))
                        {
                            resp.EnsureSuccessStatusCode();
                            string respJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            Newtonsoft.Json.Linq.JObject obj = Newtonsoft.Json.Linq.JObject.Parse(respJson);
                            string responseText = obj.Value<string>("response") ?? string.Empty;
                            long tDone = phaseSw.ElapsedMilliseconds;

                            if (DebugLogOllamaTraffic)
                            {
                                Debug.Log($"[Ollama] '{Path.GetFileName(file)}' total={tDone} ms | image={tImageDone - tImageStart} ms base64={tBase64Done - tImageDone} ms (img bytes={imgBytes.Length} b64={base64Image.Length})");
                                long totalDuration = obj.Value<long>("total_duration");
                                if (totalDuration > 0)
                                {
                                    Debug.Log($"[Ollama server] '{Path.GetFileName(file)}' total={totalDuration / 1_000_000} ms load={obj.Value<long>("load_duration") / 1_000_000} ms prompt_eval={obj.Value<long>("prompt_eval_duration") / 1_000_000} ms eval={obj.Value<long>("eval_duration") / 1_000_000} ms eval_count={obj.Value<int>("eval_count")}");
                                }
                            }

                            return new CaptionResult
                            {
                                path = file,
                                caption = responseText
                            };
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    Debug.LogWarning($"Ollama request for '{file}' timed out after {settings.AITimeout}s.");
                    return null;
                }
                catch (HttpRequestException httpE)
                {
                    Debug.LogError($"Could not connect to Ollama for '{file}': {httpE.Message}");
                    return null;
                }
                catch (InvalidOperationException opE)
                {
                    Debug.LogError($"Ollama model error for '{file}', image might be too small: {opE.Message}");
                    return null;
                }
                catch (Exception e)
                {
                    Debug.LogError($"Could not get Ollama result for '{file}': {e.Message}");
                    return null;
                }
            }
        }

        private static async Task<List<CaptionResult>> CaptionWithLMStudio(
            List<string> filenames,
            List<string> prompts,
            string modelName,
            IBrainSettings settings,
            Action<float, string> progressCallback,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(modelName))
            {
                Debug.LogError("LM Studio model name is not configured.");
                return new List<CaptionResult>();
            }

            List<CaptionResult> resultList = new List<CaptionResult>();
            int parallelCount = Math.Max(1, settings.LMStudioParallelRequests);

            for (int batchStart = 0; batchStart < filenames.Count; batchStart += parallelCount)
            {
                if (cancellationToken.IsCancellationRequested) break;

                int batchSize = Math.Min(parallelCount, filenames.Count - batchStart);
                List<Task<CaptionResult>> batchTasks = new List<Task<CaptionResult>>();

                for (int i = 0; i < batchSize; i++)
                {
                    int idx = batchStart + i;
                    string file = filenames[idx];
                    string prompt = prompts[idx];
                    progressCallback?.Invoke((float)idx / filenames.Count, Path.GetFileName(file));
                    batchTasks.Add(ProcessLMStudioRequest(file, prompt, modelName, settings, cancellationToken));
                }

                CaptionResult[] batchResults;
                try
                {
                    batchResults = await Task.WhenAll(batchTasks);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                resultList.AddRange(batchResults.Where(r => r != null));
            }

            return resultList;
        }

        private static async Task<CaptionResult> ProcessLMStudioRequest(string file, string prompt, string modelName, IBrainSettings settings, CancellationToken cancellationToken)
        {
            using (CancellationTokenSource linked = CreateRequestCts(cancellationToken, settings.AITimeout))
            {
                try
                {
                    (byte[] imgBytes, string mime) = await ProcessImageForCaption(file, settings.AIMinSize, linked.Token);

                    string base64Image = Convert.ToBase64String(imgBytes);
                    string imageDataUri = $"data:{mime};base64,{base64Image}";

                    LMStudioChatRequest request = new LMStudioChatRequest
                    {
                        model = modelName,
                        messages = new List<LMStudioChatMessage>
                        {
                            new LMStudioChatMessage
                            {
                                role = "user",
                                content = new List<LMStudioContent>
                                {
                                    new LMStudioContent
                                    {
                                        type = "text",
                                        text = prompt
                                    },
                                    new LMStudioContent
                                    {
                                        type = "image_url",
                                        image_url = new LMStudioImageUrl
                                        {
                                            url = imageDataUri
                                        }
                                    }
                                }
                            }
                        },
                        temperature = 0.95f,
                        max_tokens = 5000
                    };

                    using (HttpClient httpClient = new HttpClient())
                    {
                        // HttpClient.Timeout is intentionally left at the default (Infinite-ish);
                        // cancellation is driven by the linked token (global stop + AITimeout).
                        httpClient.Timeout = Timeout.InfiniteTimeSpan;
                        string json = JsonConvert.SerializeObject(request);
                        StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
                        HttpResponseMessage response = await httpClient.PostAsync($"{Intelligence.LMStudioServiceUrl}/v1/chat/completions", content, linked.Token);

                        if (response.IsSuccessStatusCode)
                        {
                            string responseJson = await response.Content.ReadAsStringAsync();
                            LMStudioChatResponse chatResponse = JsonConvert.DeserializeObject<LMStudioChatResponse>(responseJson);

                            if (chatResponse?.choices != null && chatResponse.choices.Count > 0)
                            {
                                string caption = chatResponse.choices[0].message?.content;
                                return new CaptionResult
                                {
                                    path = file,
                                    caption = caption
                                };
                            }
                            else
                            {
                                Debug.LogWarning($"LM Studio returned an empty response for '{file}'.");
                                return null;
                            }
                        }
                        else
                        {
                            string errorContent = await response.Content.ReadAsStringAsync();
                            Debug.LogError($"LM Studio API error for '{file}': {response.StatusCode} - {errorContent}");
                            return null;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // global cancel: propagate so the outer Task.WhenAll observes it
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // per-request timeout: skip
                    Debug.LogWarning($"LM Studio request for '{file}' timed out after {settings.AITimeout}s.");
                    return null;
                }
                catch (HttpRequestException httpE)
                {
                    Debug.LogError($"Could not connect to LM Studio for '{file}': {httpE.Message}");
                    return null;
                }
                catch (InvalidOperationException opE)
                {
                    Debug.LogError($"LM Studio model error for '{file}', image might be too small or model not loaded: {opE.Message}");
                    return null;
                }
                catch (Exception e)
                {
                    Debug.LogError($"Could not get LM Studio result for '{file}': {e.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Builds a CTS linked to the caller's token; if <paramref name="timeoutSeconds"/> &gt; 0,
        /// it will additionally cancel after that many seconds.
        /// </summary>
        private static CancellationTokenSource CreateRequestCts(CancellationToken outer, int timeoutSeconds)
        {
            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
            if (timeoutSeconds > 0) linked.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            return linked;
        }
    }
}