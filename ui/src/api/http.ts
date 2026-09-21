/**
 * 统一请求层：拼接运行时配置中的后端基址、附加登录令牌，并把后端错误
 * 转成可直接展示的中文文案。
 *
 * 后端基址来自 `public/conf/setting.json`，源码中不硬编码域名。
 */

import { getAppConfig, joinApiUrl } from '@/config/appConfig'
import { clearToken, getToken } from '@/auth/token'

/** 带 HTTP 状态码的请求错误，便于调用方按状态码分支处理。 */
export class ApiError extends Error {
  /** HTTP 状态码；网络异常或超时时为 0。 */
  readonly status: number

  constructor(message: string, status: number) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

/** 请求参数。 */
export interface RequestOptions {
  /** HTTP 方法，默认 `GET`。 */
  method?: string
  /** 请求体，传入对象时自动序列化为 JSON。 */
  body?: unknown
  /** 是否附加 `Authorization` 头，默认 `true`。 */
  auth?: boolean
  /**
   * 收到 401 时是否触发登录失效处理（清空令牌并跳转登录页），默认 `true`。
   * 登录/注册接口自身的 401 表示凭据错误，应传 `false`。
   */
  handleUnauthorized?: boolean
}

/** 登录失效时的回调，由应用启动时注册，避免请求层直接依赖路由。 */
let unauthorizedHandler: (() => void) | null = null

/** 注册登录失效处理回调。 */
export function setUnauthorizedHandler(handler: () => void): void {
  unauthorizedHandler = handler
}

/** 拼装带超时控制的 `AbortSignal`。 */
function createTimeoutSignal(timeoutMs: number): { signal: AbortSignal; dispose: () => void } {
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), timeoutMs)
  return { signal: controller.signal, dispose: () => clearTimeout(timer) }
}

/** 解析响应体：JSON 按 JSON 解析，其余按文本读取，空响应返回 `null`。 */
async function readPayload(response: Response): Promise<unknown> {
  const contentType = response.headers.get('Content-Type') ?? ''
  if (contentType.includes('json')) {
    try {
      return await response.json()
    } catch {
      return null
    }
  }

  const text = await response.text()
  return text.length > 0 ? text : null
}

/** 从错误响应体中提取可读文案：优先字段级校验错误，其次 message/title/detail。 */
function extractErrorMessage(payload: unknown, status: number): string {
  if (typeof payload === 'string' && payload.trim().length > 0) {
    return payload
  }

  if (payload !== null && typeof payload === 'object') {
    const problem = payload as Record<string, unknown>

    const errors = problem.errors
    if (errors !== null && typeof errors === 'object') {
      const messages = Object.values(errors as Record<string, unknown>)
        .flatMap((value) => (Array.isArray(value) ? value : [value]))
        .filter((value): value is string => typeof value === 'string' && value.length > 0)
      if (messages.length > 0) {
        return messages.join('；')
      }
    }

    for (const field of ['message', 'detail', 'title'] as const) {
      const value = problem[field]
      if (typeof value === 'string' && value.length > 0) {
        return value
      }
    }
  }

  return `请求失败（HTTP ${status}）`
}

/**
 * 发起请求并返回解析后的响应体。
 *
 * @throws 网络异常、超时或非 2xx 响应时抛出 {@link ApiError}。
 */
export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const url = joinApiUrl(path)
  const { timeoutMs } = getAppConfig().api
  const { signal, dispose } = createTimeoutSignal(timeoutMs)

  const headers: Record<string, string> = { Accept: 'application/json' }
  if (options.body !== undefined) {
    headers['Content-Type'] = 'application/json'
  }

  const token = getToken()
  if (token !== null && options.auth !== false) {
    headers.Authorization = `Bearer ${token}`
  }

  let response: Response
  try {
    response = await fetch(url, {
      method: options.method ?? 'GET',
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal,
    })
  } catch (error) {
    if (error instanceof Error && error.name === 'AbortError') {
      throw new ApiError(`请求超时（${timeoutMs}ms）：${url}`, 0)
    }

    throw new ApiError(`网络异常，请确认后端服务已启动：${url}`, 0)
  } finally {
    dispose()
  }

  const payload = await readPayload(response)

  if (!response.ok) {
    if (response.status === 401 && options.handleUnauthorized !== false) {
      clearToken()
      unauthorizedHandler?.()
    }

    throw new ApiError(extractErrorMessage(payload, response.status), response.status)
  }

  return payload as T
}
