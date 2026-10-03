# TASK: Implement Book Cover Thumbnails for Discord RPC & Perfectly Align Toggles in SummaryWindow

Repository: `https://github.com/etb190/Avalanche`  
Auth Token: `ghp_***` (provided via environment / push URL)

---

### Overview & Core Issues to Resolve

Two specific issues from the v1.16.0 Discord RPC integration must be resolved:

1. **Book Cover Thumbnails Are Missing in Discord Presence:**  
   Currently, `DiscordRpcController.cs` hardcodes `["large_image"] = "kindle"`. It must replicate the exact book thumbnail lookup and automatic upload system used by PDF Summarizer / Axo so book covers actually display on Discord.
2. **Toggle Alignment in `SummaryWindow`:**  
   Currently, the `Recap` and `Discord` toggles are placed in `DialogChrome.TitleBarExtras.Centered`. Because the title bar's right column contains ~140px of caption buttons and chips, the title bar center is shifted ~70px to the left of the window center. As a result, the toggles look awkwardly floating to the left, completely misaligned from the stepper controls (`< [250-329] >`) below them. The user wants the toggles placed **right above the range inputs and arrow buttons, perfectly aligned**.

---

### 1. Book Thumbnail & Cover Art System (`Features/Discord/DiscordCoverService.cs`)

Replicate the exact thumbnail resolution and caching system from `pdf-summarizer-extension` / `Axo`:

#### A. Storage Paths & Persistent Cache
* **Local Thumbnail Directories:**
  * Primary: `C:\Users\PC\Desktop\database\books\ThumbnailCache`
  * Secondary (Articles fallback): `C:\Users\PC\Desktop\database\Articles\ThumbnailCache`
* **Persistent URL Cache File:**
  * `C:\Users\PC\Desktop\database\books\cover_urls.json`
  * JSON object mapping `cleanTitle` -> `https://...` image URL.

#### B. Thumbnail Matching Algorithm
When `DiscordRpcController` prepares the activity for a document:
1. Normalize and clean the title:
   ```csharp
   string cleanTitle = Path.GetFileNameWithoutExtension(filePath).Trim();
   ```
2. **Step 1 — Cache Check:**  
   Check `cover_urls.json` (cached in-memory in a thread-safe `Dictionary<string, string>`). If `urlCache.TryGetValue(cleanTitle, out var cachedUrl)` and it starts with `https://`, use it immediately with **0 ms latency**.
3. **Step 2 — Local File Lookup:**  
   If not in cache, search `ThumbnailCache`:
   * **Exact match:** Check if `File.Exists(Path.Combine(THUMBNAIL_DIR, cleanTitle + ".jpg"))`.
   * **Fuzzy / Substring match:** If exact match does not exist, enumerate all `.jpg` files in `ThumbnailCache`. Compare the filename without `.jpg` against `cleanTitle.ToLowerInvariant()`:
     ```csharp
     name == lower || lower.Contains(name) || name.Contains(lower)
     ```
   * If not found in the primary books directory, repeat the search in `C:\Users\PC\Desktop\database\Articles\ThumbnailCache`.
4. **Step 3 — Anonymous Upload (`uguu.se`):**  
   If a local `.jpg` thumbnail is found:
   * Upload to `https://uguu.se/upload` via `HttpClient` as a `MultipartFormDataContent`.
   * Add the byte array/file stream with name `"files[]"` and filename `"cover.jpg"`.
   * Send `POST https://uguu.se/upload`.
   * Parse the JSON response:
     ```json
     {
       "success": true,
       "files": [
         {
           "name": "cover.jpg",
           "url": "https://a.uguu.se/xxxxxx.jpg",
           "size": 12345
         }
       ]
     }
     ```
   * Extract `url = files[0].url`.
   * Store `urlCache[cleanTitle] = url` and persist to `cover_urls.json`.
5. **Step 4 — Fallback & Resilience:**  
   If no thumbnail exists locally, or if the network upload fails, fall back to `"kindle"`. All file I/O and HTTP operations must be wrapped in `try/catch` so that failures never throw, log noisy errors, or crash the application.

#### C. Discord Activity Integration
* In `DiscordRpcController.cs`:
  * Resolve cover art asynchronously in the background so that reading state updates to Discord are never delayed by network calls.
  * If the cover art finishes uploading after the initial activity was already dispatched, immediately re-send `SET_ACTIVITY` with the new `large_image` URL.
  * Activity payload:
    ```csharp
    ["large_image"] = !string.IsNullOrEmpty(coverUrl) ? coverUrl : "kindle",
    ["large_text"] = Truncate(title),
    ["small_image"] = "https://cdn.discordapp.com/app-icons/1547881478774595705/9f913825c797ce44d9a79a0ccaeeec5b.png",
    ["small_text"] = "Avalanche"
    ```

---

### 2. Perfect Toggle Alignment in `SummaryWindow`

* **The Problem:**  
  In the user's screenshot, `Recap` and `Discord` in `DialogChrome.TitleBarExtras.Centered` are displaced to the left by ~70px because Column 0 of the title bar grid does not span the full window width (Column 1 takes ~140px for chips and caption buttons).
* **The Solution:**  
  Move the toggles so they sit **directly above the range inputs and arrow buttons, perfectly aligned**.
  * Remove `BuildRecapToggle()` and `BuildDiscordToggle()` from `DialogChrome.TitleBarExtras.Centered`.
  * In `SummaryWindow.xaml` (`BodyRoot`):
    * Add a dedicated row directly above the stepper row (or adjust row 0) containing a horizontal `StackPanel`:
      * `HorizontalAlignment="Center"`
      * `Orientation="Horizontal"`
      * `Margin="0,12,0,8"`
      * Contains:
        1. The `Recap` toggle switch (`TestModeToggle`) + bold `Recap` label.
        2. A clean horizontal separator gap (18px).
        3. The `Discord` toggle switch (`TestModeToggle`) + bold `Discord` label.
    * Immediately below this toggle row sits the existing stepper row (`NavPrevBtn`, `NavStartBox`, `NavNextBtn`), also with `HorizontalAlignment="Center"`.
  * **Result:** Both the toggle row and the stepper row share the exact same horizontal center (`HorizontalAlignment="Center"`), placing the toggles directly on top of `< [start-end] >`, with 100% pixel-perfect vertical alignment.

---

### 3. Verification & Checks
1. Run `dotnet build` with zero warnings and zero errors.
2. Run `dotnet test` and confirm all tests pass.
3. Open `SummaryWindow`:
   * Verify the `Recap` and `Discord` toggles sit directly above the range stepper (`< [range] >`), perfectly centered and aligned with the stepper controls.
   * Verify clicking each toggle flips smoothly with the iOS thumb glide and updates/persists `recap.enabled` and `discord.rpc.enabled`.
4. Open a PDF whose book title has a cover in `C:\Users\PC\Desktop\database\books\ThumbnailCache` (e.g. `Ancient Mesopotamia...`, `A Brief History of Everyone Who Ever Lived`, etc.):
   * Check Discord: verify the cover art is uploaded to `uguu.se` (and cached in `cover_urls.json`) and shows as the large cover image in Discord Rich Presence!
   * Verify the small image displays the Kindle/Avalanche badge.
