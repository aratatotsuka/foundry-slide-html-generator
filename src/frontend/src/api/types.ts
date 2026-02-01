export type Aspect = '16:9' | '4:3'

export type JobStatus = 'queued' | 'running' | 'succeeded' | 'failed'

export interface GenerateRequest {
  prompt: string
  aspect: Aspect
  imageBase64?: string
}

export interface GenerateResponse {
  jobId: string
}

export interface JobSources {
  urls: string[]
  files: string[]
}

export interface JobStatusResponse {
  status: JobStatus
  step?: string
  error?: string
  previewPngUrl?: string
  sources?: JobSources
}

