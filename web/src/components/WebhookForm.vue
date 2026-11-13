<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { apiGet, apiSend } from '../api/client';
import type { Webhook, WebhookTemplate } from '../api/types';
import { reportError } from '../lib/errors';
import { WEBHOOK_EVENT_LABELS, WEBHOOK_EVENT_TYPES } from '../lib/webhook';
import { showToast } from './ToastHost.vue';

const props = defineProps<{
  /** 要编辑的 webhook；`null` 表示新建。 */
  webhook: Webhook | null;
  /** 作用域：全局 / 项目。 */
  scope: 'global' | 'project';
  /** 项目级 webhook 绑定的项目 id（全局作用域时不用给）。 */
  projectId?: string;
}>();

const emit = defineEmits<{
  /** 存好了（带上存好的 webhook）。 */
  saved: [webhook: Webhook];
  /** 用户点了「取消」。 */
  cancel: [];
}>();

/** 一行自定义 Header。 */
interface HeaderRow {
  key: string;
  value: string;
}

const name = ref('');
const url = ref('');
const method = ref('POST');
const headerRows = ref<HeaderRow[]>([]);
const bodyTemplate = ref('');
const events = ref<string[]>([]);
const enabled = ref(true);
const secret = ref('');
const clearSecret = ref(false);
const saving = ref(false);

/** 是不是新建。 */
const isNew = computed(() => props.webhook === null);

/**
 * 换成另一个 webhook 时把表单重置成它的配置。
 *
 * 父组件可能连着编辑两个不同的 webhook（组件实例会被复用），
 * 所以每次换 webhook 都要把表单整体重置 —— 上一条的值留在新表单里更糟。
 */
watch(
  () => props.webhook,
  (webhook) => {
    name.value = webhook?.name ?? '';
    url.value = webhook?.url ?? '';
    method.value = webhook?.method ?? 'POST';
    headerRows.value = webhook
      ? Object.entries(webhook.headers).map(([key, value]) => ({ key, value }))
      : [];
    bodyTemplate.value = webhook?.bodyTemplate ?? '';
    events.value = webhook ? [...webhook.events] : [];
    // 新建时的初值按**作用域**给（后端也是按作用域定默认值的）：
    // 全局默认禁用、项目级默认启用 —— 开关虽然锁着（新建不传 enabled），
    // 显示的值也得跟实际创建出来的结果一致。
    enabled.value = webhook?.enabled ?? props.scope === 'project';
    // 密钥明文不回传：每次编辑都从空开始。
    secret.value = '';
    clearSecret.value = false;
  },
  { immediate: true },
);

/**
 * 「清除密钥」与「新填的密钥」**互斥**。
 *
 * 两者同时成立时，保存会以「清除」为准（后端三态里 `""` 优先于非空），
 * 新填的密钥就被静默丢掉了 —— 用户不会知道。两个方向都要管：
 * - 勾上「清除密钥」→ 输入框里的内容清掉；
 * - 输入框里填了**非空白**的内容 → 「清除密钥」自动取消勾选。
 *
 * ⚠️ 判空口径必须与 {@link secretValue} 一致（都用 trim）：
 * 一个用 trim、一个用原始长度，会出现「只填了一个空格」这种
 * 两个控件都不生效的状态 —— 勾选被取消（按原始长度判），保存时又因
 * trim 后为空而发 `null`（= 不改），用户明确要的「清除」就这么丢了。
 */
watch(secret, (value) => {
  if (value.trim().length > 0 && clearSecret.value) {
    clearSecret.value = false;
  }
});

watch(clearSecret, (checked) => {
  if (checked) {
    secret.value = '';
  }
});

/** 自定义 Header 收成后端要的对象（空键的行忽略）。 */
function buildHeaders(): Record<string, string> {
  const headers: Record<string, string> = {};
  for (const row of headerRows.value) {
    const key = row.key.trim();
    if (!key) {
      continue;
    }
    headers[key] = row.value;
  }
  return headers;
}

/**
 * 密钥的值。
 *
 * 后端的三态（`WebhookUpdateRequest.Secret`）：
 * - `null` = **不改**（密码框留空就是这个意思）；
 * - `""` = 清空密钥（勾了「清除密钥」）；
 * - 非空 = 替换成新密钥。
 *
 * 新建时 `null` 与 `""` 都表示「不设密钥」。
 *
 * ⚠️ **值不 trim**：密钥首尾的空白是有意义的，trim 会静默改掉用户填的东西。
 * 只用 trim 判断「是不是留空了」（留空 → `null` = 不改）。
 */
