# TASK: AI Chat Button in Text Editor Ribbon, Built-in Browser SNDL Chrome Extension & NVIDIA NIM Model Upgrades (Gemma 4, DeepSeek V4.1 Flash, Nemotron 3 Embed)

Repository: `https://github.com/etb190/Avalanche`  
Target Files:
- `MainWindow.xaml`
- `MainWindow.xaml.cs`
- `Shell/SettingsPanel.cs`
- `Controls/WebBrowserControl.xaml.cs`
- `Features/AI/AiSurfaceModels.cs`
- `Features/AI/AiSettingsViewModel.cs`
- `Features/AI/OllamaEmbeddingClient.cs`
- `Strings/en-US.xaml`

---

## 1. Text Editor Ribbon: Add AI Chat Button on Far Right (Consistent with PDF & Browser Modes)

### Problem:
In PDF Editor mode, the ribbon toolbar features the AI Chat button (`AiChatBtn`) on the far right. In Browser mode, the far-right container hosts summary/chat actions. However, when switching to **Text Editor mode**, `RightContainer` is collapsed in `ApplyEditorToolbarFace(leads: true)`, leaving no AI Chat button on the ribbon toolbar. The user cannot quickly toggle the AI Sidechat while working in the Text Editor.

### Solution:
1. In `MainWindow.xaml`:
   - Under the top ribbon's right-aligned action area (beside `BrowserSumBar`), add an `EditorSumBar` (or a shared far-right container) containing an AI Chat button for the Text Editor:
     ```xml
     <StackPanel x:Name="EditorSumBar" Orientation="Horizontal"
                 HorizontalAlignment="Right" Visibility="Collapsed">
         <Button x:Name="EditorAiChatBtn" Content="&#xE8BD;" Style="{StaticResource ToolbarButton}"
                 Click="AiChatBtn_Click" ToolTip="{DynamicResource Str_TT_AiChat}"/>
     </StackPanel>
     ```
   - Also ensure `BrowserSumBar` includes both `WebSumBtn` and `WebAiChatBtn` wired to `AiChatBtn_Click`:
     ```xml
     <StackPanel x:Name="BrowserSumBar" Orientation="Horizontal"
                 HorizontalAlignment="Right" Visibility="Collapsed">
         <Button x:Name="WebSumBtn" Content="&#xE8A5;" Style="{StaticResource ToolbarButton}"
                 Click="WebSumBtn_Click" ToolTip="{DynamicResource Str_TT_WebSummarize}"
                 Visibility="Collapsed"/>
         <Button x:Name="WebAiChatBtn" Content="&#xE8BD;" Style="{StaticResource ToolbarButton}"
                 Click="AiChatBtn_Click" ToolTip="{DynamicResource Str_TT_AiChat}"/>
     </StackPanel>
     ```
2. In `Shell/SettingsPanel.cs`:
   - In `ApplyEditorToolbarFace(bool leads)`:
     When `leads == true`:
     - Show `EditorSumBar.Visibility = Visibility.Visible;` (displaying `EditorAiChatBtn` on the far right of the editor ribbon).
     When `leads == false`:
     - Collapse `EditorSumBar.Visibility = Visibility.Collapsed;`.
   - In `ApplyBrowserToolbarFace(bool leads)`:
     When `leads == true`:
     - Ensure both `WebSumBtn` and `WebAiChatBtn` are visible inside `BrowserSumBar`.
     When `leads == false`:
     - Collapse `BrowserSumBar.Visibility = Visibility.Collapsed;`.

---

## 2. Web Browser: Install & Load SNDL Chrome Extension (`ghbpooidfbjigoogmpjemckcpabalfpb`) into Built-in WebView2 Browser

### Problem:
The user wants to add the **SNDL** Chrome Web Store extension to Avalanche's built-in browser component:
- Store Link: `https://chromewebstore.google.com/detail/sndl/ghbpooidfbjigoogmpjemckcpabalfpb`
- Chrome Web Store Extension ID: `ghbpooidfbjigoogmpjemckcpabalfpb`

Because WebView2's browser engine is embedded within WPF, standard inline Chrome Web Store "Add to Chrome" button interactions fail with "Download interrupted" since Google's store installer expects the standalone desktop Chrome application.

