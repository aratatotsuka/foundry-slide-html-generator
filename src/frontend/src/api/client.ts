import type { GenerateRequest, GenerateResponse, JobStatusResponse } from './types'

const API_BASE_URL = (import.meta.env.VITE_API_BASE_URL as string | undefined) ?? ''

export function joinApiUrl(path: string): string {
  if (!API_BASE_URL) return path
  return API_BASE_URL.replace(/\/$/, '') + path
}

async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(joinApiUrl(path), {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(init?.headers ?? {}),
    },
  })

  if (!res.ok) {
    const text = await res.text().catch(() => '')
    throw new Error(`${res.status} ${res.statusText}${text ? `: ${text}` : ''}`)
  }

  return (await res.json()) as T
}

export async function generate(req: GenerateRequest): Promise<GenerateResponse> {
  return fetchJson<GenerateResponse>('/api/generate', {
    method: 'POST',
    body: JSON.stringify(req),
  })
}

export async function getJob(jobId: string): Promise<JobStatusResponse> {
  return fetchJson<JobStatusResponse>(`/api/jobs/${jobId}`)
}

export async function downloadHtml(jobId: string, downloadKey?: string): Promise<void> {
  const res = await fetch(joinApiUrl(`/api/jobs/${jobId}/result.html`), {
    headers: {
      ...(downloadKey ? { 'X-Download-Key': downloadKey } : {}),
    },
  })

  if (!res.ok) {
    const text = await res.text().catch(() => '')
    throw new Error(`${res.status} ${res.statusText}${text ? `: ${text}` : ''}`)
  }

  const blob = await res.blob()
  const url = URL.createObjectURL(blob)
  try {
    const a = document.createElement('a')
    a.href = url
    a.download = `${jobId}.html`
    document.body.appendChild(a)
    a.click()
    a.remove()
  } finally {
    URL.revokeObjectURL(url)
  }
}

