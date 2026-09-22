/**
 * 产品静态信息：供「用户设置」页的关于区块展示。
 *
 * 版本号**不在此硬编码**——它由 `vite.config.ts` 在构建时从 `package.json` 注入
 * `__APP_VERSION__`（单一数据源），此处只做转出。其余为产品定位与授权的固定文案，
 * 需要调整时集中改这一个文件。
 */

/** 产品名：与 `index.html` 的标题、header 品牌区保持一致。 */
export const PRODUCT_NAME = '仓鼠理财管家'

/** 一句话简介：取自项目 README 的产品定位。 */
export const PRODUCT_DESCRIPTION = '一款专注家庭记账理财的软件'

/** 版本号，构建时由 Vite 注入。 */
export const APP_VERSION: string = __APP_VERSION__

/** 开源许可标识。 */
export const LICENSE_NAME = 'MIT License'

/** 版权所有者：与仓库 LICENSE 中的署名一致。 */
export const COPYRIGHT_HOLDER = 'delly.net'

/** 主要技术栈，按前后端两行展示。 */
export const TECH_STACK: ReadonlyArray<{ label: string; value: string }> = [
  { label: '前端', value: 'Vue 3.5 · TypeScript · Vite · Pinia' },
  { label: '后端', value: 'ASP.NET Core 10 · SqlSugar · SQLite / PostgreSQL' },
]
