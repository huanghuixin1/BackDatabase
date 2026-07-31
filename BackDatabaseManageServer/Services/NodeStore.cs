using System.Text.Json;
using BackDatabaseManageServer.Models;
using Microsoft.AspNetCore.DataProtection;

namespace BackDatabaseManageServer.Services;

public sealed class NodeStore
{
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly object _sync = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public NodeStore(string baseDirectory)
    {
        Directory.CreateDirectory(baseDirectory);
        _path = Path.Combine(baseDirectory, "nodes.json");
        var keysDirectory = Path.Combine(baseDirectory, "data-protection-keys");
        _protector = DataProtectionProvider.Create(keysDirectory).CreateProtector("BackDatabaseManageServer.NodePassword.v1");
    }

    public IReadOnlyList<BackNode> List()
    {
        lock (_sync)
            return Load().Select(ToNode).ToArray();
    }

    public BackNode? Find(Guid id)
    {
        lock (_sync)
            return Load().Where(node => node.Id == id).Select(ToNode).SingleOrDefault();
    }

    public BackNode Add(BackNodeWriteRequest request)
    {
        Validate(request);
        lock (_sync)
        {
            var nodes = Load();
            var node = new StoredNode
            {
                Id = Guid.NewGuid(),
                Name = request.Name.Trim(),
                BaseUrl = NormalizeUrl(request.BaseUrl),
                ProtectedWebPassword = Protect(request.WebPassword ?? ""),
                Enabled = request.Enabled,
            };
            nodes.Add(node);
            Save(nodes);
            return ToNode(node);
        }
    }

    public BackNode? Update(Guid id, BackNodeWriteRequest request)
    {
        Validate(request);
        lock (_sync)
        {
            var nodes = Load();
            var node = nodes.SingleOrDefault(item => item.Id == id);
            if (node is null)
                return null;

            node.Name = request.Name.Trim();
            node.BaseUrl = NormalizeUrl(request.BaseUrl);
            node.Enabled = request.Enabled;
            if (!string.IsNullOrEmpty(request.WebPassword) || request.ClearWebPassword)
                node.ProtectedWebPassword = Protect(request.WebPassword ?? "");
            Save(nodes);
            return ToNode(node);
        }
    }

    public bool Delete(Guid id)
    {
        lock (_sync)
        {
            var nodes = Load();
            var removed = nodes.RemoveAll(item => item.Id == id) > 0;
            if (removed)
                Save(nodes);
            return removed;
        }
    }

    private List<StoredNode> Load()
    {
        if (!File.Exists(_path))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<StoredNode>>(File.ReadAllText(_path), _jsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"nodes.json 格式错误: {ex.Message}", ex);
        }
    }

    private void Save(List<StoredNode> nodes)
    {
        var temporaryPath = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(nodes, _jsonOptions));
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private BackNode ToNode(StoredNode node) => new()
    {
        Id = node.Id,
        Name = node.Name,
        BaseUrl = node.BaseUrl,
        WebPassword = Unprotect(node.ProtectedWebPassword),
        Enabled = node.Enabled,
    };

    private string Protect(string value) => _protector.Protect(value);
    private string Unprotect(string value) => string.IsNullOrEmpty(value) ? "" : _protector.Unprotect(value);

    private static void Validate(BackNodeWriteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("节点名称不能为空。", nameof(request));
        if (request.Name.Any(char.IsControl))
            throw new ArgumentException("节点名称不能包含控制字符。", nameof(request));
        if (!Uri.TryCreate(request.BaseUrl?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException("BaseUrl 必须是有效的 http 或 https 地址。", nameof(request));
        if (!string.IsNullOrEmpty(request.WebPassword)
            && (request.WebPassword.Any(char.IsControl) || request.WebPassword != request.WebPassword.Trim()))
            throw new ArgumentException("节点口令不能包含控制字符或首尾空白。", nameof(request));
    }

    private static string NormalizeUrl(string value) => value.Trim().TrimEnd('/');

    private sealed class StoredNode
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string ProtectedWebPassword { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }
}
