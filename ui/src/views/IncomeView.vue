<script setup lang="ts">
/**
 * /income 页面：记一笔收入。
 *
 * 页面本身只是一层壳（标题 + 说明 + 表单），记账逻辑全在 `EntryRecordForm` 里——
 * 支出页与本页只有文案与提交类型不同，故各自是独立文件、共用同一个表单组件。
 * 独立文件使 `/income` 与 `/expense` 之间切换必然重新挂载，已填的表单不会残留到另一种记账上。
 */
import EntryRecordForm from '@/components/EntryRecordForm.vue'
</script>

<template>
  <main class="income">
    <header class="head">
      <h1 class="title">收入</h1>
      <p class="subtitle">
        记一笔进账：先选币种，再选【收入账户】（钱进了哪个账户）与【来源账户】（钱从哪里来），
        然后填入金额与摘要。收入会使收入账户的余额增加。
        来源账户留空即表示钱来自本账套之外（记入系统账本账户）；填一个不存在的账户名，
        系统会为你自动创建一个个人往来账户。只有币种相同的账户之间才能记账。
      </p>
    </header>

    <EntryRecordForm mode="Income" />
  </main>
</template>

<style scoped>
.income {
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
