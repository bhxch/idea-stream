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

CREATE TABLE IF NOT EXISTS setting (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
