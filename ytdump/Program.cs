﻿using System.Collections.Concurrent;
using System.CommandLine;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

var builder = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .AddEnvironmentVariables();

var configuration = builder.Build();
string? configApiKey = configuration["YouTube:ApiKey"] ?? configuration["YT_APIKEY"];

var accessTokenOption = new Option<string?>(["-k", "--key"], () => null, "The YouTube API Key. Can be specified in an environment variable YT_APIKEY.");

accessTokenOption.AddValidator(r =>
{
    var value = r.GetValueOrDefault<string?>() ?? configApiKey;

    if (string.IsNullOrEmpty(value))
    {
        r.ErrorMessage = "A YouTube API key is required. Please specify it via the commandline argument -k/--key or by using the the environment variable YT_APIKEY";
    }
});

var videosOption = new Option<string[]?>(["-v", "--video"], () => null, "The list of video IDs to process.")
{
    AllowMultipleArgumentsPerToken = true
};

var playlistsOption = new Option<string[]?>(["-p", "--playlist"], () => null, "The list of playlist IDs to process.")
{
    AllowMultipleArgumentsPerToken = true
};

var outputFileOption = new Option<string?>(["-o", "--output"], () => null, "The output file name and path. Default is comments.json");
var inputConfigOption = new Option<string?>(["-f", "--file"], () => null, "The JSON file that describes a set of videos and playlists.");

var rootCommand = new RootCommand
{
    accessTokenOption,
    videosOption,
    playlistsOption,
    outputFileOption,
    inputConfigOption
};

rootCommand.SetHandler(RunAsync, accessTokenOption, videosOption, playlistsOption, outputFileOption, inputConfigOption);
rootCommand.AddValidator(r =>
{
    var videos = r.GetValueForOption(videosOption);
    var playlists = r.GetValueForOption(playlistsOption);
    var configFile = r.GetValueForOption(inputConfigOption);

    if (videos is null or [] && playlists is null or [] && configFile is null)
    {
        r.ErrorMessage = "No videos, playlists or configuration file specified";
    }
});

await rootCommand.InvokeAsync(args);

