using System.Text.Json.Serialization;

namespace BFCrewSync.Models;

/// <summary>
/// Mirrors the JSON contract the Roblox-side "Crew Tools" script polls for
/// (bf_crew_sync.txt / bf_crew_register.txt and their _mirror counterparts).
/// Property names are locked to the existing schema (v4) — do not rename
/// without bumping Version and updating the Lua-side reader.
/// </summary>
public class SyncPayload
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 4;

    [JsonPropertyName("action")]
    public string Action { get; set; } = "START"; // START | REGISTER | CANCEL

    [JsonPropertyName("owner")]
    public string Owner { get; set; } = "";

    [JsonPropertyName("crewId")]
    public string CrewId { get; set; } = "";

    [JsonPropertyName("crewOwner")]
    public string CrewOwner { get; set; } = "";

    [JsonPropertyName("targetEpoch")]
    public long TargetEpoch { get; set; }

    [JsonPropertyName("generatedAt")]
    public long GeneratedAt { get; set; }

    [JsonPropertyName("syncId")]
    public string SyncId { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SyncPayload))]
internal partial class SyncPayloadJsonContext : JsonSerializerContext
{
}
