namespace BackDatabaseManageServer.Models;

public sealed class BackNode
{
    public Guid Id { get; init; }
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string WebPassword { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class BackNodeView
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public bool Enabled { get; init; }
    public bool PasswordConfigured { get; init; }
    public bool? Online { get; init; }
    public DateTimeOffset? LastCheckedAtUtc { get; init; }
    public string? OnlineError { get; init; }
}

public sealed class BackNodeWriteRequest
{
    public string Name { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string? WebPassword { get; init; }
    public bool ClearWebPassword { get; init; }
    public bool Enabled { get; init; } = true;
}
