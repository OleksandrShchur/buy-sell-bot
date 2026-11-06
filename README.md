# Ukrainian Telegram classifieds bot

A .NET 10 minimal API bot for a Telegram supergroup with forum topics. One «🛒 Купити або продати» button opens a shared listing builder, with photo albums, review, field changes, moderation by multiple admins, and publishing to category topics. All bot copy is centralized in `Texts/Texts.cs`; category titles and location shortcuts are configuration.

## Run locally

Install the .NET 10 SDK. Create a bot with **@BotFather → /newbot** and keep its token private. Each configured admin must open the bot and send **/start**.

From the repository root, open a tunnel in a separate terminal:

```powershell
ngrok http 5223
```

Use the tunnel's HTTPS address. Configure secrets (PowerShell):

```powershell
dotnet user-secrets set "Bot:Token" "YOUR_BOT_TOKEN" --project buy-sell-bot.Server
dotnet user-secrets set "Bot:WebhookSecret" ([Guid]::NewGuid().ToString("N")) --project buy-sell-bot.Server
dotnet user-secrets set "Bot:WebhookUrl" "https://YOUR_TUNNEL_HOST/api/telegram/webhook" --project buy-sell-bot.Server
dotnet user-secrets set "Bot:AdminIds:0" "YOUR_TELEGRAM_USER_ID" --project buy-sell-bot.Server

dotnet restore buy-sell-bot.slnx
dotnet test buy-sell-bot.slnx --no-restore -m:1
dotnet run --project buy-sell-bot.Server --launch-profile http
```

The tunnel must run before the app starts. Refresh `Bot:WebhookUrl` when the tunnel hostname changes. User secrets are loaded in Development; production should use environment variables or a secret manager. Never put real tokens in source control or shell scripts.

The app registers `setWebhook` at startup with the secret token, `message` and `callback_query` updates, and one webhook connection to preserve arrival order. It also registers Ukrainian `/start`, `/cancel`, and `/help` commands. There is no long polling or separate manual registration step. Do not register another deployment against the same bot.

## Group and topic setup

1. Enable **Topics** in the supergroup and create the category topics.
2. Add the bot as an **administrator** able to send text and photos, with **Manage Topics** enabled for the topic setup. Group-wide **Delete Messages** permission is optional: input cleanup happens in private chats. Keep the bot's Telegram privacy settings consistent with receiving commands addressed to it.
3. With a configured admin account, send `/topicid@YourBotUsername` inside each topic. The reply shows the chat id and `message_thread_id`.
4. Set `Bot:ChatId` to the returned `-100…` id and each category's `TopicId` in `appsettings.json`, user secrets, or environment variables. Restart to apply changes.

`ChatId = 0` and `TopicId = 0` are allowed for initial discovery. Publication is blocked until both are configured. An authorized admin can use `/topicid` in any supergroup topic.

Missing publication settings produce an explanatory message to the acting admin and leave the listing pending, with its action buttons available. Telegram 400/403 publication rejections also restore moderation and notify the acting admin. Unexpected failures include their cause and stack trace in logs, with the bot token and webhook secret redacted.

```powershell
dotnet user-secrets set "Bot:ChatId" "-1001234567890" --project buy-sell-bot.Server
dotnet user-secrets set "Categories:0:TopicId" "42" --project buy-sell-bot.Server
# Repeat for the other categories. Add admins with Bot:AdminIds:1, :2, etc.
```

Environment variables use double underscores: `Bot__Token`, `Bot__WebhookUrl`, `Bot__WebhookSecret`, `Bot__ChatId`, `Bot__AdminIds__0`, `Categories__0__TopicId`, `Storage__Path`. `Locations` is an optional array of 2–60 character shortcuts.

`Bot:ChatUrl` (or `Bot__ChatUrl`) sets the public or invitation link behind «у спільноті» in the welcome message. The URL is embedded in that phrase; Telegram link previews are disabled so no preview card appears below the message.

## HTTP