async Task RunAsync(string? apiKey, string[]? videosIds, string[]? playlists, string? outputPath, string? configFile)
{
    var client = new HttpClient();
    var an = typeof(Program).Assembly.GetName();
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(an.Name!, $"{an.Version}"));

    apiKey ??= configApiKey;
    playlists ??= [];
    videosIds ??= [];
    outputPath = outputPath is null
        ? Path.Combine(Environment.CurrentDirectory, "comments.json")
        : Path.GetFullPath(outputPath);

    if (configFile is not null)
    {
        Console.WriteLine($"Config: {configFile}");

        await using var configFileStream = new FileStream(configFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var inputFile = await JsonSerializer.DeserializeAsync(configFileStream, YouTubeJsonContext.Default.YouTubeInputFile);

        videosIds = [.. inputFile?.Videos ?? []];
        playlists = [.. inputFile?.Playlists ?? []];
    }
    else
    {
        if (playlists.Length > 0)
        {
            // Print inputs here
            Console.WriteLine($"Playlists: {string.Join(", ", playlists)}");
        }
        else
        {
            Console.WriteLine("No playlists specified");
        }

        if (videosIds.Length > 0)
        {
            Console.WriteLine($"Videos: {string.Join(", ", videosIds)}");
        }
        else
        {
            Console.WriteLine("No videos specified");
        }
    }


    var sw = Stopwatch.StartNew();

    // Process playlists and the videos in parallel

    // Helper memoization function for video fetching, this should avoid fetching the same video multiple times in parallel
    ConcurrentDictionary<string, Task<YouTubeOutputVideo?>> videoCache = [];
    async Task<YouTubeOutputVideo?> GetYouTubeVideoCachedAsync(string videoId) =>
        await videoCache.GetOrAdd(videoId, id => GetYouTubeVideoAsync(id));

    async Task<YouTubeOutputVideo?[]> GetYouTubeVideosAsync(Task<List<string>> videosTask) =>
        await Task.WhenAll((await videosTask).Select(GetYouTubeVideoCachedAsync));

    var playListVideosTask = Task.WhenAll(playlists.Select(GetVideoIdsFromPlaylistAsync).Select(GetYouTubeVideosAsync));
    var videosTask = Task.WhenAll(videosIds.Select(GetYouTubeVideoCachedAsync));

    var playlistVideos = await playListVideosTask;
    var videos = await videosTask;

    // Combine and filter out unprocessed videos, also dedupe videos by ID
    YouTubeOutputVideo[] allVideos = [.. videos.Concat(playlistVideos.SelectMany(s => s)).Where(v => v != null).DistinctBy(v => v!.Id)!];

    sw.Stop();

    if (allVideos.Length > 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(fileStream, allVideos, YouTubeJsonContext.Default.YouTubeOutputVideoArray);

        Console.WriteLine();
        Console.WriteLine($"Processed {allVideos.Length} vidoes in {sw.Elapsed}");
        Console.WriteLine($"Wrote output to {outputPath}");
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine("No videos processed.");
    }

    async Task<bool> GetVideoInfoAsync(YouTubeOutputVideo video)
    {
        string apiUrl = $"https://www.googleapis.com/youtube/v3/videos?part=snippet&id={video.Id}&key={apiKey}";

        HttpResponseMessage response = await client.GetAsync(apiUrl);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Error: {response.StatusCode}");
            return false;
        }

        var videoResponse = await response.Content.ReadFromJsonAsync(YouTubeJsonContext.Default.YouTubeVideoResponse);

        if (videoResponse == null || videoResponse.Items.Count == 0)
        {
            return false;
        }

        var snippet = videoResponse.Items[0].Snippet;
        video.Title = snippet.Title;
        video.UploadDate = snippet.PublishedAt;
        video.Url = $"https://www.youtube.com/watch?v={video.Id}";

        return true;
    }

    async Task<List<string>> GetVideoIdsFromPlaylistAsync(string playlistId)
    {
        string apiUrl = $"https://www.googleapis.com/youtube/v3/playlistItems?part=snippet&maxResults=50&playlistId={playlistId}&key={apiKey}";

        var videoIds = new List<string>();
        string? nextPageToken = null;

        Console.WriteLine($"Fetching videos from playlist {playlistId}");

        try
        {
            do
            {
                string requestUrl = string.IsNullOrEmpty(nextPageToken)
                    ? apiUrl
                    : $"{apiUrl}&pageToken={nextPageToken}";

                HttpResponseMessage response = await client.GetAsync(requestUrl);

                if (!response.IsSuccessStatusCode)
                {
                    return videoIds;
                }

                var result = await response.Content.ReadFromJsonAsync(YouTubeJsonContext.Default.YouTubeVideoResponse);

                if (result?.Items != null)
                {
                    foreach (var item in result.Items)
                    {
                        var videoId = item.Snippet?.ResourceId?.VideoId;
                        if (!string.IsNullOrEmpty(videoId))
                        {
                            videoIds.Add(videoId);
                        }
                    }
                }

                nextPageToken = result?.NextPageToken;

            } while (!string.IsNullOrEmpty(nextPageToken));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred: {ex.Message}");
        }

        Console.WriteLine($"Found {videoIds.Count} videos in playlist {playlistId}");

        return videoIds;
    }

    async Task<YouTubeOutputVideo?> GetYouTubeVideoAsync(string videoId)
    {
        string apiUrl = $"https://www.googleapis.com/youtube/v3/commentThreads?part=snippet,replies&videoId={videoId}&key={apiKey}&maxResults=100";

        string? nextPageToken = null;

        Console.WriteLine($"Processing Video {videoId}");

        var video = new YouTubeOutputVideo
        {
            Id = videoId
        };

        // Write the video title and URL into the file
        if (!await GetVideoInfoAsync(video))
        {
            Console.WriteLine($"Could not fetch video info for video {videoId}");
            return null;
        }

        Console.WriteLine($"Video Title: {video.Title}");
        Console.WriteLine($"Video URL: {video.Url}");

        try
        {
            do
            {
                string requestUrl = string.IsNullOrEmpty(nextPageToken)
                    ? apiUrl
                    : $"{apiUrl}&pageToken={nextPageToken}";

                HttpResponseMessage response = await client.GetAsync(requestUrl);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Error: {response.StatusCode}");
                    return null;
                }

                var commentResponse = await response.Content.ReadFromJsonAsync(YouTubeJsonContext.Default.YouTubeCommentResponse);

                if (commentResponse == null)
                {
                    return null;
                }

                foreach (var item in commentResponse.Items)
                {
                    video.Comments.Add(new YouTubeOutputComment
                    {
                        Id = item.Snippet.TopLevelComment.Id,
                        Author = item.Snippet.TopLevelComment.Snippet.AuthorDisplayName,
                        Text = item.Snippet.TopLevelComment.Snippet.TextDisplay,
                        PublishedAt = item.Snippet.TopLevelComment.Snippet.PublishedAt
                    });


                    // Handle replies
                    foreach (var reply in item.Replies.Comments)
                    {
                        video.Comments.Add(new YouTubeOutputComment
                        {
                            Id = reply.Id,
                            Author = reply.Snippet.AuthorDisplayName,
                            Text = reply.Snippet.TextDisplay,
                            PublishedAt = reply.Snippet.PublishedAt,
                            ParentId = item.Id
                        });
                    }
                }

                nextPageToken = commentResponse.NextPageToken;

            } while (!string.IsNullOrEmpty(nextPageToken));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred: {ex.Message}");
        }

        return video;
    }
}
