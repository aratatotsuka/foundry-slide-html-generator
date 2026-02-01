# Foundry Slide HTML Generator

PowerPoint用スライド（16:9 / 4:3）に変換する前段の **「スライドHTML」** を生成し、**フロントにはHTMLを返さずPNGプレビューのみ** を表示する Web アプリです。

- Frontend: TypeScript + React + MUI (Vite)
- Backend: .NET 8 (C#) Minimal API
- Azure AI Foundry 呼び出し: C# + REST API + `Azure.Identity` (`DefaultAzureCredential`)
- 画像プレビュー生成: Playwright (headless Chromium)

## 重要（生HTML非表示について）

「開発者ツールで生HTMLが見れない」を文字通り満たすため、**フロントにはHTML文字列を返しません**。バックエンドでHTMLを保存し、Playwrightでレンダリングした **PNGのみ** を返します。

ただし、`/api/jobs/{jobId}/result.html`（Download HTML）を有効化すると、ユーザーはHTMLを入手できます。これは要件と矛盾し得るため、デフォルトでは無効です。

## 構成

```
/seed-data            # 起動時にFoundryへアップロードしVector Store作成（file_search用）
/src/backend          # .NET 8 minimal api + Playwright + Foundry REST
/src/frontend         # React + TS + MUI
docker-compose.yml
```

## 主要API

- `POST /api/generate`
  - body: `{ prompt: string, aspect: "16:9"|"4:3", imageBase64?: string }`
  - resp: `{ jobId: string }`
- `GET /api/jobs/{jobId}`
  - resp: `{ status, step?, error?, previewPngUrl?, sources? }`
- `GET /api/jobs/{jobId}/preview.png`
  - resp: `image/png`（成功時のみ）
- `GET /api/jobs/{jobId}/result.html`（任意 / デフォルト無効）

## マルチエージェント（必須仕様）

バックエンドが以下を順に `/openai/responses` で呼び出して合成します（Connected Agentsは不使用）。

1. `agent_planner` (JSON)
2. `agent_web_research` (JSON, tool: `web_search_preview`)
3. `agent_file_research` (JSON, tool: `file_search` + Vector Store)
4. `agent_html_generator` (HTMLのみ)
5. `agent_validator` (JSON) → issuesがあれば最大2回 generator 修正ループ

起動時に `/agents` を使って「存在確認 → 無ければcreate → あればupdate」を行います。

## ローカル実行（Docker 推奨）

### 1) 前提
- Azureログイン（`DefaultAzureCredential` が通る状態）
  - 例: `az login`
  - 例: VS/VS Code の Azure アカウントでサインイン

Docker で動かす場合は、コンテナ内からも認証できる必要があります（推奨: `EnvironmentCredential`）。
例: `AZURE_TENANT_ID / AZURE_CLIENT_ID / AZURE_CLIENT_SECRET` を `docker compose` に渡してください。

### 2) 環境変数

最低限（必須）:

- `FOUNDRY_PROJECT_ENDPOINT`
  - 例: `https://{resource}.services.ai.azure.com/api/projects/{projectName}`
- `MODEL_DEPLOYMENT_NAME`

任意:
- `FOUNDRY_API_VERSION`（既定: `2025-11-15-preview`）
- `USE_AGENT_APPLICATION`（既定: `false`）
- `FOUNDRY_APPLICATION_ENDPOINT`（`USE_AGENT_APPLICATION=true` のとき）

Agent Application 切り替えについて:
- `USE_AGENT_APPLICATION=false` の場合: `FOUNDRY_PROJECT_ENDPOINT` をベースに `/openai/responses` を呼びます。
- `USE_AGENT_APPLICATION=true` の場合: `FOUNDRY_APPLICATION_ENDPOINT` をベースに `/openai/responses` を呼びます。
  - 起動時プロビジョニング（`/agents` と seed-data→Vector Store）は `FOUNDRY_PROJECT_ENDPOINT` 側で行います。

DefaultAzureCredential（Dockerでよく使う）:
- `AZURE_TENANT_ID`
- `AZURE_CLIENT_ID`
- `AZURE_CLIENT_SECRET`

### 3) 起動

```
docker compose up --build
```

- Frontend: `http://localhost:5173`
- Backend: `http://localhost:8080`

## ローカル実行（Dockerなし）

### Backend

PlaywrightのChromiumが必要です（初回のみ）。

```
cd src/backend/FoundrySlideHtmlGenerator.Backend
dotnet build
pwsh .\\bin\\Debug\\net8.0\\playwright.ps1 install chromium
dotnet run --launch-profile http
```

### Frontend

`src/frontend/.env` を作成:

```
VITE_API_BASE_URL=http://localhost:8080
```

起動:

```
cd src/frontend
npm install
npm run dev
```

## Vector Store（file_search）

- 起動時に `/seed-data` の `.md/.pdf/.txt` を Foundry へアップロードし、Vector Store を作成します。
- Vector Store ID は state store に保存します（開発はローカルJSON）。

State store 切り替え（任意）:
- `STATE_STORE=local`（既定）: `STATE_LOCAL_PATH=data/state.json`
- `STATE_STORE=appconfig`: `STATE_APPCONFIG_ENDPOINT=...`
- `STATE_STORE=keyvault`: `STATE_KEYVAULT_URI=...`

## Download HTML（任意）

デフォルト無効です。有効化する場合:

- `ALLOW_HTML_DOWNLOAD=true`
- （任意）`HTML_DOWNLOAD_API_KEY=...` を設定し、フロントから `X-Download-Key` を送る

## 注意点 / 制約

- クライアントにHTMLを送れば、DevTools等で隠せません（そのためデフォルトはPNGのみ）。
- `web_search_preview` / `file_search` はモデルやFoundry側の設定によって利用可否が変わる場合があります。
- Windows で `npm` が `os=linux` 設定になっている場合、Rollupのネイティブ依存が壊れます。
  - `~/.npmrc` の `os=linux` を外す、または `set npm_config_os=win32` で回避してください。
