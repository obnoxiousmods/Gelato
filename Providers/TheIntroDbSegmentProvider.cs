// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Gelato.Config;
using Gelato.Services;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace Gelato.Providers;

/// <summary>
/// TheIntroDB media segment provider — supports intro, recap, credits, and preview segments.
/// </summary>
public class TheIntroDbSegmentProvider : IMediaSegmentProvider
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;
    private const string ImdbIdPattern = @"\btt\d{7,8}\b";
    private const string SeasonEpisodePattern = @"S(?<season>\d{1,2})E(?<episode>\d{1,2})";

    private static readonly Regex ImdbIdRegex = new(
        ImdbIdPattern,
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );
    private static readonly Regex SeasonEpisodeRegex = new(
        SeasonEpisodePattern,
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private readonly ILibraryManager _libraryManager;
    private readonly TheIntroDbClient _theIntroDbClient;
    private readonly ILogger<TheIntroDbSegmentProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TheIntroDbSegmentProvider"/> class.
    /// </summary>
    public TheIntroDbSegmentProvider(
        ILibraryManager libraryManager,
        TheIntroDbClient theIntroDbClient,
        ILogger<TheIntroDbSegmentProvider> logger
    )
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(theIntroDbClient);
        ArgumentNullException.ThrowIfNull(logger);

        _libraryManager = libraryManager;
        _theIntroDbClient = theIntroDbClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Gelato TheIntroDB";

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        Debug.Assert(request.ItemId != Guid.Empty, "Media segment request should contain an item id.");

        var config = GelatoPlugin.Instance?.Configuration;
        if (config?.IntroDbProvider == IntroDbProvider.IntroDB)
        {
            return Array.Empty<MediaSegmentDto>();
        }

        _theIntroDbClient.SetApiKey(config?.IntroDbApiKey);

        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is not Episode episode)
        {
            return Array.Empty<MediaSegmentDto>();
        }

        if (!TryGetImdbId(episode, out var imdbId))
        {
            _logger.LogDebug(
                "Skipping TheIntroDB lookup for {ItemId}: IMDb id missing.",
                request.ItemId
            );
            return Array.Empty<MediaSegmentDto>();
        }

        if (!TryGetSeasonEpisodeNumbers(episode, out var seasonNumber, out var episodeNumber))
        {
            _logger.LogDebug(
                "Skipping TheIntroDB lookup for {ItemId}: invalid season/episode number.",
                request.ItemId
            );
            return Array.Empty<MediaSegmentDto>();
        }

        TheIntroDbSegmentResult? result;
        try
        {
            result = await _theIntroDbClient
                .GetSegmentsAsync(imdbId, seasonNumber, episodeNumber, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "TheIntroDB lookup failed for {ItemId} (IMDb {ImdbId} S{Season}E{Episode}).",
                request.ItemId, imdbId, seasonNumber, episodeNumber
            );
            return Array.Empty<MediaSegmentDto>();
        }

        if (result is null)
        {
            _logger.LogInformation(
                "TheIntroDB returned no segments for {ItemId} (IMDb {ImdbId} S{Season}E{Episode}).",
                request.ItemId, imdbId, seasonNumber, episodeNumber
            );
            return Array.Empty<MediaSegmentDto>();
        }

        var segments = new List<MediaSegmentDto>();

        AddSegments(segments, request.ItemId, result.Intros, MediaSegmentType.Intro, episode, "intro");
        AddSegments(segments, request.ItemId, result.Recaps, MediaSegmentType.Recap, episode, "recap");
        AddSegments(segments, request.ItemId, result.Credits, MediaSegmentType.Outro, episode, "credits");
        AddSegments(segments, request.ItemId, result.Previews, MediaSegmentType.Preview, episode, "preview");

        return segments;
    }

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is Episode);

    private void AddSegments(
        List<MediaSegmentDto> output,
        Guid itemId,
        List<TheIntroDbSegment> segments,
        MediaSegmentType type,
        Episode episode,
        string typeName
    )
    {
        foreach (var seg in segments)
        {
            var startTicks = (long)(seg.StartSeconds * TicksPerSecond);

            long endTicks;
            if (seg.EndSeconds.HasValue)
            {
                endTicks = (long)(seg.EndSeconds.Value * TicksPerSecond);
            }
            else if (episode.RunTimeTicks.HasValue && episode.RunTimeTicks.Value > 0)
            {
                endTicks = episode.RunTimeTicks.Value;
            }
            else
            {
                _logger.LogDebug(
                    "TheIntroDB {Type} segment for {ItemId} has null end_ms and no known runtime; skipping.",
                    typeName, itemId
                );
                continue;
            }

            if (endTicks <= startTicks)
            {
                _logger.LogWarning(
                    "TheIntroDB returned invalid {Type} segment for {ItemId}: start={Start} >= end={End}.",
                    typeName, itemId, startTicks, endTicks
                );
                continue;
            }

            if (episode.RunTimeTicks.HasValue
                && episode.RunTimeTicks.Value > 0
                && endTicks > episode.RunTimeTicks.Value)
            {
                _logger.LogWarning(
                    "TheIntroDB returned {Type} segment beyond duration for {ItemId}; clamping.",
                    typeName, itemId
                );
                endTicks = episode.RunTimeTicks.Value;
            }

            output.Add(new MediaSegmentDto
            {
                ItemId = itemId,
                StartTicks = startTicks,
                EndTicks = endTicks,
                Type = type,
            });
        }
    }

    private bool TryGetImdbId(Episode episode, out string imdbId)
    {
        if (
            episode.SeriesId != Guid.Empty
            && _libraryManager.GetItemById(episode.SeriesId) is Series series
        )
        {
            if (
                series.ProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var seriesImdbId)
                && !string.IsNullOrWhiteSpace(seriesImdbId)
            )
            {
                imdbId = seriesImdbId;
                return true;
            }
        }

        if (
            episode.ProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var providerImdbId)
            && !string.IsNullOrWhiteSpace(providerImdbId)
        )
        {
            imdbId = providerImdbId;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(episode.Path))
        {
            var match = ImdbIdRegex.Match(episode.Path);
            if (match.Success)
            {
                imdbId = match.Value;
                return true;
            }
        }

        imdbId = string.Empty;
        return false;
    }

    private static bool TryGetSeasonEpisodeNumbers(
        Episode episode,
        out int seasonNumber,
        out int episodeNumber
    )
    {
        seasonNumber = episode.AiredSeasonNumber ?? episode.ParentIndexNumber ?? 0;
        episodeNumber = episode.IndexNumber ?? 0;

        if (seasonNumber > 0 && episodeNumber > 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(episode.Path))
        {
            var match = SeasonEpisodeRegex.Match(episode.Path);
            if (
                match.Success
                && int.TryParse(match.Groups["season"].Value, out var parsedSeason)
                && int.TryParse(match.Groups["episode"].Value, out var parsedEpisode)
            )
            {
                seasonNumber = parsedSeason;
                episodeNumber = parsedEpisode;
                return seasonNumber > 0 && episodeNumber > 0;
            }
        }

        return seasonNumber > 0 && episodeNumber > 0;
    }
}
