# TASK: Implement Genre Architecture in Summary Navigator (Dropdown After Language & 6 Specialized Prompts)

Repository: `https://github.com/etb190/Avalanche`  
Auth Token: `ghp_***` (provided via environment / push URL)

---

### Overview & Core Architecture
Currently, Avalanche's Summary Navigator is hardcoded to a single non-fiction prompt template. When reading fiction, philosophical literature, empirical scientific papers, self-help books, or legal treatises, that rigid structure creates severe friction (e.g. forcing academic thesis language onto novels, or dropping statistical data from research papers).

Implement a flexible **Genre Architecture** across Avalanche's AI pipeline:
1. **Genre Dropdown in `SummaryWindow`**: Placed directly after the Language dropdown in Row 2.
2. **Per-Book Genre Persistence**: Persisted per document so each book remembers its own genre across sessions and tab switches.
3. **6 Specialized Genre Prompt Personas**:
   - **Non-Fiction Classic** *(with Epistemic Accuracy)*
   - **Fiction** *(Chronological Event-by-Event Retelling)*
   - **Philosophical Fiction** *(Non-Fiction Intellectual Rigor + Narrative Drama)*
   - **Research Papers & Science** *(Hierarchical Statistics & Methodology)*
   - **Self-Help & Business** *(Practitioner's Action Notes & Named Frameworks)*
   - **Law & Statutes** *(Holdings, Doctrinal Elements & Canons of Interpretation)*

---

### 1. The Genre Dropdown in `SummaryWindow.xaml` (Row 2)

* In `Features/Summary/SummaryWindow.xaml` (Row 2 `WrapPanel`):
  * Position a new `ComboBox` named `GenreCombo` **directly after `LangCombo`**:
    ```xml
    <ComboBox x:Name="GenreCombo" Style="{StaticResource DarkComboBox}" Width="150" Height="28"
              Margin="8,0,0,0" VerticalAlignment="Center" MaxDropDownHeight="280"
              ToolTip="{DynamicResource Str_SummaryGenreTT}"/>
    ```
  * Dropdown options:
    1. **Non-Fiction Classic** (`nonfiction_classic`, default)
    2. **Fiction** (`fiction`)
    3. **Philosophical Fiction** (`philosophical_fiction`)
    4. **Research Paper** (`research_papers`)
    5. **Self-Help & Business** (`self_help`)
    6. **Law & Statutes** (`law`)
* In `Features/Summary/SummaryWindow.xaml.cs`:
  * Populate `GenreCombo` using localized strings.
  * Wire selection changed to persist the chosen genre for the active document:
    * Stored under the document's state key (`book.<docId>.genre`), falling back to user preference default (`summary.default_genre`).
    * When switching tabs or opening a book, restore that book's saved genre.
  * Pass `SelectedGenre` into `PageSummarizer.GenerateAsync(...)`.

---

### 2. The 6 Specialized Genre Prompts (`Features/Summary/PageSummarizer.cs`)

Update `PageSummarizer.cs` to supply genre-tailored system prompts via `DigestSystemPrompt(int targetWords, string language, string genre, bool fromNotes)`:

#### A. Non-Fiction Classic (`nonfiction_classic`)
* **Role:** Argumentative and intellectual condenser.
* **Core Mandate:**
  - Core thesis, logical reasoning steps, arguments, and positions.
  - Evidence, historical records, and case studies supporting claims.
  - **Comparisons & Analogies:** Every comparison and cross-cultural/historical analogy the author draws MUST be preserved (these are primary explanatory tools).
  - **Counterarguments & Debates:** Opposing theories the author critiques or disproves.
  - Specific named artifacts, sites, and persons.
* **EPISTEMIC ACCURACY RULE (CRITICAL):**
  Faithfully preserve the author's exact degree of certainty without flattening hypotheses into settled facts:
  - State verified facts as facts (e.g. *"The excavations at Eridu revealed eighteen distinct temple strata."*).
  - State hypotheses, speculations, and interpretations with their proper qualifiers (e.g. *"Evidence suggests..."*, *"Archaeologists hypothesize that seasonal flooding caused the abandonment..."*).
  - State open debates by naming the competing positions and why they clash.
* **Anti-Meta Rule:** State content directly. Zero meta-language (banned: *"The author discusses..."*, *"The text explores..."*). Dense flowing prose paragraphs.

#### B. Fiction (`fiction`)
* **Role:** Dense, chronological event-by-event retelling. You are a condenser, not a literary critic.
* **Core Mandate:**
  - The reader is using this INSTEAD of reading the book. Every plot event, scene, and character development must appear in chronological order.
  - Every scene transition, location shift, and time jump stated explicitly.
  - Every character action, decision, secret, and key line of dialogue reproduced (condensed, but recognizable).
  - Every plot turn, revelation, and complication preserved.
  - Track character dynamics: who is with whom, who knows what secret, and who is doing what.
* **FORBIDDEN (FAILURE MODES):**
  - Discussing "themes", "symbolism", or "literary devices" — tell the STORY, do not review it.
  - Skipping an event because it seemed "minor" or "transitional".
  - Merging distinct scenes into vague generalities (*"adventures continue"*).
  - Meta-language (*"The chapter depicts..."*).
  - Plain flowing prose. No bullet lists.

#### C. Philosophical Fiction (`philosophical_fiction`)
* **Role:** Dual-layer synthesis: Non-Fiction Intellectual Rigor + Narrative Drama.
* **Core Mandate:**
  1. **Substantive Philosophical Arguments (Non-Fiction Rigor):**
     - Extract the core philosophical, moral, theological, or political arguments articulated in dialogues, monologues, or narration.
     - Trace logical steps, premises, and counterarguments debated by characters or the author.
     - Name the philosophical positions engaged with (nihilism, determinism, rational egoism, utilitarianism, absurdism, faith) and what the text concludes about them.
  2. **Narrative & Psychological Events:**
     - Chronological plot events, scene shifts, encounters, and decisions without skipping scenes.
     - Detail the psychological crises, moral breakdowns, and confessions of the characters.
     - Show how the events of the plot directly test, validate, or shatter the philosophical theories the characters hold.
  - Dense flowing prose paragraphs.

#### D. Research Papers & Scientific Studies (`research_papers`)
* **Role:** Empirical methodology reviewer and quantitative data condenser.
* **Core Mandate (Hierarchical Tiers):**
  - **Tier 1 — Statistics & Quantitative Data (Top Priority):** Every quantitative result ($n$, $\%$, mean, median, SD, $p$-values, confidence intervals, effect sizes, regression coefficients, hazard ratios) reported verbatim with exact units and referents.
  - **Tier 2 — Inferential Reasoning & Models:** The exact statistical test, model, or logical step used to bridge data to claims (e.g. two-way ANOVA, linear regression controlling for age). State the test and its output.
  - **Tier 3 — Conclusions & Magnitude:** Findings attached to the magnitude of the effect (*"reduced infection rate by 24%, 95% CI [16%, 32%]"* rather than *"had a significant effect"*).
  - **Tier 4 — Methodology:** Sample demographics, control conditions, intervention protocols, and instruments.
  - **Tier 5 — Context & Theory:** Background definitions and prior literature gaps.
  - Adaptability: If the paper is qualitative or theoretical, report definitions, frameworks, and qualitative evidence substantively without inventing numbers.

#### E. Self-Help & Business (`self_help`)
* **Role:** Practitioner's executive action notes.
* **Core Mandate:**
  - **Direct Imperative Principles:** State every principle and technique as a direct command (*"When facing X, execute Y because Z"* rather than *"The author suggests doing X"*).
  - **Named Models & Frameworks:** If the author names a framework, matrix, or dichotomy (e.g. *"System 1 vs System 2"*, *"The Eisenhower Matrix"*, *"Fixed vs Growth Mindset"*), state the name, its operational rule, and how to execute it.
  - **Exercises & Reflection Prompts Verbatim:** Reproduce journaling prompts, self-audits, and diagnostic steps verbatim so the reader can actually perform them.
  - **Anecdote Compression:** Compress every case study or story into ONE sentence stating the operational lesson. Skip fluff and filler anecdotes.

#### F. Law & Statutes (`law`)
* **Role:** Judicial clerk and legal doctrine brief.
* **Core Mandate:**
  - **Issues & Procedural Posture:** The constitutional, statutory, or common-law question presented.
  - **Holding & Rule of Law:** The binding rule established or applied.
  - **Doctrinal / Statutory Elements:** Enumerate mandatory conjunctive or disjunctive conditions ($1, 2, 3$), evidentiary thresholds, and burdens of proof.
  - **Judicial Reasoning & Canons:** How the court applied precedent, statutory plain meaning, or constitutional doctrine.
  - **Exceptions & Safe Harbors:** Narrowing conditions, affirmative defenses, and statutory exemptions.
  - **Dissents:** Core legal divergence and counter-doctrine argued by dissenting judges.
  - Exact legal terms of art preserved (*strict scrutiny*, *mens rea*, *proximate cause*).

---

### 3. Localization Parity
Add all required genre strings across `Strings/en-US.xaml` and all other 15 language dictionaries:
* `Str_SummaryGenreTT`: `"Select book genre to tailor summary and analytical focus"`
* `Str_Genre_Nonfiction`: `"Non-Fiction Classic"`
* `Str_Genre_Fiction`: `"Fiction"`
* `Str_Genre_Philosophical`: `"Philosophical Fiction"`
* `Str_Genre_Research`: `"Research Paper"`
* `Str_Genre_SelfHelp`: `"Self-Help & Business"`
* `Str_Genre_Law`: `"Law & Statutes"`

---

### 4. Verification & Checks
1. Run `dotnet build` with zero warnings and zero errors.
2. Run `dotnet test` and confirm all 2,000+ unit tests pass (including localization parity).
3. Open `SummaryWindow`:
   * Verify `GenreCombo` sits in Row 2 directly after `LangCombo`.
   * Verify selecting a genre persists for that book and updates the summary generation pipeline.
   * Switch tabs to another book: verify each book maintains its own selected genre independently.
