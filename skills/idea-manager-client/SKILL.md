---
name: idea-manager
description: 管理本地 idea 记事本（项目、想法、标签、完成状态、合并、回收站、webhook 通知）。当用户要记录、查看、整理、批量操作 idea/想法，或要配置 Telegram 通知时使用。
---
# idea-manager（薄壳 · 说明书在服务端）

本 skill 的说明书由 idea 服务端分发，**不在本目录里**。本目录只有两个文件：
本文件（指针）与 `scripts/fetch.py`（取件器）。

## 第一步：取说明书

    scripts/fetch.py                      # 索引页：有哪些能力、怎么调、细节去哪找
    scripts/fetch.py <路径>               # 按索引页里的路由表取某一份细节
    scripts/fetch.py /openapi/v1.json     # 完整接口契约

## 第二步：按索引页给的姿势调 API

索引页里有 curl 模板与能力清单（能力 → 方法 + 路径）。契约在 `/openapi/v1.json`。

## 连接配置（只读环境变量）

| 变量 | 必填 | 说明 |
|---|---|---|
| `IDEA_BASE_URL` | 是 | 实例地址，如 `https://ideas.example.com` |
| `IDEA_USER` | 否 | nginx basic auth 用户名（与密码同时存在时才发） |
| `IDEA_PASS` | 否 | nginx basic auth 密码 |
| `IDEA_TOKEN` | 否 | 可选。存在时额外发 `X-Api-Key` 头 |
| `IDEA_SKILL_CACHE_DIR` | 否 | 缓存根目录（默认 `${XDG_CACHE_HOME:-~/.cache}/idea-skill`） |

## 取件失败怎么查

- `missing_config` / `invalid_config`：`IDEA_BASE_URL` 没设或写错了（要带 scheme）。
- `auth_error`：basic auth 的用户名或密码不对。
- `network_error`：网络不可达或超时。
- `skill_not_distributed`：服务端没有分发 skill 内容（部署漏了 `skill/` 目录）。
- `not_found`：要取的文件不存在。
- `skill_integrity_error`：取到的内容与服务端清单对不上（多半是传输中被改了），重试一次。
- 服务端暂时连不上、但本地有缓存时：stderr 会有一行 `warning:`，这时拿到的是**上一版**手册。
