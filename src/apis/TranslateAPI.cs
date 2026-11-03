using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.apis
{
    public static class TranslateAPI
    {
        public static readonly Dictionary<string, Func<TranslationRequest, CancellationToken, IAsyncEnumerable<TranslationUpdate>>>
            TRANSLATE_FUNCTIONS = new()
        {
            { "Google", Google },
            { "Google2", Google2 },
            { "Ollama", Ollama },
            { "OpenAI", OpenAI },
            { "LMStudio", LMStudio },
            { "DeepL", DeepL },
            { "OpenRouter", OpenRouter },
            { "Youdao", Youdao },
            { "MTranServer", MTranServer },
            { "Baidu", Baidu },
            { "LibreTranslate", LibreTranslate },
        };

        public static readonly List<string> LLM_BASED_APIS = new()
        {
            "Ollama", "OpenAI", "OpenRouter", "LMStudio"
        };

        public static readonly List<string> NO_CONFIG_APIS = new()
        {
            "Google", "Google2"
        };

        private const int ResponseHeaderTimeoutSeconds = 15;
        private const int StreamReadTimeoutSeconds = 30;
        private static readonly HttpClient client = new()
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        public static async IAsyncEnumerable<TranslationUpdate> TranslateAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            if (!TRANSLATE_FUNCTIONS.TryGetValue(request.Settings.ApiName, out var function))
            {
                yield return new TranslationUpdate(
                    TranslationUpdateKind.Failed,
                    Error: $"Unsupported translation API: {request.Settings.ApiName}");
                yield break;
            }

            await foreach (var update in function(request, token).WithCancellation(token))
                yield return update;
        }

        private static async IAsyncEnumerable<TranslationUpdate> SingleResultAsync(
            Func<CancellationToken, Task<string>> operation,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            string result;
            try
            {
                result = await operation(token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                yield return new TranslationUpdate(TranslationUpdateKind.Failed, Error: ex.Message);
                yield break;
            }

            if (result.StartsWith("[ERROR]", StringComparison.Ordinal))
            {
                yield return new TranslationUpdate(TranslationUpdateKind.Failed, Error: result);
                yield break;
            }

            yield return new TranslationUpdate(TranslationUpdateKind.Text, result);
            yield return new TranslationUpdate(TranslationUpdateKind.Completed);
        }

        private static async IAsyncEnumerable<TranslationUpdate> OpenAI(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            if (request.Settings.Protocol == OpenAIProtocol.Responses)
            {
                await foreach (var update in OpenAIResponses(request, token).WithCancellation(token))
                    yield return update;
                yield break;
            }

            await foreach (var update in OpenAIChatCompletions(request, token).WithCancellation(token))
                yield return update;
        }

        private static async IAsyncEnumerable<TranslationUpdate> OpenAIChatCompletions(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            var body = new BaseLLMRequestData(
                request.Settings.ModelName,
                BuildMessages(request),
                request.Settings.Temperature)
            {
                stream = request.Settings.Stream
            };

            if (!request.Settings.Stream)
            {
                await foreach (var update in SingleResultAsync(
                    cancellationToken => SendOpenAIChatCompletionsAsync(request, body, cancellationToken), token)
                    .WithCancellation(token))
                    yield return update;
                yield break;
            }

            using var response = await SendJsonAsync(
                request.Settings.ApiUrl,
                body,
                request.Settings.ApiKey,
                token);
            if (!response.IsSuccessStatusCode)
            {
                yield return await CreateHttpErrorAsync(response, token);
                yield break;
            }

            bool completed = false;
            await foreach (var item in ReadSseAsync(response, token).WithCancellation(token))
            {
                if (item.Data == "[DONE]")
                {
                    completed = true;
                    continue;
                }

                if (!TryParseChatDelta(item.Data, out var delta, out var finishReason, out var error))
                {
                    yield return new TranslationUpdate(TranslationUpdateKind.Failed, Error: error);
                    yield break;
                }

                if (delta.Length > 0)
                    yield return new TranslationUpdate(TranslationUpdateKind.Text, delta);
                if (finishReason != null)
                    completed = true;
            }

            yield return completed
                ? new TranslationUpdate(TranslationUpdateKind.Completed)
                : new TranslationUpdate(
                    TranslationUpdateKind.Incomplete,
                    Error: "The OpenAI Chat Completions stream ended before completion.");
        }

        private static async Task<string> SendOpenAIChatCompletionsAsync(
            TranslationRequest request,
            BaseLLMRequestData body,
            CancellationToken token)
        {
            using var response = await SendJsonAsync(request.Settings.ApiUrl, body, request.Settings.ApiKey, token);
            if (!response.IsSuccessStatusCode)
                return await ReadHttpErrorAsync(response, token);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
                return $"[ERROR] OpenAI error: {error}";

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return "[ERROR] OpenAI response is missing choices.";

            var message = choices[0].GetProperty("message");
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                return "[ERROR] OpenAI response is missing text content.";

            return RemoveModelThinking(content.GetString() ?? string.Empty);
        }

        private static async IAsyncEnumerable<TranslationUpdate> OpenAIResponses(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            var body = new
            {
                model = request.Settings.ModelName,
                instructions = FormatPrompt(request),
                input = BuildInput(request),
                temperature = request.Settings.Temperature,
                stream = request.Settings.Stream,
                store = false
            };

            if (!request.Settings.Stream)
            {
                await foreach (var update in SingleResultAsync(
                    cancellationToken => SendOpenAIResponsesAsync(request, body, cancellationToken), token)
                    .WithCancellation(token))
                    yield return update;
                yield break;
            }

            using var response = await SendJsonAsync(
                request.Settings.ApiUrl,
                body,
                request.Settings.ApiKey,
                token);
            if (!response.IsSuccessStatusCode)
            {
                yield return await CreateHttpErrorAsync(response, token);
                yield break;
            }

            bool completed = false;
            await foreach (var item in ReadSseAsync(response, token).WithCancellation(token))
            {
                string eventType = item.Event;
                if (string.IsNullOrEmpty(eventType) && TryGetEventType(item.Data, out var dataType))
                    eventType = dataType;

                if (eventType == "response.output_text.delta")
                {
                    if (!TryGetStringProperty(item.Data, "delta", out var delta))
                    {
                        yield return new TranslationUpdate(
                            TranslationUpdateKind.Failed,
                            Error: "OpenAI Responses delta is missing text.");
                        yield break;
                    }
                    if (delta.Length > 0)
                        yield return new TranslationUpdate(TranslationUpdateKind.Text, delta);
                }
                else if (eventType == "response.completed")
                {
                    completed = true;
                }
                else if (eventType == "response.failed")
                {
                    yield return new TranslationUpdate(
                        TranslationUpdateKind.Failed,
                        Error: TryGetErrorMessage(item.Data) ?? "OpenAI Responses reported a failure.");
                    yield break;
                }
                else if (eventType == "error")
                {
                    yield return new TranslationUpdate(
                        TranslationUpdateKind.Failed,
                        Error: TryGetErrorMessage(item.Data) ?? "OpenAI Responses reported an error.");
                    yield break;
                }
            }

            yield return completed
                ? new TranslationUpdate(TranslationUpdateKind.Completed)
                : new TranslationUpdate(
                    TranslationUpdateKind.Incomplete,
                    Error: "The OpenAI Responses stream ended before completion.");
        }

        private static async Task<string> SendOpenAIResponsesAsync(
            TranslationRequest request,
            object body,
            CancellationToken token)
        {
            using var response = await SendJsonAsync(request.Settings.ApiUrl, body, request.Settings.ApiKey, token);
            if (!response.IsSuccessStatusCode)
                return await ReadHttpErrorAsync(response, token);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
                return $"[ERROR] OpenAI Responses error: {error}";

            if (TryGetStringProperty(root, "output_text", out var outputText))
                return RemoveModelThinking(outputText);

            if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
                return "[ERROR] OpenAI Responses response is missing output text.";

            var builder = new StringBuilder();
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var type) || type.GetString() != "message" ||
                    !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var contentItem in content.EnumerateArray())
                {
                    if (contentItem.TryGetProperty("type", out var contentType) &&
                        contentType.GetString() == "output_text" &&
                        TryGetStringProperty(contentItem, "text", out var text))
                        builder.Append(text);
                }
            }

            return builder.Length == 0
                ? "[ERROR] OpenAI Responses response contains no output text."
                : RemoveModelThinking(builder.ToString());
        }

        private static async IAsyncEnumerable<TranslationUpdate> Ollama(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (var update in SingleResultAsync(
                cancellationToken => SendOllamaAsync(request, cancellationToken), token)
                .WithCancellation(token))
                yield return update;
        }

        private static async Task<string> SendOllamaAsync(TranslationRequest request, CancellationToken token)
        {
            var body = new OllamaRequestData(
                request.Settings.ModelName,
                BuildMessages(request),
                request.Settings.Temperature)
            {
                stream = false,
                keep_alive = request.Settings.KeepAlive
            };
            using var response = await SendJsonAsync(
                TextUtil.NormalizeUrl(request.Settings.ApiUrl + "/api/chat"), body, string.Empty, token);
            if (!response.IsSuccessStatusCode)
                return await ReadHttpErrorAsync(response, token);

            var result = JsonSerializer.Deserialize<OllamaConfig.Response>(
                await response.Content.ReadAsStringAsync(token));
            return RemoveModelThinking(result?.message?.content ?? "[ERROR] Ollama response is missing text content.");
        }

        private static async IAsyncEnumerable<TranslationUpdate> LMStudio(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (var update in SingleResultAsync(
                cancellationToken => SendLMStudioAsync(request, cancellationToken), token)
                .WithCancellation(token))
                yield return update;
        }

        private static async Task<string> SendLMStudioAsync(TranslationRequest request, CancellationToken token)
        {
            var body = new
            {
                model = request.Settings.ModelName,
                system_prompt = FormatPrompt(request),
                input = BuildInput(request),
                temperature = request.Settings.Temperature,
                stream = false,
                store = false
            };
            using var response = await SendJsonAsync(
                TextUtil.NormalizeUrl(request.Settings.ApiUrl) + "/chat", body, string.Empty, token);
            if (!response.IsSuccessStatusCode)
                return await ReadHttpErrorAsync(response, token);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!document.RootElement.TryGetProperty("output", out var output) ||
                output.ValueKind != JsonValueKind.Array)
                return "[ERROR] LM Studio response is missing output.";

            foreach (var item in output.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) && type.GetString() == "message" &&
                    TryGetStringProperty(item, "content", out var content))
                    return RemoveModelThinking(content);
            }
            return "[ERROR] LM Studio response contains no message text.";
        }

        private static async IAsyncEnumerable<TranslationUpdate> OpenRouter(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (var update in SingleResultAsync(
                cancellationToken => SendOpenRouterAsync(request, cancellationToken), token)
                .WithCancellation(token))
                yield return update;
        }

        private static async Task<string> SendOpenRouterAsync(TranslationRequest request, CancellationToken token)
        {
            var body = new BaseLLMRequestData(
                request.Settings.ModelName,
                BuildMessages(request),
                request.Settings.Temperature)
            {
                stream = false
            };
            using var response = await SendJsonAsync(
                "https://openrouter.ai/api/v1/chat/completions", body, request.Settings.ApiKey, token);
            if (!response.IsSuccessStatusCode)
                return await ReadHttpErrorAsync(response, token);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return "[ERROR] OpenRouter response is missing choices.";
            var message = choices[0].GetProperty("message");
            return RemoveModelThinking(message.GetProperty("content").GetString() ?? string.Empty);
        }

        private static async IAsyncEnumerable<TranslationUpdate> Google(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (var update in SingleResultAsync(
                cancellationToken => SendGoogleAsync(request, cancellationToken), token)
                .WithCancellation(token))
                yield return update;
        }

        private static async Task<string> SendGoogleAsync(TranslationRequest request, CancellationToken token)
        {
            string text = PrepareTraditionalText(request);
            string encodedText = Uri.EscapeDataString(text);
            string url = $"https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl=auto&" +
                         $"tl={Uri.EscapeDataString(request.Settings.TargetLanguage)}&q={encodedText}";
            using var response = await SendJsonAsync(url, null, string.Empty, token, HttpMethod.Get);
            if (!response.IsSuccessStatusCode)
                return await ReadHttpErrorAsync(response, token);
            var result = JsonSerializer.Deserialize<List<List<string>>>(await response.Content.ReadAsStringAsync(token));
            return result?[0]?[0] ?? "[ERROR] Google response contains no translation.";
        }

        private static async IAsyncEnumerable<TranslationUpdate> Google2(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (var update in SingleResultAsync(
                cancellationToken => SendGoogle2Async(request, cancellationToken), token)
                .WithCancellation(token))
                yield return update;
        }

        private static async Task<string> SendGoogle2Async(TranslationRequest request, CancellationToken token)
        {
            string encodedText = Uri.EscapeDataString(PrepareTraditionalText(request));
            string url = $"https://dictionaryextension-pa.googleapis.com/v1/dictionaryExtensionData?" +
                         $"language={Uri.EscapeDataString(request.Settings.TargetLanguage)}&" +
                         $"key=AIzaSyA6EEtrDCfBkHV8uU2lgGY-N383ZgAOo7Y&term={encodedText}&strategy=2";
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
            requestMessage.Headers.Add("x-referer", "chrome-extension://mgijmajocgfcbeboacabfgobmjgjcoja");
            using var response = await SendAsync(requestMessage, token);
            if (!response.IsSuccessStatusCode)
                return await ReadHttpErrorAsync(response, token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!document.RootElement.TryGetProperty("translateResponse", out var translated) ||
                !TryGetStringProperty(translated, "translateText", out var text))
                return "[ERROR] Google2 response contains no translation.";
            return text;
        }

        private static async IAsyncEnumerable<TranslationUpdate> DeepL(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (var update in SingleResultAsync(
                cancellationToken => SendDeepLAsync(request, cancellationToken), token)
                .WithCancellation(token))
                yield return update;
        }

        private static async Task<string> SendDeepLAsync(TranslationRequest request, CancellationToken token)
        {
            string language = DeepLConfig.SupportedLanguages.TryGetValue(
                request.Settings.TargetLanguage, out var value) ? value : request.Settings.TargetLanguage;
            var body = new { text = new[] { PrepareTraditionalText(request) }, target_lang = language };
            using var response = await SendJsonAsync(
                TextUtil.NormalizeUrl(request.Settings.ApiUrl), body, request.Settings.ApiKey, token,
                authorizationScheme: "DeepL-Auth-Key");
            if (!response.IsSuccessStatusCode)
                return await ReadHttpErrorAsync(response, token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var translations = document.RootElement.GetProperty("translations");
            return translations.GetArrayLength() == 0
                ? "[ERROR] DeepL response contains no translation."
                : translations[0].GetProperty("text").GetString() ?? string.Empty;
        }

        private static async IAsyncEnumerable<TranslationUpdate> Youdao(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (var update in SingleResultAsync(
                cancellationToken => SendYoudaoAsync(request, cancellationToken), token)
                .WithCancellation(token))
                yield return update;
        }

        private static async Task<string> SendYoudaoAsync(TranslationRequest request, CancellationToken token)
        {
            string language = YoudaoConfig.SupportedLanguages.TryGetValue(
                request.Settings.TargetLanguage, out var value) ? value : request.Settings.TargetLanguage;
            string text = PrepareTraditionalText(request);
            string salt = DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string sign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(
                $"{request.Settings.Parameter("AppKey")}{text}{salt}{request.Settings.Parameter("AppSecret")}")))
                .ToLowerInvariant();
            var parameters = new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = "auto",
                ["to"] = language,
                ["appKey"] = request.Settings.Parameter("AppKey"),
                ["salt"] = salt,
                ["sign"] = sign
            };
            using var content = new FormUrlEncodedContent(parameters);
            using var message = new HttpRequestMessage(HttpMethod.Post, request.Settings.ApiUrl) { Content = content };
            using var response = await SendAsync(message, token);
            if (!response.IsSuccessStatusCode)
                return await ReadHttpErrorAsync(response, token);
            var result = JsonSerializer.Deserialize<YoudaoConfig.TranslationResult>(
                await response.Content.ReadAsStringAsync(token));
            if (result?.errorCode != "0")
                return $"[ERROR] Youdao error: {result?.errorCode}";
            return result.translation?.FirstOrDefault() ?? "[ERROR] Youdao response contains no translation.";
        }

        private static async IAsyncEnumerable<TranslationUpdate> MTranServer(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (var update in SingleResultAsync(
                cancellationToken => SendMTranServerAsync(request, cancellationToken), token)
                .WithCancellation(token))
                yield return update;
        }

        private static async Task<string> SendMTranServerAsync(TranslationRequest request, CancellationToken token)
        {
            string targetLanguage = MTranServerConfig.SupportedLanguages.TryGetValue(
                request.Settings.TargetLanguage, out var value) ? value : request.Settings.TargetLanguage;
            var body = new
            {
                text = PrepareTraditionalText(request),
                to = targetLanguage,
                from = request.Settings.Parameter("SourceLanguage")
            };
            using var response = await SendJsonAsync(
                TextUtil.NormalizeUrl(request.Settings.ApiUrl), body, request.Settings.ApiKey, token);
            if (!response.IsSuccessStatusCode)
                return await ReadHttpErrorAsync(response, token);
            var result = JsonSerializer.Deserialize<MTranServerConfig.Response>(
                await response.Content.ReadAsStringAsync(token));
            return result?.result ?? "[ERROR] MTranServer response contains no translation.";
        }

        private static async IAsyncEnumerable<TranslationUpdate> Baidu(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (var update in SingleResultAsync(
                cancellationToken => SendBaiduAsync(request, cancellationToken), token)
                .WithCancellation(token))
                yield return update;
        }

        private static async Task<string> SendBaiduAsync(TranslationRequest request, CancellationToken token)
        {
            string language = BaiduConfig.SupportedLanguages.TryGetValue(
                request.Settings.TargetLanguage, out var value) ? value : request.Settings.TargetLanguage;
            string text = PrepareTraditionalText(request);
            string salt = DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string sign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(
                $"{request.Settings.Parameter("AppId")}{text}{salt}{request.Settings.Parameter("AppSecret")}")))
                .ToLowerInvariant();
            var parameters = new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = "auto",
                ["to"] = language,
                ["appid"] = request.Settings.Parameter("AppId"),
                ["salt"] = salt,
                ["sign"] = sign
            };
            using var content = new FormUrlEncodedContent(parameters);
            using var message = new HttpRequestMessage(HttpMethod.Post, request.Settings.ApiUrl) { Content = content };
            using var response = await SendAsync(message, token);
            if (!response.IsSuccessStatusCode)
                return await ReadHttpErrorAsync(response, token);
            var result = JsonSerializer.Deserialize<BaiduConfig.TranslationResult>(
                await response.Content.ReadAsStringAsync(token));
            if (result?.error_code is not null && result.error_code != "0")
                return $"[ERROR] Baidu error: {result.error_code}";
            return result?.trans_result?.FirstOrDefault()?.dst ?? "[ERROR] Baidu response contains no translation.";
        }

        private static async IAsyncEnumerable<TranslationUpdate> LibreTranslate(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (var update in SingleResultAsync(
                cancellationToken => SendLibreTranslateAsync(request, cancellationToken), token)
                .WithCancellation(token))
                yield return update;
        }

        private static async Task<string> SendLibreTranslateAsync(TranslationRequest request, CancellationToken token)
        {
            string targetLanguage = LibreTranslateConfig.SupportedLanguages.TryGetValue(
                request.Settings.TargetLanguage, out var value) ? value : request.Settings.TargetLanguage;
            var body = new
            {
                q = PrepareTraditionalText(request),
                target = targetLanguage,
                source = "auto",
                format = "text",
                api_key = request.Settings.ApiKey
            };
            using var response = await SendJsonAsync(
                TextUtil.NormalizeUrl(request.Settings.ApiUrl), body, string.Empty, token);
            if (!response.IsSuccessStatusCode)
                return await ReadHttpErrorAsync(response, token);
            var result = JsonSerializer.Deserialize<LibreTranslateConfig.Response>(
                await response.Content.ReadAsStringAsync(token));
            return result?.translatedText ?? "[ERROR] LibreTranslate response contains no translation.";
        }

        private static List<BaseLLMConfig.Message> BuildMessages(TranslationRequest request)
        {
            var messages = new List<BaseLLMConfig.Message>
            {
                new() { role = "system", content = FormatPrompt(request) }
            };
            foreach (var context in request.Contexts)
            {
                messages.Add(new BaseLLMConfig.Message { role = "user", content = $"🔤 {context.SourceText} 🔤" });
                messages.Add(new BaseLLMConfig.Message { role = "assistant", content = context.TranslatedText });
            }
            messages.Add(new BaseLLMConfig.Message { role = "user", content = $"🔤 {request.Text} 🔤" });
            return messages;
        }

        private static string BuildInput(TranslationRequest request)
        {
            var builder = new StringBuilder();
            foreach (var context in request.Contexts)
                builder.Append("🔤 ").Append(context.SourceText).Append(" 🔤 → ")
                    .Append(context.TranslatedText).AppendLine();
            builder.Append("🔤 ").Append(request.Text).Append(" 🔤");
            return builder.ToString();
        }

        private static string FormatPrompt(TranslationRequest request) =>
            string.Format(request.Settings.Prompt, request.Settings.TargetLanguage);

        private static string PrepareTraditionalText(TranslationRequest request)
        {
            if (!request.Settings.ContextAware || request.Contexts.Count == 0)
                return request.Text;
            var context = string.Join("\n", request.Contexts.Select(item =>
                $"🔤 {item.SourceText} 🔤 → {item.TranslatedText}"));
            return $"{context} 🔤 {request.Text} 🔤";
        }

        private static string RemoveModelThinking(string value) => RegexPatterns.ModelThinking().Replace(value, "");

        private static async Task<HttpResponseMessage> SendJsonAsync(
            string url,
            object? body,
            string apiKey,
            CancellationToken token,
            HttpMethod? method = null,
            string? authorizationScheme = null)
        {
            using var request = new HttpRequestMessage(method ?? HttpMethod.Post, url);
            if (body != null)
            {
                string json = JsonSerializer.Serialize(body, body.GetType());
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                if (authorizationScheme == null)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                else
                    request.Headers.TryAddWithoutValidation("Authorization", $"{authorizationScheme} {apiKey}");
            }
            return await SendAsync(request, token);
        }

        private static async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(ResponseHeaderTimeoutSeconds));
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }

        private static async Task<TranslationUpdate> CreateHttpErrorAsync(
            HttpResponseMessage response,
            CancellationToken token)
        {
            return new TranslationUpdate(
                TranslationUpdateKind.Failed,
                Error: await ReadHttpErrorAsync(response, token));
        }

        private static async Task<string> ReadHttpErrorAsync(
            HttpResponseMessage response,
            CancellationToken token)
        {
            string body = await response.Content.ReadAsStringAsync(token);
            return $"[ERROR] HTTP {response.StatusCode}: {body}";
        }

        private static async IAsyncEnumerable<SseEvent> ReadSseAsync(
            HttpResponseMessage response,
            [EnumeratorCancellation] CancellationToken token)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? eventName = null;
            var data = new StringBuilder();
            while (true)
            {
                string? line = await ReadLineWithTimeoutAsync(reader, token);
                if (line == null)
                {
                    if (data.Length > 0)
                        yield return new SseEvent(eventName ?? string.Empty, data.ToString());
                    yield break;
                }

                if (line.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        if (data[^1] == '\n')
                            data.Length--;
                        yield return new SseEvent(eventName ?? string.Empty, data.ToString());
                        data.Clear();
                        eventName = null;
                    }
                    continue;
                }
                if (line.StartsWith(':'))
                    continue;
                if (line.StartsWith("event:", StringComparison.Ordinal))
                    eventName = line[6..].Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                    data.Append(line[5..].TrimStart()).Append('\n');
            }
        }

        private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(StreamReadTimeoutSeconds));
            return await reader.ReadLineAsync(timeout.Token);
        }

        private static bool TryParseChatDelta(
            string json,
            out string delta,
            out string? finishReason,
            out string? error)
        {
            delta = string.Empty;
            finishReason = null;
            error = null;
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var errorElement))
                {
                    error = $"OpenAI Chat Completions error: {errorElement}";
                    return false;
                }
                if (!root.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                {
                    error = "OpenAI Chat Completions event is missing choices.";
                    return false;
                }
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null)
                    finishReason = finish.GetString();
                if (choice.TryGetProperty("delta", out var deltaElement) &&
                    deltaElement.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                    delta = RemoveModelThinking(content.GetString() ?? string.Empty);
                return true;
            }
            catch (JsonException ex)
            {
                error = $"Invalid OpenAI Chat Completions event: {ex.Message}";
                return false;
            }
        }

        private static bool TryGetEventType(string json, out string eventType)
        {
            eventType = string.Empty;
            try
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.TryGetProperty("type", out var type) &&
                    (eventType = type.GetString() ?? string.Empty).Length > 0;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryGetStringProperty(JsonElement element, string name, out string value)
        {
            value = string.Empty;
            return element.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.String &&
                (value = property.GetString() ?? string.Empty) is not null;
        }

        private static bool TryGetStringProperty(string json, string name, out string value)
        {
            value = string.Empty;
            try
            {
                using var document = JsonDocument.Parse(json);
                return TryGetStringProperty(document.RootElement, name, out value);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string? TryGetErrorMessage(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.String)
                        return error.GetString();
                    if (error.TryGetProperty("message", out var message))
                        return message.GetString();
                    return error.ToString();
                }
                return root.TryGetProperty("message", out var rootMessage) ? rootMessage.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private sealed record SseEvent(string Event, string Data);
    }
}
