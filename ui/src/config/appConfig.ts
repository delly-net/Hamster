/**
 * 运行时配置：从 `public/conf/setting.json` 读取。
 *
 * 放在 `public/` 下的资源会被 Vite 原样拷贝到构建产物，因此部署后运维可直接
 * 修改该文件调整后端地址，无需重新构建前端。加载失败时降级为默认值，不阻断启动。
 */

/** 后端接口相关配置。 */
export interface ApiConfig {
  /** 后端服务基址，例如 `http://localhost:5004`（末尾斜杠会被忽略）。 */
  baseUrl: string
  /** OpenAPI 文档路径，相对 `baseUrl`。 */
  openApiDocPath: string
  /** 调试请求与文档拉取的超时时间（毫秒）。 */
  timeoutMs: number
}

/** 应用级运行时配置。 */
export interface AppConfig {
  api: ApiConfig
}

/** 配置文件地址（`public` 根目录下的相对路径）。 */
const CONFIG_URL = '/conf/setting.json'

/** 配置文件缺失或字段缺省时的兜底值。 */
export const DEFAULT_APP_CONFIG: AppConfig = {
  api: {
    baseUrl: 'http://localhost:5004',
    openApiDocPath: '/openapi/v1.json',
    timeoutMs: 15000,
  },
}

/** 去除末尾斜杠，避免拼接出 `//` 形式的地址。 */
function trimTrailingSlash(value: string): string {
  return value.replace(/\/+$/, '')
}

/** 把外部读入的原始对象合并到默认值之上，缺字段时保持兜底。 */
function mergeConfig(raw: Partial<AppConfig> | null | undefined): AppConfig {
  return {
    api: {
      ...DEFAULT_APP_CONFIG.api,
      ...raw?.api,
      baseUrl: trimTrailingSlash(raw?.api?.baseUrl ?? DEFAULT_APP_CONFIG.api.baseUrl),
    },
  }
}

let cached: AppConfig | null = null

/**
 * 读取运行时配置。重复调用只会在首次真正发起请求。
 *
 * @returns 合并兜底值后的配置对象。
 */
export async function loadAppConfig(): Promise<AppConfig> {
  if (cached) {
    return cached
  }

  try {
    const response = await fetch(CONFIG_URL, { cache: 'no-cache' })
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`)
    }

    cached = mergeConfig((await response.json()) as Partial<AppConfig>)
  } catch (error) {
    console.warn(`[Hamster] 读取运行时配置 ${CONFIG_URL} 失败，已回退默认值：`, error)
    cached = mergeConfig(null)
  }

  return cached
}

/** 同步获取已加载的配置；未加载时返回默认值。 */
export function getAppConfig(): AppConfig {
  return cached ?? DEFAULT_APP_CONFIG
}

/** 拼接 `baseUrl` 与路径，保证中间恰好一个斜杠。 */
export function joinApiUrl(path: string): string {
  const { baseUrl } = getAppConfig().api
  return `${baseUrl}/${path.replace(/^\/+/, '')}`
}
