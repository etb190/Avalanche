# TASK: Text Editor Ribbon AI Chat Button, GLM 5.3 & GLM 5.3 Flash Models, and Extension Icon Loading Fix

Repository: `https://github.com/etb190/Avalanche`  
Target Files:
- `MainWindow.xaml`
- `MainWindow.xaml.cs`
- `Shell/SettingsPanel.cs`
- `Controls/WebBrowserControl.xaml.cs`
- `Features/AI/AiSurfaceModels.cs`
- `Features/AI/AiSettingsViewModel.cs`
- `Strings/en-US.xaml`

---

## 1. Text Editor Ribbon: Add AI Sidechat Button to Ribbon Toolbar

### Problem:
When switching to **Text Editor mode**, the AI Sidechat button is missing from the toolbar ribbon.
1. In `MainWindow.xaml`, `EditorAiChatBtn` was created with hardcoded `Visibility="Collapsed"` inside `EditorAiChatBar`. When `ApplyEditorToolbarFace(leads: true)` made `EditorAiChatBar.Visibility = Visibility.Visible`, its child button `EditorAiChatBtn` remained `Collapsed` and completely invisible.
2. Furthermore, in Text Editor mode, the primary editor actions (`NewFileBtn`, `EditorRibbonOpenBtn`, `EditorRibbonSaveBtn`, `TextEditorBtn`) all sit on the **left side** of the ribbon (`LeftBar` / `GrpEditorFiles`). The user expects the AI Sidechat button to be immediately available on the ribbon toolbar in Text Editor mode.

### Fix:
1. **Add AI Chat Button to the Editor's Left File Action Group (`GrpEditorFiles`):**
   In `MainWindow.xaml` under `GrpEditorFiles` (beside Open and Save):
   ```xml
   <StackPanel x:Name="GrpEditorFiles" Orientation="Horizontal" Visibility="Collapsed">
       <Button x:Name="EditorRibbonOpenBtn" Content="&#xE8DA;" Style="{StaticResource ToolbarButton}"
               Click="EditorRibbonOpen_Click" ToolTip="{DynamicResource Str_Editor_OpenDoc}"/>
       <Button x:Name="EditorRibbonSaveBtn" Content="&#xE74E;" Style="{StaticResource ToolbarButton}"
               Click="EditorRibbonSave_Click" ToolTip="{DynamicResource Str_Editor_SaveDoc}"/>
       <Button x:Name="EditorAiChatLeftBtn" Content="&#xE8BD;" Style="{StaticResource ToolbarButton}"
               Click="AiChatBtn_Click" ToolTip="{DynamicResource Str_TT_AiChat}"/>
   </StackPanel>
   ```
2. **Fix Visibility on the Far-Right Editor Button (`EditorAiChatBtn`):**
   In `MainWindow.xaml`, remove `Visibility="Collapsed"` from `EditorAiChatBtn` so it defaults to visible when its parent `EditorAiChatBar` is displayed:
   ```xml
   <StackPanel x:Name="EditorAiChatBar" Orientation="Horizontal"
               HorizontalAlignment="Right" Visibility="Collapsed">
       <Button x:Name="EditorAiChatBtn" Content="&#xE8BD;" Style="{StaticResource ToolbarButton}"
               Click="AiChatBtn_Click" ToolTip="{DynamicResource Str_TT_AiChat}"/>
   </StackPanel>
   ```
3. In `Shell/SettingsPanel.cs`:
   - In `ApplyEditorToolbarFace(bool leads)`:
     When `leads == true`:
     Ensure `GrpEditorFiles.Visibility = Visibility.Visible;` and `EditorAiChatBar.Visibility = Visibility.Visible;` so the AI Chat button is present and visible on the Text Editor ribbon.
     When `leads == false`:
     Collapse `GrpEditorFiles` and `EditorAiChatBar`.

---

## 2. AI Models: Add `z-ai/glm-5.3` (GLM 5.3) & Relabel Existing Model to GLM 5.3 Flash

