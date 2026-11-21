using Azure;
using Azure.AI.ContentSafety;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TuringTown.Models;

namespace TuringTown.Services.Games;

public enum ContentSafetyProvider
{
    /// <summary>No external check (the default).</summary>
    None,

    /// <summary>Azure AI Content Safety.</summary>
    Azure
}

/// <summary>
/// The "ContentSafety" section. Severities are on Azure's four-level scale: 0 safe, 2 low, 4 medium, 6 high.
/// </summary>
public sealed class ContentSafetyOptions
{
    public const string SectionName = "ContentSafety";

    public ContentSafetyProvider Provider { get; set; } = ContentSafetyProvider.None;

    public string Endpoint { get; set; } = "";

    /// <summary>Development only. Empty means managed identity.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>A Family Friendly room blocks anything at or above this severity, in every category.</summary>
    public int FamilyThreshold { get; set; } = 2;

    /// <summary>
    /// A mature room relaxes only the Violence category to this severity. Hate, self-harm and sexual content stay at
    /// <see cref="FamilyThreshold"/>, because the mandatory safety rules apply in both modes (docs/content-policy.md).
    /// </summary>
    public int MatureThreshold { get; set; } = 4;

    public int TimeoutMs { get; set; } = 2000;

    /// <summary>
    /// When the service cannot be reached: true lets the message through (the game keeps working, and the model's own
    /// filters and <see cref="ChatFilter"/> still apply), false blocks it.
    /// </summary>
    public bool FailOpen { get; set; } = true;
}

/// <summary>Scores a text per safety category. Behind an interface so the rules below are testable without Azure.</summary>
public interface IContentSafetyClient
{
    /// <summary>Category name (Hate, SelfHarm, Sexual, Violence) to severity.</summary>
    Task<IReadOnlyDictionary<string, int>> AnalyzeAsync(string text, CancellationToken ct);
}

public sealed class AzureContentSafetyClient : IContentSafetyClient
{
    private readonly ContentSafetyClient _client;

    public AzureContentSafetyClient(IOptions<ContentSafetyOptions> options)
    {
        ContentSafetyOptions o = options.Value;
        Uri endpoint = new(o.Endpoint);
        _client = string.IsNullOrWhiteSpace(o.ApiKey)
            ? new ContentSafetyClient(endpoint, new DefaultAzureCredential())
            : new ContentSafetyClient(endpoint, new AzureKeyCredential(o.ApiKey));
    }

    public async Task<IReadOnlyDictionary<string, int>> AnalyzeAsync(string text, CancellationToken ct)
    {
        Response<AnalyzeTextResult> response = await _client.AnalyzeTextAsync(
            new AnalyzeTextOptions(text) { OutputType = AnalyzeTextOutputType.FourSeverityLevels }, ct);

        return response.Value.CategoriesAnalysis.ToDictionary(c => c.Category.ToString(), c => c.Severity ?? 0);
    }
}

/// <summary>
/// The content filter for every chat line, human or bot, every onboarding answer and every generated question. Stricter
/// when Family Friendly is on. Which categories relax in a mature room is in <see cref="ContentSafetyOptions"/>.
/// </summary>
public sealed class SeverityContentFilter(IContentSafetyClient client, IOptions<ContentSafetyOptions> options, ILogger<SeverityContentFilter> logger) : IContentFilter
{
    private const string Violence = "Violence";

    public async Task<bool> IsAllowedAsync(string text, ContentPolicy policy, CancellationToken ct)
    {
        ContentSafetyOptions o = options.Value;
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMilliseconds(o.TimeoutMs));

        try
        {
            IReadOnlyDictionary<string, int> severities = await client.AnalyzeAsync(text, limit.Token);
            foreach ((string category, int severity) in severities)
            {
                int threshold = policy.FamilyFriendly || category != Violence ? o.FamilyThreshold : o.MatureThreshold;
                if (severity >= threshold) return false;
            }
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Content safety check failed ({Type}); {Action}", ex.GetType().Name, o.FailOpen ? "letting the text through" : "blocking the text");
            return o.FailOpen;
        }
    }
}
