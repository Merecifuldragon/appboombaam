using System.IO;
using System.Text;
using System.Text.Json;
using BFCrewSync.Models;

namespace BFCrewSync.Services;

public class SyncFileService
{
    private readonly Random _rng = new();

    public string RootDirectory { get; set; } = "";

    private const string SyncFile = "bf_crew_sync.txt";
    private const string SyncMirror = "bf_crew_sync_mirror.txt";
    private const string SyncTmp = "bf_crew_sync.tmp";
    private const string RegisterFile = "bf_crew_register.txt";
    private const string RegisterMirror = "bf_crew_register_mirror.txt";
    private const string RegisterTmp = "bf_crew_register.tmp";
    private const string LegacySuffix = "_legacy.txt";

    private const string ScanRequestFile = "bf_crew_scan_request.txt";
    private const string ScanRequestTmp = "bf_crew_scan_request.tmp";
    private const string ScanRequestMirror = "bf_crew_scan_request_mirror.txt";

    public string GenerateSyncId()
    {
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int rand6 = _rng.Next(0, 1_000_000);
        return $"{ms}-{rand6:D6}";
    }

    private void EnsureRoot()
    {
        if (string.IsNullOrWhiteSpace(RootDirectory))
            throw new InvalidOperationException("Root target directory is not set. Pick a folder first.");
        Directory.CreateDirectory(RootDirectory);
    }

    /// <summary>
    /// Writes payloadJson to `tmpName` then does an atomic File.Move/Replace
    /// onto `finalName` so any process polling `finalName` never observes a
    /// half-written file. Also mirrors the same content to `mirrorName`.
    /// </summary>
    private void AtomicWriteWithMirror(string payloadJson, string tmpName, string finalName, string mirrorName)
    {
        EnsureRoot();

        string tmpPath = Path.Combine(RootDirectory, tmpName);
        string finalPath = Path.Combine(RootDirectory, finalName);
        string mirrorPath = Path.Combine(RootDirectory, mirrorName);

        File.WriteAllText(tmpPath, payloadJson, new UTF8Encoding(false));

        // File.Move(overwrite:true) uses ReplaceFile/MoveFileEx under the hood
        // on Windows — the destination never appears empty/partial to a reader.
        File.Move(tmpPath, finalPath, overwrite: true);

        File.WriteAllText(mirrorPath, payloadJson, new UTF8Encoding(false));
    }

    private static string Serialize(SyncPayload payload) =>
        JsonSerializer.Serialize(payload, SyncPayloadJsonContext.Default.SyncPayload);

    public SyncPayload BuildPayload(string action, string owner, string crewId, string crewOwner, long targetEpoch, string? syncId = null)
    {
        return new SyncPayload
        {
            Version = 4,
            Action = action,
            Owner = owner,
            CrewId = crewId,
            CrewOwner = crewOwner,
            TargetEpoch = targetEpoch,
            GeneratedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            SyncId = syncId ?? GenerateSyncId()
        };
    }

    /// <summary>SET SYNC — writes action:"START" to the primary sync file + mirror.</summary>
    public SyncPayload WriteSetSync(string owner, string crewId, string crewOwner, long targetEpoch, bool legacyFallback)
    {
        var payload = BuildPayload("START", owner, crewId, crewOwner, targetEpoch);
        AtomicWriteWithMirror(Serialize(payload), SyncTmp, SyncFile, SyncMirror);

        if (legacyFallback)
            WriteLegacyString(SyncFile, crewId, targetEpoch);

        return payload;
    }

    /// <summary>REGISTER — writes action:"REGISTER" to the register file + mirror.</summary>
    public SyncPayload WriteRegister(string owner, string crewId, string crewOwner, long targetEpoch, bool legacyFallback)
    {
        var payload = BuildPayload("REGISTER", owner, crewId, crewOwner, targetEpoch);
        AtomicWriteWithMirror(Serialize(payload), RegisterTmp, RegisterFile, RegisterMirror);

        if (legacyFallback)
            WriteLegacyString(RegisterFile, crewId, targetEpoch);

        return payload;
    }

    /// <summary>CANCEL — overwrites the primary sync file with action:"CANCEL".</summary>
    public SyncPayload WriteCancel(string owner, string crewId, string crewOwner)
    {
        var payload = BuildPayload("CANCEL", owner, crewId, crewOwner, targetEpoch: 0);
        AtomicWriteWithMirror(Serialize(payload), SyncTmp, SyncFile, SyncMirror);
        return payload;
    }

    /// <summary>
    /// Writes a scan request with a fresh requestId. The executor-side
    /// listener is expected to run its existing crew-scan logic and respond
    /// via bf_crew_scan_result.txt with the same requestId (see
    /// ScanListenerService, which reads that response back).
    /// </summary>
    public ScanRequestPayload WriteScanRequest()
    {
        var payload = new ScanRequestPayload
        {
            RequestId = GenerateSyncId(),
            RequestedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        string json = JsonSerializer.Serialize(payload, ScanJsonContext.Default.ScanRequestPayload);
        AtomicWriteWithMirror(json, ScanRequestTmp, ScanRequestFile, ScanRequestMirror);
        return payload;
    }

    /// <summary>
    /// Legacy fallback format for older Lua-side readers:
    /// "&lt;crewId&gt;@@&lt;targetEpoch&gt;" written alongside the JSON file,
    /// same base name with a _legacy.txt suffix.
    /// </summary>
    private void WriteLegacyString(string basedOnFileName, string crewId, long targetEpoch)
    {
        string legacyPath = Path.Combine(
            RootDirectory,
            Path.GetFileNameWithoutExtension(basedOnFileName) + LegacySuffix);

        File.WriteAllText(legacyPath, $"{crewId}@@{targetEpoch}", new UTF8Encoding(false));
    }
}
