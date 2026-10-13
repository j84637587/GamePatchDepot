[CmdletBinding()]
param(
    [string] $CredentialFile = (Join-Path $env:LOCALAPPDATA 'GamePatchDepot\credentials.json'),

    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]] $PatchArguments
)

$ErrorActionPreference = 'Stop'
$environmentChanges = @{}
$secretAccessKey = $null
$exitCode = 1

function Set-TemporaryEnvironmentVariable {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [AllowEmptyString()]
        [string] $Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return
    }

    $currentValue = [Environment]::GetEnvironmentVariable($Name, 'Process')
    if ([string]::IsNullOrWhiteSpace($currentValue)) {
        $script:environmentChanges[$Name] = $currentValue
        [Environment]::SetEnvironmentVariable($Name, $Value, 'Process')
    }
}

try {
    if (Test-Path -LiteralPath $CredentialFile -PathType Leaf) {
        $credentials = Get-Content -LiteralPath $CredentialFile -Raw | ConvertFrom-Json
        if ($credentials.schemaVersion -ne 1) {
            throw "不支援的憑證檔版本：$($credentials.schemaVersion)"
        }

        $secureSecret = ConvertTo-SecureString -String $credentials.secretAccessKey
        $secretAccessKey = [System.Net.NetworkCredential]::new('', $secureSecret).Password

        Set-TemporaryEnvironmentVariable 'PATCH_SIGNING_PRIVATE_KEY_FILE' $credentials.signingPrivateKeyFile
        Set-TemporaryEnvironmentVariable 'PATCH_R2_ACCOUNT_ID' $credentials.accountId
        Set-TemporaryEnvironmentVariable 'PATCH_R2_ENDPOINT' $credentials.endpoint
        Set-TemporaryEnvironmentVariable 'PATCH_R2_ACCESS_KEY_ID' $credentials.accessKeyId
        Set-TemporaryEnvironmentVariable 'PATCH_R2_SECRET_ACCESS_KEY' $secretAccessKey
    }

    Push-Location $PSScriptRoot
    try {
        & dotnet run `
            --project (Join-Path $PSScriptRoot 'src\PatchPublisher') `
            --configuration Release `
            -- @PatchArguments

        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}
finally {
    foreach ($entry in $environmentChanges.GetEnumerator()) {
        if ($null -eq $entry.Value) {
            Remove-Item -LiteralPath "Env:$($entry.Key)" -ErrorAction SilentlyContinue
        }
        else {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }

    $secretAccessKey = $null
}

exit $exitCode
