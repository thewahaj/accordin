using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AccordIn.Plugin.Models;

namespace AccordIn.Plugin.Services
{
    /// <summary>
    /// Calls the Azure OpenAI chat completions endpoint with the pre-built account plan prompt.
    ///
    /// Responsibilities that live here (matching azureClient.js + the message-building in run.js):
    ///   1. Read endpoint / API key / deployment from D365 environment variables
    ///   2. Strip individual opportunity values before serialising account data (LLM never sees them)
    ///   3. Build the user message: pipeline facts → contact role guidance → account JSON
    ///   4. POST to Azure OpenAI and return raw text
    ///   5. Parse and strip ```json fences from the response
    ///
    /// Revenue figures in the parsed response are overwritten by the caller (GeneratePlan)
    /// with the pre-calculated PipelineResult values — this class does not do that post-processing.
    /// </summary>
    internal class AzureOpenAIClient
    {
        // ---- D365 environment variable schema names ----------------------------------------
        // These must match the schemaname values in the environmentvariabledefinition table.
        private const string EnvVarEndpoint   = "wrl_accordin_AzureOpenAIEndpoint";
        private const string EnvVarApiKey     = "wrl_accordin_AzureOpenAIKey";
        private const string EnvVarDeployment = "wrl_accordin_ModelDeployment";
        private const string EnvVarApiVersion = "wrl_accordin_AzureOpenAIApiVersion";

        // ---- Model call parameters — matches azureClient.js --------------------------------
        private const int    MaxTokens  = 4000;
        private const double Temperature = 0.3;

        private readonly string         _endpoint;
        private readonly string         _apiKey;
        private readonly string         _deploymentName;
        private readonly string         _apiVersion;
        private readonly ITracingService _tracer;

        public AzureOpenAIClient(
            string endpoint,
            string apiKey,
            string deploymentName,
            string apiVersion,
            ITracingService tracer = null)
        {
            if (string.IsNullOrWhiteSpace(endpoint))       throw new ArgumentNullException(nameof(endpoint));
            if (string.IsNullOrWhiteSpace(apiKey))         throw new ArgumentNullException(nameof(apiKey));
            if (string.IsNullOrWhiteSpace(deploymentName)) throw new ArgumentNullException(nameof(deploymentName));
            if (string.IsNullOrWhiteSpace(apiVersion))     throw new ArgumentNullException(nameof(apiVersion));

            _endpoint       = endpoint.TrimEnd('/');
            _apiKey         = apiKey;
            _deploymentName = deploymentName;
            _apiVersion     = apiVersion;
            _tracer         = tracer;
        }

        // -----------------------------------------------------------------------------------------
        // Factory — reads credentials from D365 environment variables
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Reads the four required environment variables from Dataverse and returns a configured client.
        /// Throws <see cref="InvalidPluginExecutionException"/> if any variable is missing or has no value.
        /// </summary>
        public static AzureOpenAIClient FromEnvironmentVariables(
            IOrganizationService svc,
            ITracingService tracer = null)
        {
            var endpoint       = ReadEnvVar(svc, EnvVarEndpoint);
            var apiKey         = ReadEnvVar(svc, EnvVarApiKey);
            var deploymentName = ReadEnvVar(svc, EnvVarDeployment);
            var apiVersion     = ReadEnvVar(svc, EnvVarApiVersion);

            return new AzureOpenAIClient(endpoint, apiKey, deploymentName, apiVersion, tracer);
        }

        // -----------------------------------------------------------------------------------------
        // High-level entry point — used by GeneratePlan
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Builds the full prompt from pre-calculated pipeline data and enriched contacts,
        /// calls the model, and returns the raw text alongside the parsed plan.
        ///
        /// Opportunity values are stripped from the account data before it is sent to the model —
        /// the model receives pipeline totals as injected facts instead (CLAUDE.md §1).
        /// </summary>
        public ParseResult Generate(
            string          systemPrompt,
            AccountPlanData data,
            PipelineResult  pipeline)
        {
            var userMessage = BuildUserMessage(data, pipeline);
            var messages = new[]
            {
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage { Role = "user",   Content = userMessage  },
            };

            _tracer?.Trace($"[AccordIn] Calling model '{_deploymentName}' — {data.Contacts?.Count ?? 0} contacts, {data.Opportunities?.Count ?? 0} opportunities");

            var rawText = PostChatCompletions(messages);
            return ParseResponse(rawText);
        }

        /// <summary>
        /// Low-level call — accepts a pre-built message list.
        /// Used by RefinePlan for conversational follow-up turns.
        /// maxTokens defaults to 2500 for chat (vs 4000 for initial generation, per chat.js).
        /// </summary>
        public string Call(IList<ChatMessage> messages, int maxTokens = 2500)
        {
            return PostChatCompletions(messages, maxTokens);
        }

        // -----------------------------------------------------------------------------------------
        // User message construction — port of the message-building block in run.js
        // -----------------------------------------------------------------------------------------

