<#
    .Synopsis
    Transfers yellow taxi data from NYC trip website to an Azure Storage Blob container

    .Example
        # Download all data to container 'nyctaxidata' & subfolder 'current'
        Get-Dataset -connectionString "DefaultEndpointsProtocol=https;AccountName=..." -containerName nyctaxidata -subfolderName current

    .Example
        # Download all data to container 'nyctaxidata' root
        Get-Dataset -connectionString "DefaultEndpointsProtocol=https;AccountName=..." -containerName nyctaxidata
#>
param(
    # The subscription id where the template will be deployed.
    [Parameter(Mandatory = $True)]
    [string]$subscriptionId,
    # The name of the resource group in to which to deploy the storage account that will hold the Taxi data
    [Parameter(Mandatory = $True)]
    [string]$resourceGroupName,
    # The name of the storage account to create
    [Parameter(Mandatory = $True)]
    [string]$storageAccountName,
    # Optional, Azure region to which to deploy all resources. Defaults to West US 2
    [string]$region = "westus2",
    # The name of the container in your Azure Storage account in to which the files will be transferred
    [parameter(Mandatory = $true)]
    [string]$containerName,
    # [Optional] The name of the subfolder within the container in to which the files will be transferred
    [string]$subfolderName = $null)

Function Get-BlobFilePath {
    param([string]$folder,
        [string]$filename)

    return "$folder/$filename".TrimStart('/')
}

# sign in
$currentSubscription = Get-AzSubscription -SubscriptionID $subscriptionId -ErrorAction SilentlyContinue
if (!$currentSubscription) {
    Write-Host "Logging in..."
    Login-AzAccount >$null
}

$ErrorActionPreference = "Stop"

# select subscription
Select-AzSubscription -Subscription $subscriptionId >$null

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

$targetStorageAccount = Get-AzStorageAccount -ResourceGroupName $resourceGroupName -Name $storageAccountName -ErrorAction SilentlyContinue 
if (!$targetStorageAccount) {
    Write-Host "Creating storage account..."
    $targetStorageAccount = New-AzStorageAccount -ResourceGroupName $resourceGroupName -Name $storageAccountName -SkuName Standard_LRS -Location $region -Kind BlobStorage -AccessTier Hot
}

$context = $targetStorageAccount.Context

$targetContainer = Get-AzStorageContainer -Context $context -Name $containerName -ErrorAction SilentlyContinue
if (!$targetContainer) {
    Write-Host "Creating container..."
    $targetContainer = New-AzStorageContainer -Context $context -Name $containerName -Permission Container
}

for ($i = 1; $i -le 12; $i++) {
    $targetFilename = "yellow_tripdata_2017-$($i.ToString('00')).csv"
    Write-Output "Queuing download for $targetFilename ..."
    Start-AzStorageBlobCopy `
        -AbsoluteUri "https://s3.amazonaws.com/nyc-tlc/trip+data/$targetFilename" `
        -DestContainer $containerName `
        -DestBlob (Get-BlobFilePath $subfolderName $targetFilename) `
        -DestContext $context -Force -ErrorAction SilentlyContinue >$null
}

$ErrorActionPreference = "Continue"
$allDone = $false
$lastFileCompleted = $null
Write-Output "Waiting for downloads to complete..."

if ([System.String]::IsNullOrEmpty($subfolderName)) {
    Write-Host "Save this URL for later: $($targetContainer.CloudBlobContainer.Uri)/" -ForegroundColor Yellow
}
else {
    Write-Host "Save this URL for later: $($targetContainer.CloudBlobContainer.Uri)/$($subfolderName)/" -ForegroundColor Yellow
}

Write-Output "You can Ctrl+C if you don't want to wait, otherwise Progress is shown at the top of this window."

for ($i = 1; $i -le 12; $i++) {
    $targetFilename = "yellow_tripdata_2017-$($i.ToString('00')).csv"
    Get-AzStorageBlobCopyState -Context $context -Blob (Get-BlobFilePath $subfolderName $targetFilename) -Container $containerName -WaitForComplete >$null
}

Write-Host "Done!" -ForegroundColor Green
