using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using TuringTown.Models;
using TuringTown.Services.Ai;

namespace TuringTown.Services.Storage;

public enum StorageProvider
{
    InMemory,
    AzureBlob
}

public sealed class BlobStorageOptions
{
    /// <summary>https://account.blob.core.windows.net. Production uses managed identity with this.</summary>
    public string ServiceUri { get; set; } = "";

    public string ArchiveContainer { get; set; } = "turingtown-transcripts";

    /// <summary>Development only (for example Azurite). Never put a real connection string in appsettings.json.</summary>
    public string ConnectionString { get; set; } = "";
}

/// <summary>
/// The "Storage" section. Bot memory is always in memory (rooms are, too). Only the opt-in transcript archive can go to
/// Azure Blob, and it is off by default.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Where the transcript archive goes when <see cref="ArchiveTranscripts"/> is on.</summary>
    public StorageProvider Provider { get; set; } = StorageProvider.InMemory;

    public bool ArchiveTranscripts { get; set; }

    public BlobStorageOptions Blob { get; set; } = new();
}

public sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        if (options is { ArchiveTranscripts: true, Provider: StorageProvider.AzureBlob }
            && string.IsNullOrWhiteSpace(options.Blob.ServiceUri) && string.IsNullOrWhiteSpace(options.Blob.ConnectionString))
        {
            return ValidateOptionsResult.Fail("Storage:Blob:ServiceUri is missing (or set Storage:Blob:ConnectionString for local development).");
        }

        return ValidateOptionsResult.Success;
    }
}

public sealed record ArchivedPlayer(string Alias, string Role);

public sealed record ArchivedQuestion(string Text, QuestionKind Kind, IReadOnlyList<string> Options);

public sealed record ArchivedLine(string Alias, string Text, DateTimeOffset SentAtUtc);

/// <summary>
/// A finished game, for tuning the bots. It is written once, at the Results stage, when every role is public anyway, and
/// holds only aliases and game text: no names, ids of any kind, connection ids, tokens, room codes or IP addresses, and no
/// onboarding answers. No bot ever reads it.
/// </summary>
public sealed record GameArchive(
    Guid GameId,
    DateTimeOffset EndedUtc,
    string Winner,
    IReadOnlyList<ArchivedPlayer> Players,
    IReadOnlyList<ArchivedQuestion> Questions,
    IReadOnlyList<RoundOutcome> Rounds,
    IReadOnlyList<ArchivedLine> Chat)
{
    /// <summary>Where it is stored: one file per game, grouped by day so a lifecycle rule can expire old days.</summary>
    public string BlobName => $"{EndedUtc:yyyy-MM-dd}/{GameId:N}.json";
}

public interface IGameArchive
{
    Task ArchiveAsync(GameArchive archive, CancellationToken ct);
}

/// <summary>The default: nothing is kept.</summary>
public sealed class NullGameArchive : IGameArchive
{
    public Task ArchiveAsync(GameArchive archive, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>For local runs and tests.</summary>
public sealed class InMemoryGameArchive : IGameArchive
{
    private readonly List<GameArchive> _archives = [];

    public IReadOnlyList<GameArchive> Archives
    {
        get
        {
            lock (_archives)
                return _archives.ToArray();
        }
    }

    public Task ArchiveAsync(GameArchive archive, CancellationToken ct)
    {
        lock (_archives)
            _archives.Add(archive);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Writes one JSON file per game to a private blob container. Set a lifecycle rule on the container to delete old files
/// (the plan is 30 days). Production authenticates with the app's managed identity (role "Storage Blob Data Contributor").
/// </summary>
public sealed class BlobGameArchive(BlobContainerClient container) : IGameArchive
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _created = new(1, 1);
    private bool _containerReady;

    public static BlobGameArchive Create(BlobStorageOptions options)
    {
        BlobContainerClient container = string.IsNullOrWhiteSpace(options.ConnectionString)
            ? new BlobServiceClient(new Uri(options.ServiceUri), new Azure.Identity.DefaultAzureCredential()).GetBlobContainerClient(options.ArchiveContainer)
            : new BlobContainerClient(options.ConnectionString, options.ArchiveContainer);
        return new BlobGameArchive(container);
    }

    public async Task ArchiveAsync(GameArchive archive, CancellationToken ct)
    {
        await EnsureContainerAsync(ct);
        await container.GetBlobClient(archive.BlobName).UploadAsync(
            BinaryData.FromString(Serialize(archive)),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" } },
            ct);
    }

    internal static string Serialize(GameArchive archive) => JsonSerializer.Serialize(archive, Json);

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (_containerReady) return;

        await _created.WaitAsync(ct);
        try
        {
            // Private: no public access.
            await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
            _containerReady = true;
        }
        finally
        {
            _created.Release();
        }
    }
}
