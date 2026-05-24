using Hoardr.Core.Data;
using SproutDB.Core;
using static Hoardr.Core.Data.Sprout;

namespace Hoardr.Core.Registry;

public static class UploadStatus
{
    public const string Active = "active";
    public const string Abandoned = "abandoned";
}

public sealed record UploadSession(
    ulong Id, string Uuid, string Repo, long BytesWritten,
    DateTime StartedAt, DateTime LastChunkAt, string Status);

/// <summary>Tracks the DB side of in-progress uploads (the temp files live in <see cref="BlobStore"/>).</summary>
public sealed class UploadSessionService(ISproutDatabase db, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    public void Create(string uuid, string repo)
    {
        var now = Dt(UtcNow);
        db.Exec($"upsert upload_sessions {{uuid: {Q(uuid)}, repo: {Q(repo)}, bytes_written: 0, started_at: {Q(now)}, last_chunk_at: {Q(now)}, status: {Q(UploadStatus.Active)}}}");
    }

    public UploadSession? Get(string uuid) => Map(db.Exec($"get upload_sessions where uuid = {Q(uuid)}").Data);

    public void Touch(string uuid, long bytesWritten)
    {
        var session = Get(uuid);
        if (session is null)
            return;

        db.Exec($"upsert upload_sessions {{_id: {session.Id}, bytes_written: {bytesWritten}, last_chunk_at: {Q(Dt(UtcNow))}}}");
    }

    public void SetStatus(string uuid, string status)
    {
        var session = Get(uuid);
        if (session is null)
            return;

        db.Exec($"upsert upload_sessions {{_id: {session.Id}, status: {Q(status)}}}");
    }

    public void Delete(string uuid) => db.Exec($"delete upload_sessions where uuid = {Q(uuid)}");

    /// <summary>Active sessions whose last chunk arrived before <paramref name="cutoff"/> (stale).</summary>
    public IReadOnlyList<UploadSession> ListStaleActive(DateTime cutoff)
        => ListWhere($"status = {Q(UploadStatus.Active)} and last_chunk_at < {Q(Dt(cutoff))}");

    /// <summary>Sessions of any status started before <paramref name="cutoff"/> (eligible for final purge).</summary>
    public IReadOnlyList<UploadSession> ListStartedBefore(DateTime cutoff)
        => ListWhere($"started_at < {Q(Dt(cutoff))}");

    private IReadOnlyList<UploadSession> ListWhere(string where)
    {
        var data = db.Exec($"get upload_sessions where {where}").Data;
        if (data is not { Count: > 0 })
            return [];

        return [.. data.Select(MapRow)];
    }

    private static UploadSession? Map(List<Dictionary<string, object?>>? data)
        => data is { Count: > 0 } ? MapRow(data[0]) : null;

    private static UploadSession MapRow(Dictionary<string, object?> row)
        => new(row.U64("_id"), row.Str("uuid"), row.Str("repo"), row.I64("bytes_written"),
               row.Dt("started_at"), row.Dt("last_chunk_at"), row.Str("status"));
}
