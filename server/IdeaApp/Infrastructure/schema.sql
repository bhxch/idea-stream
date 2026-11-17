CREATE TABLE IF NOT EXISTS project (
    id            TEXT PRIMARY KEY,          -- 'inbox' 或 UUIDv7
    name          TEXT NOT NULL,
    sort_order    INTEGER NOT NULL DEFAULT 0,
    deleted_at    TEXT NULL,                 -- 非空 = 在回收站
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_project_order   ON project(deleted_at, sort_order);
CREATE INDEX IF NOT EXISTS ix_project_active  ON project(updated_at DESC);

CREATE TABLE IF NOT EXISTS idea (
    id            TEXT PRIMARY KEY,
    project_id    TEXT NOT NULL REFERENCES project(id),
    title         TEXT NULL,
    content       TEXT NOT NULL,
    done          INTEGER NOT NULL DEFAULT 0,
    deleted_at    TEXT NULL,                 -- 非空 = 在回收站
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_idea_list       ON idea(project_id, deleted_at, updated_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_idea_updated    ON idea(deleted_at, updated_at DESC);
CREATE INDEX IF NOT EXISTS ix_idea_project    ON idea(project_id);

CREATE TABLE IF NOT EXISTS tag (
    id   INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE
);

CREATE TABLE IF NOT EXISTS idea_tag (
    idea_id TEXT NOT NULL REFERENCES idea(id) ON DELETE CASCADE,
    tag_id  INTEGER NOT NULL REFERENCES tag(id)  ON DELETE CASCADE,
    PRIMARY KEY (idea_id, tag_id)
);
CREATE INDEX IF NOT EXISTS ix_idea_tag_tag ON idea_tag(tag_id);

CREATE TABLE IF NOT EXISTS webhook (
    id            TEXT PRIMARY KEY,
    name          TEXT NOT NULL,
    url           TEXT NOT NULL,
    method        TEXT NOT NULL DEFAULT 'POST',
    headers_json  TEXT NOT NULL DEFAULT '{}',   -- { "Header": "value" }
    body_template TEXT NOT NULL,                -- JSON 模板，含 {{...}} 占位符
    events_json   TEXT NOT NULL DEFAULT '[]',   -- 订阅的事件类型数组；[] = 不订阅任何
    scope         TEXT NOT NULL,                -- 'global' | 'project'
    project_id    TEXT NULL REFERENCES project(id) ON DELETE CASCADE,
    secret        TEXT NULL,                    -- 非空则带 HMAC 签名头
    enabled       INTEGER NOT NULL DEFAULT 1,
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS webhook_delivery (
    id              TEXT PRIMARY KEY,
    webhook_id      TEXT NOT NULL REFERENCES webhook(id) ON DELETE CASCADE,
    event_id        TEXT NOT NULL,
    event_type      TEXT NOT NULL,
    payload         TEXT NOT NULL,              -- 已渲染的请求体
    status          TEXT NOT NULL,              -- 'pending' | 'delivered' | 'failed'
    attempts        INTEGER NOT NULL DEFAULT 0,
    last_error      TEXT NULL,
    created_at      TEXT NOT NULL,
    updated_at      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_delivery_webhook ON webhook_delivery(webhook_id, created_at DESC);
-- 一个 (webhook, 事件) 只允许有一行：手动重投与在途投递可能同时插同一行，
-- 没有这个唯一索引就会插出两行 —— 列表出现重复条目，attempts 分散在两行，
-- 手动重投只更新其中一行。
CREATE UNIQUE INDEX IF NOT EXISTS ix_delivery_event ON webhook_delivery(webhook_id, event_id);

CREATE TABLE IF NOT EXISTS setting (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

-- 想法的全文索引。三列对应三个可检索字段：正文、标签名、项目名。
-- 存的是**切分后**的词元串，不是原文 —— 所以不能用 external content 表
-- （external content 表会用本表的 tokenize 去处理原文，中文会整段成一个 token）。
--
-- ⚠️ FTS5 虚表的列清单**建表时定死**，IF NOT EXISTS 改不了 ——
-- 旧库（4 列，含 title）必须先 DROP 再重建，见 SqliteDatabase.Initialize 里的形状检查。
CREATE VIRTUAL TABLE IF NOT EXISTS idea_fts USING fts5(
    content, tags, project,
    tokenize = 'unicode61 remove_diacritics 0'
);

CREATE VIRTUAL TABLE IF NOT EXISTS project_fts USING fts5(
    name,
    tokenize = 'unicode61 remove_diacritics 0'
);

CREATE VIRTUAL TABLE IF NOT EXISTS tag_fts USING fts5(
    name,
    tokenize = 'unicode61 remove_diacritics 0'
);

-- 索引元信息。key = 'index.version'，value = 当前版本号。
-- 版本号变了就整体重建索引 —— 分词规则改了、或老库还没建索引，都靠它。
CREATE TABLE IF NOT EXISTS search_meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