function secretValue(): string | null {
  if (clearSecret.value) {
    return '';
  }
  if (secret.value.trim().length === 0) {
    return null;
  }
  return secret.value;
}

/** 加一行 Header。 */
function addHeader(): void {
  headerRows.value = [...headerRows.value, { key: '', value: '' }];
}

/** 删掉一行 Header。 */
function removeHeader(index: number): void {
  headerRows.value = headerRows.value.filter((_, position) => position !== index);
}

/**
 * 预填 Telegram 模板。
 *
 * 模板里的 URL 是占位符（`<BOT_TOKEN>`），bot token 由用户自己填 ——
 * 模板本身不含任何凭据。
 */
async function applyTelegramTemplate(): Promise<void> {
  try {
    const template = await apiGet<WebhookTemplate>('/webhooks/templates/telegram');
    url.value = template.url;
    bodyTemplate.value = template.bodyTemplate;
  } catch (error) {
    reportError(error, '获取 Telegram 模板失败');
  }
}

/** 保存。 */
async function onSave(): Promise<void> {
  if (saving.value) {
    return;
  }
  const trimmedName = name.value.trim();
  const trimmedUrl = url.value.trim();
  const trimmedMethod = method.value.trim() || 'POST';
  if (!trimmedName) {
    showToast('webhook 名称不能为空', 'error');
    return;
  }
  if (!trimmedUrl) {
    showToast('webhook 的 URL 不能为空', 'error');
    return;
  }
  if (!bodyTemplate.value.trim()) {
    // 后端也会拒（Body 模板不能为空），但先说清楚省得用户白等一次请求。
    showToast('Body 模板不能为空', 'error');
    return;
  }
  saving.value = true;
  try {
    if (props.webhook) {
      const updated = await apiSend<Webhook>('PATCH', `/webhooks/${props.webhook.id}`, {
        name: trimmedName,
        url: trimmedUrl,
        method: trimmedMethod,
        headers: buildHeaders(),
        bodyTemplate: bodyTemplate.value,
        events: [...events.value],
        enabled: enabled.value,
        secret: secretValue(),
      });
      emit('saved', updated);
      return;
    }
    // 新建**不传 enabled**：启用与否由作用域决定
    // （全局默认禁用、项目级默认启用），创建完再改由 PATCH 负责。
    const created = await apiSend<Webhook>('POST', '/webhooks', {
      name: trimmedName,
      url: trimmedUrl,
      method: trimmedMethod,
      headers: buildHeaders(),
      bodyTemplate: bodyTemplate.value,
      events: [...events.value],
      scope: props.scope,
      projectId: props.scope === 'project' ? (props.projectId ?? null) : null,
      secret: secretValue(),
    });
    emit('saved', created);
  } catch (error) {
    reportError(error, '保存 webhook 失败');
  } finally {
    saving.value = false;
  }
}
</script>

