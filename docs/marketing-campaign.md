# TrayTrigger marketing campaign

Written 2026-10-08. Starting point: 3 GitHub stars, about 7 downloads per stable release, no
code signing, no posts anywhere. Goal for the first 90 days: 300 stars, 2,000 downloads, one
YouTube video or article by someone else, and a Discussions tab with strangers in it.

## 1. Positioning

**One line:** *TrayTrigger sets Windows up for the game you're about to play, and puts everything
back when you quit.*

**Why this line.** Gamers call every optimizer "snake oil" because optimizers change things
permanently and quietly. TrayTrigger's whole design is the opposite: per game, visible, reverted,
logged in Activity & History. That is the one thing no competitor leads with, so lead with it.

**Three support points**, always in this order:
1. **Per game.** HDR, CPU cores, power plan, DLSS Override, scripts, tools: chosen for each game.
2. **Everything goes back.** When the game exits, after a crash, and the Activity page shows what
   it did and what it couldn't put back.
3. **Light and honest.** A tray app. Open source. Says which tweaks are placebo in its own UI.

**Never say:** boost, FPS booster, optimizer, unlock, turbo. Those words trigger the downvote.
**Say:** set up, put back, per game, what changed, 1% lows.

**Name the enemies.** Razer Cortex (leaves PCs changed), Process Lasso (paid nag, hard to set up),
hand-edited registry tweaks (permanent, forgotten). Not Playnite: it's a library, not a rival,
and its users are a target audience.

## 2. Audiences, in priority order

| Audience | Where they are | Their pain | The TrayTrigger hook |
|---|---|---|---|
| X3D owners (7950X3D, 9950X3D) | r/Amd, r/AMDHelp, r/buildapc | Games landing on the wrong CCD; Process Lasso setup | CPU Cores picks the V-Cache CCD per game, no Lasso |
| OLED and HDR monitor owners | r/OLED_Gaming, r/Monitors, r/ultrawidemasterrace | HDR left on after a game, washed-out desktop | Per-game HDR that turns itself off |
| Sim racers and flight simmers | r/simracing, r/iRacing, r/flightsim, r/hotas | Start SimHub, CrewChief, TrackIR by hand every time | Tools that start with the game and close after |
| Tweak enthusiasts | r/OptimizedGaming, Blur Busters, Guru3D, Overclock.net | Tweaks they can't undo, timer resolution tools | Session-scoped tweaks with a restore log |
| Launcher-haters | r/pcgaming, r/Steam, r/EpicGamesPC | Launchers that stay open; EA app, Ubisoft Connect | Launchers close themselves after the game |
| Open-source and Windows-tool people | r/opensource, r/windowsapps, r/coolgithubprojects, Hacker News | Want lightweight tools with source | C#/WPF, MIT, 60 MB, signed checksums |

## 3. Assets to make before the first post

Everything below is reused across every channel. Make them once, well.

1. **The 20-second GIF.** Tray menu, click a game, watch HDR turn on and the power plan switch,
   game closes, both go back, Activity page shows the two rows. No voice, no music. This is the
   top of the README and the first image in every post.
2. **The before-and-after chart.** One game on your PC, Off vs Optimized vs Aggressive, average
   FPS and 1% lows, five runs each, CapFrameX or PresentMon. Publish the raw CSV. Even a small gain
   with honest error bars beats a big claim. If a tweak shows nothing, say so in the chart; that
   honesty is the brand.
3. **Six screenshots** at 1920x1080, light and dark: Library poster grid, Edit Game performance
   tab, System page, Hardware Specs, Activity & History, the tray menu. Reused on the site,
   AlternativeTo, MajorGeeks, Softpedia, and every forum post.
4. **The 90-second video.** Screen capture, your voice, one problem solved end to end. Upload to
   YouTube unlisted first for the press pitch, then public.
5. **A press kit page** on the GitHub Pages site: logo, screenshots, GIF, one-paragraph and
   one-line descriptions, your contact, the chart. Reviewers take what's easy.
6. **README rewrite.** First screen: GIF, the one line, three bullets, Download button, "What it
   changes and what it puts back" link. Move the verification and signing sections down.
