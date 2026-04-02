using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AccordIn.Plugin.Services
{
    public class IntentClassifier
    {
        private readonly string _endpoint;
        private readonly string _apiKey;
        private readonly string _deployment;
        private readonly string _apiVersion;

        public IntentClassifier(string endpoint, string apiKey, string deployment, string apiVersion)
        {
            _endpoint = endpoint?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(endpoint));
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
            _apiVersion = apiVersion ?? throw new ArgumentNullException(nameof(apiVersion));
        }

        public IntentResult Classify(string userMessage, PlanContext context, string systemPrompt)
        {
            var contextJson = JsonSerializer.Serialize(context, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

            var userContent = $"Plan context:\n{contextJson}\n\nUser instruction: {userMessage}";

            var requestBody = new
            {
                model = _deployment,
                max_tokens = 500,
                temperature = 0,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userContent }
                }
            };

            var json = JsonSerializer.Serialize(requestBody);

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("api-key", _apiKey);

                var url = $"{_endpoint}/openai/deployments/{Uri.EscapeDataString(_deployment)}/chat/completions?api-version={Uri.EscapeDataString(_apiVersion)}";
                var response = client.PostAsync(
                    url,
                    new StringContent(json, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();

                var responseJson = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"[AccordIn] IntentClassifier error: {responseJson}");

                using (var doc = JsonDocument.Parse(responseJson))
                {
                    var content = doc.RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("message")
                        .GetProperty("content")
                        .GetString();

                    var cleaned = content?.Trim();
                    if (cleaned != null && cleaned.StartsWith("```", StringComparison.Ordinal))
                    {
                        var start = cleaned.IndexOf('\n') + 1;
                        var end = cleaned.LastIndexOf("```", StringComparison.Ordinal);
                        if (end > start) cleaned = cleaned.Substring(start, end - start).Trim();
                    }

                    return JsonSerializer.Deserialize<IntentResult>(
                               cleaned ?? "{}",
                               new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                           ?? new IntentResult
                           {
                               Action = "query",
                               Response = "I could not understand that request."
                           };
                }
            }
        }
    }

    public class PlanContext
    {
        [JsonPropertyName("cadences")]
        public List<CadenceContext> Cadences { get; set; } = new List<CadenceContext>();

        [JsonPropertyName("actions")]
        public List<ActionContext> Actions { get; set; } = new List<ActionContext>();

        [JsonPropertyName("recommendations")]
        public List<RecommendationContext> Recommendations { get; set; } = new List<RecommendationContext>();

        [JsonPropertyName("contacts")]
        public List<ContactContext> Contacts { get; set; } = new List<ContactContext>();
    }

    public class CadenceContext
    {
        [JsonPropertyName("d365Id")]
        public string D365Id { get; set; }

        [JsonPropertyName("contactName")]
        public string ContactName { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("frequency")]
        public string Frequency { get; set; }

        [JsonPropertyName("channel")]
        public string Channel { get; set; }

        [JsonPropertyName("purpose")]
        public string Purpose { get; set; }
    }

    public class ActionContext
    {
        [JsonPropertyName("d365Id")]
        public string D365Id { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("priority")]
        public string Priority { get; set; }

        [JsonPropertyName("channel")]
        public string Channel { get; set; }

        [JsonPropertyName("suggestedTiming")]
        public string SuggestedTiming { get; set; }
    }

    public class RecommendationContext
    {
        [JsonPropertyName("d365Id")]
        public string D365Id { get; set; }

        [JsonPropertyName("productName")]
        public string ProductName { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("rationale")]
        public string Rationale { get; set; }

        [JsonPropertyName("confidence")]
        public string Confidence { get; set; }

        [JsonPropertyName("estimatedValue")]
        public double EstimatedValue { get; set; }
    }

    public class ContactContext
    {
        [JsonPropertyName("d365ContactId")]
        public string D365ContactId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("planRole")]
        public string PlanRole { get; set; }
    }

    public class IntentResult
    {
        [JsonPropertyName("action")]
        public string Action { get; set; }

        [JsonPropertyName("targetId")]
        public string TargetId { get; set; }

        [JsonPropertyName("changes")]
        public JsonElement? Changes { get; set; }

        [JsonPropertyName("response")]
        public string Response { get; set; }
    }
}
