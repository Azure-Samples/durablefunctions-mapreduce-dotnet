<#
 .SYNOPSIS
    Deploys the Serverless Map Reduce template to Azure

 .DESCRIPTION
    Deploys an Azure Resource Manager template for 2 Azure Functions + related Storage and Application Insights instances
#>

param(
    # The subscription id where the template will be deployed.
    [Parameter(Mandatory = $True)]
    [string]$subscriptionId,
    # The base name of all resources deployed. eg: <baseName>rg, <baseName>storage, etc.
    [Parameter(Mandatory = $True)]
    [string]$baseName,
    # Optional, Azure region to which to deploy all resources. Defaults to West US 2
    [string]$region = "westus2"   
)

function ZipDeploy([string]$resourceGroupName, [string]$appname, [string]$zipFilePath, [string]$platform) {
    dotnet pack -c Release -v q >$null
    Remove-Item bin/release/$platform/publish/local.settings.json -Force -ErrorAction SilentlyContinue >$null
    Remove-Item deploy.zip -Force -ErrorAction SilentlyContinue >$null
    Compress-Archive -Path "bin/release/$platform/publish/*" -DestinationPath $zipFilePath >$null

    $publishProfile = [xml](Get-AzWebAppPublishingProfile -ResourceGroupName $resourceGroupName -Name $appname)
    foreach ($profile in $publishProfile.FirstChild.ChildNodes) {
        if ($profile.publishMethod -eq "MSDeploy") {
            $creds = @{
                username = $profile.userName
                password = $profile.userPWD
                url      = $profile.publishUrl
            }
            break
        }
    }

    $base64AuthInfo = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(("{0}:{1}" -f $creds.username, $creds.password)))
    $userAgent = "powershell/1.0"
    Invoke-RestMethod -Uri "https://$($creds.url)/api/zipdeploy" -Headers @{Authorization = ("Basic {0}" -f $base64AuthInfo)} -UserAgent $userAgent -Method POST -InFile $zipFilePath -ContentType "multipart/form-data"

    Remove-Item deploy.zip
}

if ($baseName.Length -gt 18 -or [System.String]::IsNullOrWhiteSpace($baseName)) {
    Write-Error "baseName must be between 1 and 18 characters in length" -RecommendedAction "Reduce length of baseName parameter" -Category InvalidArgument
    return
}

$templateFilePath = "template.json"
$resourceGroupName = $baseName + "RG"

# sign in
$currentSubscription = Get-AzSubscription -SubscriptionID $subscriptionId -ErrorAction SilentlyContinue
if (!$currentSubscription) {
    Write-Host "Logging in..."
    Login-AzAccount >$null
}

$ErrorActionPreference = "Stop"

# select subscription
Select-AzSubscription -Subscription $subscriptionId >$null

# Register required RPs
foreach ($resourceProvider in @("microsoft.insights", "microsoft.storage", "microsoft.web")) {
    Register-AzResourceProvider -ProviderNamespace $resourceProvider >$null
}

#Create or check for existing resource group
$resourceGroup = Get-AzResourceGroup -Name $resourceGroupName -ErrorAction SilentlyContinue
if (!$resourceGroup) {
    if (!$region) {
        Write-Host "Resource group '$resourceGroupName' does not exist. To create a new resource group, please enter a location."
        $region = Read-Host "region"
    }
    Write-Host "Creating resource group '$resourceGroupName' in location '$region'"
    New-AzResourceGroup -Name $resourceGroupName -Location $region >$null
}
else {
    Write-Host "Using existing resource group '$resourceGroupName'"
}

# Start the deployment
Write-Host "Deploying Azure resources..."
New-AzResourceGroupDeployment -ResourceGroupName $resourceGroupName -TemplateFile $templateFilePath -base_name $baseName -region $region >$null

Write-Host "Deploying v1 app bits..." -ForegroundColor Yellow
$appname = $baseName + "v1"

Set-Location .\ServerlessMapReduce.v1
ZipDeploy $resourceGroupName $appname "deploy.zip" "net461"

Write-Host "Deploying v2 app bits..." -ForegroundColor Yellow
$appname = $baseName + "v2"

Set-Location ..\ServerlessMapReduce.v2
ZipDeploy $resourceGroupName $appname "deploy.zip" "netstandard2.0"

Set-Location ..

Write-Host "Done!" -ForegroundColor Green
