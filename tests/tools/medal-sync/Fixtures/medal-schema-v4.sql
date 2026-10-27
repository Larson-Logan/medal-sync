-- Medal library schema, user_version 4, extracted read-only from Medal 2643.583.1 on 2026-10-24.
-- Schema only: no library rows, settings or account data. Used by the synthetic test fixtures.
PRAGMA user_version = 4;
CREATE TABLE content_counts (
  key TEXT PRIMARY KEY,
  count INTEGER NOT NULL DEFAULT 0
) WITHOUT ROWID;
CREATE TABLE contents (
  created_at INTEGER NOT NULL,
  category_id TEXT,
  video_path TEXT,
  image_path TEXT,
  thumbnail_path TEXT,
  metadata BLOB NOT NULL,
  remote_content_id TEXT UNIQUE,
  local_content_id TEXT UNIQUE NOT NULL,
  parent_id TEXT
, film_reel_path TEXT);
CREATE TABLE deleted_contents (
  local_content_id TEXT UNIQUE NOT NULL,
  deleted_at INTEGER NOT NULL,
  blob BLOB NOT NULL
);
CREATE TABLE key_values (
  key TEXT UNIQUE NOT NULL,
  value BLOB NOT NULL
);
CREATE INDEX content_category_id ON contents (category_id);
CREATE INDEX content_category_is_game ON contents (json_extract(metadata, '$.remoteContent.category.isGame'), category_id);
CREATE INDEX content_created_at ON contents (created_at);
CREATE INDEX content_local_id ON contents (local_content_id);
CREATE INDEX content_remote_id ON contents (remote_content_id);
CREATE INDEX deleted_contents_deleted_at ON deleted_contents (deleted_at);
CREATE TRIGGER content_counts_drafts_ad AFTER DELETE ON contents
     WHEN json_extract(OLD.metadata, '$.projectId') IS NOT NULL
     BEGIN
       UPDATE content_counts SET count = count - 1 WHERE key = 'drafts';
     END;
CREATE TRIGGER content_counts_drafts_ai AFTER INSERT ON contents
     WHEN json_extract(NEW.metadata, '$.projectId') IS NOT NULL
     BEGIN
       UPDATE content_counts SET count = count + 1 WHERE key = 'drafts';
     END;
CREATE TRIGGER content_counts_drafts_au AFTER UPDATE OF metadata ON contents
     WHEN COALESCE(json_extract(OLD.metadata, '$.projectId') IS NOT NULL, 0) IS NOT COALESCE(json_extract(NEW.metadata, '$.projectId') IS NOT NULL, 0)
     BEGIN
       UPDATE content_counts SET count = count
         + (CASE WHEN json_extract(NEW.metadata, '$.projectId') IS NOT NULL THEN 1 ELSE -1 END)
       WHERE key = 'drafts';
     END;
CREATE TRIGGER content_counts_favorites_ad AFTER DELETE ON contents
     WHEN json_extract(OLD.metadata, '$.isFavorited') = 1
     BEGIN
       UPDATE content_counts SET count = count - 1 WHERE key = 'favorites';
     END;
CREATE TRIGGER content_counts_favorites_ai AFTER INSERT ON contents
     WHEN json_extract(NEW.metadata, '$.isFavorited') = 1
     BEGIN
       UPDATE content_counts SET count = count + 1 WHERE key = 'favorites';
     END;
CREATE TRIGGER content_counts_favorites_au AFTER UPDATE OF metadata ON contents
     WHEN COALESCE(json_extract(OLD.metadata, '$.isFavorited') = 1, 0) IS NOT COALESCE(json_extract(NEW.metadata, '$.isFavorited') = 1, 0)
     BEGIN
       UPDATE content_counts SET count = count
         + (CASE WHEN json_extract(NEW.metadata, '$.isFavorited') = 1 THEN 1 ELSE -1 END)
       WHERE key = 'favorites';
     END;
