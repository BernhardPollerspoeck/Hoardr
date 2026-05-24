using SproutDB.Core;

namespace Hoardr.Core.Data;

/// <summary>
/// Creates the full registry schema. Mirrors the data model in hoard-spec.md.
///
/// Notes on SproutDB:
/// - No composite unique indexes; (repo, name) / (repo, digest) uniqueness is enforced in code.
/// - Digests are stored in canonical form "sha256:&lt;64 hex&gt;" = 71 chars.
/// - ref_count is a plain counter maintained by the registry services / GC.
/// </summary>
public sealed class M001_InitialSchema : IMigration
{
    public int Order => 1;

    public void Up(ISproutDatabase db)
    {
        // --- Accounts & permissions -------------------------------------------------
        db.Query("create table accounts (name string 255 strict, password_hash string 255, created_at datetime)");
        db.Query("create index unique accounts.name");

        db.Query("create table repo_permissions (account_id ulong, repo string 255, can_pull bool default false, can_push bool default false)");
        db.Query("create index repo_permissions.account_id");
        db.Query("create index repo_permissions.repo");

        // --- Blobs (content-addressed, dedup via ref_count) -------------------------
        db.Query("create table blobs (digest string 71 strict, size ulong, ref_count uint default 0, created_at datetime)");
        db.Query("create index unique blobs.digest");

        db.Query("create table repo_blobs (repo string 255, blob_digest string 71)");
        db.Query("create index repo_blobs.repo");
        db.Query("create index repo_blobs.blob_digest");

        // --- Manifests (per repo+digest) & their child references -------------------
        db.Query("create table manifests (digest string 71, repo string 255, media_type string 255, ref_count uint default 0)");
        db.Query("create index manifests.digest");
        db.Query("create index manifests.repo");

        // child_kind: 'blob' | 'manifest'  (manifest = entry of a manifest list / index)
        db.Query("create table manifest_refs (parent_digest string 71, child_digest string 71, child_kind string 10)");
        db.Query("create index manifest_refs.parent_digest");
        db.Query("create index manifest_refs.child_digest");

        // --- Tags -------------------------------------------------------------------
        db.Query("create table tags (repo string 255, name string 255, manifest_digest string 71, pushed_at datetime)");
        db.Query("create index tags.repo");

        // --- Tag retention (per-repo override of the global default) ----------------
        db.Query("create table retention_overrides (repo string 255 strict, keep_min uint, max_age_days uint)");
        db.Query("create index unique retention_overrides.repo");

        // --- Upload sessions --------------------------------------------------------
        db.Query("create table upload_sessions (uuid string 36 strict, repo string 255, bytes_written ulong default 0, started_at datetime, last_chunk_at datetime, status string 20 default 'active')");
        db.Query("create index unique upload_sessions.uuid");
    }
}
