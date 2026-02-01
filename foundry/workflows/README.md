# Foundry Portal Workflow (見えるWorkflow)

このフォルダの `*.workflow.yaml` は **Microsoft Foundry の「Workflows」画面に貼り付けて作成できる** Declarative Workflow 定義です。

注意: Backend の `USE_WORKFLOWS=true` は **ローカル(in-process)でワークフローを実行するだけ**で、Foundry ポータルに Workflow リソースを自動作成しません。
また、この実行は Foundry 側の「Workflows > Traces/Runs」に紐づきません（ポータルの Workflow 自体が実行されないため）。

Backend から Foundry ポータルの Workflow を実行し、ポータル側の Traces/Runs にも残したい場合は `USE_FOUNDRY_WORKFLOW=true` を使います。

## 作り方 (ポータルに表示させる)

1. Foundry プロジェクトに以下の Agent が存在することを確認
   - `agent-planner`
   - `agent-html-generator`
   - `agent-validator`
2. Foundry ポータル → 対象 Project → `Workflows` → `Create workflow`
3. YAML 表示に切り替えて `slide-html-generator.workflow.yaml` の内容を貼り付け
4. 保存（slide-html-generator）すると Workflows 一覧に表示されます

## 実行

- この Workflow は `OnConversationStart` トリガーです（ポータルの YAML 検証でも `Manual` は受け付けられません）
- Workflows のテスト/実行画面で会話を開始し、最初のユーザーメッセージにプロンプトを書いてください
  - 例: `4:3 で、社内ブランドガイドに沿った「四半期レビュー」スライドを1枚作って`
  - メッセージに `4:3` が含まれる場合は 4:3、それ以外は 16:9 として扱います

## Backend からポータルWorkflowを使う (Traces/Runs に残す)

Backend の環境変数で Workflow 実行モードを有効化します。

```
USE_FOUNDRY_WORKFLOW=true
FOUNDRY_WORKFLOW_NAME=slide-html-generator
```

このモードは Foundry 側で作成済みの Workflow（Agent: workflow）を `FOUNDRY_WORKFLOW_NAME` で指定し、`/openai/responses` の `agent` 参照として実行します。
この Workflow は Assistants API (`/assistants`) には出てきません（Agent として管理されます）。
