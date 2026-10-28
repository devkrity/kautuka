$login = Invoke-RestMethod -Uri 'http://localhost:8000/api/auth/login' -Method POST -ContentType 'application/json' -Body '{"username":"admin","password":"admin"}'
$token = $login.accessToken
$hdrs = @{"Authorization"="Bearer $token"; "Content-Type"="application/json"}

$body = @{
    name = "simple"
    modelId = "gemma4"
    systemPrompt = "You are a helpful receptionist at a Speech Pathology Clinic. You have access to a dice-rolling tool called roll_dice. When the user asks to roll a die, roll dice, or generate a random number, you MUST call the roll_dice tool immediately - never simulate or guess the result."
    allowCommandExecution = $false
    mcpServerIds = @("microsoft-learn-mcp")
    skillAttachments = @(@{ skillId = "roll-dice"; mode = "require_approval" })
    session = @{
        historyEnabled = $true
        maxHistoryRuns = 5
        summariesEnabled = $true
        summaryModelId = $null
    }
    sandbox = @{
        enabled = $true
        allowedLanguages = @()
        maxMemory = "2g"
        maxTimeout = 120
    }
    approvalConfig = @{
        requireMcpIds = @()
        timeoutSeconds = 300
        requireMcpFunctionFilters = @{}
        disabledMcpFunctions = @{}
    }
    persona = @{
        category = "office_worker"
        style = "professional"
        tone = "empathetic"
        verbosity = "concise"
        office_worker = @{
            company_name = "DevKrity"
            title = "Receptionist"
            department = "Admin"
            industry = "Speech Pathology Clinic"
        }
    }
} | ConvertTo-Json -Depth 10

$result = Invoke-RestMethod -Uri 'http://localhost:8000/api/settings/agents/simple' -Method PUT -Headers $hdrs -Body $body
Write-Host "Updated. systemPrompt:" $result.systemPrompt