- `GET /health` → HTTP 200, `{"status":"ok"}` while the app runs.
- `HEAD /health` → HTTP 200 with no response body.
- `POST /api/telegram/webhook` requires `X-Telegram-Bot-Api-Secret-Token`; missing or wrong secrets return 401, malformed JSON returns 400.
- Authorized updates are written atomically to the local inbox and queued before returning 200. At 4,096 unfinished updates, new updates receive 503 so Telegram can retry. No Telegram network call happens in the request handler.

The HTTPS proxy should forward to the app's HTTP port and limit request bodies to 1 MiB. The webhook secret is compared in constant time. `/health` is a process check, not a check of Telegram permissions.

## Builder and moderation

The ordered `StepDefinition` collection in `Flow/StepCatalog.cs` defines the eight steps: category, title, condition, price/budget, photos, location, description, and contact. Add or remove definitions there; the engine navigates the collection. New listings use a neutral `#оголошення` tag, with no extra buy/sell selection. The single start keyboard is explicitly removed when a flow starts, and resuming a flow does not show the main menu.

Each session keeps a product post and a separate text message below it for the current prompt and action buttons. The action message shows only the step number, such as `Крок 1 з 8`; there is no progress bar. Validation errors appear in the action message. An empty photo count is hidden.

An empty draft shows only the current prompt and buttons. The product post appears after the first listing information is entered and then stays in place while information is collected and reviewed. The first photo is attached to that post with its text as the caption. Multiple photos use an album with the product text on the first photo. Later text changes edit that caption without resending the photos. Telegram does not support appending items to an existing album, so changes to the photo collection can replace the album; its action message is then recreated below it with fresh callbacks. Review does not create another preview copy. After submission, the private product post remains and its action buttons are removed.

Photos are deduplicated by Telegram's unique id, limited to ten, and rendered after a 600 ms quiet period. Only Telegram file identifiers are stored; image bytes are never downloaded. A single photo uses `sendPhoto` or `editMessageMedia`; two to ten use `sendMediaGroup`.

The contact step **always asks the author** which contact may appear publicly: their Telegram profile, or a phone number entered as text or sent as their own Telegram contact. A username is never treated as automatic consent. Contact information is omitted from previews and publication is blocked until the author makes a choice. Choosing a phone number keeps the Telegram username out of the public post. Moderators can edit listing details but cannot choose or change the author's contact on their behalf.

The same formatter renders previews and published posts. It HTML-escapes all user values and checks the **parsed text** against the 1,024 character caption limit. HTML source can be longer because entities such as `&amp;` count as one displayed character. Prices are positive, at most 999,999,999 with two decimal places. Shared listings accept a number, a range, negotiable pricing, free offers, or exchanges. Titles, locations, descriptions and contact data have bounded lengths. Older saved Buy/Sell listings retain their original intent and price rules.

Admin callbacks require a configured Telegram user id, a matching card id and the current revision. A persisted claim plus a per-listing lock makes the first admin action win. Editing uses a separate working copy; cancelling restores the original moderation card. Author and moderator sessions coexist in one user's JSON document, and a switch button appears when both exist. `/start` resumes the author's flow, or the moderator session if no author flow exists. Maintenance removes stale sessions left by interrupted transitions.

`/start` recreates the saved draft's visible UI, clears unfinished cancel/restart confirmations, and retains its entered fields. `/cancel` sends a fresh confirmation so it remains usable if the previous prompt was hidden or deleted. Replaced UI messages are removed after their replacements are sent successfully. If displaying a command's response fails, the user's command is retained. A stale session without a usable draft returns to the main menu.

Old draft sessions are upgraded to the separate post/actions layout when resumed. Old drafts and resubmissions without an explicit contact choice return to the contact step. An older pending listing without that choice must be returned to its author through decline/resubmit before it can be published; contact consent is not inferred from previously saved usernames or phone numbers. Existing published Telegram posts are not modified.

## Persistence and recovery

**Storage:Path must point to a writable directory retained across restarts and deployments. Run exactly one app instance per storage directory.** Locks and caches are in process; this file-based implementation does not coordinate multiple replicas.

```text
data/
  listings/{8-char-id}.json
  sessions/{userId}.json      # independent Author and Moderator slots + active mode
  inbox/{updateId}.json       # durable admission, completion and retry state
```

