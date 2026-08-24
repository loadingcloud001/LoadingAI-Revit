# Revit AI Assistant → Hermes / Kilo / Claude 級自主 Agent 計劃

**日期**: 2026-08-24（已更新：確認無現成 Revit 兼容方案，選定在現有 RevitAiPoc 底座上增強）
**目標**: 將現有 Revit AI Add-in（WebView2 窄面板 + AgentLoop + PluginLoader）升級為具備自主規劃/執行/恢復能力的 Agent，UI/UX 對標 Hermes/Kilo Code/Claude Code/Codex，單面板為主兼容 CLI，從 CLI 做起，可用性與易用性達到生產可用。

## 1. 已確認決策（來自訪談）

- **自主模式**: `Plan → Approve → Act`。用戶一句話 → Agent 生成帶 Todo 的 Plan 卡片 → 用戶批准（或逐個批准 L3）→ Agent 自主連調多個 tool/plugin 完成任務，L3 經 TaskDialog 二次確認。
- **UI 形態**: 保持 340px 單 Dockable Pane，不擴寬/不彈新窗口，但內部兼容 CLI。即「單面板增強」為容器，CLI 以輸入增強+可折疊終端形式嵌入。
- **CLI**: 兩者都要。1) Chat 輸入框內 `/slash` 命令 + `@mention` 上下文引用（首版） 2) 底部可折疊 `xterm.js` 終端模擬器（次版），兩者共享同一命令路由。
- **自主能力**: 上下文自取 + 記憶恢復 + 子 Agent 並行。Agent 需主動讀取選中/活動視圖/可見構件/警告，會話可持久化/回滾/重試，後期支持子 Agent 分解並行子任務。

## 2. 對標分析與復用策略

> **你的追問：50 個熱門 Agent 有無可直接兼容 Revit 的？答案：沒有。** 這 50 個全是「代碼/網頁 Agent」（VS Code 插件、CLI、Cloud、Browser），宿主是文件系統/終端/瀏覽器，無 Revit 進程、無 Revit API、無 ExternalEvent/Transaction/ElementId/族/視圖/警告等 BIM 概念。即使通過 OpenRouter 用同一模型，也缺少 Revit 側的工具與宿主橋。Autodesk 生態內也無現成開源 Revit Agent 可直接安裝。最務實路徑是「復用它們的 UI/CLI 範式與開源組件，復接到你現有的 RevitAiPoc 宿主上」，而非直接安裝使用。

- **Kilo Code**（#3，401B tokens，VS Code/JetBrains/CLI）：頂部 `Ask/Plan/Build/Auto` 段控、Plan Todo 清單、工具時間線（可折疊卡片）、@文件/符號引用、Diff 預覽。**本項目已復用**：mode switcher 與 Plan 卡；**待補齊**：Todo 狀態機、@提及、工具卡片統一為 Kilo 時間線。Kilo 開源可抄其 `Command/Palette + Timeline` 組件，但其宿主是 VS Code Extension Host，無法直接在 Revit WebView2 內運行。
- **Claude Code**（#2，685B，Anthropic CLI）：CLI 優先、`/command` 斜槓、連續自主執行、可中斷/恢復、子 Agent。復用思路：輸入框解析 `/` 與 `@`，AgentLoop 支持可中斷的 64 輪循環 + checkpoint。Claude Code 為封閉 CLI，不能嵌入 Revit，但其「斜槓+子 Agent」交互可 1:1 模仿。
- **Hermes Agent**（#1，2.31T，Nous Research，開源自進化）：持久記憶、40+ 工具、定時自動化、子 Agent、skill 自生成。復用思路：其「記憶跨會話 + skill 自生成」對應我們的 `CheckpointStore` + `ToolRegistry` 自選插件。Hermes 為通用桌面 Agent，無 Revit 工具，需重寫 Revit 側工具才能用。
- **Codex / Cline / Roo 等**：同理均為文件/終端宿主，無 Revit 綁定。

**結論**：沒有零改造成本的現成方案。Kilo/Hermes 等最值得「抄設計與抄組件」，但宿主必須是 `RevitAiPoc` 現有橋（`RevitBridge.cs + HostPage.xaml.cs + WebView2`）。若硬要 fork 其一（如 fork `Kilo Code` 或 `Hermes` 倉庫再加 Revit adapter），成本反而高於在現有已打通 Revit 的代碼庫上增強。