7. **Code signing.** Azure Artifact Signing (renamed from Trusted Signing), $9.99 a month, open to
   individuals in the US and Canada with an ID check. Do it before the campaign: the SmartScreen
   warning is where half the clicks die. Say "signed" in the README once it's live.
8. **winget manifest.** `winget install TrayTrigger` in the README is a trust signal on its own,
   and Scoop and Chocolatey follow.

## 4. Channels and the order to use them

### Phase 0: foundations (weeks 1-2)

- Sign the installer. Submit the winget manifest. Rewrite the README.
- Create the AlternativeTo listing (alternative to Razer Cortex, Process Lasso, Playnite,
  AutoActions, Nyrna) and the MajorGeeks and Softpedia submissions. These are slow but permanent.
- Open a PR to add TrayTrigger to the awesome-windows and awesome-gaming lists on GitHub.
- Enable GitHub Discussions properly: pin a Welcome post, seed an Ideas thread and a "Show your
  setup" thread. An empty Discussions tab says "nobody's here".
- Comment, don't post, on Reddit for two weeks: answer HDR, X3D and launcher questions with real
  help and no link. This builds the account history r/pcgaming and r/Windows11 check. When a
  thread asks for exactly what TrayTrigger does, say "I made a tool for this" with the link.
  That's allowed everywhere, including r/OLED_Gaming where promotion posts are banned.

### Phase 1: the first wave (weeks 3-6), one post a week

Order matters: start where the rules are friendliest and the audience is sharpest, learn from the
comments, then move to the big subreddits with a polished post.

| Week | Where | Post title (draft) | Why here first |
|---|---|---|---|
| 3 | r/opensource, r/coolgithubprojects (same day) | "I built an open-source tray app that sets Windows up per game and puts everything back when you quit" | Promotion welcome; low stakes; early feedback on the GIF and README |
| 4 | r/Amd + r/AMDHelp | "7950X3D/9950X3D: I made a free tool that keeps each game on the V-Cache CCD and undoes it after, no Process Lasso" | Sharpest single hook; X3D owners test things and post results |
| 5 | r/simracing + r/iRacing | "My tray app starts SimHub, CrewChief and TrackIR with the sim and closes them after" | Niche with a daily pain; message the mods first |
| 6 | r/windowsapps + r/software | "TrayTrigger: per-game Windows settings that revert themselves (open source, 60 MB, tray app)" | General Windows-tool audience; post has been refined twice by now |

Post format for every one of these:
- Title: problem first, "free" or "open-source", no feature list.
- First line: "I'm the developer."
- GIF, then three sentences, then the chart, then the link, then "what it doesn't do" (no kernel
  driver, nothing injected, never touches anti-cheat games for Suspend). The limits paragraph
  is what earns trust.
- Stay in the thread for 24 hours and answer everything, including the hostile ones, with
  specifics.
- Post Tuesday to Thursday, 8-10 am US Eastern.

### Phase 2: the big rooms (weeks 7-10)

- **r/pcgaming**: one post, built on the best-received angle from Phase 1. Verified-developer
  flair if the mods offer it.
- **r/OptimizedGaming**: the enthusiast crowd. Lead with the Activity log and the honesty about
  placebo tweaks; this is the one place to go deep on which tweaks do what.
- **r/Windows11**: eligible once the repo has been public 30 days (it has) and with AI use in the
  code disclosed per their September 2026 rule. Lead with "puts everything back".
