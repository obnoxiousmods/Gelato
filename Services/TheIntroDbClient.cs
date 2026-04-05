// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// Client for retrieving intro/credits/recap/preview timestamps from TheIntroDB (theintrodb.org).
/// </summary>
public sealed class TheIntroDbClient
{
    /// <summary>
    /// Default timeout for TheIntroDB requests, in seconds.
    /// </summary>
    public const int DefaultTimeoutSeconds = 10;

    private const string BaseUrl = "https://api.theintrodb.org/v2";
    private const string MediaPath = "/media";
    private const double MillisecondsPerSecond = 1000d;

    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<TheIntroDbClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TheIntroDbClient"/> class.
    /// </summary>
    /// <param name="httpClient">HTTP client.</param>
    /// <param name="logger">Logger.</param>
    public TheIntroDbClient(HttpClient httpClient, ILogger<TheIntroDbClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(BaseUrl, UriKind.Absolute);
        }

        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json")
        );
    }

    /// <summary>
    /// Set the API key to use for authenticated requests.
    /// </summary>
    public void SetApiKey(string? apiKey)
    {
        _httpClient.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>
    /// Fetch all segment timestamps for a specific episode.
    /// </summary>
    /// <param name="imdbId">IMDb id.</param>
    /// <param name="seasonNumber">Season number.</param>
    /// <param name="episodeNumber">Episode number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Segment result or null if not available.</returns>
    public async Task<TheIntroDbSegmentResult?> GetSegmentsAsync(
        string imdbId,
        int seasonNumber,
        int episodeNumber,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            throw new ArgumentException("IMDb id must be provided.", nameof(imdbId));
        }

        if (seasonNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seasonNumber), "Season number must be positive.");
        }

        if (episodeNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(episodeNumber), "Episode number must be positive.");
        }

        var requestUri = new UriBuilder(new Uri(_httpClient.BaseAddress!, MediaPath))
        {
            Query = $"imdb_id={Uri.EscapeDataString(imdbId)}&season={seasonNumber}&episode={episodeNumber}",
        }.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning(
                "TheIntroDB rate limit hit for {ImdbId} S{Season}E{Episode}.",
                imdbId, seasonNumber, episodeNumber
            );
            return null;
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            _logger.LogWarning(
                "TheIntroDB request rejected for {ImdbId} S{Season}E{Episode}.",
                imdbId, seasonNumber, episodeNumber
            );
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "TheIntroDB request failed for {ImdbId} S{Season}E{Episode} with status {Status}.",
                imdbId, seasonNumber, episodeNumber, response.StatusCode
            );
            return null;
        }

#if EMBY
        using var payloadStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#else
        using var payloadStream = await response
            .Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
#endif
        var payload = await JsonSerializer
            .DeserializeAsync<TheIntroDbMediaResponse>(payloadStream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        if (payload is null)
        {
            _logger.LogWarning(
                "TheIntroDB response could not be parsed for {ImdbId} S{Season}E{Episode}.",
                imdbId, seasonNumber, episodeNumber
            );
            return null;
        }

        return new TheIntroDbSegmentResult(
            imdbId,
            seasonNumber,
            episodeNumber,
            ToSegments(payload.Intro),
            ToSegments(payload.Recap),
            ToSegments(payload.Credits),
            ToSegments(payload.Preview)
        );
    }

    private static List<TheIntroDbSegment> ToSegments(List<TheIntroDbSegmentRaw>? raw)
    {
        var result = new List<TheIntroDbSegment>();
        if (raw is null) return result;

        foreach (var s in raw)
        {
            if (s.StartMs < 0) continue;
            result.Add(new TheIntroDbSegment(
                s.StartMs / MillisecondsPerSecond,
                s.EndMs.HasValue ? s.EndMs.Value / MillisecondsPerSecond : null
            ));
        }

        return result;
    }

    private sealed class TheIntroDbMediaResponse
    {
        [JsonPropertyName("intro")]
        public List<TheIntroDbSegmentRaw>? Intro { get; set; }

        [JsonPropertyName("recap")]
        public List<TheIntroDbSegmentRaw>? Recap { get; set; }

        [JsonPropertyName("credits")]
        public List<TheIntroDbSegmentRaw>? Credits { get; set; }

        [JsonPropertyName("preview")]
        public List<TheIntroDbSegmentRaw>? Preview { get; set; }
    }

    private sealed class TheIntroDbSegmentRaw
    {
        [JsonPropertyName("start_ms")]
        public long StartMs { get; set; }

        [JsonPropertyName("end_ms")]
        public long? EndMs { get; set; }
    }
}

public sealed record TheIntroDbSegment(double StartSeconds, double? EndSeconds);

public sealed record TheIntroDbSegmentResult(
    string ImdbId,
    int Season,
    int Episode,
    List<TheIntroDbSegment> Intros,
    List<TheIntroDbSegment> Recaps,
    List<TheIntroDbSegment> Credits,
    List<TheIntroDbSegment> Previews
);
