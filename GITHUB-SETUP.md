# Putting Museek on GitHub (and hooking up the updater)

## 1. One-time setup
1. Make a free account at github.com.
2. Install **Git for Windows** (git-scm.com) - or just use **GitHub Desktop** (desktop.github.com), which is easier.
3. On github.com click **New repository**, name it `Museek`. Decide public/private (see "Public or private?" below). Do NOT tick "add README".

## 2. Before you upload - set the updater address
Open `Services/AppInfo.cs` and set:
    public const string DefaultUpdateUrl = "https://api.github.com/repos/YOURNAME/Museek/releases/latest";
(replace YOURNAME; if releases live in a different repo, use that repo's name). Do this BEFORE your first release,
otherwise the published exe won't know where to look for updates.

## 3. Upload the code (PowerShell, inside the Museek folder)
    git init
    git add .
    git status          # check: settings.json, .spotify_cache, .env must NOT be listed
    git commit -m "First version"
    git branch -M main
    git remote add origin https://github.com/YOURNAME/Museek.git
    git push -u origin main
(GitHub Desktop: File > Add local repository > Publish repository.)

## 4. Publish a release (automatic build)
    git tag v1.0.0
    git push origin v1.0.0
GitHub builds Museek.exe (Actions tab shows progress, ~5 min) and creates a Release with the exe attached.
Friends download from:  https://github.com/YOURNAME/Museek/releases/latest

## 5. Shipping an update later
Change code -> commit -> push, then tag the next number:
    git tag v1.0.1
    git push origin v1.0.1
Everyone's Museek shows "Update available" within a day and can install it from Customize > Updates & help.

## Public or private?
- The updater downloads from GitHub WITHOUT logging in, so the repo that holds the Releases must be PUBLIC.
- Want to keep the source private (e.g. you plan to sell it)? Make a second PUBLIC repo called `Museek-releases`
  (releases only, no code). In release.yml uncomment the `repository:` and `token:` lines, create a Personal Access Token
  with "contents: write" on that repo, store it in the source repo under Settings > Secrets > Actions as RELEASES_TOKEN,
  and point DefaultUpdateUrl at the releases repo.
- A public repo with no LICENSE file means "all rights reserved": people can read it but not legally reuse it.
  Adding an open-source license (MIT etc.) lets anyone copy and even resell it.

## Safety checklist
- Never commit settings.json, .spotify_cache or .env (the .gitignore already blocks them).
- If a key ever gets committed: revoke/rotate it (Spotify dashboard, Google AI Studio) - deleting the file is not enough.
