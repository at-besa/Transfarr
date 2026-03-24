# f:\DEV\besa\Transfarr\start-two-clients.ps1

Write-Host "Killing existing dotnet processes to free local development ports..."
taskkill /F /IM dotnet.exe /T 2>$null

Write-Host ""

# CRITICAL: Force Development mode so ASP.NET Core loads the Blazor WebAssembly static assets dynamically without a 'publish' step.
$env:ASPNETCORE_ENVIRONMENT="Development"

Write-Host "--- 🌍 Starting Global Signaling Server ---"
Start-Process "dotnet" -ArgumentList "run --project Transfarr.Signaling/Transfarr.Signaling.csproj --no-launch-profile --urls `"http://localhost:5135`""
Start-Sleep -Seconds 2

Write-Host "--- 💻 Starting Primary Node (Client A) ---"
Start-Process "dotnet" -ArgumentList "run --project Transfarr.Node/Transfarr.Node.csproj --no-launch-profile --urls `"http://localhost:5155`""

Write-Host "--- 🤖 Starting Secondary Node (Mock User B) ---"
Start-Process "dotnet" -ArgumentList "run --project Transfarr.Node/Transfarr.Node.csproj --no-launch-profile --urls `"http://localhost:5156`""

Write-Host ""
Write-Host "=============================================="
Write-Host "Done! The UI is now NATIVELY hosted inside the daemon! 🎉"
Write-Host "=============================================="
Write-Host ""
Write-Host "👉 Client A (You):"
Write-Host "   Browser: http://localhost:5155"
Write-Host ""
Write-Host "👉 Client B (Mock User):"
Write-Host "   Browser: http://localhost:5156"
Write-Host ""
Write-Host "The UI will automatically map to its local node API."
