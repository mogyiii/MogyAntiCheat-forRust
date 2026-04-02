# MogyAntiCheat Admin Recipes (EN)

This document is a practical, task-oriented companion to `README.en.md` and `docs/MAIN_FEATURES_GUIDE.en.md`.
Use it when you need exact steps, examples, and safe rollout patterns.

## README vs Main Features vs This File

- `README.en.md`: quick start + full project snapshot.
- `docs/MAIN_FEATURES_GUIDE.en.md`: feature-focused explanation of how the anti-cheat works.
- `docs/ADMIN_RECIPES.en.md` (this file): hands-on operations recipes with copy-ready examples.

## 1) Add a New Weapon Config Entry (JSON)

File:
- `server/<identity>/oxide/config/MogyAntiCheat.json`

Under `Weapons`, add your weapon short name:

```json
{
  "Weapons": {
    "rifle.ak": { "MaxAccuracy": 0.38, "SampleCount": 40, "SafeDistance": 25.0 },
    "smg.custom": { "MaxAccuracy": 0.36, "SampleCount": 35, "SafeDistance": 16.0 }
  }
}
```

Field guidance:
- `MaxAccuracy`: lower value = stricter detection.
- `SampleCount`: higher value = smoother, slower reaction.
- `SafeDistance`: higher value = less extra suspicion from medium range.

After editing:
1. Save config.
2. Reload plugin.
3. Use `/ac-why smg.custom` while testing.

## 2) Add/Update Weapon Live In-Game

Use admin command:

```text
/ac-weapon <weaponShortName|active> <MaxAccuracy|SampleCount|SafeDistance> <value>
```

Examples:

```text
/ac-weapon smg.custom MaxAccuracy 0.36
/ac-weapon smg.custom SampleCount 35
/ac-weapon smg.custom SafeDistance 16
/ac-weapon active MaxAccuracy 0.34
```

Notes:
- command saves config immediately.
- `active` uses your currently held projectile weapon.

## 3) Add a New Language (Current Architecture)

Current plugin version (`1.9.1`) treats supported language codes as code-defined.
That means a new language is not only a JSON file change.

You need both:
1. Language file: `oxide/lang/<code>/MogyAntiCheat.json`
2. Plugin code update in `MogyAntiCheat.cs`

### 3.1 Create Language JSON file

Start from English file and translate values:

- source template: `oxide/lang/en/MogyAntiCheat.json`
- new file example: `oxide/lang/de/MogyAntiCheat.json`

Keep keys identical.

### 3.2 Register language in plugin code

In `MogyAntiCheat.cs`, add a message dictionary (for fallback safety), then register it in `Init()` and include it in supported codes.

Pattern to follow (example for `de`):

```csharp
private static readonly Dictionary<string, string> MessagesDe = new Dictionary<string, string>
{
    ["NoPermission"] = "Du hast keine Berechtigung ...",
    // ... all keys
};

void Init()
{
    lang.RegisterMessages(MessagesEn, this, "en");
    lang.RegisterMessages(MessagesHu, this, "hu");
    lang.RegisterMessages(MessagesDe, this, "de");
    // ...
}

private List<string> GetSupportedLanguageCodes()
{
    var supported = new List<string>();

    if (MessagesEn.Count > 0) supported.Add("en");
    if (MessagesHu.Count > 0 && !supported.Contains("hu")) supported.Add("hu");
    if (MessagesDe.Count > 0 && !supported.Contains("de")) supported.Add("de");

    return supported;
}
```

### 3.3 Activate and test

1. Reload plugin.
2. `/ac-lang de`
3. `/ac-help` and `/ac-check` to verify translations.

If a key is missing, fallback behavior may show another language or a missing-key warning.

## 4) Safe Weapon Tuning Workflow

1. Start with defaults.
2. Observe `/ac-list` during normal peak activity.
3. Investigate edge players with `/ac-check <name>` + `/ac-why <weapon>`.
4. Adjust one field at a time.
5. Keep notes of each change and date.

Recommended first moves:
- false positives -> slightly raise `MaxAccuracy` or increase `SampleCount`
- too lenient behavior -> slightly lower `MaxAccuracy`
- long-range over-penalty -> raise `SafeDistance`

## 5) Useful Validation Checklist

- new weapon short name is correct (`/ac-weapon active ...` can help confirm)
- config JSON is valid
- plugin reloaded successfully
- expected language code is in supported list
- `/ac-why` output matches intended thresholds
- debug mode (`/ac-debug on`) only during controlled testing windows

---

Related:
- `README.en.md`
- `docs/MAIN_FEATURES_GUIDE.en.md`
- `docs/CONFIG_SCHEMA.md`
- `docs/PUBLIC_API.md`
