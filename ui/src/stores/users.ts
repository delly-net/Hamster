/**
 * 用户管理状态：面向系统管理员，封装用户列表与激活/停用、重置链接、删除三类操作。
 *
 * 所有接口都需要管理员令牌并会在后端回查数据库鉴权，非管理员调用一律 403。
 */

import { ref } from 'vue'
import { defineStore } from 'pinia'
import { request } from '@/api/http'

/** 用户管理列表项（后端不回传任何凭据字段）。 */
export interface AdminUser {
  id: number
  username: string
  createdAt: string
  isAdmin: boolean
  isActive: boolean
}

/** 密码重置链接。 */
export interface ResetLink {
  /** 完整可复制的重置地址，已带用户名与令牌查询参数。 */
  resetUrl: string
  /** 链接过期时间（UTC，ISO 8601）。 */
  expiresAt: string
}

/** 管理端用户接口基址。 */
const ADMIN_USERS_PATH = '/api/admin/users'

export const useUsersStore = defineStore('users', () => {
  const users = ref<AdminUser[]>([])
  const loading = ref(false)

  /** 拉取用户列表。 */
  async function listUsers(): Promise<AdminUser[]> {
    loading.value = true
    try {
      const result = await request<AdminUser[]>(ADMIN_USERS_PATH)
      users.value = result
      return result
    } finally {
      loading.value = false
    }
  }

  /**
   * 启用或停用用户。
   *
   * @param id 目标用户 Id。
   * @param active 目标状态。
   */
  async function setActive(id: number, active: boolean): Promise<void> {
    await request<void>(`${ADMIN_USERS_PATH}/${id}/${active ? 'activate' : 'deactivate'}`, {
      method: 'POST',
    })
  }

  /**
   * 为指定用户生成密码重置链接（有效期 15 分钟、一次性）。
   * 重复调用会使先前生成的链接失效。
   */
  async function createResetLink(id: number): Promise<ResetLink> {
    return await request<ResetLink>(`${ADMIN_USERS_PATH}/${id}/reset-link`, { method: 'POST' })
  }

  /** 删除用户；同时使其已签发的令牌立即失效。 */
  async function deleteUser(id: number): Promise<void> {
    await request<void>(`${ADMIN_USERS_PATH}/${id}`, { method: 'DELETE' })
  }

  return { users, loading, listUsers, setActive, createResetLink, deleteUser }
})
