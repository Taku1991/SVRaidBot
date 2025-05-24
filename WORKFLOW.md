# Git Workflow Dokumentation

## 🌟 Übersicht
Dieses Dokument beschreibt die Git-Workflows für:
- ✅ **Saubere deutsche Übersetzungs-PRs** (für upstream)
- ✅ **Eigene Entwicklung** (im Fork)
- ✅ **Branch-Management**

---

## 🔄 1. Deutsche Übersetzungen (Saubere PRs für upstream)

### **Schritt 1: Sauberen Branch vom upstream erstellen**
```bash
# Neueste Änderungen holen
git fetch upstream

# Sauberen Branch VOM UPSTREAM erstellen (ohne eigene Commits)
git checkout -b german-fix-2025-01-XX upstream/main
```

### **Schritt 2: Deutsche Übersetzungen bearbeiten**
```bash
# Übersetzungsdateien bearbeiten (z.B.)
# SysBot.Pokemon/SV/BotRaid/Language/EmbedLanguageMappings.json

# Nur die geänderten Übersetzungsdateien hinzufügen
git add SysBot.Pokemon/SV/BotRaid/Language/EmbedLanguageMappings.json
```

### **Schritt 3: Committen und Push**
```bash
# Aussagekräftigen Commit erstellen
git commit -m "Improve German translations: [Beschreibung der Änderungen]"

# Branch zu GitHub pushen
git push origin german-fix-2025-01-XX
```

### **Schritt 4: Pull Request erstellen**
1. Gehe zu: `https://github.com/Taku1991/SVRaidBot`
2. Klicke "Compare & pull request" für den neuen Branch
3. **Base repository:** `bdawg1989/SVRaidBot` (upstream)
4. **Base branch:** `main`
5. Erstelle PR mit guter Beschreibung

### **Schritt 5: Aufräumen nach Merge**
```bash
# Zurück zum main
git checkout main

# Branch lokal löschen
git branch -d german-fix-2025-01-XX

# Branch remote löschen
git push origin --delete german-fix-2025-01-XX
```

---

## 🛠️ 2. Eigene Entwicklung (Fork-Features)

### **Für Features in deinem Fork:**
```bash
# Im main branch arbeiten oder Feature-Branch erstellen
git checkout main

# Neue Features entwickeln
git add [deine-dateien]
git commit -m "Add new feature: [beschreibung]"
git push origin main
```

### **Für größere Features:**
```bash
# Feature-Branch erstellen
git checkout -b feature-xy

# Entwickeln...
git add [dateien]
git commit -m "Feature: [beschreibung]"
git push origin feature-xy

# Später in main mergen
git checkout main
git merge feature-xy
git push origin main
```

---

## 🔄 3. Sync mit Upstream halten

### **Main Branch auf upstream Stand bringen:**
```bash
# Neueste Änderungen holen
git fetch upstream

# Main auf upstream Stand setzen (VORSICHT: Überschreibt lokale Änderungen)
git checkout main
git reset --hard upstream/main
git push origin main --force-with-lease
```

### **Sanftere Variante (mit Merge):**
```bash
git checkout main
git fetch upstream
git merge upstream/main
git push origin main
```

---

## 📁 4. Branch-Übersicht

### **Aktuelle Branch-Struktur:**
- `main` - Deine Hauptentwicklung (mit Workflow-Fixes)
- `my-local-work` - Deine ursprünglichen lokalen Änderungen
- `german-translations-clean` - Aktueller PR (kann nach Merge gelöscht werden)

### **Empfohlene Naming Convention:**
- `german-fix-YYYY-MM-DD` - Für deutsche Übersetzungen
- `feature-[name]` - Für neue Features
- `bugfix-[beschreibung]` - Für Bugfixes

---

## ⚠️ Wichtige Hinweise

### **Für saubere Upstream-PRs:**
- ✅ **IMMER** vom `upstream/main` starten
- ✅ **NUR** Übersetzungsdateien ändern
- ✅ **Kurze, fokussierte** Commits
- ✅ **Aussagekräftige** Commit-Messages

### **Remote-Konfiguration überprüfen:**
```bash
git remote -v
# Sollte zeigen:
# origin    https://github.com/Taku1991/SVRaidBot.git
# upstream  https://github.com/bdawg1989/SVRaidBot.git
```

### **Falls upstream fehlt:**
```bash
git remote add upstream https://github.com/bdawg1989/SVRaidBot.git
```

---

## 🚀 Schnellreferenz

### **Deutsche Übersetzung (komplett):**
```bash
git fetch upstream
git checkout -b german-fix-$(date +%Y-%m-%d) upstream/main
# [Übersetzungen bearbeiten]
git add [übersetzungsdateien]
git commit -m "Improve German translations: [beschreibung]"
git push origin german-fix-$(date +%Y-%m-%d)
# [PR auf GitHub erstellen]
```

### **Nach PR-Merge aufräumen:**
```bash
git checkout main
git branch -d german-fix-*
git push origin --delete german-fix-*
```

---

**💡 Tipp:** Speichere diese Datei als Referenz und aktualisiere sie bei Bedarf! 

# Git Upstream Update - Main Branch

## Schritte zum Aktualisieren des main branch mit upstream changes

### 1. Upstream Remote prüfen
```bash
git remote -v
```
Stelle sicher, dass `upstream` auf das ursprüngliche Repository zeigt.

### 2. Upstream Changes fetchen
```bash
git fetch upstream
```
Lädt alle neuen Commits vom upstream Repository herunter, ohne sie zu mergen.

### 3. Zum main branch wechseln
```bash
git checkout main
```

### 4. Upstream changes mergen
**Option A: Merge**
```bash
git merge upstream/main
```

**Option B: Rebase (für sauberen Verlauf)**
```bash
git rebase upstream/main
```

### 5. Änderungen zu deinem Fork pushen
```bash
git push origin main
```

## Alternative: Direct Pull
Wenn du bereits auf dem main branch bist:
```bash
git pull upstream main
```
Das macht fetch und merge in einem Schritt.

## Wichtige Hinweise
- Stelle sicher, dass du keine uncommitted changes hast
- Falls doch, verwende `git stash` vor dem Update
- Mit `git stash pop` kannst du die Changes später wieder anwenden