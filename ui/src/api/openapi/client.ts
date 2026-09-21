/**
 * OpenAPI 文档拉取与调试请求发送。
 *
 * 后端基址来自运行时配置 `public/conf/setting.json`，源码中不硬编码域名。
 */

import { getAppConfig, joinApiUrl } from '@/config/appConfig'
import { HTTP_METHODS } from './types'
import type {
  HttpMethod,
  InvokeResult,
  OpenApiDocument,
  OpenApiPathItem,
  OperationEntry,
} from './types'

/** 文档不可用（后端未以 Development 环境启动）时抛出的错误标识。 */
export const DOC_UNAVAILABLE_MESSAGE =
  '接口文档不可用。该文档仅在开发环境暴露，请确认后端以 Development 环境启动，且基址配置正确。'

/** 拼装带超时控制的 `AbortSignal`。 */
function createTimeoutSignal(timeoutMs: number): { signal: AbortSignal; dispose: () => void } {
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), timeoutMs)
  return { signal: controller.signal, dispose: () => clearTimeout(timer) }
}

/** 当前文档地址，供页面回显与排障提示使用。 */
export function getOpenApiDocUrl(): string {
  return joinApiUrl(getAppConfig().api.openApiDocPath)
}

/**
 * 拉取并解析 OpenAPI 文档。
 *
 * @throws 文档不可用、网络异常或 JSON 解析失败时抛出带中文说明的 `Error`。
 */
export async function fetchOpenApiDocument(): Promise<OpenApiDocument> {
  const url = getOpenApiDocUrl()
  const { timeoutMs } = getAppConfig().api
  const { signal, dispose } = createTimeoutSignal(timeoutMs)

  try {
    const response = await fetch(url, { signal, headers: { Accept: 'application/json' } })

    if (response.status === 404) {
      throw new Error(DOC_UNAVAILABLE_MESSAGE)
    }

    if (!response.ok) {
      throw new Error(`拉取接口文档失败：${url} 返回 HTTP ${response.status} ${response.statusText}`)
    }

    return (await response.json()) as OpenApiDocument
  } catch (error) {
    if (error instanceof Error && error.name === 'AbortError') {
      throw new Error(`拉取接口文档超时（${timeoutMs}ms）：${url}`)
    }

    if (error instanceof Error && (error.message.startsWith('拉取接口文档失败') || error.message === DOC_UNAVAILABLE_MESSAGE)) {
      throw error
    }

    // 其余情况通常是后端未启动或跨域被拒
    throw new Error(`无法连接后端服务：${url}。请确认后端已启动，且允许来自当前页面的跨域请求。`)
  } finally {
    dispose()
  }
}

/** 把文档中的路径项展开成扁平的操作条目列表，便于清单渲染与筛选。 */
export function toOperationEntries(doc: OpenApiDocument | null): OperationEntry[] {
  const entries: OperationEntry[] = []
  const paths = doc?.paths ?? {}

  for (const [path, item] of Object.entries(paths)) {
    for (const method of HTTP_METHODS) {
      const operation = (item as OpenApiPathItem)[method]
      if (!operation) {
        continue
      }

      entries.push({
        key: `${method} ${path}`,
        method,
        path,
        operation,
        tag: operation.tags?.[0] ?? '默认分组',
      })
    }
  }

  return entries
}

/** 构建查询串，忽略空值。 */
function buildQueryString(values: Record<string, string>): string {
  const params = new URLSearchParams()

  for (const [key, value] of Object.entries(values)) {
    if (value !== '') {
      params.append(key, value)
    }
  }

  const query = params.toString()
  return query ? `?${query}` : ''
}

/** 发送调试请求所需的输入。 */
export interface InvokeOptions {
  method: HttpMethod
  /** 文档中的路径模板，例如 `/api/sample/accounts/{id}`。 */
  path: string
  /** 路径参数名 → 取值。 */
  pathValues: Record<string, string>
  /** 查询参数名 → 取值。 */
  queryValues: Record<string, string>
  /** 请求头，值等于空串的项会被跳过。 */
  headers: Record<string, string>
  /** 请求体文本；空串表示不发送请求体。 */
  body: string
}

/**
 * 按调试面板填写的内容发送一次真实请求。
 *
 * @returns 状态码、耗时、响应头与响应体；响应体可解析为 JSON 时同时返回解析结果。
 * @throws 路径参数缺失、网络异常或超时时抛出带中文说明的 `Error`。
 */
export async function invokeOperation(options: InvokeOptions): Promise<InvokeResult> {
  // 路径参数替换：未填的占位符必须提前拦截，否则会请求到字面量 `{id}` 路径
  const missing = Object.entries(options.pathValues)
    .filter(([, value]) => value === '')
    .map(([name]) => name)

  if (missing.length > 0) {
    throw new Error(`请先填写必填路径参数：${missing.join('、')}`)
  }

  let resolvedPath = options.path
  for (const [name, value] of Object.entries(options.pathValues)) {
    resolvedPath = resolvedPath.replace(`{${name}}`, encodeURIComponent(value))
  }

  const url = `${joinApiUrl(resolvedPath)}${buildQueryString(options.queryValues)}`

  const headers = new Headers()
  for (const [name, value] of Object.entries(options.headers)) {
    if (value !== '') {
      headers.set(name, value)
    }
  }

  const hasBody = options.body.trim() !== ''
  if (hasBody && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }

  const { timeoutMs } = getAppConfig().api
  const { signal, dispose } = createTimeoutSignal(timeoutMs)
  const startedAt = performance.now()

  try {
    const response = await fetch(url, {
      method: options.method.toUpperCase(),
      headers,
      body: hasBody ? options.body : undefined,
      signal,
    })

    const bodyText = await response.text()

    let bodyJson: unknown = null
    try {
      bodyJson = bodyText ? JSON.parse(bodyText) : null
    } catch {
      // 非 JSON 响应（HTML 错误页、纯文本等）保留原文展示
    }

    return {
      status: response.status,
      statusText: response.statusText,
      durationMs: Math.round(performance.now() - startedAt),
      url,
      headers: [...response.headers.entries()],
      bodyText,
      bodyJson,
    }
  } catch (error) {
    if (error instanceof Error && error.name === 'AbortError') {
      throw new Error(`请求超时（${timeoutMs}ms）：${url}`)
    }

    throw new Error(`请求失败：${url}。请确认后端已启动，且允许来自当前页面的跨域请求。`)
  } finally {
    dispose()
  }
}
