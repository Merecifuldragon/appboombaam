using System.Text.Json.Serialization;

namespace BFCrewSync.Models;

/// <summary>
/// Written by the app to bf_crew_scan_request.txt. The executor-side
/// listener watches for a requestId it hasn't seen before, runs its
/// existing crew-scan logic, and responds via ScanResultPayload.
/// </summary>
public class ScanRequestPayload
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 4;

    [JsonPropertyName("action")]
    public string Action { get; set; } = "SCAN_CREW_ID";

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("requestedAt")]
    public long RequestedAt { get; set; }
}

/// <summary>
/// Written by the executor to bf_crew_scan_result.txt in response to a
/// ScanRequestPayload. The app ignores any result whose requestId doesn't
/// match its currently pending request (stale/duplicate response).
/// </summary>
public class ScanResultPayload
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 4;

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("respondedAt")]
    public long RespondedAt { get; set; }

    [JsonPropertyName("found")]
    public bool Found { get; set; }

    [JsonPropertyName("crewId")]
    public string? CrewId { get; set; }

    [JsonPropertyName("crewOwner")]
    public string? CrewOwner { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ScanRequestPayload))]
[JsonSerializable(typeof(ScanResultPayload))]
internal partial class ScanJsonContext : JsonSerializerContext
{
}
