<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { apiGet, apiSend } from '../api/client';
import type { Webhook } from '../api/types';
import { reportError } from '../lib/errors';
import DeliveryList from './DeliveryList.vue';
import WebhookForm from './WebhookForm.vue';

const props = defineProps<{
  /** 作用域：全局 / 项目。 */
  scope: 'global' | 'project';
  /** 项目级 webhook 绑定的项目 id（全局作用域时不用给）。 */
  projectId?: string;
  /** 区块标题。 */
  title?: string;
}>();

/** 列表里的 webhook。 */
const webhooks = ref<Webhook[]>([]);

/**
 * 正在编辑的 webhook。
 *
 * `undefined` = 没在编辑；`null` = 正在新建；一个 webhook = 正在编辑它。
 */
const editing = ref<Webhook | null | undefined>(undefined);

/** 展开投递记录的那个 webhook id（`null` = 都收着）。 */
const deliveryId = ref<string | null>(null);

/** 等着确认删除的 webhook id（`null` = 没在确认）。 */
const confirmingId = ref<string | null>(null);

/**
 * 拉列表。
 *
 * 全局作用域不带 `projectId`（后端按「不带就是只列全局的」处理）；
 * 项目作用域带上项目 id（后端列绑定到该项目的那些）。
 *
 * 带上 `unmask=true`：后端默认回传的是遮蔽版（url 的路径段与请求头的值
 * 都换了 `***`），页面要编辑配置、要看完整的 url，所以显式要原值。
 */
async function load(): Promise<void> {
  try {
    webhooks.value =
      props.scope === 'project'
        ? await apiGet<Webhook[]>('/webhooks', {
            projectId: props.projectId,
            unmask: 'true',
          })
        : await apiGet<Webhook[]>('/webhooks', { unmask: 'true' });
  } catch (error) {
    reportError(error, '加载 webhook 列表失败');
  }
}

/** 开始新建。 */
function startCreate(): void {
  editing.value = null;
  deliveryId.value = null;
  confirmingId.value = null;
}

/** 开始编辑。 */
function startEdit(webhook: Webhook): void {
  editing.value = webhook;
  deliveryId.value = null;
  confirmingId.value = null;
}

/** 关闭表单。 */
function cancelEdit(): void {
  editing.value = undefined;
}

/** 存好了：关掉表单并刷新列表。 */
async function onSaved(): Promise<void> {
  editing.value = undefined;
  await load();
}

/** 启用 / 禁用。 */
async function toggleEnabled(webhook: Webhook): Promise<void> {
  try {
    await apiSend<void>('PATCH', `/webhooks/${webhook.id}`, {
      enabled: !webhook.enabled,
    });
    // PATCH 的响应是遮蔽版（写接口不返回完整凭据），不能拿它覆盖列表里的行 ——
    // 改成再拉一次列表：多一次 GET，换来列表状态与服务端一致。
    await load();
  } catch (error) {
    reportError(error, '切换启用状态失败');
  }
}

/** 问一句再删（删想法、删项目都有二次确认，删 webhook 也一样）。 */
function askDelete(webhook: Webhook): void {
  confirmingId.value = webhook.id;
}

/** 不删了。 */
function cancelDelete(): void {
  confirmingId.value = null;
}

/**
 * 确认删除。
 *
 * 删掉的是配置与它的投递记录 —— 不可逆，所以确认过才发请求。
 */
async function remove(webhook: Webhook): Promise<void> {
  try {
    await apiSend<void>('DELETE', `/webhooks/${webhook.id}`);
    confirmingId.value = null;
    if (deliveryId.value === webhook.id) {
      deliveryId.value = null;
    }
    await load();
  } catch (error) {
    reportError(error, '删除 webhook 失败');
  }
}

/** 展开 / 收起投递记录。 */
function toggleDeliveries(webhook: Webhook): void {
  deliveryId.value = deliveryId.value === webhook.id ? null : webhook.id;
}