**不自研原則**: UI 基建已用 Tailwind+shadcn+lucide+open-model-selector，新增僅引入 `xterm.js` + `xterm-addon-fit`（MIT）與 `@ mention` 下拉（復用 shadcn Command），不引入重型編輯器。

## 3. 現狀與差距

- **現有**: `src/RevitAiPoc/Llm/AgentLoop.cs:42-212` 64 輪 + ToolSelector Top-20 + HistoryTrimmer 40 條 + Mode 過濾；`HostPage.xaml.cs:227-529` WebView2 橋；`web/src/App.tsx:80-778` 單面板 Chat + Plan/Approval/Reasoning 卡 + 附件壓縮；`ProviderSettings.cs` 多提供商；`Plugins/Core` 動態加載。
- **差距**:
  1. 無 `/` 命令路由與 `@` 上下文，輸入僅為自由文本
  2. Plan 為事後解析文本（`PlanParser.cs`），非 Agent 結構化輸出，無 Todo 狀態同步（pending→running→done）
  3. 工具時間線與 Approval 分離，L3 無真正阻塞（已 noted），無 Diff/預覽（如參數修改前後對比）
  4. 無終端面板，日誌/錯誤僅散在消息流
  5. 無自動上下文注入（當前選中/視圖需用戶口述）
  6. 無會話持久化/回滾/重試，無子 Agent

## 4. 目標架構

```
[Web Chat Input] --"/cmd + @ctx + text + attachments"--> [CLI Router (web)] --"send {text, attachments, mode, slash, mentions}"--> [HostPage]
   |                                                                               |
   |<-- Suggestion/Reasoning/Plan/Todo/Tool Timeline <-- [AgentLoop (Planner→Executor)] <--> [ToolRegistry + PluginLoader]
   |                                                                               |
   +-- [xterm Terminal (collapsible)] <-- log stream (tool_call/result, audit) --+
   |                                                                               |
   +-- [Context Provider] -- auto inject selection/view/warnings --> prompt   [Checkpoint Store] --> resume/rollback
```

**核心改動點**:
- `web/src/cli/` 新模塊：`slashCommands.ts`（命令表+參數補全）、`mentions.ts`（@搜選中/視圖/族/警告，提供列表）、`inputParser.ts`（將原始輸入解析為 {slash, mentions, cleanText}）
- `web/src/components/Terminal.tsx`：基於 xterm.js 的折疊面板，接收 `tool_call/result/error` 流，可輸入下一條 `/` 命令
- `src/RevitAiPoc/Llm/AgentLoop.cs`：拆為 `Planner`（Plan 模式只 L1，輸出結構化 Plan）與 `Executor`（Build 模式執行 Todo），增加 `TodoItem {id,title,tool,args,status,error}` 狀態機，通過 `onTodoUpdate` 回推前端；增加 `subAgent` 佔位（Executor 可 spawn 子 AgentLoop 並行查多視圖）
- `src/RevitAiPoc/Revit/ContextCollector.cs` 新類：`Collect()` 返回 `{selection, activeView, visibleCount, warnings}` 供每次 `RunAsync` 前自動注入 system prompt 尾部
- `src/RevitAiPoc/Llm/CheckpointStore.cs` 新類：保存 `history + todos + mode` 到 `%AppData%\RevitAiPoc\checkpoints\{id}.json`，支持 `list/resume/rollback`
- `src/RevitAiPoc/Ui/HostPage.xaml.cs`：`send` 增加 `slash, mentions` 解析；`ExecuteToolAsync` 增加真正阻塞式 `TaskCompletionSource` 等待 `approveTool`（已計劃第 2 階段），但首版保持 TaskDialog 兜底

## 5. UI/UX 詳細設計（單面板 + CLI）

**布局（340px 內，從上到下）**:
1. **Header**: Provider 膠囊（現有）+ Mode 段控（Ask/Plan/Build/Auto，已有）+ 右上角 Terminal 開關（`_Terminal` 圖標）
2. **Context Bar**（新增，高度 22px，可折疊）：顯示 `已選 3 | 活動視圖: Level 1 | 可見 412 | 警告 2`，點擊展開詳情，@時高亮
3. **Message Timeline**（現有，優化）: 統一為 Kilo 時間線樣式
   - `user` 氣泡（含附件縮略）
   - `reasoning` 淡黃可折疊
   - `plan` 卡（標題+描述+ Todo 清單，狀態點_pending灰/running藍脈衝/done綠/error紅，底部「批准並執行/調整」）
   - `tool_call` 藍卡（Tool 名 + 參數預覽，可展開 JSON）
   - `tool_result` 綠/紅卡（結果截斷 400 字，可複製）
   - `approval` 琥珀卡（L3 待批，批准/拒絕）
   - `assistant` Markdown 渲染
   - `suggestions` 橫向晶片
