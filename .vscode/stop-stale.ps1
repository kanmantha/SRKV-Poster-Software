# Stops every process that can lock this project's build outputs so builds never
# fail with "DailyPosterGenerator.dll is being used by another process".
# Scoped strictly to this project folder - other apps' dotnet processes are safe.
$ErrorActionPreference = 'SilentlyContinue'

$projectRoot = Split-Path -Parent $PSScriptRoot
$binRoot = Join-Path $projectRoot 'bin'

# 1) dotnet / app-host processes whose command line mentions this project
#    (covers: dotnet run, dotnet watch, dotnet exec, VS F5 with app host).
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='DailyPosterGenerator.exe'" |
    Where-Object { $_.CommandLine -like '*SRKV_Poster_Software*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

# 2) Anything (any process name) that still has one of our built binaries loaded -
#    catches exotic launchers regardless of how they were started.
Get-Process -Name dotnet, DailyPosterGenerator -ErrorAction SilentlyContinue |
    Where-Object { $_.Id -ne $PID } |
    ForEach-Object {
        try
        {
            if (@($_.Modules | Where-Object { $_.FileName -like "$binRoot*" }).Count -gt 0)
            {
                Stop-Process -Id $_.Id -Force
            }
        }
        catch
        {
            # Modules can throw for protected processes - nothing to free there.
        }
    }

# 3) Whatever is listening on the ports this app uses (from launchSettings + default).
$ports = @(5011)
$launchSettings = Join-Path $projectRoot 'Properties\launchSettings.json'
if (Test-Path $launchSettings)
{
    $matches = Select-String -Path $launchSettings -Pattern '://localhost:(\d+)' -AllMatches
    foreach ($m in $matches.Matches)
    {
        $ports += [int]$m.Groups[1].Value
    }
}

foreach ($port in ($ports | Sort-Object -Unique))
{
    Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.OwningProcess -Force }
}

# Give Windows a moment to release file handles before MSBuild retries the copy.
Start-Sleep -Milliseconds 1500
exit 0