CREATE TRIGGER content_counts_posts_ad AFTER DELETE ON contents
     WHEN (
  json_extract(OLD.metadata, '$.remoteContent.privacy') = 0 AND
  (
  OLD.remote_content_id IS NOT NULL AND (
    json_extract(OLD.metadata, '$.remoteContent.contentUrl') IS NOT NULL OR
    json_extract(OLD.metadata, '$.remoteContent.contentUrlHls') IS NOT NULL OR
    json_extract(OLD.metadata, '$.remoteContent.socialMediaVideo') IS NOT NULL OR
    json_extract(OLD.metadata, '$.remoteContent.publishedAt') IS NOT NULL OR
    json_extract(OLD.metadata, '$.remoteContent.contentUploadedAt') IS NOT NULL OR
    json_extract(OLD.metadata, '$.uploadedAt') IS NOT NULL OR
    (
  json_extract(OLD.metadata, '$.remoteContent.contentType') = 29 AND
  json_extract(OLD.metadata, '$.remoteContent.thumbnailUrl') IS NOT NULL AND
  json_extract(OLD.metadata, '$.remoteContent.thumbnailUrl') NOT LIKE '%default-content-thumbnail%'
)
  )
)
)
     BEGIN
       UPDATE content_counts SET count = count - 1 WHERE key = 'posts';
     END;
CREATE TRIGGER content_counts_posts_ai AFTER INSERT ON contents
     WHEN (
  json_extract(NEW.metadata, '$.remoteContent.privacy') = 0 AND
  (
  NEW.remote_content_id IS NOT NULL AND (
    json_extract(NEW.metadata, '$.remoteContent.contentUrl') IS NOT NULL OR
    json_extract(NEW.metadata, '$.remoteContent.contentUrlHls') IS NOT NULL OR
    json_extract(NEW.metadata, '$.remoteContent.socialMediaVideo') IS NOT NULL OR
    json_extract(NEW.metadata, '$.remoteContent.publishedAt') IS NOT NULL OR
    json_extract(NEW.metadata, '$.remoteContent.contentUploadedAt') IS NOT NULL OR
    json_extract(NEW.metadata, '$.uploadedAt') IS NOT NULL OR
    (
  json_extract(NEW.metadata, '$.remoteContent.contentType') = 29 AND
  json_extract(NEW.metadata, '$.remoteContent.thumbnailUrl') IS NOT NULL AND
  json_extract(NEW.metadata, '$.remoteContent.thumbnailUrl') NOT LIKE '%default-content-thumbnail%'
)
  )
)
)
     BEGIN
       UPDATE content_counts SET count = count + 1 WHERE key = 'posts';
     END;
