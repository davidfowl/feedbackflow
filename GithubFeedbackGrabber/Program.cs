﻿using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

var builder = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .AddEnvironmentVariables();

var configuration = builder.Build();

// The access token can be specified in the command line argument -t/--token or by using the environment variable GITHUB_TOKEN
var configAccessToken = configuration["Github:ApiKey"] ?? configuration["GITHUB_TOKEN"] ?? "";

var accessToken = new Option<string?>(["-t", "--token"], () => null, "The Github access token. Can be specified in an environment variable GITHUB_TOKEN.");

accessToken.AddValidator(r =>
{
    var value = r.GetValueOrDefault<string?>() ?? configAccessToken;

    if (string.IsNullOrWhiteSpace(value))
    {
        r.ErrorMessage = "A Github access token is required. Please specify it via the command line argument -t/--token or by using the environment variable GITHUB_TOKEN";
    }
});

var repoOption = new Option<string?>(["-r", "--repository"], () => null, "GitHub repository in the format owner/repo") { IsRequired = true };

repoOption.AddValidator(r =>
{
    var repo = r.GetValueOrDefault<string?>();

    if (string.IsNullOrWhiteSpace(repo))
    {
        r.ErrorMessage = "Unable to determine the repository. Please specify it via the command line argument -repo";
    }
    else if (!repo.Contains('/'))
    {
        r.ErrorMessage = "Invalid repository format, expected owner/repository. Please specify it in the configuration section Github:Repo";
    }
});

var labelOption = new Option<string[]>(["-l", "-labels"], "When exporting issues, what labels should be used to filter.")
{
    AllowMultipleArgumentsPerToken = true
};

var includeDiscussionsOption = new Option<bool?>(["-d", "--include-discussions"], () => null, "Include github discussions");

var rootCommand = new RootCommand("CLI tool for extracting Github issues and discussions in a JSON format.")
{
    accessToken,
    repoOption,
    labelOption,
    includeDiscussionsOption
};

rootCommand.SetHandler(RunAsync, accessToken, repoOption, labelOption, includeDiscussionsOption);

await rootCommand.InvokeAsync(args);

