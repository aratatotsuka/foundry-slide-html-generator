import { useEffect, useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  CardHeader,
  Container,
  Divider,
  FormControl,
  FormControlLabel,
  FormLabel,
  LinearProgress,
  Link,
  List,
  ListItem,
  Radio,
  RadioGroup,
  Stack,
  Step,
  StepLabel,
  Stepper,
  TextField,
  Typography,
} from '@mui/material'
import { downloadHtml, generate, getJob, joinApiUrl } from './api/client'
import type { Aspect, JobStatusResponse } from './api/types'

const steps = ['Plan', 'Research(Web)', 'Research(File)', 'Generate HTML', 'Validate']

function getActiveStep(job: JobStatusResponse | null): number {
  if (!job) return 0
  if (job.status === 'succeeded') return steps.length
  const idx = job.step ? steps.indexOf(job.step) : -1
  return idx >= 0 ? idx : 0
}

export default function App() {
  const [prompt, setPrompt] = useState('')
  const [aspect, setAspect] = useState<Aspect>('16:9')
  const [imageDataUrl, setImageDataUrl] = useState<string | undefined>(undefined)
  const [imageName, setImageName] = useState<string | undefined>(undefined)

  const [jobId, setJobId] = useState<string | null>(null)
  const [job, setJob] = useState<JobStatusResponse | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const [downloadKey, setDownloadKey] = useState('')

  const activeStep = useMemo(() => getActiveStep(job), [job])
  const isRunning = job?.status === 'queued' || job?.status === 'running'

  useEffect(() => {
    if (!jobId) return
    if (job?.status === 'succeeded' || job?.status === 'failed') return

    let cancelled = false
    const poll = async () => {
      try {
        const res = await getJob(jobId)
        if (!cancelled) setJob(res)
      } catch (e) {
        if (!cancelled) setError((e as Error).message)
      }
    }

    poll()
    const handle = window.setInterval(poll, 1000)
    return () => {
      cancelled = true
      window.clearInterval(handle)
    }
  }, [jobId, job?.status])

  const previewSrc = useMemo(() => {
    if (!job?.previewPngUrl) return null
    return joinApiUrl(job.previewPngUrl)
  }, [job?.previewPngUrl])

  const onPickImage: React.ChangeEventHandler<HTMLInputElement> = async (e) => {
    const file = e.target.files?.[0]
    setError(null)

    if (!file) {
      setImageDataUrl(undefined)
      setImageName(undefined)
      return
    }

    if (file.type !== 'image/png' && file.type !== 'image/jpeg') {
      setError('画像は png / jpg のみ対応です。')
      e.target.value = ''
      return
    }

    if (file.size > 4 * 1024 * 1024) {
      setError('画像サイズが大きすぎます（最大 4MB）。')
      e.target.value = ''
      return
    }

    const reader = new FileReader()
    reader.onload = () => {
      setImageDataUrl(String(reader.result))
      setImageName(file.name)
    }
    reader.onerror = () => setError('画像の読み込みに失敗しました。')
    reader.readAsDataURL(file)
  }

  const onGenerate = async () => {
    setError(null)
    setBusy(true)
    setJob(null)
    setJobId(null)
    try {
      const res = await generate({ prompt, aspect, imageBase64: imageDataUrl })
      setJobId(res.jobId)
      setJob({ status: 'queued', step: undefined, error: undefined, previewPngUrl: undefined, sources: { urls: [], files: [] } })
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const onDownloadHtml = async () => {
    if (!jobId) return
    setError(null)
    try {
      await downloadHtml(jobId, downloadKey || undefined)
    } catch (e) {
      setError((e as Error).message)
    }
  }

  return (
    <Container maxWidth="md" sx={{ py: 4 }}>
      <Stack spacing={3}>
        <Stack spacing={1}>
          <Typography variant="h4" fontWeight={700}>
            Foundry Slide HTML Generator
          </Typography>
          <Typography variant="body2" color="text.secondary">
            生成結果は PNG プレビューのみ表示します（HTML文字列はフロントへ返しません）。
          </Typography>
        </Stack>

        {error && <Alert severity="error">{error}</Alert>}

        <Card>
          <CardHeader title="Input" />
          <CardContent>
            <Stack spacing={2}>
              <TextField
                label="Prompt"
                value={prompt}
                onChange={(e) => setPrompt(e.target.value)}
                multiline
                minRows={8}
                fullWidth
                placeholder="スライド化したい内容を入力…（フロントは生プロンプトのまま送信。比率などの制約はサーバー側で追記します）"
              />

              <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} alignItems={{ sm: 'center' }}>
                <Button variant="outlined" component="label">
                  画像アップロード（任意）
                  <input hidden type="file" accept="image/png,image/jpeg" onChange={onPickImage} />
                </Button>
                <Typography variant="body2" color="text.secondary">
                  {imageName ? imageName : '未選択'}
                </Typography>
                {imageName && (
                  <Button
                    variant="text"
                    onClick={() => {
                      setImageName(undefined)
                      setImageDataUrl(undefined)
                    }}
                  >
                    クリア
                  </Button>
                )}
              </Stack>

              <FormControl>
                <FormLabel>スライド比率</FormLabel>
                <RadioGroup row value={aspect} onChange={(e) => setAspect(e.target.value as Aspect)}>
                  <FormControlLabel value="16:9" control={<Radio />} label="16:9" />
                  <FormControlLabel value="4:3" control={<Radio />} label="4:3" />
                </RadioGroup>
              </FormControl>

              <Button
                variant="contained"
                size="large"
                onClick={onGenerate}
                disabled={busy || isRunning || prompt.trim().length === 0}
              >
                生成
              </Button>
            </Stack>
          </CardContent>
        </Card>

        <Card>
          <CardHeader title="Progress" subheader={jobId ? `jobId: ${jobId}` : undefined} />
          <CardContent>
            <Stack spacing={2}>
              <Stepper activeStep={activeStep} alternativeLabel>
                {steps.map((label) => (
                  <Step key={label}>
                    <StepLabel>{label}</StepLabel>
                  </Step>
                ))}
              </Stepper>

              {(isRunning || busy) && <LinearProgress />}

              {job?.status === 'failed' && (
                <Alert severity="error">
                  {job.error || 'ジョブが失敗しました。'}
                </Alert>
              )}

              {job?.status === 'succeeded' && (
                <Alert severity="success">完了しました。</Alert>
              )}
            </Stack>
          </CardContent>
        </Card>

        <Card>
          <CardHeader title="Preview (PNG)" />
          <CardContent>
            {!previewSrc && (
              <Typography variant="body2" color="text.secondary">
                まだ生成されていません。
              </Typography>
            )}
            {previewSrc && (
              <Box
                component="img"
                src={previewSrc}
                alt="preview"
                sx={{
                  width: '100%',
                  borderRadius: 1,
                  border: '1px solid',
                  borderColor: 'divider',
                }}
              />
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader title="Sources" />
          <CardContent>
            <Stack spacing={2}>
              <Stack spacing={1}>
                <Typography variant="subtitle2">URLs</Typography>
                {job?.sources?.urls?.length ? (
                  <List dense>
                    {job.sources.urls.map((u) => (
                      <ListItem key={u} sx={{ pl: 0 }}>
                        <Link href={u} target="_blank" rel="noreferrer" underline="hover">
                          {u}
                        </Link>
                      </ListItem>
                    ))}
                  </List>
                ) : (
                  <Typography variant="body2" color="text.secondary">
                    なし
                  </Typography>
                )}
              </Stack>

              <Divider />

              <Stack spacing={1}>
                <Typography variant="subtitle2">Files</Typography>
                {job?.sources?.files?.length ? (
                  <List dense>
                    {job.sources.files.map((f) => (
                      <ListItem key={f} sx={{ pl: 0 }}>
                        <Typography variant="body2">{f}</Typography>
                      </ListItem>
                    ))}
                  </List>
                ) : (
                  <Typography variant="body2" color="text.secondary">
                    なし
                  </Typography>
                )}
              </Stack>
            </Stack>
          </CardContent>
        </Card>

        <Card>
          <CardHeader title="Download HTML (Optional)" />
          <CardContent>
            <Stack spacing={2}>
              <Typography variant="body2" color="text.secondary">
                サーバー側で <code>ALLOW_HTML_DOWNLOAD=true</code> のときのみ利用できます（この時点でHTMLはユーザーに渡ります）。
              </Typography>

              <TextField
                label="X-Download-Key (optional)"
                value={downloadKey}
                onChange={(e) => setDownloadKey(e.target.value)}
                fullWidth
              />

              <Button variant="outlined" disabled={!jobId || job?.status !== 'succeeded'} onClick={onDownloadHtml}>
                Download HTML
              </Button>
            </Stack>
          </CardContent>
        </Card>
      </Stack>
    </Container>
  )
}
