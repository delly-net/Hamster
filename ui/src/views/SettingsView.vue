<script setup lang="ts">
/**
 * /settings 页面：用户设置，含「修改密码」与「关于」两个区块。
 *
 * 面向**所有登录用户**（路由标了 `requiresAuth`，不带 `requiresAdmin`）。
 *
 * 改密成功后**主动清空本地登录态**：JWT 不携带密码版本，服务端无法使已签发的旧令牌
 * 立即失效（见 README「已知边界」），故由前端收口——用户必须用新密码重新登录，
 * 避免「密码已改但旧令牌仍在用」的认知差。成功提示与「前往登录」链接的范式
 * 沿用 `ResetPasswordView.vue`。
 */
import { computed, ref } from 'vue'
import { RouterLink } from 'vue-router'
import { ApiError, request } from '@/api/http'
import {
  APP_VERSION,
  COPYRIGHT_HOLDER,
  LICENSE_NAME,
  PRODUCT_DESCRIPTION,
  PRODUCT_NAME,
  TECH_STACK,
} from '@/config/appInfo'
import { useAuthStore } from '@/stores/auth'

/** 与后端保持一致的密码规则。 */
const PASSWORD_MIN_LENGTH = 6

const auth = useAuthStore()

const currentPassword = ref('')
const newPassword = ref('')
const confirmPassword = ref('')
const submitting = ref(false)
const errorMessage = ref('')
const successMessage = ref('')

/** 改密成功后登录态已清空，表单不再可提交。 */
const done = computed(() => successMessage.value.length > 0)

/** 当前年份：版权行的年份随构建产物所在年份自动推进。 */
const currentYear = new Date().getFullYear()

/** 提交前的前端校验，返回错误文案；通过时返回空串。 */
function validate(): string {
  if (currentPassword.value.length === 0) {
    return '请输入原密码'
  }

  if (newPassword.value.length < PASSWORD_MIN_LENGTH) {
    return `新密码至少 ${PASSWORD_MIN_LENGTH} 位`
  }

  if (newPassword.value !== confirmPassword.value) {
    return '两次输入的新密码不一致'
  }

  return ''
}

