<script setup lang="ts">
/**
 * /transfer 页面：记一笔转账。
 *
 * 页面本身只是一层壳（标题 + 说明 + 表单），记账逻辑全在 `EntryRecordForm` 里——
 * 收入页、支出页与本页只有文案与提交类型不同，故各自是独立文件、共用同一个表单组件。
 * 独立文件使三条路由之间切换必然重新挂载，已填的表单不会残留到另一种记账上。
 */
import EntryRecordForm from '@/components/EntryRecordForm.vue'
</script>

<template>
  <main class="transfer" data-entry-mode="Transfer">
    <header class="head">
      <h1 class="title">转账</h1>
      <p class="subtitle">
        记一笔账户之间的资金搬运：先选币种，再选【转出账户】（钱从哪个账户出）与【转入账户】
        （钱进到哪个账户），然后填入金额与摘要。转出账户的余额减少、转入账户的余额增加，
        两边金额相等。两个账户都必须从候选中选定，且只允许选择资金账户与负债账户——
        往来账户记录的是「谁欠谁」而不是「钱放在哪」，不作为转账的两端。
        转账不支持跨币种，只有币种相同的账户之间才能转账。
      </p>
    </header>

    <EntryRecordForm mode="Transfer" />
  </main>
</template>

<style scoped>
.transfer {
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
  /* 标题随记账类型着色（蓝）：--entry-color 由本元素上的 data-entry-mode 经 base.css 别名层折出，
     兜底仍取标题色，故页面脱离记账类型上下文时表现不变 */
  color: var(--entry-color, var(--color-heading));
}

.subtitle {
  font-size: 13px;
  line-height: 1.7;
  opacity: 0.75;
}
</style>