onMounted(() => {
  void load();
});
</script>

<template>
  <section class="webhooks">
    <div class="webhooks__head">
      <h2 v-if="title" class="webhooks__title">{{ title }}</h2>
      <button class="webhooks__new" type="button" @click="startCreate">新建 webhook</button>
    </div>

    <ul class="webhooks__list">
      <li
        v-for="webhook in webhooks"
        :key="webhook.id"
        class="webhooks__item"
        :data-webhook-id="webhook.id"
      >
        <div class="webhooks__row">
          <span class="webhooks__name">{{ webhook.name }}</span>
          <span class="webhooks__state">{{ webhook.enabled ? '已启用' : '已禁用' }}</span>
          <span class="webhooks__events">
            {{ webhook.events.length > 0 ? webhook.events.join('、') : '不订阅任何事件' }}
          </span>
          <span class="webhooks__secret">{{ webhook.hasSecret ? '已设密钥' : '无密钥' }}</span>
        </div>

        <p class="webhooks__url">{{ webhook.url }}</p>

        <div class="webhooks__actions">
          <button class="webhooks__edit" type="button" @click="startEdit(webhook)">编辑</button>
          <button class="webhooks__toggle" type="button" @click="toggleEnabled(webhook)">
            {{ webhook.enabled ? '禁用' : '启用' }}
          </button>
          <button class="webhooks__delete" type="button" @click="askDelete(webhook)">删除</button>
          <button class="webhooks__deliveries" type="button" @click="toggleDeliveries(webhook)">
            投递记录
          </button>
        </div>

        <!-- 删 webhook 是破坏性的（配置与投递记录一起没了）：先问一句。 -->
        <div v-if="confirmingId === webhook.id" class="webhooks__confirm">
          <span class="webhooks__confirm-text">删除这个 webhook？它的配置和投递记录会一起没了。</span>
          <button class="webhooks__confirm-yes" type="button" @click="remove(webhook)">
            确认删除
          </button>
          <button class="webhooks__confirm-no" type="button" @click="cancelDelete">取消</button>
        </div>

        <DeliveryList v-if="deliveryId === webhook.id" :webhook-id="webhook.id" />
      </li>
    </ul>

    <p v-if="webhooks.length === 0" class="webhooks__empty">还没有 webhook。</p>

    <WebhookForm
      v-if="editing !== undefined"
      :webhook="editing"
      :scope="scope"
      :project-id="projectId"
      @saved="onSaved"
      @cancel="cancelEdit"
    />
  </section>
</template>

<style scoped>
.webhooks__head {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.webhooks__title {
  flex: 1;
  margin: 0;
  font-size: 16px;
}

.webhooks__list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 12px 0 0;
  padding: 0;
  list-style: none;
}

.webhooks__item {
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--panel);
  box-shadow: var(--shadow);
}

.webhooks__row {
  display: flex;
  align-items: baseline;
  gap: 10px;
  flex-wrap: wrap;
}

.webhooks__name {
  flex: 1 1 auto;
  min-width: 0;
  overflow-wrap: anywhere;
  font-weight: 600;
}

.webhooks__state,
.webhooks__events,
.webhooks__secret {
  color: var(--muted);
  font-size: 12px;
}

.webhooks__url {
  margin: 4px 0 0;
  color: var(--muted);
  font-size: 12px;
  overflow-wrap: anywhere;
}

.webhooks__actions {
  display: flex;
  gap: 6px;
  margin-top: 8px;
  flex-wrap: wrap;
}

.webhooks__delete {
  color: var(--danger);
}

.webhooks__confirm {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 8px;
  padding: 6px 8px;
  border: 1px solid var(--danger);
  border-radius: 6px;
}

.webhooks__confirm-text {
  flex: 1;
  font-size: 12px;
}

.webhooks__empty {
  color: var(--muted);
  font-size: 13px;
}
</style>
