<script setup lang="ts">
/**
 * /reset-password 页面：密码重置落地页。
 *
 * 由管理员在「用户管理」页生成专属链接后交付给用户，链接形如
 * `/reset-password?username=xxx&token=yyy`，有效期 15 分钟且一次性。
 *
 * 本页**必须免登录**（见路由 meta）：被重置的用户通常处于未登录态，
 * 若要求登录则链接形同虚设。校验为「用户名 + 令牌」双因子：即使链接被转发，
 * 拿到令牌的人仍需知道对应的用户名才能改密。
 *
 * 用户名输入框由查询参数预填但**允许修改**：预填只是省事，
 * 真正的匹配由后端完成，篡改它只会得到统一的失败提示。
 */
import { computed, ref } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import { ApiError, request } from '@/api/http'

/** 与后端保持一致的密码规则。 */
const PASSWORD_MIN_LENGTH = 6

const route = useRoute()

/** 从查询参数取单个字符串值，非字符串（数组/缺失）时返回空串。 */
function queryValue(key: string): string {
  const value = route.query[key]
  return typeof value === 'string' ? value : ''
}

// 令牌来自链接，缺失即链接不完整，此时不允许提交
const token = queryValue('token')

const username = ref(queryValue('username'))
const password = ref('')
const confirmPassword = ref('')
const submitting = ref(false)
const errorMessage = ref('')
const successMessage = ref('')

const linkIncomplete = computed(() => token.length === 0)
/** 链接不完整或已提交成功时，表单不可再提交。 */
const formDisabled = computed(() => linkIncomplete.value || successMessage.value.length > 0)

/** 提交前的前端校验，返回错误文案；通过时返回空串。 */
function validate(): string {
  if (username.value.trim().length === 0) {
    return '请输入用户名'
  }

  if (password.value.length < PASSWORD_MIN_LENGTH) {
    return `密码至少 ${PASSWORD_MIN_LENGTH} 位`
  }

  if (password.value !== confirmPassword.value) {
    return '两次输入的密码不一致'
  }

  return ''
}

/** 提交新密码。 */
async function submit(): Promise<void> {
  if (submitting.value || formDisabled.value) {
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
    const result = await request<{ message: string }>('/api/auth/reset-password', {
      method: 'POST',
      body: {
        username: username.value.trim(),
        token,
        newPassword: password.value,
      },
      // 失败一律是 400（链接/用户名不匹配），不应触发全局登录失效处理
      auth: false,
      handleUnauthorized: false,
    })

    successMessage.value = result.message
    password.value = ''
    confirmPassword.value = ''
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '操作失败，请稍后重试'
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <main class="reset">
    <img alt="Hamster logo" class="brand-logo" src="@/assets/logo.png" width="56" height="56" />

    <h1 class="title">设置新密码</h1>
    <p class="subtitle">重置链接有效期 15 分钟且仅可使用一次。</p>

    <p v-if="linkIncomplete" class="error">
      重置链接不完整：缺少令牌参数。请向管理员重新索取重置链接。
    </p>

    <p v-if="successMessage" class="success">
      {{ successMessage }}
      <RouterLink class="inline-link" to="/login">前往登录</RouterLink>
    </p>

    <form class="form" @submit.prevent="submit">
      <label class="field">
        <span class="label">用户名</span>
        <input
          v-model="username"
          type="text"
          autocomplete="username"
          placeholder="重置链接对应的用户名"
          :disabled="submitting || formDisabled"
        />
      </label>

      <label class="field">
        <span class="label">新密码</span>
        <input
          v-model="password"
          type="password"
          autocomplete="new-password"
          :placeholder="`至少 ${PASSWORD_MIN_LENGTH} 位`"
          :disabled="submitting || formDisabled"
        />
      </label>

      <label class="field">
        <span class="label">确认新密码</span>
        <input
          v-model="confirmPassword"
          type="password"
          autocomplete="new-password"
          placeholder="再次输入新密码"
          :disabled="submitting || formDisabled"
        />
      </label>

      <p v-if="errorMessage" class="error">{{ errorMessage }}</p>

      <button type="submit" class="submit" :disabled="submitting || formDisabled">
        {{ submitting ? '提交中…' : '设置新密码' }}
      </button>
    </form>

    <p class="hint">
      若链接已过期，请联系系统管理员在「用户管理」中重新生成。
    </p>
  </main>
</template>

<style scoped>
/* 与登录页同源：空白布局下 #app 是整屏纵向 flex 容器（见 main.css），
   `margin: auto` 让卡片在 logo 与页脚之间的剩余空间内居中。 */
.reset {
  width: 100%;
  max-width: 25rem;
  margin: auto;
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  padding: 2rem 1.75rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-card);
  background: var(--color-background-soft);
  box-shadow: var(--shadow-card);
}

.brand-logo {
  display: block;
  align-self: center;
  filter: drop-shadow(0 8px 14px rgba(90, 58, 34, 0.22));
}

.title {
  font-size: 20px;
  font-weight: 600;
  color: var(--color-heading);
}

.subtitle {
  font-size: 13px;
  opacity: 0.75;
}

.form {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  margin-top: 0.5rem;
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

.error {
  padding: 0.5rem 0.7rem;
  border: 1px solid var(--color-danger-border);
  border-radius: var(--radius-control);
  background: var(--color-danger-soft);
  color: var(--color-danger);
  font-size: 13px;
  line-height: 1.6;
}

/* 成功态复用主色而非绿色：全站没有语义化的 success 令牌，
   新增一个绿色会破坏暖色视觉体系 */
.success {
  padding: 0.5rem 0.7rem;
  border: 1px solid var(--color-accent);
  border-radius: var(--radius-control);
  background: var(--color-accent-soft);
  color: var(--color-accent-strong);
  font-size: 13px;
  line-height: 1.7;
}

.inline-link {
  color: inherit;
  font-weight: 600;
  text-decoration: underline;
}

.submit {
  padding: 0.6rem 1rem;
  border: 1px solid var(--color-accent);
  border-radius: var(--radius-control);
  background: var(--color-accent);
  color: var(--color-accent-contrast);
  font-size: 14px;
  font-weight: 600;
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

.hint {
  font-size: 12px;
  opacity: 0.6;
  line-height: 1.7;
}
</style>