CREATE TRIGGER content_counts_posts_au AFTER UPDATE OF metadata, remote_content_id ON contents
     WHEN COALESCE((
  json_extract(OLD.metadata, '$.remoteContent.privacy') = 0 AND
  (
  OLD.remote_content_id IS NOT NULL AND (
    json_extract(OLD.metadata, '$.remoteContent.contentUrl') IS NOT NULL OR
    json_extract(OLD.metadata, '$.remoteContent.contentUrlHls') IS NOT NULL OR
    json_extract(OLD.metadata, '$.remoteContent.socialMediaVideo') IS NOT NULL OR
    json_extract(OLD.metadata, '$.remoteContent.publishedAt') IS NOT NULL OR
    json_extract(OLD.metadata, '$.remoteContent.contentUploadedAt') IS NOT NULL OR
    json_extract(OLD.metadata, '$.uploadedAt') IS NOT NULL OR
    (
  json_extract(OLD.metadata, '$.remoteContent.contentType') = 29 AND
  json_extract(OLD.metadata, '$.remoteContent.thumbnailUrl') IS NOT NULL AND
  json_extract(OLD.metadata, '$.remoteContent.thumbnailUrl') NOT LIKE '%default-content-thumbnail%'
)
  )
)
), 0) IS NOT COALESCE((
  json_extract(NEW.metadata, '$.remoteContent.privacy') = 0 AND
  (
  NEW.remote_content_id IS NOT NULL AND (
    json_extract(NEW.metadata, '$.remoteContent.contentUrl') IS NOT NULL OR
    json_extract(NEW.metadata, '$.remoteContent.contentUrlHls') IS NOT NULL OR
    json_extract(NEW.metadata, '$.remoteContent.socialMediaVideo') IS NOT NULL OR
    json_extract(NEW.metadata, '$.remoteContent.publishedAt') IS NOT NULL OR
    json_extract(NEW.metadata, '$.remoteContent.contentUploadedAt') IS NOT NULL OR
    json_extract(NEW.metadata, '$.uploadedAt') IS NOT NULL OR
    (
  json_extract(NEW.metadata, '$.remoteContent.contentType') = 29 AND
  json_extract(NEW.metadata, '$.remoteContent.thumbnailUrl') IS NOT NULL AND
  json_extract(NEW.metadata, '$.remoteContent.thumbnailUrl') NOT LIKE '%default-content-thumbnail%'
)
  )
)
), 0)
     BEGIN
       UPDATE content_counts SET count = count
         + (CASE WHEN (
  json_extract(NEW.metadata, '$.remoteContent.privacy') = 0 AND
  (
  NEW.remote_content_id IS NOT NULL AND (
    json_extract(NEW.metadata, '$.remoteContent.contentUrl') IS NOT NULL OR
    json_extract(NEW.metadata, '$.remoteContent.contentUrlHls') IS NOT NULL OR
    json_extract(NEW.metadata, '$.remoteContent.socialMediaVideo') IS NOT NULL OR
    json_extract(NEW.metadata, '$.remoteContent.publishedAt') IS NOT NULL OR
    json_extract(NEW.metadata, '$.remoteContent.contentUploadedAt') IS NOT NULL OR
    json_extract(NEW.metadata, '$.uploadedAt') IS NOT NULL OR
    (
  json_extract(NEW.metadata, '$.remoteContent.contentType') = 29 AND
  json_extract(NEW.metadata, '$.remoteContent.thumbnailUrl') IS NOT NULL AND
  json_extract(NEW.metadata, '$.remoteContent.thumbnailUrl') NOT LIKE '%default-content-thumbnail%'
)
  )
)
) THEN 1 ELSE -1 END)
       WHERE key = 'posts';
     END;
CREATE TRIGGER content_counts_screenshots_ad AFTER DELETE ON contents
     WHEN (
  OLD.image_path IS NOT NULL OR
  json_extract(OLD.metadata, '$.remoteContent.contentType') = 29
)
     BEGIN
       UPDATE content_counts SET count = count - 1 WHERE key = 'screenshots';
     END;
CREATE TRIGGER content_counts_screenshots_ai AFTER INSERT ON contents
     WHEN (
  NEW.image_path IS NOT NULL OR
  json_extract(NEW.metadata, '$.remoteContent.contentType') = 29
)
     BEGIN
       UPDATE content_counts SET count = count + 1 WHERE key = 'screenshots';
     END;
CREATE TRIGGER content_counts_screenshots_au AFTER UPDATE OF metadata, image_path ON contents
     WHEN COALESCE((
  OLD.image_path IS NOT NULL OR
  json_extract(OLD.metadata, '$.remoteContent.contentType') = 29
), 0) IS NOT COALESCE((
  NEW.image_path IS NOT NULL OR
  json_extract(NEW.metadata, '$.remoteContent.contentType') = 29
), 0)
     BEGIN
       UPDATE content_counts SET count = count
         + (CASE WHEN (
  NEW.image_path IS NOT NULL OR
  json_extract(NEW.metadata, '$.remoteContent.contentType') = 29
) THEN 1 ELSE -1 END)
       WHERE key = 'screenshots';
     END;
INSERT INTO content_counts (key, count) VALUES ('__defsVersion', 3);
INSERT INTO content_counts (key, count) VALUES ('drafts', 0);
INSERT INTO content_counts (key, count) VALUES ('favorites', 0);
INSERT INTO content_counts (key, count) VALUES ('posts', 0);
INSERT INTO content_counts (key, count) VALUES ('screenshots', 0);
