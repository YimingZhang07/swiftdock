# Read all retained rolling segments: one session can cross a size or date boundary.
function Read-SwiftdockSession {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$Directory,
        [Parameter(Mandatory=$true)][int]$ProcessId,
        [Parameter(Mandatory=$true)][datetime]$StartedAfter
    )
    $events = @(Get-ChildItem -LiteralPath $Directory -File -Filter 'swiftdock-*.jsonl' |
        Where-Object { $_.Name -match '^swiftdock-\d{8}(_\d{3,})?\.jsonl$' } |
        Sort-Object LastWriteTimeUtc, Name -Descending |
        ForEach-Object { Get-Content -LiteralPath $_.FullName -Encoding UTF8 } |
        ForEach-Object {
            # Another process may still be appending its last line. The selected process
            # has exited and drained its logger before the measurement scripts read here.
            try { $entry = $_ | ConvertFrom-Json -ErrorAction Stop }
            catch { return }
            if ($entry.Pid -eq $ProcessId -and $entry.Session -and $entry.'@t' -and
                [DateTimeOffset]::Parse($entry.'@t').UtcDateTime -ge $StartedAfter.ToUniversalTime()) { $entry }
        })
    $startup = $events | Where-Object { $_.EventId.Name -eq 'startup' } |
        Sort-Object '@t' -Descending | Select-Object -First 1
    $sessionId = [Guid]::Empty
    if ($null -eq $startup -or -not [Guid]::TryParse([string]$startup.Session, [ref]$sessionId)) {
        throw "Startup CLEF session missing for process ${ProcessId}: $Directory"
    }
    $events | Where-Object { $_.Session -eq $startup.Session } | Sort-Object '@t'
}
