# TASK: Implement Native Discord Rich Presence in Avalanche (Title Bar Toggle & Pure C# IPC)

Repository: `https://github.com/etb190/Avalanche`  
Auth Token: `ghp_***` (provided via environment / push URL)

---

### Overview & Core Architecture
Implement **Discord Rich Presence (RPC)** in Avalanche so that while a user is reading a document, their Discord profile displays real-time reading progress:
- **Title (Details):** Clean book title (e.g. `Mesopotamia: A History`).
- **Page (State):** Current progress formatted as `Page 42 of 350`.
- **Timer:** Elapsed reading time (`00:14:22 elapsed`), preserved continuously across page flips.
- **Assets:** Avalanche icon and book indicator.

Unlike browser extensions that require a separate Node.js bridge server, Avalanche is a native Windows C# application and must implement Discord RPC **the best way possible: pure native C# via Windows Named Pipes (`\\.\pipe\discord-ipc-0` through `9`) with zero external NuGet bloat, zero background Node processes, and zero open HTTP ports.**

The toggle switch to enable/disable Discord RPC is placed in `SummaryWindow`'s top middle bar **directly after the Recap toggle**.

---

### 1. The Discord RPC Toggle in `SummaryWindow` Title Bar
* In `Features/Summary/SummaryWindow.xaml.cs`:
  * In the top middle bar (`DialogChrome.TitleBarExtras.Centered`), place the Discord toggle **directly after the Recap toggle** with a 16px horizontal separation gap.
  * Use the **exact same iOS-style sliding switch** (`TestModeToggle`):
    * 40px track, 16px white thumb gliding 18px in 150ms.
    * Paired with a bold label `Discord` (`Str_Lbl_DiscordRpc`).
    * Tooltip: `Str_TT_DiscordRpc` ("Broadcast reading activity to Discord Rich Presence").
    * Behavior: Click flips in preview mouse down (preventing DragMove interference).
    * Checked: Activates Discord RPC and immediately broadcasts current reading activity.
    * Unchecked: Deactivates Discord RPC and clears the presence from Discord immediately.
  * State: Persisted in user settings (`discord.rpc.enabled`, default: `true`).

---

### 2. Zero-Dependency Native Discord IPC Client (`Features/Discord/DiscordRpcClient.cs`)
Implement a lightweight, robust, native C# Discord IPC client using `System.IO.Pipes.NamedPipeClientStream`:
1. **Named Pipe Connection:**
   * Scans `discord-ipc-0` through `discord-ipc-9`.
   * Non-blocking asynchronous connection. If Discord is not running or Discord desktop is closed, fail gracefully and silently without throwing unhandled exceptions or freezing the UI thread.
2. **Wire Protocol:**
   * Packet format: `[Int32 Opcode (LE)][Int32 Length (LE)][UTF-8 JSON Payload]`.
   * **Opcode 0 (Handshake):**
     ```json
     { "v": 1, "client_id": "1547881478774595705" }
     ```
     (Default Client ID: `1547881478774595705`, configurable via `discord.rpc.client_id`).
   * **Opcode 1 (Frame - SET_ACTIVITY):**
     ```json
     {
       "cmd": "SET_ACTIVITY",
       "args": {
         "pid": <process_id>,
         "activity": {
           "type": 0,
           "details": "<Sanitized Book Title>",
           "state": "Page 42 of 350",
           "timestamps": {
             "start": <session_start_unix_timestamp>
           },
           "assets": {
             "large_image": "kindle",
             "large_text": "<Sanitized Book Title>",
             "small_image": "https://cdn.discordapp.com/app-icons/1547881478774595705/9f913825c797ce44d9a79a0ccaeeec5b.png",
             "small_text": "Avalanche"
           },
           "instance": true
         }
       },
       "nonce": "<guid>"
     }
     ```
   * **Clear Activity:** Send `SET_ACTIVITY` with `activity: null` when closing a document, exiting, or when the user toggles Discord RPC off.

---

### 3. Controller & Session Management (`Features/Discord/DiscordRpcController.cs`)
1. **Singleton Controller:**
   * Manages connection lifecycle, auto-reconnect backoff (if Discord is launched later), and state dispatch.
2. **Session Start Timestamp Preservation:**
   * When opening a book, record `_sessionStartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()`.
   * As the reader turns pages, the `_sessionStartTime` **remains intact** so Discord's elapsed timer counts upward smoothly (`01:25 elapsed`) without resetting back to 0 on every page flip.
   * Switching to a completely different document resets `_sessionStartTime`.
3. **Throttling / Debounce:**
   * Rapidly scrolling or skimming pages triggers page changes in milliseconds. Debounce the Discord update by **400ms** so IPC packets are not spammed.
4. **Title Sanitization:**
   * Strip file extension (`.pdf`, etc.).
   * Replace underscores and multiple dashes with spaces.
   * Proper casing while maintaining possessives/contractions (e.g. `Israel's`).
   * Truncate to Discord's 128-character limit.

---

### 4. Integration with MainWindow & Viewer
* In `MainWindow.xaml.cs` (or viewer navigation hooks):
  * When a document is loaded: call `DiscordRpcController.OnDocumentOpened(title, currentPage, totalPages)`.
  * When page turns (`CurrentPageChanged`): call `DiscordRpcController.OnPageChanged(currentPage, totalPages)`.
  * When a document is closed or app exits: call `DiscordRpcController.OnDocumentClosed()`.
  * When the Discord toggle in `SummaryWindow` is flipped: reflect immediately.

---

### 5. Localization Parity
Add the new localization keys across all 16 language resource dictionaries (`Strings/en-US.xaml`, `de-DE.xaml`, `fr-FR.xaml`, `es-ES.xaml`, etc.):
* `Str_Lbl_DiscordRpc`: `"Discord"`
* `Str_TT_DiscordRpc`: `"Broadcast reading activity to Discord Rich Presence"`

---

### 6. Verification
1. Run `dotnet build` with zero warnings and zero errors.
2. Run `dotnet test` and confirm all 2,000+ tests pass (including localization parity).
3. Open `SummaryWindow`:
   * Verify the `Discord` toggle sits in the top middle bar directly after the `Recap` toggle, matching its exact iOS switch styling.
4. Open a PDF document:
   * With Discord running on the PC and toggle ON: check Discord profile. Verify details show the clean document title, state shows `Page X of Y`, elapsed reading timer counts up, and assets render cleanly.
   * Flip pages: verify state updates to the new page within 400ms and the timer does not reset.
   * Turn toggle OFF: verify Discord presence disappears immediately.
   * Turn toggle ON: verify Discord presence reappears immediately.
