<script setup lang="ts">
/**
 * /login 页面：登录与注册合一的表单。
 *
 * 产品 Logo 置于卡片内部顶部，与标题、Tab、表单构成同一视觉整体（空白布局的卡片外头部
 * 已不再渲染任何品牌标识，见 App.vue）。
 *
 * 提交成功后跳回来源页（由路由守卫在 `redirect` 查询参数中携带），无来源页时回首页。
 */
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/http'
import { useAuthStore } from '@/stores/auth'

type Mode = 'login' | 'register'

/** 与后端保持一致的校验规则。 */
const USERNAME_PATTERN = /^[A-Za-z0-9_]+$/
const USERNAME_MIN_LENGTH = 3
const USERNAME_MAX_LENGTH = 32
const PASSWORD_MIN_LENGTH = 6

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const mode = ref<Mode>('login')
const username = ref('')
const password = ref('')
const submitting = ref(false)
const errorMessage = ref('')
const successMessage = ref('')

const isRegister = computed(() => mode.value === 'register')
const submitLabel = computed(() => {
  if (submitting.value) {
    return isRegister.value ? '注册中…' : '登录中…'
  }
  return isRegister.value ? '注册' : '登录'
})

/** 切换登录/注册，清空上一轮的结果提示。 */
function switchMode(next: Mode): void {
  if (mode.value === next || submitting.value) {
    return
  }

  mode.value = next
  errorMessage.value = ''
  successMessage.value = ''
}

/** 提交前的前端校验，返回错误文案；通过时返回空串。 */
function validate(): string {
  const name = username.value.trim()

  if (name.length === 0) {
    return '请输入用户名'
  }

  if (name.length < USERNAME_MIN_LENGTH || name.length > USERNAME_MAX_LENGTH) {
    return `用户名需为 ${USERNAME_MIN_LENGTH}-${USERNAME_MAX_LENGTH} 位`
  }

  if (!USERNAME_PATTERN.test(name)) {
    return '用户名只能包含字母、数字与下划线'
  }

  if (password.value.length < PASSWORD_MIN_LENGTH) {
    return `密码至少 ${PASSWORD_MIN_LENGTH} 位`
  }

  return ''
}

/** 提交登录或注册。 */
async function submit(): Promise<void> {
  if (submitting.value) {
    return
  }

  const validationError = validate()
  if (validationError.length > 0) {
    errorMessage.value = validationError
    return
  }

  submitting.value = true
  errorMessage.value = ''
  successMessage.value = ''

  try {
    const name = username.value.trim()

    if (isRegister.value) {
      // 注册不再自动登录：账号需管理员激活，故留在本页并切回登录 Tab，
      // 让用户看清「注册成功但还不能登录」的原因
      successMessage.value = await auth.register(name, password.value)
      mode.value = 'login'
      password.value = ''
      return
    }

    await auth.login(name, password.value)

    const redirect = route.query.redirect
    await router.replace(typeof redirect === 'string' && redirect.length > 0 ? redirect : '/')
  } catch (error) {
    errorMessage.value = error instanceof ApiError ? error.message : '操作失败，请稍后重试'
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <main class="auth">
    <img alt="Hamster logo" class="brand-logo" src="@/assets/logo.png" width="56" height="56" />

    <h1 class="title">{{ isRegister ? '注册仓鼠账号' : '登录仓鼠理财管家' }}</h1>
    <p class="subtitle">
      {{ isRegister ? '注册后需管理员激活，方可登录。' : '登录后即可管理你的账户与账目。' }}
    </p>

    <div class="tabs" role="tablist">
      <button
        type="button"
        role="tab"
        :aria-selected="!isRegister"
        :class="{ active: !isRegister }"
        @click="switchMode('login')"
      >
        登录
      </button>
      <button
        type="button"
        role="tab"
        :aria-selected="isRegister"
        :class="{ active: isRegister }"
        @click="switchMode('register')"
      >
        注册
      </button>
    </div>

    <form class="form" @submit.prevent="submit">
      <label class="field">
        <span class="label">用户名</span>
        <input
          v-model="username"
          type="text"
          autocomplete="username"
          placeholder="3-32 位字母、数字或下划线"
          :disabled="submitting"
        />
      </label>

      <label class="field">
        <span class="label">密码</span>
        <input
          v-model="password"
          type="password"
          :autocomplete="isRegister ? 'new-password' : 'current-password'"
          :placeholder="`至少 ${PASSWORD_MIN_LENGTH} 位`"
          :disabled="submitting"
        />
      </label>

      <p v-if="errorMessage" class="error">{{ errorMessage }}</p>
      <p v-if="successMessage" class="success">{{ successMessage }}</p>

      <button type="submit" class="submit" :disabled="submitting">{{ submitLabel }}</button>
    </form>

    <p class="hint">登录令牌有效期 1 天，保存在浏览器本地；令牌失效后需重新登录。</p>
  </main>
</template>

<style scoped>
/* 空白布局下 #app 是整屏纵向 flex 容器（见 main.css），
   `margin: auto` 让表单卡片在 logo 与页脚之间的剩余空间内水平垂直居中。
   切勿改写为在 `body.layout-blank #app > *` 上做 flex 居中——那会覆盖本组件的
   横向布局并让表单子元素收缩（见 main.css 注释）。 */
.auth {
  width: 100%;
  max-width: 25rem;
  margin: auto;
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  /* 卡片化：奶油面 + 暖棕细边 + 大圆角 + 柔和投影 */
  padding: 2rem 1.75rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-card);
  background: var(--color-background-soft);
  box-shadow: var(--shadow-card);
}

/* 卡片内品牌标识：顶部居中（卡片是纵向 flex 列，靠 align-self 水平居中） */
.brand-logo {
  display: block;
  align-self: center;
  /* 位图 logo 已带透明圆角，加一层柔和投影使其从奶油卡片面上浮起（与全站同源） */
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

.tabs {
  display: flex;
  gap: 0.5rem;
  margin-top: 0.5rem;
}

.tabs button {
  flex: 1;
  padding: 0.5rem 1rem;
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-control);
  background: none;
  color: inherit;
  font-size: 13.5px;
  cursor: pointer;
  transition:
    background-color 0.3s,
    border-color 0.3s,
    color 0.3s;
}

.tabs button.active {
  border-color: var(--color-accent);
  background: var(--color-accent-soft);
  color: var(--color-accent-strong);
  font-weight: 600;
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

.error {
  padding: 0.5rem 0.7rem;
  border: 1px solid var(--color-danger-border);
  border-radius: var(--radius-control);
  background: var(--color-danger-soft);
  color: var(--color-danger);
  font-size: 13px;
}

/* 成功提示复用主色而非绿色：全站只有 danger 一种语义色，不额外引入绿色 */
.success {
  padding: 0.5rem 0.7rem;
  border: 1px solid var(--color-accent);
  border-radius: var(--radius-control);
  background: var(--color-accent-soft);
  color: var(--color-accent-strong);
  font-size: 13px;
  line-height: 1.7;
}

/* 实心主色按钮：登录页唯一的强视觉锚点 */
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
