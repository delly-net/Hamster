/// <reference types="vite/client" />

/**
 * 由 `vite.config.ts` 的 `define` 在构建时注入，取自 `package.json` 的 `version`。
 * 仅在应用代码中可读，测试等未经过 Vite 转换的场景取不到。
 */
declare const __APP_VERSION__: string
