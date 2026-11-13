<script setup lang="ts">
import { onMounted, ref, watch } from 'vue';
import { apiGet, apiSend } from '../api/client';
import type { Delivery } from '../api/types';
import { reportError } from '../lib/errors';
import { formatRelativeTime } from '../lib/format';

const props = defineProps<{
  /** 要看投递记录的 webhook id。 */
  webhookId: string;
}>();

/** 投递记录（最近的在前，后端给的顺序）。 */
const deliveries = ref<Delivery[]>([]);

/** 正在重投的记录 id（防重复点击）。 */
const busyId = ref<string | null>(null);

/** 拉一次投递记录。 */
async function load(): Promise<void> {
  try {
    deliveries.value = await apiGet<Delivery[]>(`/webhooks/${props.webhookId}/deliveries`);
  } catch (error) {
    reportError(error, '加载投递记录失败');
  }
}

/**
 * 手动重投。
 *
 * 重投的是记录里**已渲染的 payload**，不按当前模板重新渲染 ——
 * 渲染需要事件的全量数据，那东西压根没存下来。
 */
async function retry(delivery: Delivery): Promise<void> {
  if (busyId.value) {
    return;
  }
  busyId.value = delivery.id;
  try {
    await apiSend<Delivery>(
      'POST',
      `/webhooks/${props.webhookId}/deliveries/${delivery.id}/retry`,
    );
    // 重投是异步的（投进队列就返回）：刷新一次看最新状态。
    await load();
  } catch (error) {
    reportError(error, '重投失败');
  } finally {
    busyId.value = null;
  }
}

watch(() => props.webhookId, () => {
  void load();
});

onMounted(() => {
  void load();
});
</script>

<template>
  <div class="delivery-list">
    <ul v-if="deliveries.length > 0" class="delivery-list__items">
      <li
        v-for="delivery in deliveries"
        :key="delivery.id"
        class="delivery__row"
        :data-delivery-id="delivery.id"
      >
        <span class="delivery__event">{{ delivery.eventType }}</span>
        <span class="delivery__status">{{ delivery.status }}</span>
        <span class="delivery__attempts">尝试 {{ delivery.attempts }} 次</span>
        <span class="delivery__time">{{ formatRelativeTime(delivery.createdAt) }}</span>
        <span v-if="delivery.lastError" class="delivery__error">{{ delivery.lastError }}</span>
        <button
          class="delivery__retry"
          type="button"
          :disabled="busyId !== null"
          @click="retry(delivery)"
        >
          重投
        </button>
      </li>
    </ul>

    <p v-else class="delivery__empty">还没有投递记录。</p>
  </div>
</template>

<style scoped>
.delivery-list {
  margin-top: 8px;
}

.delivery-list__items {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.delivery__row {
  display: flex;
  align-items: baseline;
  gap: 8px;
  flex-wrap: wrap;
  padding: 4px 6px;
  border: 1px solid var(--border);
  border-radius: 6px;
  font-size: 12px;
}

.delivery__event {
  font-weight: 600;
}

.delivery__status,
.delivery__attempts,
.delivery__time {
  color: var(--muted);
}

.delivery__error {
  flex: 1 1 100%;
  color: var(--danger);
  overflow-wrap: anywhere;
}

.delivery__empty {
  margin: 0;
  color: var(--muted);
  font-size: 12px;
}
</style>