4. **Terminal**（新增，可折疊，默認收起）：xterm 高 120px，顯示同樣流但為純文本 log，支持用戶在終端繼續輸入 `/clear /resume`，與 Chat 輸入同步
5. **Composer**（現有，增強）:
   - 輸入框支持 `/` 觸發 Command Palette（shadc Command 下拉，含 `/plan /build /ask /clear /help /export /isolate` 等），`@` 觸發 Mention 下拉（@selection @activeView @warnings @levels 等）
   - 占位文案按 mode 變化（已做），新增 `/` 提示行 `輸入 / 查看命令，@ 引用上下文`
   - 發送按鈕保持，添加 `↑` 歷史回填

**易用性細節**:
- `/` 與 `@` 均支持鍵盤上下選擇、Tab 補全、Esc 關閉
- Plan 批准後自動切 Build 並回放「按剛才的計劃執行」已實現，保留
- 工具執行時 composer 禁止輸入但終端可輸入 `stop`，與現有 `isRunning` 一致
- 錯誤與空狀態提供可操作建議（如「需 Key → 打開管理」已做，補「上下文為空 → 先選幾個構件」）

## 6. CLI 命令表（首版）

| 命令 | 參數 | 說明 | 對應工具示例 |
|---|---|---|---|
| `/plan <目標>` | - | 強制 Plan 模式（等同點 Plan） | 僅 L1 |
| `/build <目標>` | - | 強制 Build 模式 | 全量 |
| `/ask <問題>` | - | 強制 Ask 模式 | L1 |
| `/clear` | - | 清空會話 | `clear` |
| `/help` | - | 列命令 | 本地 |
| `/export <fmt>` | `ifc/dwg/pdf` | 快捷匯出 | `export_*` |
| `/isolate [cat]` | 可選類型 | 隔離當前選中 | `isolate_in_view` |
| `/hide` | - | 隱藏選中 | `hide_in_view` |
| `/resume [id]` | 可選 | 恢復上次會話 | CheckpointStore |

`@` 提及：`@selection` `@activeView` `@visible` `@warnings` `@levels` `@sheets`，解析後將對應 ID/名注入 `cleanText` 尾部 `[context: ...]`，同時前端展示為藍色晶片

## 7. 任務清單（按序，給實施 Agent）

**Phase 1 — CLI 基礎（單面板可用）**
1. `web/src/cli/slashCommands.ts` + `mentions.ts` + `inputParser.ts` 實現；`web/src/App.tsx` Composer 增加 `/` 與 `@` 下拉、解析、晶片展示；`bridge.ts` `ToHost` 增加 `slash?:string, mentions?:string[]`
2. `src/RevitAiPoc/Revit/ContextCollector.cs` 實現 `Collect()`；`HostPage.xaml.cs` `send` 注入上下文到 `userMsg`（如 `FromMultimodal` 旁附加上下文文本）
3. `src/RevitAiPoc/Llm/AgentLoop.cs` 增加 `TodoItem/TodoUpdate` 與 `onTodoUpdate` 回調，Plan 模式改為流式更新 Todo（`pending→running→done`），前端 Plan 卡訂閱更新
4. 統一工具時間線樣式與 Approval 真阻塞（`TaskCompletionSource` 等待 `approveTool`，超時 180s，拒絕返回 `Cancelled by user`）
5. 補測試：`CliParserTests.cs`（`/plan`/`@selection` 解析）、`ContextCollectorTests`  mock、已有 `ToolSelector/ChatHistoryTrimmer/LlmJson` 保持通過

