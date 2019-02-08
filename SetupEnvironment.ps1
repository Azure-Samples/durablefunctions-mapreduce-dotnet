Write-Host "Adding Powershell Gallery repository for Azure Resource Manager installation. Please type 'y' to accept addition of this gallery repo so we can run Azure Resource Manager commands to configure deployment of the template." -ForegroundColor Yellow

Set-PSRepository PSGallery -InstallationPolicy Trusted

Write-Host "Thanks! Installing Azure RM PowerShell module..." -ForegroundColor Yellow
Install-Module -Name AzureRM >$null

Write-Host "Installing Chocolatey so we can automate installation of other components needed to build the solution..." -ForegroundColor Yellow
Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://chocolatey.org/install.ps1'))

Write-Host "Installing components via Chocolatey..." -ForegroundColor Yellow
cinst dotnetcore-sdk netfx-4.6.1-devpack -y