### Architecture & Mechanism:
Avalanche solves this via its **Store Riders** pipeline in `Controls/WebBrowserControl.xaml.cs` (which already powers AdGuard, Free Download Manager, and Free VPN):
1. **Official Update Crate Fetching:**
   `StoreCrxUrl(extId)` issues an HTTP GET to Google's official Chrome extension update service:
   `https://update.googleapis.com/service/update2/crx?response=redirect&acceptformat=crx2,crx3&x=id%3D{extId}%26uc&prodversion=131.0.0.0`
   This delivers the verified, genuine CRX3 archive directly from Google.
2. **Envelope Peeling & Zip Extraction:**
   `UnpackCrx(byte[] crx, string targetDir)` verifies the 16-byte CRX3 header (`Cr24`), parses the version and protobuf header length, and extracts the payload zip into `%LocalAppData%\Avalanche\WebView2Data\extensions\sndl`.
3. **Profile Registration:**
   `LoadPendingExtensions(CoreWebView2 core)` scans the extensions folder, verifies `manifest.json`, and registers the unpacked extension with the active profile via `core.Profile.AddBrowserExtensionAsync(dir)`.
4. **Offline / Inbox Fallback:**
   Users can also drop an existing `sndl.crx` into `WebView2Data/extensions/inbox` or place an unpacked folder with `manifest.json` into `WebView2Data/extensions/sndl`, which is immediately unpacked and registered on launch.

### Implementation:
In `Controls/WebBrowserControl.xaml.cs`:
1. Add the SNDL extension ID and target directory constants alongside the existing store riders:
   ```csharp
   private const string SndlExtId = "ghbpooidfbjigoogmpjemckcpabalfpb";
   private static string SndlExtDir => Path.Combine(ExtensionsRoot, "sndl");
   ```
2. Register SNDL in the `EnsureStoreRiders()` rider array:
   ```csharp
   (string Url, string Dir)[] riders =
   {
       (StoreCrxUrl(AdGuardExtId), AdGuardExtDir),
       (StoreCrxUrl(FreeDownloadManagerExtId), FdmExtDir),
       (StoreCrxUrl(FreeVpnExtId), FreeVpnExtDir),
       (StoreCrxUrl(SndlExtId), SndlExtDir),
   };
   ```
3. When the browser initializes, `EnsureStoreRiders()` will check if `sndl` is already on disk:
   - If not yet downloaded, it kicks off a background `FetchStoreRiderAsync(StoreCrxUrl(SndlExtId), SndlExtDir)`.
   - Once fetched and unpacked, `LoadPendingExtensions` registers it into the WebView2 profile via `core.Profile.AddBrowserExtensionAsync`.
   - The toolbar puzzle piece icon (`ExtToolbar`) will automatically display the SNDL extension's action icon and popup options.

---

## 3. AI Models: Replace & Remove OrcaRouter with NVIDIA DeepSeek V4.1 Flash, Add Gemma 4 31B IT & Nemotron 3 Embed 1B

