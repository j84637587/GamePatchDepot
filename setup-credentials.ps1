[CmdletBinding()]
param(
    [string] $CredentialFile = (Join-Path $env:LOCALAPPDATA 'GamePatchDepot\credentials.json'),
    [string] $SigningPrivateKeyFile = 'F:\Secure\GamePatchDepot\signing-private.pem'
)

$ErrorActionPreference = 'Stop'

function Read-RequiredValue {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CurrentValue,

        [Parameter(Mandatory = $true)]
        [string] $Prompt
    )

    if (-not [string]::IsNullOrWhiteSpace($CurrentValue)) {
        return $CurrentValue
    }

    $value = Read-Host $Prompt
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "$Prompt 不可空白。"
    }

    return $value.Trim()
}

if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') {
    throw '此腳本必須在 Windows 執行，因為憑證使用 Windows DPAPI 加密。'
}

$accountId = $env:PATCH_R2_ACCOUNT_ID
$endpoint = $env:PATCH_R2_ENDPOINT
if ([string]::IsNullOrWhiteSpace($accountId) -and [string]::IsNullOrWhiteSpace($endpoint)) {
    $accountId = Read-RequiredValue '' 'Cloudflare Account ID'
}

$accessKeyId = Read-RequiredValue $env:PATCH_R2_ACCESS_KEY_ID 'R2 Access Key ID'

if ([string]::IsNullOrWhiteSpace($env:PATCH_R2_SECRET_ACCESS_KEY)) {
    $secureSecret = Read-Host 'R2 Secret Access Key' -AsSecureString
}
else {
    $secureSecret = ConvertTo-SecureString -String $env:PATCH_R2_SECRET_ACCESS_KEY -AsPlainText -Force
}

if (-not (Test-Path -LiteralPath $SigningPrivateKeyFile -PathType Leaf)) {
    throw "找不到簽章私鑰：$SigningPrivateKeyFile"
}

$credentialDirectory = Split-Path -Parent $CredentialFile
New-Item -ItemType Directory -Path $credentialDirectory -Force | Out-Null

$credentialData = [ordered]@{
    schemaVersion         = 1
    accountId            = $accountId
    endpoint             = $endpoint
    accessKeyId          = $accessKeyId
    secretAccessKey      = ConvertFrom-SecureString -SecureString $secureSecret
    signingPrivateKeyFile = (Resolve-Path -LiteralPath $SigningPrivateKeyFile).Path
}

$json = $credentialData | ConvertTo-Json
[System.IO.File]::WriteAllText($CredentialFile, $json, [System.Text.UTF8Encoding]::new($false))

$secureSecret = $null
$json = $null

Write-Host "已儲存 Windows 使用者加密憑證：$CredentialFile"
Write-Host '之後直接使用 .\patch.ps1，不必再次輸入環境變數。'