### Problem:
The codebase currently has `z-ai/glm-5.3-flash` under the choice `glm`, labeled simply as `Glm`.
The user wants to:
1. Label the existing `z-ai/glm-5.3-flash` model as **`GLM 5.3 Flash`** (or `GLM 5.3 Flash (NIM)`).
2. Add the full **`z-ai/glm-5.3`** model on NVIDIA NIM, labeled as **`GLM 5.3`** (or `GLM 5.3 (NIM)`).
   - Base URL: `https://integrate.api.nvidia.com/v1`
   - Model ID: `z-ai/glm-5.3`
   - API Key: User's `$NVIDIA_API_KEY` (or `NimGuestApiKey`)

### Implementation:
1. In `Features/AI/AiSurfaceModels.cs`:
   - Keep the existing flash model and add the new full GLM 5.3 model:
     ```csharp
     // GLM 5.3 Flash (existing)
     public const string GlmChoice = "glm";
     public const string GlmFlashChoice = "glm-flash";
     public const string GlmFlashModel = "z-ai/glm-5.3-flash";
     public const string GlmModel = GlmFlashModel; // backwards-compatible alias

     // GLM 5.3 (new full model)
     public const string Glm53Choice = "glm-5.3";
     public const string Glm53Model = "z-ai/glm-5.3";
     ```
   - In `NormalizeChoice(string? value)`:
     ```csharp
     if (string.Equals(value, Glm53Choice, StringComparison.OrdinalIgnoreCase)) return Glm53Choice;
     if (string.Equals(value, GlmChoice, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(value, GlmFlashChoice, StringComparison.OrdinalIgnoreCase)) return GlmChoice;
     ```
   - In `Configure(AiProviderConfig baseConfig, AiSurface surface)`:
     ```csharp
     case Glm53Choice: return NimIdentity(baseConfig, Glm53Model);
     case GlmChoice: return NimIdentity(baseConfig, GlmModel);
     ```
2. In `Strings/en-US.xaml`:
   - Update `Str_AiModelGlm` and add `Str_AiModelGlm53`:
     ```xml
     <sys:String x:Key="Str_AiModelGlm">GLM 5.3 Flash (NIM)</sys:String>
     <sys:String x:Key="Str_AiModelGlm53">GLM 5.3 (NIM)</sys:String>
     ```
   *(And add equivalent entries in the other localization files)*
3. In `MainWindow.xaml`:
   In all 10 surface ComboBoxes:
   - Keep `<ComboBoxItem Content="{DynamicResource Str_AiModelGlm}" Tag="glm"/>` (now displays "GLM 5.3 Flash (NIM)").
   - Add `<ComboBoxItem Content="{DynamicResource Str_AiModelGlm53}" Tag="glm-5.3"/>` (displays "GLM 5.3 (NIM)").

---

## 3. Web Browser: Fix Extension Icon Loading for SNDL & Manifest V3 Extensions

### Problem:
The SNDL extension (`ghbpooidfbjigoogmpjemckcpabalfpb`) installs and functions in the built-in browser, but its button in the extension toolbar / puzzle row renders without an icon (blank or empty).

### Root Cause:
In `Controls/WebBrowserControl.xaml.cs`:
1. `ReadExtManifest` calls:
   ```csharp
   string icon = hasAction ? PickIconEntry(action) : "";
   if (icon.Length == 0 && root.TryGetProperty("icons", out System.Text.Json.JsonElement icons))
       icon = PickIconEntry(icons);
   ```
2. In `PickIconEntry(JsonElement container)`:
   ```csharp
   if (!container.TryGetProperty("default_icon", out System.Text.Json.JsonElement di)) return "";
   ```
   - `container` is the root `"icons"` object from `manifest.json`.
   - In standard Chrome extensions (including SNDL), `"icons"` is a direct map of sizes:
     `"icons": { "16": "icon16.png", "48": "icon48.png", "128": "icon128.png" }`.
   - Because `"icons"` does not contain a child `"default_icon"` property, `container.TryGetProperty("default_icon")` returns `false`.
   - `PickIconEntry(icons)` immediately returns `""`.
   - `LoadExtIcon(dir, "")` returns `null`.
   - In `WebBrowserControl.xaml`, the toolbar button binds to `IconSource`, which is `null`, resulting in an invisible/empty button face.