/** 提交改密请求。 */
async function submit(): Promise<void> {
  if (submitting.value || done.value) {
    return
  }

  const validationError = validate()
  if (validationError.length > 0) {
    errorMessage.value = validationError
    return
  }

  submitting.value = true
  errorMessage.value = ''

  try {
    const result = await request<{ message: string }>('/api/auth/change-password', {
      method: 'POST',
      body: {
        currentPassword: currentPassword.value,
        newPassword: newPassword.value,
      },
      // 400 表示原密码错误等业务校验失败，不应触发全局登录失效处理
      handleUnauthorized: false,
    })

    // 改密即登出：见文件头注释，旧令牌在服务端仍有效，须由用户重新登录收敛
    auth.logout()
    successMessage.value = result.message
    currentPassword.value = ''
    newPassword.value = ''
    confirmPassword.value = ''
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '操作失败，请稍后重试'
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <main class="settings">
    <header class="head">
      <h1 class="title">用户设置</h1>
      <p class="subtitle">修改登录密码，以及查看本软件的产品信息。</p>
    </header>

    <section class="panel">
      <h2 class="panel-title">修改密码</h2>
      <p class="panel-hint">修改成功后当前登录将失效，请使用新密码重新登录。</p>

      <p v-if="successMessage" class="success">
        {{ successMessage }}
        <RouterLink class="inline-link" to="/login">前往登录</RouterLink>
      </p>

      <form class="form" @submit.prevent="submit">
        <label class="field">
          <span class="label">原密码</span>
          <input
            v-model="currentPassword"
            type="password"
            autocomplete="current-password"
            placeholder="当前使用的密码"
            :disabled="submitting || done"
          />
        </label>

        <label class="field">
          <span class="label">新密码</span>
          <input
            v-model="newPassword"
            type="password"
            autocomplete="new-password"
            :placeholder="`至少 ${PASSWORD_MIN_LENGTH} 位`"
            :disabled="submitting || done"
          />
        </label>

        <label class="field">
          <span class="label">确认新密码</span>
          <input
            v-model="confirmPassword"
            type="password"
            autocomplete="new-password"
            placeholder="再次输入新密码"
            :disabled="submitting || done"
          />
        </label>

        <p v-if="errorMessage" class="error">{{ errorMessage }}</p>

        <div class="actions">
          <button type="submit" class="submit" :disabled="submitting || done">
            {{ submitting ? '提交中…' : '修改密码' }}
          </button>
        </div>
      </form>
    </section>

    <section class="panel">
      <h2 class="panel-title">关于</h2>

      <div class="product">
        <img alt="Hamster logo" class="brand-logo" src="@/assets/logo.png" width="48" height="48" />
        <div class="product-text">
          <p class="product-name">
            {{ PRODUCT_NAME }}
            <span class="version">v{{ APP_VERSION }}</span>
          </p>
          <p class="product-desc">{{ PRODUCT_DESCRIPTION }}</p>
        </div>
      </div>

      <dl class="facts">
        <div v-for="item in TECH_STACK" :key="item.label" class="fact">
          <dt>{{ item.label }}</dt>
          <dd>{{ item.value }}</dd>
        </div>
        <div class="fact">
          <dt>开源许可</dt>
          <dd>{{ LICENSE_NAME }}</dd>
        </div>
      </dl>

      <p class="copyright">
        © {{ currentYear }} {{ COPYRIGHT_HOLDER }} · {{ LICENSE_NAME }} 许可发布
      </p>
    </section>
  </main>
</template>

<style scoped>
.settings {
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

/* 两个区块共用卡片外观；内容宽度按表单可读性收敛，不随内容区无限拉伸 */
.panel {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  max-width: 34rem;
  padding: 1.25rem 1.5rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-card);
  background: var(--color-background-soft);
  box-shadow: var(--shadow-card);
}

.panel-title {
  font-size: 15px;
  font-weight: 600;
  color: var(--color-heading);
}

.panel-hint {
  font-size: 12.5px;
  line-height: 1.7;
  opacity: 0.75;
}

.form {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.field {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.label {
  font-size: 12.5px;
  opacity: 0.75;
}

.field input {
  padding: 0.55rem 0.7rem;
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-control);
  background: var(--color-background);
  color: inherit;
  font-size: 14px;
  font-family: inherit;
  box-shadow: var(--shadow-control);
  transition:
    border-color 0.3s,
    outline-color 0.3s;
}

.field input:focus {
  outline: 2px solid var(--color-accent-soft);
  outline-offset: 1px;
  border-color: var(--color-accent);
}

.field input:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

/* 成功 / 失败两态均复用既有语义令牌：全站只有 danger 一种语义色，
   不额外引入绿色以维持暖色视觉体系 */
.success,
.error {
  padding: 0.5rem 0.7rem;
  border-radius: var(--radius-control);
  font-size: 13px;
  line-height: 1.7;
}

.success {
  border: 1px solid var(--color-accent);
  background: var(--color-accent-soft);
  color: var(--color-accent-strong);
}

.error {
  border: 1px solid var(--color-danger-border);
  background: var(--color-danger-soft);
  color: var(--color-danger);
}

.inline-link {
  color: inherit;
  font-weight: 600;
  text-decoration: underline;
}

.actions {
  display: flex;
  gap: 0.5rem;
}

.submit {
  padding: 0.55rem 1.1rem;
  border: 1px solid var(--color-accent);
  border-radius: var(--radius-control);
  background: var(--color-accent);
  color: var(--color-accent-contrast);
  font-size: 14px;
  font-weight: 600;
  font-family: inherit;
  cursor: pointer;
  transition:
    background-color 0.3s,
    border-color 0.3s,
    transform 0.15s;
}

@media (hover: hover) {
  .submit:not(:disabled):hover {
    border-color: var(--color-accent-strong);
    background: var(--color-accent-strong);
  }
}

.submit:not(:disabled):active {
  transform: translateY(1px);
}

.submit:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

.product {
  display: flex;
  align-items: center;
  gap: 0.85rem;
}

.brand-logo {
  display: block;
  flex: none;
  /* 与 header 的品牌 logo 同源投影，保持浮起感一致 */
  filter: drop-shadow(0 4px 8px rgba(90, 58, 34, 0.22));
}

.product-text {
  min-width: 0;
}

.product-name {
  display: flex;
  align-items: baseline;
  gap: 0.5rem;
  font-size: 16px;
  font-weight: 600;
  color: var(--color-heading);
}

.version {
  font-size: 12.5px;
  font-weight: 500;
  color: var(--color-accent-strong);
}

.product-desc {
  margin-top: 0.2rem;
  font-size: 13px;
  opacity: 0.75;
}

.facts {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  padding-top: 0.75rem;
  border-top: 1px solid var(--color-border);
}

.fact {
  display: flex;
  gap: 0.75rem;
  font-size: 13px;
  line-height: 1.7;
}

.fact dt {
  flex: none;
  width: 4.5rem;
  opacity: 0.7;
}

.fact dd {
  min-width: 0;
}

.copyright {
  font-size: 12px;
  line-height: 1.7;
  opacity: 0.6;
}
</style>