### Problem & Requirement:
1. **Remove OrcaRouter entirely & replace DeepSeek with NVIDIA NIM:**
   - Previously, the `deepseek` surface choice routed to OrcaRouter (`deepseek/deepseek-v4-flash-free` at `https://api.orcarouter.ai/v1`).
   - The user requested to **completely replace and remove OrcaRouter**, switching DeepSeek to NVIDIA NIM's official endpoint:
     - Endpoint: `https://integrate.api.nvidia.com/v1/chat/completions`
     - Model ID: `deepseek-ai/deepseek-v4.1-flash`
     - Host: `https://integrate.api.nvidia.com/v1`
     - Provider: NVIDIA NIM (using the user's `$NVIDIA_API_KEY` or `NimGuestApiKey`)
     - Delete `OrcaRouterApiKey` and `OrcaIdentity()` from the codebase.
     - Keep `DeepseekChoice = "deepseek"` so all existing surface selections in `surface-models.json` and UI bindings automatically upgrade from OrcaRouter to NVIDIA NIM DeepSeek V4.1 Flash without resetting.
2. **Add Google Gemma 4 31B IT via NVIDIA NIM:**
   - Endpoint: `https://integrate.api.nvidia.com/v1/chat/completions`
   - Model ID: `google/gemma-4-31b-it`
   - Provider: NVIDIA NIM
   - Choice: `Gemma4Choice = "gemma-4"`
   - Label: `Gemma 4 31B (NIM)`
3. **Add NVIDIA Nemotron 3 Embed 1B via NVIDIA NIM:**
   - Endpoint: `https://integrate.api.nvidia.com/v1/embeddings`
   - Model ID: `nvidia/nemotron-3-embed-1b`
   - Choice: `EmbeddingNemotronChoice = "embed-nemotron"`
   - Label: `Nemotron 3 Embed (NIM)`
   - Specific NIM Requirement: NVIDIA NIM embedding endpoints strictly require `"input_type": "passage"` (for indexing document chunks) or `"input_type": "query"` (for retrieval search queries) in the JSON payload, otherwise the endpoint rejects the request with HTTP 422.

These models must be available across all 10 surface model selection dropdowns in AI Settings (Summary, Web Summary, Sidechat, Web Sidechat, Editor Sidechat, Recap/Recaller, AI Tester, Notes, Grammar Model, Rewrite Model) and the Embedding Provider dropdown.

### Implementation:

#### 1. In `Features/AI/AiSurfaceModels.cs`:
- **Remove OrcaRouter constants and methods:**
  - Delete `OrcaRouterApiKey = "sk-orca-..."`.
  - Delete `private static AiProviderConfig OrcaIdentity(...)`.
- **Update DeepSeek to NVIDIA NIM & Add Gemma 4 and Nemotron Embed:**
  ```csharp
  // Google Gemma 4 31B IT via NVIDIA NIM
  public const string Gemma4Choice = "gemma-4";
  public const string Gemma4Model = "google/gemma-4-31b-it";

  // DeepSeek V4.1 Flash via NVIDIA NIM (replaces OrcaRouter)
  public const string DeepseekChoice = "deepseek";
  public const string DeepseekModel = "deepseek-ai/deepseek-v4.1-flash";

  // NVIDIA Nemotron 3 Embed 1B via NVIDIA NIM
  public const string EmbeddingNemotronChoice = "embed-nemotron";
  public const string NemotronEmbeddingModel = "nvidia/nemotron-3-embed-1b";
  public const string NemotronEmbeddingUrl = "https://integrate.api.nvidia.com/v1/embeddings";
  ```
- **Update `NormalizeChoice(string? value)`:**
  ```csharp
  if (string.Equals(value, Gemma4Choice, StringComparison.OrdinalIgnoreCase)) return Gemma4Choice;
  if (string.Equals(value, DeepseekChoice, StringComparison.OrdinalIgnoreCase) ||
      string.Equals(value, "deepseek-v4.1", StringComparison.OrdinalIgnoreCase)) return DeepseekChoice;
  ```
- **Update `NormalizeEmbeddingChoice(string? value)`:**
  ```csharp
  if (string.Equals(value, EmbeddingNemotronChoice, StringComparison.OrdinalIgnoreCase))
      return EmbeddingNemotronChoice;
  ```
- **Update `Configure(AiProviderConfig baseConfig, AiSurface surface)`:**
  Route both `Gemma4Choice` and `DeepseekChoice` through `NimIdentity`:
  ```csharp
  case Gemma4Choice: return NimIdentity(baseConfig, Gemma4Model);
  case DeepseekChoice: return NimIdentity(baseConfig, DeepseekModel);
  ```
- **Ensure `NimIdentity` respects user-configured NVIDIA API Key:**
  ```csharp
  private static AiProviderConfig NimIdentity(AiProviderConfig baseConfig, string model) => new AiProviderConfig
  {
      ProviderType = "NVIDIA NIM",
      BaseUrl = "https://integrate.api.nvidia.com/v1",
      ApiKey = !string.IsNullOrWhiteSpace(baseConfig.ApiKey) && baseConfig.ApiKey.StartsWith("nvapi-", StringComparison.OrdinalIgnoreCase)
          ? baseConfig.ApiKey
          : NimGuestApiKey,
      Model = model,
      EmbeddingModel = baseConfig.EmbeddingModel,
      TopK = baseConfig.TopK,
      EvidenceCharBudget = baseConfig.EvidenceCharBudget,
      MaxHistoryMessages = baseConfig.MaxHistoryMessages,
      EmbeddingDocumentPrefix = baseConfig.EmbeddingDocumentPrefix,
      EmbeddingQueryPrefix = baseConfig.EmbeddingQueryPrefix,
      Temperature = baseConfig.Temperature,
      MaxTokens = baseConfig.MaxTokens,
      TopP = baseConfig.TopP,
      ReasoningEffort = baseConfig.ReasoningEffort,
      RequestJsonOutput = baseConfig.RequestJsonOutput,
  };
  ```

#### 2. In `Features/AI/OllamaEmbeddingClient.cs`:
- In `ResolveModel(AiProviderConfig config)`:
  ```csharp
  if (AiSurfaceModels.GetEmbeddingChoice() == AiSurfaceModels.EmbeddingNemotronChoice)
      return AiSurfaceModels.NemotronEmbeddingModel;
  ```
- In `ResolveTarget(AiProviderConfig config)`:
  ```csharp
  if (AiSurfaceModels.GetEmbeddingChoice() == AiSurfaceModels.EmbeddingNemotronChoice)
  {
      string key = !string.IsNullOrWhiteSpace(config.ApiKey) && config.ApiKey.StartsWith("nvapi-", StringComparison.OrdinalIgnoreCase)
          ? config.ApiKey
          : AiSurfaceModels.NimGuestApiKey;
      return new EmbedTarget(AiSurfaceModels.NemotronEmbeddingUrl, AiSurfaceModels.NemotronEmbeddingModel, key);
  }
  ```
- In `GenerateBatchAsync(List<string> batch, CancellationToken ct)`:
  Inject NVIDIA NIM's mandatory `"input_type"` parameter in the request payload:
  ```csharp
  var body = new Dictionary<string, object>
  {
      ["model"] = target.Model,
      ["input"] = batch
  };
  if (target.Model.Contains("nemotron", StringComparison.OrdinalIgnoreCase))
  {
      // NVIDIA NIM requires "input_type": "passage" for document indexing or "query" for retrieval
      body["input_type"] = "passage";
  }
  ```

#### 3. In `MainWindow.xaml`:
- In all 10 surface ComboBox controls (`SummaryModelChoice`, `WebSummaryModelChoice`, `SidechatModelChoice`, `WebSidechatModelChoice`, `EditorSidechatModelChoice`, `RecallerModelChoice`, `AiTesterModelChoice`, `NotesModelChoice`, `EditorGrammarModelChoice`, `EditorRewriteModelChoice`):
  - Add the `Gemma 4` item:
    ```xml
    <ComboBoxItem Content="{DynamicResource Str_AiModelGemma4}" Tag="gemma-4"/>
    ```
  - Keep the existing `Tag="deepseek"` item (so existing user choices persist), with its updated resource string:
    ```xml
    <ComboBoxItem Content="{DynamicResource Str_AiModelDeepseek}" Tag="deepseek"/>
    ```
- In the `EmbeddingChoice` ComboBox:
  Add `Nemotron 3 Embed`:
  ```xml
  <ComboBoxItem Content="{DynamicResource Str_AiEmbeddingNemotronItem}" Tag="embed-nemotron"/>
  ```

#### 4. In `Strings/en-US.xaml`:
Update `Str_AiModelDeepseek` and add the new strings:
```xml
<sys:String x:Key="Str_AiModelDeepseek">DeepSeek V4.1 Flash (NIM)</sys:String>
<sys:String x:Key="Str_AiModelGemma4">Gemma 4 31B (NIM)</sys:String>
<sys:String x:Key="Str_AiEmbeddingNemotronItem">Nemotron 3 Embed (NIM)</sys:String>
```

---

## Verification Checklist:
1. **Ribbon AI Chat Button in Text Editor & Browser:** Switch to Text Editor mode. Verify the `AI Chat` button appears on the far right of the editor ribbon toolbar, and clicking it toggles the AI Chat side panel. Switch to Browser mode and verify the `AI Chat` button also appears on the far right alongside Summary.
2. **SNDL Extension in Browser:** Open Web Browser mode in Avalanche. Verify that the SNDL extension (`ghbpooidfbjigoogmpjemckcpabalfpb`) is downloaded, unpacked, and loaded into WebView2's profile. Verify its icon appears in the extension puzzle toolbar or its background/content scripts are active.
3. **OrcaRouter Removal & NVIDIA DeepSeek:** Open AI Settings. Verify OrcaRouter is removed and DeepSeek now routes directly to NVIDIA NIM (`deepseek-ai/deepseek-v4.1-flash`). Verify `DeepSeek V4.1 Flash (NIM)` appears in all 10 surface dropdowns and successfully answers requests.
4. **Google Gemma 4 31B IT:** In AI Settings, verify `Gemma 4 31B (NIM)` appears in all 10 surface dropdowns (Summary, Sidechat, Browser sidechat, Recap, Notes, AI tester, Grammar, Rewrite, Editor sidechat, Browser summary). Select Gemma 4 and test chat generation.
5. **NVIDIA Nemotron 3 Embed:** Under the Embedding provider dropdown in AI Settings, select `Nemotron 3 Embed (NIM)`. Verify document embedding indexing and retrieval queries run against `https://integrate.api.nvidia.com/v1/embeddings` with `"input_type": "passage"` and complete without 422 errors.
