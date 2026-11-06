$botToken = "<BOT_TOKEN>"
$webhookSecret = "<SECRET_TOKEN>"
$webhookUrl = "<WEBHOOK_URL>"

Invoke-WebRequest `
    -Method Post `
    -Uri "https://api.telegram.org/bot${botToken}/setWebhook" `
    -Body @{
    url             = $webhookUrl
    secret_token    = $webhookSecret
    allowed_updates = '["message","callback_query"]'
    max_connections = 1
}
        max_connections = 1
    }
