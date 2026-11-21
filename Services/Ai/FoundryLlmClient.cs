#pragma warning disable OPENAI001 // The token-based client constructors are marked experimental by the SDK.

using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Identity;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace TuringTown.Services.Ai;

/// <summary>
/// The only class that talks to Azure AI Foundry. It uses the OpenAI .NET SDK against the Azure OpenAI v1 endpoint with
/// Chat Completions (stateless, structured output supported). Everything that depends on the model is configuration: the
/// deployment name, <c>reasoning_effort</c> and <c>max_completion_tokens</c> come from the call, and no <c>temperature</c>
/// is sent (reasoning models reject it). Auth is the API key when one is configured (development) and otherwise the
/// app's managed identity through <c>DefaultAzureCredential</c> (production).
/// No secret is ever logged.
/// </summary>
public sealed class FoundryLlmClient : ILlmClient
{
    private readonly ChatClient _chat;

    public FoundryLlmClient(IOptions<FoundryOptions> foundry, IOptions<AiOptions> ai)
        : this(Build(foundry.Value, ai.Value, null))
    {
    }

    /// <summary>For tests: a client already built, normally over a fake transport.</summary>
    internal FoundryLlmClient(ChatClient chat) => _chat = chat;

    /// <summary>
    /// Returns the v1 base URL (with a trailing slash) from whatever was configured: the resource endpoint
    /// (https://name.openai.azure.com), a project endpoint, the v1 URL, or a full request URL copied from the portal or the
    /// docs such as ".../openai/v1/responses" or ".../openai/deployments/x/chat/completions?api-version=...". Everything
    /// from "/openai" onward and any query string is dropped, and "/openai/v1/" is put in its place.
    /// </summary>
    internal static Uri V1Endpoint(string endpoint)
    {
        Uri uri = new(endpoint.Trim());
        string path = uri.AbsolutePath;
        int openai = path.IndexOf("/openai", StringComparison.OrdinalIgnoreCase);
        string prefix = (openai >= 0 ? path[..openai] : path).TrimEnd('/');
        return new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, prefix + "/openai/v1/").Uri;
    }

    internal static ChatClient Build(FoundryOptions foundry, AiOptions ai, PipelineTransport? transport)
    {
        OpenAIClientOptions options = new()
        {
            Endpoint = V1Endpoint(foundry.Endpoint),
            // A chat line is not worth waiting out retries for: the caller falls back to staying quiet.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
        };
        if (transport is not null) options.Transport = transport;

        return string.IsNullOrWhiteSpace(foundry.ApiKey)
            ? new ChatClient(foundry.Deployment, new BearerTokenPolicy(new DefaultAzureCredential(), ai.TokenScope), options)
            : new ChatClient(foundry.Deployment, new ApiKeyCredential(foundry.ApiKey), options);
    }

    /// <summary>The service's error text on one line and bounded, so it can be logged to find a misconfiguration.</summary>
    internal static string Detail(string message)
    {
        string oneLine = string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length > 400 ? oneLine[..400] + "..." : oneLine;
    }

    public async Task<LlmResult> CompleteAsync(LlmCall call, CancellationToken ct)
    {
        ChatCompletionOptions options = new() { MaxOutputTokenCount = call.MaxOutputTokens };
        if (!string.IsNullOrWhiteSpace(call.ReasoningEffort))
            options.ReasoningEffortLevel = new ChatReasoningEffortLevel(call.ReasoningEffort);
        if (call.Schema is { } schema)
            options.ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(schema.Name, BinaryData.FromString(schema.Json), jsonSchemaIsStrict: true);

        ChatCompletion completion;
        try
        {
            ClientResult<ChatCompletion> response = await _chat.CompleteChatAsync(
                [ChatMessage.CreateSystemMessage(call.System), ChatMessage.CreateUserMessage(call.User)], options, ct);
            completion = response.Value;
        }
        catch (ClientResultException ex) when (ex.Status == 400 && ex.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase))
        {
            // Azure's own filters (including Prompt Shields) refused the request. The bot stays quiet.
            throw new LlmBlockedException("The model's content filter refused the request.");
        }
        catch (ClientResultException ex)
        {
            throw new LlmServiceException(ex.Status, Detail(ex.Message), ex);
        }

        if (completion.FinishReason == ChatFinishReason.ContentFilter)
            throw new LlmBlockedException("The model's content filter stopped the reply.");

        string text = string.Concat(completion.Content.Select(part => part.Text));
        if (text.Length == 0)
        {
            // Usually the token limit was spent on reasoning. Say so, so the setting can be tuned.
            throw new InvalidOperationException(completion.FinishReason == ChatFinishReason.Length
                ? $"The {call.Kind} reply was empty because MaxOutputTokens ran out (reasoning tokens count)."
                : $"The {call.Kind} reply was empty.");
        }

        if (completion.FinishReason == ChatFinishReason.Length && call.Schema is not null)
            throw new InvalidOperationException($"The {call.Kind} reply was cut off by MaxOutputTokens, so the JSON is incomplete.");

        ChatTokenUsage usage = completion.Usage;
        return new LlmResult(
            text,
            usage?.InputTokenCount ?? 0,
            usage?.InputTokenDetails?.CachedTokenCount ?? 0,
            usage?.OutputTokenCount ?? 0,
            usage?.OutputTokenDetails?.ReasoningTokenCount ?? 0);
    }
}