### Fix:
In `Controls/WebBrowserControl.xaml.cs`:
1. **Fix `PickIconEntry` to Support Direct Size Maps:**
   Update `PickIconEntry` so that if `container` does not have `"default_icon"`, it treats `container` itself as the size-keyed map:
   ```csharp
   private static string PickIconEntry(System.Text.Json.JsonElement container)
   {
       if (container.ValueKind != System.Text.Json.JsonValueKind.Object) return "";
       System.Text.Json.JsonElement map = container;
       if (container.TryGetProperty("default_icon", out System.Text.Json.JsonElement di))
       {
           if (di.ValueKind == System.Text.Json.JsonValueKind.String)
               return di.GetString() ?? "";
           if (di.ValueKind == System.Text.Json.JsonValueKind.Object)
               map = di;
           else
               return "";
       }
       string? best = null;
       int bestSize = -1;
       foreach (System.Text.Json.JsonProperty p in map.EnumerateObject())
       {
           if (p.Value.ValueKind != System.Text.Json.JsonValueKind.String) continue;
           int size = int.TryParse(p.Name, out int s) ? s : 0;
           if (size >= bestSize)
           {
               best = p.Value.GetString();
               bestSize = size;
           }
       }
       return best ?? "";
   }
   ```
2. **Add Disk Fallback in `LoadExtIcon`:**
   If `rel` is empty or the path cannot be resolved from the manifest, look for common icon files in the extension folder:
   ```csharp
   private static ImageSource? LoadExtIcon(string dir, string? rel)
   {
       string? path = null;
       if (!string.IsNullOrWhiteSpace(rel))
       {
           path = Path.GetFullPath(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)));
       }
       if (path is null || !File.Exists(path))
       {
           // Fallback to common icon file names if manifest extraction missed
           string[] candidates = { "icon128.png", "icon48.png", "icon32.png", "icon16.png", "icon.png",
                                   "assets/icon128.png", "assets/icon48.png", "assets/icon.png",
                                   "images/icon128.png", "images/icon48.png", "images/icon.png" };
           foreach (string c in candidates)
           {
               string candPath = Path.Combine(dir, c.Replace('/', Path.DirectorySeparatorChar));
               if (File.Exists(candPath)) { path = candPath; break; }
           }
       }
       if (path is null || !File.Exists(path)) return null;

       try
       {
           BitmapImage bmp = new();
           bmp.BeginInit();
           bmp.CacheOption = BitmapCacheOption.OnLoad;
           bmp.UriSource = new Uri(path, UriKind.Absolute);
           bmp.EndInit();
           bmp.Freeze();
           return bmp;
       }
       catch { return null; }
   }
   ```
3. **Add Toolbar Glyph Fallback in XAML:**
   In `Controls/WebBrowserControl.xaml` under `WebExtIconRow`:
   If `IconSource` is null, show a fallback extension puzzle glyph (`&#xE74C;`) so the button is never a blank square:
   ```xml
   <Image Source="{Binding IconSource}" Width="16" Height="16"
          Visibility="{Binding IconSource, Converter={StaticResource NullToCollapsedConverter}}"/>
   <TextBlock Text="&#xE74C;" FontFamily="Segoe MDL2 Assets" FontSize="14"
              Foreground="{DynamicResource TextBrush}"
              Visibility="{Binding IconSource, Converter={StaticResource NullToVisibleConverter}}"/>
   ```

---

## Verification Checklist:
1. **Text Editor Ribbon AI Chat Button:** Switch to Text Editor mode. Verify the AI Sidechat button (`&#xE8BD;`) is clearly visible on the ribbon toolbar (in the left editor file group beside Open/Save and/or the right action container) and toggles the AI Chat panel when clicked.
2. **GLM 5.3 Flash & GLM 5.3 in AI Settings:** Open AI Settings. Verify both `GLM 5.3 Flash (NIM)` and `GLM 5.3 (NIM)` appear in all 10 surface dropdowns (Summary, Sidechat, Browser sidechat, Recap, Notes, AI tester, Grammar, Rewrite, Editor sidechat, Browser summary). Select `GLM 5.3 (NIM)` and verify chat responses succeed using `z-ai/glm-5.3`. Select `GLM 5.3 Flash (NIM)` and verify it uses `z-ai/glm-5.3-flash`.
3. **SNDL Extension Icon:** Open Web Browser mode. Verify the SNDL extension button in the toolbar displays its proper icon graphic rather than a blank or invisible square. Click it to verify its popup opens.