        private static string BuildUserMessage(AccountPlanData data, PipelineResult pipeline)
        {
            var sb = new StringBuilder();

            // Intent
            sb.AppendLine($"Plan intent: {(string.IsNullOrWhiteSpace(data.PlanIntent) ? "Analyse this account and suggest a strategy." : data.PlanIntent)}");
            sb.AppendLine();

            // Pre-calculated pipeline facts — injected verbatim so model cannot recompute
            sb.AppendLine("PRE-CALCULATED PIPELINE FACTS (verified - do not recalculate):");
            sb.AppendLine($"- Exact pipeline total: £{pipeline.ExactTotal:N0}");
            sb.AppendLine($"- Stage-weighted pipeline total (totalMid): £{pipeline.WeightedTotal:N0}");
            sb.AppendLine($"- totalLow ({pipeline.HighestStage ?? "n/a"} stage only at {pipeline.HighestStageWeightPct}% confidence): £{pipeline.TotalLow:N0}");
            sb.AppendLine($"- totalHigh (full pipeline if all opportunities close): £{pipeline.TotalHigh:N0}");
            sb.AppendLine($"- Opportunity count: {pipeline.OpportunityCount}");
            sb.AppendLine("- Stage breakdown:");
            foreach (var kvp in pipeline.ByStage ?? new Dictionary<string, StageBreakdown>())
            {
                sb.AppendLine($"  {kvp.Key}: {kvp.Value.Count} opportunity(s), £{kvp.Value.Total:N0} total, {(int)Math.Round(kvp.Value.Weight * 100)}% confidence weight");
            }
            sb.AppendLine();

            // Verified revenue field values — model copies these directly
            sb.AppendLine("Use these values directly:");
            sb.AppendLine($"- revenuePicture.pipelineValue = {pipeline.ExactTotal}");
            sb.AppendLine($"- revenuePicture.totalLow = {pipeline.TotalLow}");
            sb.AppendLine($"- revenuePicture.totalMid = {pipeline.WeightedTotal}");
            sb.AppendLine($"- revenuePicture.totalHigh = {pipeline.TotalHigh}");
            sb.AppendLine($"- revenueTarget = {pipeline.WeightedTotal}");
            sb.AppendLine();

            // Contact role guidance from ContactEnricher
            if (data.Contacts != null && data.Contacts.Count > 0)
            {
                sb.AppendLine("Contact planRole guidance (use suggestedPlanRole as your starting point, override only if the data clearly justifies it):");
                foreach (var c in data.Contacts)
                {
                    if (!string.IsNullOrEmpty(c.SuggestedPlanRole))
                        sb.AppendLine($"  {c.Name} ({c.Title}): suggested planRole = {c.SuggestedPlanRole}");
                }
                sb.AppendLine();
            }

            // Full account data — with opportunity values stripped
            var dataForModel = BuildDataForModel(data);
            sb.AppendLine("Account data:");
            sb.Append(JsonConvert.SerializeObject(dataForModel, Formatting.Indented));

            return sb.ToString();
        }

        /// <summary>
        /// Returns a copy of <paramref name="data"/> with opportunity values removed.
        /// The model receives only name, stage, closeDate, and status — never monetary values.
        /// This is the C# equivalent of the map() call in run.js lines 139-144.
        /// </summary>
        private static object BuildDataForModel(AccountPlanData data)
        {
            return new
            {
                planIntent  = data.PlanIntent,
                planType    = data.PlanType,
                account     = data.Account,
                productsOwned = data.ProductsOwned,
                opportunities = (data.Opportunities ?? new List<Opportunity>()).Select(o => new
                {
                    name      = o.Name,
                    stage     = o.Stage,
                    closeDate = o.CloseDate,
                    status    = o.Status,
                    // value intentionally omitted — model must not see individual figures
                }),
                contacts          = data.Contacts,
                activities        = data.Activities,
                commercialSignals = data.CommercialSignals,
                marketingSignals  = data.MarketingSignals,
                planHistory       = data.PlanHistory,
            };
        }

        // -----------------------------------------------------------------------------------------
        // HTTP call
        // -----------------------------------------------------------------------------------------

        private string PostChatCompletions(IList<ChatMessage> messages, int maxTokens = MaxTokens)
        {
            // {endpoint}/openai/deployments/{deployment}/chat/completions?api-version={version}
            var url = $"{_endpoint}/openai/deployments/{Uri.EscapeDataString(_deploymentName)}/chat/completions?api-version={Uri.EscapeDataString(_apiVersion)}";

            var requestBody = JsonConvert.SerializeObject(new
            {
                messages    = messages,
                max_tokens  = maxTokens,
                temperature = Temperature,
            });

            using (var httpClient = new HttpClient())
            {
                httpClient.Timeout = TimeSpan.FromSeconds(120);
                httpClient.DefaultRequestHeaders.Add("api-key", _apiKey);

                using (var content = new StringContent(requestBody, Encoding.UTF8, "application/json"))
                using (var response = httpClient.PostAsync(url, content).GetAwaiter().GetResult())
                {
                    var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (!response.IsSuccessStatusCode)
                    {
                        _tracer?.Trace($"[AccordIn] Azure OpenAI error {(int)response.StatusCode}: {responseBody}");
                        throw new InvalidPluginExecutionException(
                            $"Azure OpenAI returned {(int)response.StatusCode}: {TruncateForError(responseBody)}");
                    }

                    return ExtractContent(responseBody);
                }
            }
        }