- **Hacker News, Show HN**: "Show HN: TrayTrigger – per-game Windows tuning that reverts itself
  (C#, open source)". HN likes the design argument: session-scoped changes, crash-safe snapshot,
  signed manifest, no telemetry. Post at 8-9 am Eastern on a weekday.
- **Forums**: Blur Busters (Input Lag section), Guru3D (Operating Systems), Overclock.net
  (Windows). One thread each, written as a technical write-up of how the restore works, not an
  ad. These people will find bugs; that's the point.

### Phase 3: YouTube and press (weeks 8-12)

Tools in this space only break out when a creator covers them. Pitch creators who review PC
utilities and tweaks rather than games: the mid-size channels that covered Lossless Scaling,
DLSS Swapper and OptiScaler early, plus Windows-tweak channels. Find them by searching YouTube
for those three tool names and listing who covered each one in its first months; aim for
channels between 50k and 500k subscribers, where a reply is likely.

Pitch email (one per creator, personalised first line, under 120 words): what it does in one
sentence, the GIF link, the chart, "it's free and open source so there's nothing to disclose",
an offer to answer questions or give early access to a beta, the press kit link. No attachments.

Press: XDA, Neowin, gHacks, Windows Central (software section), PC Gamer hardware. Same pitch.
XDA and gHacks cover small Windows tools weekly and are the likeliest first yes. Give them a
story, not a product: "Windows 11 leaves HDR on after games; this free tool fixes it per game."

### Ongoing

- **Every release gets a post** in r/TrayTrigger (create it) and GitHub Discussions, and a
  short comment in whichever subreddit's pain it addresses. Releases are the content.
- **Answer threads weekly.** Set Reddit saved searches for "HDR turns on", "7950X3D cores",
  "launcher stays open", "Process Lasso alternative", "AutoActions". A helpful reply with a link
  from the developer converts far better than any post.
- **Discord**: not a server of your own yet. Join the OptimizedGaming and Blur Busters Discords
  and help. Start a server when Discussions has 50 people.
- **A changelog people want to read**: keep writing CHANGELOG.md the way it is, for players. Link
  it everywhere.

## 5. The launch sequence, day by day, for each post

1. Day -2: make the GIF or screenshot specific to the angle (X3D post shows the CPU Cores
   picker, not HDR).
2. Day -1: dry-run the post text in a private note; cut it by a third.
3. Day 0, 8 am ET: post. Within 10 minutes, add a first comment with the link, the chart, the
   limits paragraph and "ask me anything".
4. Day 0: reply to every comment within an hour for the first 6 hours. Upvote nothing of your own.
5. Day 0 evening: file every bug mentioned as a GitHub issue and reply with the issue link.
6. Day 1: cross-post nothing. Post the thread link in GitHub Discussions.
7. Day 3: ship a point release fixing the top complaint, reply in the thread. This is the move
   that turns a post into a community.

## 6. What to measure

Weekly, in a spreadsheet:

| Metric | Source | Target by day 90 |
|---|---|---|
| GitHub stars | repo page | 300 |
| Release downloads | `gh api .../releases` | 2,000 total |
| Unique visitors and referrers | GitHub Insights > Traffic | 500 a week |
| Discussions participants | Discussions tab | 50 |
| winget installs | winget telemetry is not public; count stars after the manifest lands | n/a |
| Posts that survived moderation | manual | all |
| Coverage by others | Google Alert "TrayTrigger" | 1 video or article |

Read the referrers after each post. The subreddit that sends the most people who then star the
repo is the one to go back to.

## 7. Risks and the honest answers

- **"Snake oil."** Answer with the chart, the Activity log and the list of tweaks the app itself
  marks as marginal. Never argue; link.
- **"Unsigned exe from a 3-star repo."** Sign it first. Until then, point at the SHA256SUMS
  signature and the open source.
- **"Anti-cheat ban risk."** Say exactly what's done: CPU Sets not affinity, no injection, no
  driver, Suspend refuses protected games. Link the help page.
- **"Just use Process Lasso / Playnite / a script."** Agree they're good, then say what's
  different in one sentence: per game, and it undoes itself.
- **AI disclosure.** r/Windows11 requires it; others ask. State it plainly: the code was written
  with AI assistance and reviewed and tested by you. Hiding it is the only losing move.
- **A bad first impression is permanent in a subreddit.** That's why Phase 1 starts small.

## 8. The first 14 days, concretely

1. Sign up for Azure Artifact Signing; start the ID check (takes days).
2. Record the GIF and the six screenshots from the current beta.
3. Run the five-run benchmark on one game and make the chart.
4. Rewrite the README's first screen.
5. Write the winget manifest and open the PR.
6. Create the AlternativeTo listing.
7. Make the press kit page on the GitHub Pages site.
8. Spend 20 minutes a day answering questions in the target subreddits, no links.
9. Draft the four Phase 1 posts in `review-notes/` so they're ready and reviewed.
10. Ship 1.5.1 stable with "signed" in its highlights, then start Phase 1 the following Tuesday.
