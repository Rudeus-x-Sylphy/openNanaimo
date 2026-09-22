using System.Text.Json.Serialization;

namespace OpenNanaimo.Adapter.Models;

public sealed class AdapterEndpoint
{
    public int Id { get; set; }
    public string Name { get; set; } = "本地一区";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 12050;
    public bool Enabled { get; set; } = true;
    public string Catalog { get; set; } = "local-1";
    public int Capacity { get; set; } = 1000;

    [JsonIgnore]
    public int CurrentPlayers { get; set; }

    [JsonIgnore]
    public string Status => !Enabled
        ? "停用"
        : CurrentPlayers >= Capacity
            ? "已满"
            : "可进入";

    [JsonIgnore]
    public string LoadStatus => $"{CurrentPlayers}/{Capacity}";
}
