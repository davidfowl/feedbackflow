using System.Text.Json.Serialization;

public class GraphqlResponse
{
    public required RepositoryData Data { get; set; }
}

public class RepositoryData
{
    public required Repository? Repository { get; set; }
}

// This type is shared between the discussions code and issues code so we will pretend
// like neither are null when they are being used to avoid splitting the object hierarchy.
public class Repository
{
    public DiscussionConnection Discussions { get; set; } = default!;
    public IssueConnection Issues { get; set; } = default!;
    public PullRequestConnection PullRequests { get; set; } = default!;
}

public class DiscussionConnection
{
    public required List<Edge<Discussion>> Edges { get; set; }
    public required PageInfo PageInfo { get; set; }
}

public class Discussion
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public required string Url { get; set; }
    public Answer? Answer { get; set; }
    public required CommentConnection Comments { get; set; }
}

public class Answer
{
    public required string Id { get; set; }
}

public class CommentConnection
{
    public required List<Edge<Comment>> Edges { get; set; }
    public PageInfo? PageInfo { get; set; }
}

public class Comment
{
    public required string Id { get; set; }
    public required string Body { get; set; }
    public required string Url { get; set; }
    public required string CreatedAt { get; set; }
    public required Author Author { get; set; }
    public CommentConnection? Replies { get; set; } // Include replies for hoisting
}

public class Author
{
    public string? Login { get; set; }
}

public class Edge<T>
{
    public required T Node { get; set; }
}

public class PageInfo
{
    public required bool HasNextPage { get; set; }
    public string? EndCursor { get; set; }
}

public class IssueConnection
{
    public required List<Edge<Issue>> Edges { get; set; }
    public required PageInfo PageInfo { get; set; }
}

public class Issue
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public required string Url { get; set; }
    public required string CreatedAt { get; set; }
    public required string UpdatedAt { get; set; }
    public required Reaction Reactions { get; set; }
    public required LabelConnection Labels { get; set; }
    public required CommentConnection Comments { get; set; }
}

public class PullRequestConnection
{
    public required List<Edge<PullRequest>> Edges { get; set; }
    public required PageInfo PageInfo { get; set; }
}

public class PullRequest
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public required string Url { get; set; }
    public required string CreatedAt { get; set; }
    public required string UpdatedAt { get; set; }
    public required Reaction Reactions { get; set; }
    public required LabelConnection Labels { get; set; }
    public required CommentConnection Comments { get; set; }
}

public class Reaction
{
    public required int TotalCount { get; set; }
}

public class LabelConnection
{
    public required List<Label> Nodes { get; set; }
}

public class Label
{
    public required string Name { get; set; }
}

// Output models
public class GithubDiscussionModel
{
    public required string Title { get; set; }
    public string? AnswerId { get; set; }
    public required string Url { get; set; }
    public required GithubCommentModel[] Comments { get; set; }
}

public class GithubIssueModel
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public required string URL { get; set; }
    public required DateTime CreatedAt { get; set; }
    public required DateTime LastUpdated { get; set; }
    public required string Body { get; set; }
    public required int Upvotes { get; set; }
    public required IEnumerable<string> Labels { get; set; }
    public required GithubCommentModel[] Comments { get; set; }
}
public class GithubCommentModel
{
    public required string Id { get; set; }
    public string? ParentId { get; set; }
    public required string Author { get; set; }
    public required string Content { get; set; }
    public required string CreatedAt { get; set; }
    public required string Url { get; set; }
}

// Graph QL queries

public class GithubRepositoryQuery
{
    public required string Query { get; set; }
    public required RepositoryQueryVariables Variables { get; set; }
    public class RepositoryQueryVariables
    {
        public required string Owner { get; set; }
        public required string Name { get; set; }
    }
}

public class GithubIssueQuery
{
    public required string Query { get; set; }
    public required IssueQueryVariables Variables { get; set; }

    public class IssueQueryVariables
    {
        public required string Owner { get; set; }
        public required string Name { get; set; }
        public string? After { get; set; }
        public string[]? Labels { get; set; }
    }
}

public class GithubDiscussionQuery
{
    public required string Query { get; set; }
    public required DiscussionQueryVariables Variables { get; set; }
    public class DiscussionQueryVariables
    {
        public required string Owner { get; set; }
        public required string Name { get; set; }
        public string? After { get; set; }
    }
}


[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
// Output models
[JsonSerializable(typeof(List<GithubDiscussionModel>))]
[JsonSerializable(typeof(List<GithubIssueModel>))]
[JsonSerializable(typeof(GithubCommentModel[]))]

// Queries

[JsonSerializable(typeof(GithubIssueQuery))]
[JsonSerializable(typeof(GithubDiscussionQuery))]
[JsonSerializable(typeof(GithubRepositoryQuery))]

[JsonSerializable(typeof(GraphqlResponse))]
[JsonSerializable(typeof(RepositoryData))]
[JsonSerializable(typeof(Repository))]
[JsonSerializable(typeof(DiscussionConnection))]
[JsonSerializable(typeof(Discussion))]
[JsonSerializable(typeof(Answer))]
[JsonSerializable(typeof(CommentConnection))]
[JsonSerializable(typeof(Comment))]
[JsonSerializable(typeof(Author))]
[JsonSerializable(typeof(Edge<>))]
[JsonSerializable(typeof(PageInfo))]
[JsonSerializable(typeof(IssueConnection))]
[JsonSerializable(typeof(Issue))]
[JsonSerializable(typeof(PullRequestConnection))]
[JsonSerializable(typeof(PullRequest))]
[JsonSerializable(typeof(Reaction))]
[JsonSerializable(typeof(LabelConnection))]
[JsonSerializable(typeof(Label))]
public partial class ModelsJsonContext : JsonSerializerContext
{
}
