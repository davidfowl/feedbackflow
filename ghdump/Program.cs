using System.CommandLine;
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
var configAccessToken = configuration["GitHub:Token"] ?? configuration["GITHUB_TOKEN"] ?? "";

var accessToken = new Option<string?>(["-t", "--token"], () => null, "The GitHub access token. Can be specified in an environment variable GITHUB_TOKEN.");

accessToken.AddValidator(r =>
{
    var value = r.GetValueOrDefault<string?>() ?? configAccessToken;

    if (string.IsNullOrWhiteSpace(value))
    {
        r.ErrorMessage = "A GitHub access token is required. Please specify it via the command line argument -t/--token or by using the environment variable GITHUB_TOKEN";
    }
});

var repoOption = new Option<string?>(["-r", "--repository"], () => null, "GitHub repository in the format owner/repo") { IsRequired = true };

repoOption.AddValidator(r =>
{
    var repo = r.GetValueOrDefault<string?>();

    if (string.IsNullOrWhiteSpace(repo))
    {
        r.ErrorMessage = "Unable to determine the repository. Please specify it via the command line argument -r/--repository. Example: -r owner/repo";
    }
    else if (repo.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries) is not [_, _])
    {
        r.ErrorMessage = "Invalid repository format, expected owner/repo. Example: dotnet/aspnetcore.";
    }
});

var labelOption = new Option<string[]>(["-l", "-labels"], "Labels to filter issues when exporting. Multiple labels can be specified.")
{
    AllowMultipleArgumentsPerToken = true
};

var includeIssuesOption = new Option<bool?>(["-i", "--include-issues"], () => null, "Include GitHub issues.");
var includePullsOption = new Option<bool?>(["-p", "--include-pull-requests"], () => null, "Include GitHub pull-requests.");
var includeDiscussionsOption = new Option<bool?>(["-d", "--include-discussions"], () => null, "Include GitHub discussions.");

var outputPath = new Option<string?>(["-o", "--output"], () => null, "The directory where the results will be written. Defaults to the current working directory");

var rootCommand = new RootCommand("CLI tool for extracting GitHub issues and discussions in a JSON format.")
{
    accessToken,
    repoOption,
    labelOption,
    includeIssuesOption,
    includePullsOption,
    includeDiscussionsOption,
    outputPath
};

rootCommand.SetHandler(RunAsync, accessToken, repoOption, labelOption, includeIssuesOption, includePullsOption, includeDiscussionsOption, outputPath);

await rootCommand.InvokeAsync(args);

