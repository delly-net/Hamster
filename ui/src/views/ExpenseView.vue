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
        记一笔开销：先选币种，再选【支出账户】（钱从哪个账户出）与【目标账户】（钱花到哪里去），
        然后填入金额与摘要。支出会使支出账户的余额减少。
        目标账户留空即表示钱花到了本账套之外（记入系统账本账户）；填一个不存在的账户名，
        系统会为你自动创建一个个人往来账户。只有币种相同的账户之间才能记账。
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