        /// <summary>
        /// Extracts choices[0].message.content from the Azure OpenAI response JSON.
        /// </summary>
        private string ExtractContent(string responseJson)
        {
            try
            {
                var obj     = JObject.Parse(responseJson);
                var content = obj["choices"]?[0]?["message"]?["content"]?.Value<string>();

                if (content == null)
                    throw new InvalidPluginExecutionException("Azure OpenAI response missing choices[0].message.content");

                return content;
            }
            catch (JsonException ex)
            {
                throw new InvalidPluginExecutionException($"Could not parse Azure OpenAI response: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------------------------
        // Response parsing — port of parseModelResponse() in azureClient.js
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Strips ```json / ``` fences and deserialises to <see cref="PlanResponse"/>.
        /// Returns a <see cref="ParseResult"/> with a non-null ParseError if deserialisation fails —
        /// callers should log the error and surface it rather than swallowing it silently.
        /// </summary>
        public static ParseResult ParseResponse(string rawText)
        {
            try
            {
                var cleaned = rawText ?? string.Empty;

                // Strip opening fence (```json or ```)
                if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
                    cleaned = cleaned.Substring(7);
                else if (cleaned.StartsWith("```"))
                    cleaned = cleaned.Substring(3);

                // Strip closing fence
                var lastFence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0)
                    cleaned = cleaned.Substring(0, lastFence);

                cleaned = cleaned.Trim();

                var parsed = JsonConvert.DeserializeObject<PlanResponse>(cleaned);
                return new ParseResult { Parsed = parsed, Raw = rawText, ParseError = null };
            }
            catch (Exception ex)
            {
                return new ParseResult { Parsed = null, Raw = rawText, ParseError = ex.Message };
            }
        }

        // -----------------------------------------------------------------------------------------
        // D365 environment variable reader
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Reads an environment variable value from Dataverse using a linked entity query.
        /// Current value (environmentvariablevalue) takes precedence over default value (definition).
        /// Throws <see cref="InvalidPluginExecutionException"/> if the variable is missing or empty.
        /// </summary>
        private static string ReadEnvVar(IOrganizationService svc, string schemaName)
        {
            var query = new QueryExpression("environmentvariabledefinition")
            {
                ColumnSet = new ColumnSet("defaultvalue"),
                Criteria  = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("schemaname", ConditionOperator.Equal, schemaName)
                    }
                },
                TopCount = 1,
            };

            // Left outer join to environmentvariablevalue — the per-environment override
            var valueLink = query.AddLink(
                "environmentvariablevalue",
                "environmentvariabledefinitionid",
                "environmentvariabledefinitionid",
                JoinOperator.LeftOuter);
            valueLink.Columns   = new ColumnSet("value");
            valueLink.EntityAlias = "envval";

            var results = svc.RetrieveMultiple(query).Entities;
            if (results.Count == 0)
                throw new InvalidPluginExecutionException(
                    $"AccordIn: environment variable '{schemaName}' not found in Dataverse. " +
                    "Ensure the solution with this variable is installed.");

            var definition   = results[0];
            var currentValue = (definition.GetAttributeValue<AliasedValue>("envval.value")?.Value as string)?.Trim();
            var defaultValue = definition.GetAttributeValue<string>("defaultvalue")?.Trim();

            var resolved = string.IsNullOrEmpty(currentValue) ? defaultValue : currentValue;
            if (string.IsNullOrEmpty(resolved))
                throw new InvalidPluginExecutionException(
                    $"AccordIn: environment variable '{schemaName}' has no value. " +
                    "Set a current value in the Power Platform admin centre.");

            return resolved;
        }

        private static string TruncateForError(string s, int maxLen = 300)
        {
            return s != null && s.Length > maxLen ? s.Substring(0, maxLen) + "…" : s;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Supporting types
    // -----------------------------------------------------------------------------------------

    internal class ChatMessage
    {
        [JsonProperty("role")]
        public string Role { get; set; }

        [JsonProperty("content")]
        public string Content { get; set; }
    }

    internal class ParseResult
    {
        public PlanResponse Parsed     { get; set; }
        public string       Raw        { get; set; }
        public string       ParseError { get; set; }

        public bool Success => Parsed != null && ParseError == null;
    }
}