Repositories load caches at startup, return detached snapshots, serialize writes by key, flush temporary files, and replace destinations with `File.Move(overwrite: true)`. Invalid persisted JSON fails startup rather than silently losing records. Back up this directory and restrict filesystem access: it contains names, contact details and Telegram file ids. Unused temporary files can be removed while the process is stopped.

Drafts inactive for `DraftTtlDays` (default 7) are removed by maintenance, including their active sessions. Pending, declined and published records remain. Completed inbox receipts are retained for seven days. Failed updates retry with backoff; unfinished inbox work resumes on restart. Recent input ids and callback nonces protect retries from applying a field twice. Failed moderation delivery, card updates and author notifications are retried by maintenance. A terminal Telegram 403 is logged and treated as handled for author notifications; blocked admins are retried for missing moderation cards.

Telegram provides no idempotency key for posting. A crash between an API acknowledgement and a disk commit cannot guarantee exactly-once external effects. Before publication the bot persists `operation: "Publishing"`; transport failures or interrupted publications become `"Uncertain"` and **are never automatically reposted**. To reconcile:

1. Stop the app and back up storage.
2. Inspect the category topic. If the post exists, fill `publishedMessageIds`, `postLink`, `status: "Published"`, `operation: "None"`, and `authorNotified: false` in its JSON.
3. If no post exists, set `operation: "None"` and `moderatorId: null` while keeping `status: "PendingApproval"`.
4. Increment `revision` and restart. Maintenance refreshes cards and sends outstanding notifications.

Transient send acknowledgements may also duplicate non-publication UI or notification messages after a crash. Publication uses the conservative recovery rule above. Noncritical cleanup tolerates old or already-deleted Telegram messages. No edits/deletes after publication are exposed by the bot.

## Source layout

```text
buy-sell-bot.slnx
Directory.Build.props
global.json
buy-sell-bot.Server/
  Program.cs
  appsettings.json
  Api/Endpoints.cs
  Bot/BuilderRenderer.cs, BuilderUi.cs, CallbackData.cs, PhotoDebouncer.cs, UpdateRouter.cs
  Flow/StepDefinition.cs, StepCatalog.cs, FlowEngine.cs, PriceParser.cs
  Domain/Models.cs
  Storage/JsonFileStore.cs, Repositories.cs
  Moderation/ModerationService.cs
  Publishing/PostFormatter.cs, Publisher.cs, PublicationBlockedException.cs
  Infrastructure/Options.cs, TelegramGateway.cs, UserLocks.cs, UpdateInbox.cs,
                 UpdateProcessor.cs, MaintenanceService.cs, ExceptionDiagnostics.cs
  Texts/Texts.cs
  Properties/launchSettings.json
buy-sell-bot.Tests/
  FlowTests.cs, PriceParserTests.cs, FormatterAndCallbackTests.cs, RepositoryTests.cs
  WorkflowTests.cs, RecoveryTests.cs, TelegramGatewayTests.cs, WebhookTests.cs, TestSupport.cs
  PublicationFailureTests.cs, BuilderUiTests.cs, ContactConsentTests.cs
```

The unused `buy-sell-bot.client` starter directory is excluded from the solution and build. The bot has no frontend or database.

Tests cover step transitions and numbering, price formats, HTML escaping and caption bounds, stale callbacks, atomic JSON writes, session isolation, competing moderators, decline/resubmit, moderator publish/cancel, uncertain publication recovery, inbox restart, webhook authentication, hidden start keyboards, separate product/actions messages, caption edits and album replacement, explicit contact consent, album debounce, safe editing/deleting, and 403/429 responses.

```powershell
dotnet test buy-sell-bot.slnx -m:1
dotnet publish buy-sell-bot.Server -c Release -o artifacts/publish
```

API references: [Telegram Bot API](https://core.telegram.org/bots/api), [Telegram.Bot package](https://www.nuget.org/packages/Telegram.Bot/22.10.3.3), [Telegram.Bot v22 migration](https://telegrambots.github.io/book/migrate/Version-22.x.html).