async Task RunAsync(string? accessToken, string? repo, string[] labels, bool? includeDiscussions)
{
    accessToken ??= configAccessToken;
    includeDiscussions ??= labels.Length == 0 ? true : false;

    var (repoOwner, repoName) = repo?.Split('/') switch
    {
    [var owner, var name] => (owner, name),
        _ => throw new InvalidOperationException("Invalid repository format, expected owner/repository. Please specify it in the configuration section Github:Repo")
    };

    var maxRetries = 5;
    var retryCount = 0;

    using var httpClient = new HttpClient();
    httpClient.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);
    var an = typeof(Program).Assembly.GetName();
    httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(an.Name!, $"{an.Version}"));

    // Print out the repository information
    Console.WriteLine($"Repository: {repoOwner}/{repoName}");
    if (labels.Length > 0)
    {
        Console.WriteLine($"Labels: {string.Join(", ", labels)}");
    }
    else
    {
        Console.WriteLine("No Labels specified.");
    }

    Console.WriteLine($"Including discussions: {(includeDiscussions.Value ? "yes" : "no")}");

    var discussionsTask = includeDiscussions.Value ? WriteDiscussionsAsync() : Task.CompletedTask;
    var issuesTask = WriteIssuesAsync(labels);

    await Task.WhenAll(discussionsTask, issuesTask);

    async Task WriteIssuesAsync(string[] labels)
    {
        var labelsPart = labels.Length > 0 ? string.Join("_", labels) : "all";
        var fileName = $"issues_{repoOwner}_{repoName}_{labelsPart}_output.json";
        var sw = Stopwatch.StartNew();

        string? issuesCursor = null;
        GraphqlResponse? graphqlResult = null;

        var allIssues = new List<GithubIssueModel>();

        do
        {
            var issuesQuery = @"
                    query($owner: String!, $name: String!, $after: String, $labels: [String!]) {
                        repository(owner: $owner, name: $name) {
                            issues(first: 100, after: $after, states: OPEN, labels: $labels) {
                                edges {
                                    node {
                                        id
                                        title
                                        body
                                        url
                                        createdAt
                                        updatedAt
                                        reactions(content: THUMBS_UP) {
                                            totalCount
                                        }
                                        labels(first: 100) {
                                            nodes {
                                                name
                                            }
                                        }
                                        comments(first: 100) {
                                            edges {
                                                node {
                                                    id
                                                    body
                                                    createdAt
                                                    url
                                                    author {
                                                        login
                                                    }
                                                }
                                            }
                                            pageInfo {
                                                hasNextPage
                                                endCursor
                                            }
                                        }
                                    }
                                }
                                pageInfo {
                                    hasNextPage
                                    endCursor
                                }
                            }
                        }
                    }";

            var queryPayload = new GithubIssueQuery
            {
                Query = issuesQuery,
                Variables = new() { Owner = repoOwner, Name = repoName, After = issuesCursor, Labels = labels is [] ? null : labels }
            };

            var graphqlResponse = await httpClient.PostAsJsonAsync("https://api.github.com/graphql", queryPayload, ModelsJsonContext.Default.GithubIssueQuery);

            if (!graphqlResponse.IsSuccessStatusCode)
            {
                if (graphqlResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    retryCount++;
                    if (retryCount > maxRetries)
                    {
                        Console.WriteLine("Max retry attempts reached. Exiting.");
                        break;
                    }

                    var retryAfterHeader = graphqlResponse.Headers.RetryAfter;
                    var retryAfterSeconds = retryAfterHeader?.Delta?.TotalSeconds ?? 60;

                    Console.WriteLine($"Rate limited. Retrying in {retryAfterSeconds} seconds...");
                    await Task.Delay(TimeSpan.FromSeconds(retryAfterSeconds));

                    continue;
                }

                var errorContent = await graphqlResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"Error: {graphqlResponse.StatusCode}, Details: {errorContent}");
                break;
            }

            graphqlResult = (await graphqlResponse.Content.ReadFromJsonAsync(ModelsJsonContext.Default.GraphqlResponse))!;

            foreach (var issueEdge in graphqlResult.Data.Repository.Issues.Edges)
            {
                var issue = issueEdge.Node;

                Console.WriteLine($"Processing issue {issue.Title} ({issue.Url})");

                var issueJson = new GithubIssueModel
                {
                    Id = issue.Id,
                    Title = issue.Title,
                    URL = issue.Url,
                    CreatedAt = DateTime.Parse(issue.CreatedAt).ToUniversalTime(),
                    LastUpdated = DateTime.Parse(issue.UpdatedAt).ToUniversalTime(),
                    Body = issue.Body,
                    Upvotes = issue.Reactions?.TotalCount ?? 0,
                    Labels = issue.Labels.Nodes.Select(label => label.Name),
                    Comments = issue.Comments.Edges.Select(commentEdge => new GithubCommentModel
                    {
                        Id = commentEdge.Node.Id,
                        Author = commentEdge.Node.Author?.Login ?? "??",
                        CreatedAt = commentEdge.Node.CreatedAt,
                        Content = commentEdge.Node.Body,
                        Url = commentEdge.Node.Url
                    }).ToArray()
                };

                allIssues.Add(issueJson);
            }

            issuesCursor = graphqlResult.Data.Repository.Issues.PageInfo.EndCursor;

        } while (graphqlResult!.Data.Repository.Issues.PageInfo.HasNextPage);

        sw.Stop();

        await using var fileStream = new FileStream(fileName, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(fileStream, allIssues, ModelsJsonContext.Default.ListGithubIssueModel);


        Console.WriteLine($"Processed {allIssues.Count} issues in {sw.Elapsed}");
        Console.WriteLine($"Issues and comments have been written to {fileName}");
    }


    async Task WriteDiscussionsAsync()
    {
        string? discussionsCursor = null;
        GraphqlResponse? graphqlResult;
        var allDiscussions = new List<GithubDiscussionModel>();
        var sw = Stopwatch.StartNew();

        do
        {
            var discussionsQuery = @"
                    query($owner: String!, $name: String!, $after: String) {
                        repository(owner: $owner, name: $name) {
                            discussions(first: 100, after: $after) {
                                edges {
                                    node {
                                        id
                                        title
                                        url
                                        createdAt
                                        updatedAt
                                        answer {
                                          id
                                        }
                                        comments(first: 100) {
                                            edges {
                                                node {
                                                    id
                                                    body
                                                    createdAt
                                                    url
                                                    author {
                                                        login
                                                    }
                                                    replies(first: 20) {
                                                        edges {
                                                            node {
                                                                id
                                                                body
                                                                createdAt
                                                                url
                                                                author {
                                                                    login
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                            pageInfo {
                                                hasNextPage
                                                endCursor
                                            }
                                        }
                                    }
                                }
                                pageInfo {
                                    hasNextPage
                                    endCursor
                                }
                            }
                        }
                    }";

            var queryPayload = new GithubDiscussionQuery
            {
                Query = discussionsQuery,
                Variables = new() { Owner = repoOwner, Name = repoName, After = discussionsCursor }
            };

            var graphqlResponse = await httpClient.PostAsJsonAsync("https://api.github.com/graphql", queryPayload, ModelsJsonContext.Default.GithubDiscussionQuery);
            graphqlResponse.EnsureSuccessStatusCode();

            graphqlResult = (await graphqlResponse.Content.ReadFromJsonAsync(ModelsJsonContext.Default.GraphqlResponse))!;

            foreach (var discussionEdge in graphqlResult.Data.Repository.Discussions.Edges)
            {
                var discussion = discussionEdge.Node;

                Console.WriteLine($"Processing discussion {discussion.Title} ({discussion.Url})");

                var commentsList = new List<GithubCommentModel>();

                // TODO: Walk through all pages of comments (properly)
                //string? commentsCursor = null;

                foreach (var commentEdge in discussion.Comments.Edges)
                {
                    var comment = commentEdge.Node;

                    var commentJson = new GithubCommentModel
                    {
                        Id = comment.Id,
                        Author = comment.Author?.Login ?? "??",
                        Content = comment.Body,
                        CreatedAt = comment.CreatedAt,
                        Url = comment.Url
                    };

                    commentsList.Add(commentJson);

                    if (comment.Replies is { } replies)
                    {
                        // Hoist replies as top-level comments
                        foreach (var replyEdge in replies.Edges)
                        {
                            var reply = replyEdge.Node;
                            var replyJson = new GithubCommentModel
                            {
                                Id = reply.Id,
                                ParentId = comment.Id,
                                Author = reply.Author?.Login ?? "??",
                                Content = reply.Body,
                                CreatedAt = reply.CreatedAt,
                                Url = reply.Url
                            };
                            commentsList.Add(replyJson);
                        }
                    }
                }

                var discussionJson = new GithubDiscussionModel
                {
                    Title = discussion.Title,
                    AnswerId = discussion.Answer?.Id,
                    Url = discussion.Url,
                    Comments = commentsList.ToArray()
                };

                allDiscussions.Add(discussionJson);
            }

            discussionsCursor = graphqlResult.Data.Repository.Discussions.PageInfo.EndCursor;

        } while (graphqlResult.Data.Repository.Discussions.PageInfo.HasNextPage);

        sw.Stop();

        var fileName = $"discussions_{repoOwner}_{repoName}_output.json";
        var outputPath = Path.Combine(Environment.CurrentDirectory, fileName);
        await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(fileStream, allDiscussions, ModelsJsonContext.Default.ListGithubDiscussionModel);

        Console.WriteLine($"Processed {allDiscussions.Count} discussions in {sw.Elapsed}");
        Console.WriteLine($"Discussions have been written to {outputPath}");
    }
}
