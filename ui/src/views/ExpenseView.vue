<script setup lang="ts">
/**
 * /expense 页面：记一笔支出。
 *
 * 页面本身只是一层壳（标题 + 说明 + 表单），记账逻辑全在 `EntryRecordForm` 里——
 * 收入页与本页只有文案与提交类型不同，故各自是独立文件、共用同一个表单组件。
 * 独立文件使 `/income` 与 `/expense` 之间切换必然重新挂载，已填的表单不会残留到另一种记账上。
 */
import EntryRecordForm from '@/components/EntryRecordForm.vue'
</script>

<template>
  <main class="expense">
    <header class="head">
      <h1 class="title">支出</h1>
      <p class="subtitle">
        记一笔开销：选定账户后填入金额与摘要即可。支出会使所选账户的余额减少，
        对手方为该账套的系统账本账户（用户无需选择它）。记完的这笔账可在「账目明细」中按时间区间查到。
      </p>
    </header>

    <EntryRecordForm mode="Expense" />
  </main>
</template>

<style scoped>
.expense {
  /* 铺满内容区：限宽与居中的职责归 .app-main，页面自身既不限宽也不叠加外边距 */
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.head {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.title {
  font-size: 20px;
  font-weight: 600;
  color: var(--color-heading);
}

.subtitle {
  font-size: 13px;
  line-height: 1.7;
  opacity: 0.75;
}
</style>