<template>
  <section class="webhook-form">
    <h3 class="webhook-form__title">{{ isNew ? '新建 webhook' : '编辑 webhook' }}</h3>

    <div class="webhook-form__row">
      <label class="webhook-form__label" for="webhook-name">名称</label>
      <input
        id="webhook-name"
        class="webhook-form__name"
        v-model="name"
        type="text"
        placeholder="给这个 webhook 起个名字"
      />
    </div>

    <div class="webhook-form__row">
      <label class="webhook-form__label" for="webhook-url">URL</label>
      <input
        id="webhook-url"
        class="webhook-form__url"
        v-model="url"
        type="text"
        placeholder="https://…"
      />
    </div>
    <p class="webhook-form__note">
      URL 里可能带凭据（比如 Telegram 的 bot token 就在路径里）——别把它贴到别处。
    </p>

    <div class="webhook-form__row">
      <label class="webhook-form__label" for="webhook-method">方法</label>
      <select id="webhook-method" class="webhook-form__method" v-model="method">
        <option value="POST">POST</option>
        <option value="GET">GET</option>
        <option value="PUT">PUT</option>
        <option value="PATCH">PATCH</option>
        <option value="DELETE">DELETE</option>
      </select>
      <button class="webhook-form__telegram" type="button" @click="applyTelegramTemplate">
        添加 Telegram webhook
      </button>
    </div>

    <fieldset class="webhook-form__box">
      <legend>自定义 Header</legend>
      <div
        v-for="(row, index) in headerRows"
        :key="index"
        class="webhook-form__header-row"
      >
        <input
          class="webhook-form__header-key"
          v-model="row.key"
          type="text"
          placeholder="Header 名"
          aria-label="Header 名"
        />
        <input
          class="webhook-form__header-value"
          v-model="row.value"
          type="text"
          placeholder="值"
          aria-label="Header 值"
        />
        <button
          class="webhook-form__header-remove"
          type="button"
          aria-label="删除这一行 Header"
          @click="removeHeader(index)"
        >
          ×
        </button>
      </div>
      <button class="webhook-form__header-add" type="button" @click="addHeader">加一行</button>
    </fieldset>

    <div class="webhook-form__row">
      <label class="webhook-form__label" for="webhook-body">Body 模板</label>
      <textarea
        id="webhook-body"
        class="webhook-form__body"
        v-model="bodyTemplate"
        rows="6"
        spellcheck="false"
        placeholder='{"text":"{{idea.content}}"}'
      ></textarea>
    </div>
    <p class="webhook-form__note">JSON 模板，占位符用双花括号包住字段名。</p>

    <fieldset class="webhook-form__box">
      <legend>订阅事件</legend>
      <label v-for="eventType in WEBHOOK_EVENT_TYPES" :key="eventType" class="webhook-form__event-label">
        <input class="webhook-form__event" type="checkbox" :value="eventType" v-model="events" />
        <span>{{ WEBHOOK_EVENT_LABELS[eventType] ?? eventType }}</span>
        <code>{{ eventType }}</code>
      </label>
    </fieldset>

    <div class="webhook-form__row">
      <label class="webhook-form__label" for="webhook-enabled">启用</label>
      <input
        id="webhook-enabled"
        class="webhook-form__enabled"
        type="checkbox"
        :disabled="isNew"
        v-model="enabled"
      />
      <span v-if="isNew" class="webhook-form__note">
        新建时由作用域决定默认值（全局默认禁用、项目级默认启用），建完再改。
      </span>
    </div>

    <div class="webhook-form__row">
      <label class="webhook-form__label" for="webhook-secret">Secret</label>
      <input
        id="webhook-secret"
        class="webhook-form__secret"
        v-model="secret"
        type="password"
        autocomplete="new-password"
        placeholder="留空表示不改"
      />
    </div>
    <label v-if="!isNew && webhook?.hasSecret" class="webhook-form__clear">
      <input class="webhook-form__clear-secret" type="checkbox" v-model="clearSecret" />
      <span>清除已设的密钥</span>
    </label>

    <div class="webhook-form__actions">
      <button class="webhook-form__save" type="button" :disabled="saving" @click="onSave">保存</button>
      <button class="webhook-form__cancel" type="button" @click="emit('cancel')">取消</button>
    </div>
  </section>
</template>

<style scoped>
.webhook-form {
  margin-top: 16px;
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--panel);
  box-shadow: var(--shadow);
}

.webhook-form__title {
  margin: 0 0 10px;
  font-size: 15px;
}

.webhook-form__row {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
  flex-wrap: wrap;
}

.webhook-form__label {
  flex: 0 0 72px;
  font-size: 13px;
  color: var(--muted);
}

.webhook-form__name,
.webhook-form__url {
  flex: 1 1 240px;
  min-width: 0;
}

.webhook-form__body {
  flex: 1 1 100%;
  width: 100%;
  min-height: 120px;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 13px;
  resize: vertical;
}

.webhook-form__note {
  margin: 0 0 8px;
  color: var(--muted);
  font-size: 12px;
}

.webhook-form__box {
  margin: 0 0 10px;
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: 6px;
}

.webhook-form__box legend {
  padding: 0 4px;
  color: var(--muted);
  font-size: 12px;
}

.webhook-form__header-row {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 6px;
}

.webhook-form__header-key,
.webhook-form__header-value {
  flex: 1 1 0;
  min-width: 0;
}

.webhook-form__event-label {
  display: flex;
  align-items: baseline;
  gap: 6px;
  margin-bottom: 4px;
  font-size: 13px;
}

.webhook-form__event code {
  color: var(--muted);
  font-size: 12px;
}

.webhook-form__clear {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 8px;
  font-size: 13px;
}

.webhook-form__actions {
  display: flex;
  gap: 8px;
}
</style>
