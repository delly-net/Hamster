<template>
  <div class="about">
    <h1>This is an about page</h1>
  </div>
</template>

<style scoped>
/* 高度与居中交由 App.vue 的内容区承担（整屏锁定、内部滚动），页面不再声明 100vh */
.about h1 {
  font-size: 20px;
  font-weight: 600;
  color: var(--color-heading);
}
</style>