**Phase 2 — 終端與恢復**
6. `web/src/components/Terminal.tsx` 引入 `xterm@5 + xterm-addon-fit`，折疊面板，接收 `tool_call/result` 流；Header 增加開關；與 Chat 輸入雙向同步
7. `src/RevitAiPoc/Llm/CheckpointStore.cs` 實現存/列/讀/刪；`HostPage` 增加 `listCheckpoints/resume/rollback` 三個 `ToHost` 類型；前端增加 `/resume` 與歷史下拉
8. 優化流體驗：`OpenAiCompatibleClient.cs` 的 `onDelta` 與 `ToolSelector` 已有流，確保 Todo 與 tool 卡在 streaming 期間增量更新

**Phase 3 — 子 Agent（可選，視前兩階段後決定是否納入首版）**
9. `AgentLoop.SpawnSubAgent(task)` 並行執行多個只讀查詢（如「統計各樓層牆」拆為每樓層一子 Agent），結果匯總回主 Agent；前端以子卡展示

**每階段驗證**:
- `npm run build`（vite 需 < 5s，gzip JS < 180KB 增量）
- `dotnet test -c Release` 保持 ≥31 通過，新增測試通過
- 手動：Revit 新建空模型，依次驗 `/plan 統計牆 → 批准 → 工具時間線流式`、`@selection 隔離`、`/clear`、終端開關、恢復

## 8. 數據流與失敗模式

- **數據流**: 用戶輸入 → CLI 解析（slash/mentions/cleanText+attachments+mode）→ HostPage 組 `ChatMessage`（多模態+上下文）→ AgentLoop（Planner/Executor + ToolSelector Top-20）→ OpenAiCompatibleClient（Responses/OpenAI）→ tool 循環（HostPage.ExecuteToolAsync → RevitBridge → ToolEntry.Handler，L2/L3 寫審計）→ 回推 timeline/terminal/plan/todo
- **失敗**:
  - `/` 未命中：不攔截，按普通文本送模型，前端提示「未知命令，用 /help」
  - `@` 引用空（如無選中）：注入 `[context: selection empty]`，模型可提示用戶先選擇
  - 工具參數非法：`LlmJson.NormalizeToolArguments` 兜底 `{}`，`ToolDiagnostics.DescribeMissing` 給最接近名
  - L3 拒絕/超時：返回 `Cancelled by user.`，Todo 標 error，前端可重試
  - Revit ExternalEvent 被拒：`RevitBridge` 已有 `bridge.raise_denied` 日誌與提示
  - 歷史過長：`ChatHistoryTrimmer` 截 2000 字 + 僅保留最新一張圖，已實現

## 9. 風險與緩解

- **WebView2 窄面板放不下終端**: 緩解：終端折疊默認收起，高度 120px，可拖拽調高；窄於 360px 時隱藏圖標文字僅留圖標
- **xterm 增加包體**: 緩解：動態 `import('xterm')` 僅在首次展開時加載，保持首屏 < 140KB gzip
- **子 Agent 增加 LLM 成本**: 緩解：Phase 3 設開關，默認關閉，僅 Build 模式且任務含「各/批量/並行」關鍵詞時自動啟用
- **上下文注入膨脹 prompt**: 緩解：ContextCollector 僅注入摘要（數量+名+前 3 條警告），詳情按需工具調用

## 10. 不做（Out of Scope）

- 擴寬 Revit 面板或彈獨立窗口（保持單面板）
- 文件系統級 Agent（如改 .rvt 文件本身以外的代碼庫 Collab）
- 自動安裝遠程插件（僅貨架展示+開關，已有）
- 直接安裝/嵌入上述 50 個 Agent 的完整宿主（如把 VS Code 插件塞進 Revit）—— 因 Revit 是獨立進程與 API 模型，此路徑不成立

## 11. 驗收標準

- 用戶可在輸入框輸入 `/plan 批次標註所有房間` → 看到結構化 Plan Todo → 點批准 → 看到工具時間線逐項 running→done，無需再手動干預（除 L3）
- 輸入 `@selection 幫我隱藏` 能正確引用當前選中並執行
- 終端可開合，內容與時間線一致，支持 `stop` 中斷
- 會話可 `/clear` 後 `/resume` 恢復
- `dotnet test` 與 `npm run build` 通過，已部署到 `%AppData%\Autodesk\Revit\Addins\2027\RevitAiPoc`

## 12. 開放問題

- 是否需在 Phase 1 同步加入 Diff 預覽（參數修改前的值 vs 新值）？建議首版先以 `tool_result` 文本呈現，二版再做結構化 Diff。