async Task RunAsync(string? accessToken, string? repo, string[] labels, bool? includeIssues, bool? includePullRequests, bool? includeDiscussions, string? outputDirectory)
{
    accessToken ??= configAccessToken;
    outputDirectory ??= Environment.CurrentDirectory;

    bool issuesIncluded = includeIssues ?? false;
    bool pullsIncluded = includePullRequests ?? false;
    bool discussionsIncluded = includeDiscussions ?? false;

    if (!issuesIncluded && !pullsIncluded && !discussionsIncluded)
    {
        // If no options are specified, include issues by default
        issuesIncluded = true;
    }

    var (repoOwner, repoName) = repo?.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries) switch
    {
        [var owner, var name] => (owner, name),
        _ => throw new InvalidOperationException("Invalid repository format, expected owner/repo. Example: dotnet/aspnetcore.")
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

    Console.WriteLine($"Including issues: {(issuesIncluded ? "yes" : "no")}");
    Console.WriteLine($"Including pull-requests: {(pullsIncluded ? "yes" : "no")}");
    Console.WriteLine($"Including discussions: {(discussionsIncluded ? "yes" : "no")}");
    Console.WriteLine($"Results directory: {outputDirectory}");

    if (!await CheckRepositoryValid(repoOwner, repoName))
    {
        return;
    }

    var sw = Stopwatch.StartNew();

    var discussionsTask = GetDiscussionsAsync(discussionsIncluded);
    var issuesTask = GetIssuesAsync(labels, issuesIncluded);
    var pullsTask = GetPullRequestsAsync(labels, pullsIncluded);

    await Task.WhenAll(discussionsTask, issuesTask, pullsTask);

    sw.Stop();

    var issues = await issuesTask;
    var pulls = await pullsTask;
    var discussions = await discussionsTask;

    await WriteResultsToDisk(outputDirectory, issuesIncluded, issues, pullsIncluded, pulls, discussionsIncluded, discussions, sw.Elapsed);

    async Task<bool> CheckRepositoryValid(string repoOwner, string repoName)
    {
        // Check if this repository is valid
        var checkRepoQuery = @"
            query($owner: String!, $name: String!) {
                repository(owner: $owner, name: $name) {
                    id
                }
            }";

        var response = await httpClient.PostAsJsonAsync("https://api.github.com/graphql", new GithubRepositoryQuery
        {
            Query = checkRepoQuery,
            Variables = new() { Owner = repoOwner, Name = repoName }
        },
        ModelsJsonContext.Default.GithubRepositoryQuery);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine(await response.Content.ReadAsStringAsync());
            return false;
        }

        var result = await response.Content.ReadFromJsonAsync(ModelsJsonContext.Default.GraphqlResponse);

        return result?.Data.Repository is not null;
    }

    async Task WriteResultsToDisk(
        string outputDirectory,
        bool includeIssues,
        List<GithubIssueModel> issues,
        bool includePullRequests,
        List<GithubIssueModel> pullRequests,
        bool includeDiscussions,
        List<GithubDiscussionModel> discussions,
        TimeSpan elapsed)
    {
        Console.WriteLine();
        Console.WriteLine($"Processing completed in {elapsed}.");

        if (issues.Count > 0)
        {
            var labelsPart = labels.Length > 0 ? string.Join("_", labels) : "all";
            var fileName = $"issues_{repoOwner}_{repoName}_{labelsPart}_output.json";

            var outputPath = Path.Combine(outputDirectory, fileName);
            await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);

            await JsonSerializer.SerializeAsync(fileStream, issues, ModelsJsonContext.Default.ListGithubIssueModel);

            Console.WriteLine($"Processed {issues.Count} issues.");
            Console.WriteLine($"Issues have been written to {fileName}.");
        }
        else if (includeIssues)
        {
            Console.WriteLine("No issues found.");
        }

        Console.WriteLine();

        if (pullRequests.Count > 0)
        {
            var fileName = $"pullrequests_{repoOwner}_{repoName}_output.json";

            var outputPath = Path.Combine(outputDirectory, fileName);

            await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await JsonSerializer.SerializeAsync(fileStream, pullRequests, ModelsJsonContext.Default.ListGithubIssueModel);

            Console.WriteLine($"Processed {pullRequests.Count} pull-requests.");
            Console.WriteLine($"Pull-requests have been written to {fileName}.");
        }
        else if (includePullRequests)
        {
            Console.WriteLine("No pull-requests found.");
        }

        Console.WriteLine();

        if (discussions.Count > 0)
        {
            var fileName = $"discussions_{repoOwner}_{repoName}_output.json";

            var outputPath = Path.Combine(outputDirectory, fileName);

            await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await JsonSerializer.SerializeAsync(fileStream, discussions, ModelsJsonContext.Default.ListGithubDiscussionModel);

            Console.WriteLine($"Processed {discussions.Count} discussions.");
            Console.WriteLine($"Discussions have been written to {fileName}.");
        }
        else if (includeDiscussions)
        {
            Console.WriteLine("No discussions found.");
        }
    }


    async Task<List<GithubIssueModel>> GetIssuesAsync(string[] labels, bool includeIssues)
    {
        if (!includeIssues)
        {
            return [];
        }

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
                                        author {
                                            login
                                        }
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

            foreach (var issueEdge in graphqlResult.Data.Repository!.Issues.Edges)
            {
                var issue = issueEdge.Node;

                Console.WriteLine($"Processing issue {issue.Title} ({issue.Url})");

                var issueJson = new GithubIssueModel
                {
                    Id = issue.Id,
                    Author = issue.Author?.Login ?? "??",
                    Title = issue.Title,
                    URL = issue.Url,
                    CreatedAt = DateTime.Parse(issue.CreatedAt).ToUniversalTime(),
                    LastUpdated = DateTime.Parse(issue.UpdatedAt).ToUniversalTime(),
                    Body = issue.Body,
                    Upvotes = issue.Reactions?.TotalCount ?? 0,
                    Labels = issue.Labels.Nodes.Select(label => label.Name),
                    Comments = [.. issue.Comments.Edges.Select(commentEdge => new GithubCommentModel
                    {
                        Id = commentEdge.Node.Id,
                        Author = commentEdge.Node.Author?.Login ?? "??",
                        CreatedAt = commentEdge.Node.CreatedAt,
                        Content = commentEdge.Node.Body,
                        Url = commentEdge.Node.Url
                    })]
                };

                allIssues.Add(issueJson);
            }

            issuesCursor = graphqlResult.Data.Repository.Issues.PageInfo.EndCursor;

        } while (graphqlResult!.Data.Repository!.Issues.PageInfo.HasNextPage);

        return allIssues;
    }


    async Task<List<GithubDiscussionModel>> GetDiscussionsAsync(bool includeDiscussions)
    {
        if (!includeDiscussions)
        {
            return [];
        }

        string? discussionsCursor = null;
        GraphqlResponse? graphqlResult;
        var allDiscussions = new List<GithubDiscussionModel>();

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

            foreach (var discussionEdge in graphqlResult.Data.Repository!.Discussions.Edges)
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
                    Comments = [.. commentsList]
                };

                allDiscussions.Add(discussionJson);
            }

            discussionsCursor = graphqlResult.Data.Repository.Discussions.PageInfo.EndCursor;

        } while (graphqlResult.Data.Repository.Discussions.PageInfo.HasNextPage);

        return allDiscussions;
    }


    async Task<List<GithubIssueModel>> GetPullRequestsAsync(string[] labels, bool includePulls)
    {
        if (!includePulls)
        {
            return [];
        }

        string? issuesCursor = null;
        GraphqlResponse? graphqlResult = null;

        var allIssues = new List<GithubIssueModel>();

        do
        {
            var issuesQuery = @"
                    query($owner: String!, $name: String!, $after: String, $labels: [String!]) {
                        repository(owner: $owner, name: $name) {
                            pullRequests(first: 100, after: $after, states: OPEN, labels: $labels) {
                                edges {
                                    node {
                                        id
                                        author {
                                            login
                                        }
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

            foreach (var issueEdge in graphqlResult.Data.Repository!.PullRequests.Edges)
            {
                var issue = issueEdge.Node;

                Console.WriteLine($"Processing pull-request {issue.Title} ({issue.Url})");

                var issueJson = new GithubIssueModel
                {
                    Id = issue.Id,
                    Author = issue.Author?.Login ?? "??",
                    Title = issue.Title,
                    URL = issue.Url,
                    CreatedAt = DateTime.Parse(issue.CreatedAt).ToUniversalTime(),
                    LastUpdated = DateTime.Parse(issue.UpdatedAt).ToUniversalTime(),
                    Body = issue.Body,
                    Upvotes = issue.Reactions?.TotalCount ?? 0,
                    Labels = issue.Labels.Nodes.Select(label => label.Name),
                    Comments = [.. issue.Comments.Edges.Select(commentEdge => new GithubCommentModel
                    {
                        Id = commentEdge.Node.Id,
                        Author = commentEdge.Node.Author?.Login ?? "??",
                        CreatedAt = commentEdge.Node.CreatedAt,
                        Content = commentEdge.Node.Body,
                        Url = commentEdge.Node.Url
                    })]
                };

                allIssues.Add(issueJson);
            }

            issuesCursor = graphqlResult.Data.Repository.PullRequests.PageInfo.EndCursor;

        } while (graphqlResult!.Data.Repository!.PullRequests.PageInfo.HasNextPage);

        return allIssues;
    }
}
