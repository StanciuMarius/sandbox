<#
.SYNOPSIS
    Drive a running s&box Sandbox session from the command line.

.DESCRIPTION
    Needs nothing installed. Windows PowerShell ships with Windows, and the
    WebSocket server comes from System.Net.HttpListener in the framework.

    The game cannot accept connections - s&box gives game code a WebSocket client
    and no listener - so this script listens and waits for the game to dial in.
    The game retries every second or so, which is where the sub-second latency on
    each call comes from.

    One call per invocation: bind, wait for the game, send the verb, print the
    reply, exit. No daemon, no background process, nothing to clean up.

.EXAMPLE
    sbx.ps1
    Lists the verbs the connected game offers.

.EXAMPLE
    sbx.ps1 spawn_prop --ident models/dev/box.vmdl

.EXAMPLE
    sbx.ps1 list_props --limit 10 -Json
#>

[CmdletBinding()]
param(
    # The verb to call. Omit to list what the game offers.
    [Parameter(Position = 0)]
    [string] $Verb,

    # Print raw JSON rather than formatted text.
    [switch] $Json,

    # Seconds to wait for the game to connect.
    [int] $TimeoutSeconds = 30,

    # Verb arguments, as --name value pairs.
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Rest
)

$ErrorActionPreference = 'Stop'

# s&box only dials these, and scans them in this order
$CandidatePorts = @(8080, 8443, 80, 443)

function Write-Info { param([string] $Message) Write-Host $Message }

<#
    Turn --key value pairs into a hashtable. A --flag with no value becomes
    "true", matching how the Node CLI behaves.
#>
function ConvertTo-ArgTable {
    param([string[]] $Tokens)

    $table = @{}
    if (-not $Tokens) { return $table }

    for ($i = 0; $i -lt $Tokens.Count; $i++) {
        $token = $Tokens[$i]
        if (-not $token.StartsWith('--')) { continue }

        $key = $token.Substring(2)
        $next = if ($i + 1 -lt $Tokens.Count) { $Tokens[$i + 1] } else { $null }

        if ($null -eq $next -or $next.StartsWith('--')) {
            $table[$key] = 'true'
        }
        else {
            $table[$key] = $next
            $i++
        }
    }

    return $table
}

<#
    Bind the first port the game might dial. A busy port is expected, not an
    error - another sbx call may be mid-flight.
#>
function Start-Listener {
    foreach ($port in $CandidatePorts) {
        $listener = New-Object System.Net.HttpListener
        $listener.Prefixes.Add("http://localhost:$port/")

        try {
            $listener.Start()
            Write-Verbose "listening on http://localhost:$port/"
            return $listener
        }
        catch {
            $listener.Close()
            Write-Verbose "port $port unavailable: $($_.Exception.Message)"
        }
    }

    throw "Could not bind any of $($CandidatePorts -join ', '). s&box only allows localhost on those ports, so one must be free. Another sbx call may still be running."
}

<# Wait for the game's WebSocket upgrade and return the socket. #>
function Wait-ForGame {
    param(
        [System.Net.HttpListener] $Listener,
        [int] $TimeoutMs
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)

    while ([DateTime]::UtcNow -lt $deadline) {
        $remaining = [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds
        if ($remaining -le 0) { break }

        $contextTask = $Listener.GetContextAsync()

        if (-not $contextTask.Wait($remaining)) { break }

        $context = $contextTask.Result

        if (-not $context.Request.IsWebSocketRequest) {
            # something else on the port - refuse it and keep waiting
            $context.Response.StatusCode = 400
            $context.Response.Close()
            continue
        }

        # [NullString]::Value, not $null - PowerShell turns $null into "" here and
        # AcceptWebSocketAsync rejects an empty subprotocol
        return $context.AcceptWebSocketAsync([NullString]::Value).GetAwaiter().GetResult().WebSocket
    }

    throw "Timed out after $([int]($TimeoutMs / 1000))s waiting for the game. Is Sandbox running with the bridge on? Q menu > Utilities > AI Agent."
}

<# Read one complete text message. #>
function Receive-Message {
    param(
        [System.Net.WebSockets.WebSocket] $Socket,
        [int] $TimeoutMs
    )

    $buffer = New-Object byte[] 65536
    $segment = New-Object 'System.ArraySegment[byte]' -ArgumentList @(, $buffer)
    $text = New-Object System.Text.StringBuilder

    do {
        $task = $Socket.ReceiveAsync($segment, [Threading.CancellationToken]::None)

        if (-not $task.Wait($TimeoutMs)) { throw 'Timed out waiting for the game to reply.' }

        $result = $task.Result

        if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
            throw 'The game closed the connection.'
        }

        [void] $text.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count))

    } while (-not $result.EndOfMessage)

    return $text.ToString()
}

function Send-Message {
    param(
        [System.Net.WebSockets.WebSocket] $Socket,
        [string] $Text
    )

    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    $segment = New-Object 'System.ArraySegment[byte]' -ArgumentList @(, $bytes)

    # GetResult() returns VoidTaskResult, which PowerShell would emit to the
    # pipeline and corrupt our output with
    [void] $Socket.SendAsync(
        $segment,
        [System.Net.WebSockets.WebSocketMessageType]::Text,
        $true,
        [Threading.CancellationToken]::None
    ).GetAwaiter().GetResult()
}

function Show-Verbs {
    param($Hello)

    Write-Info "Connected to $($Hello.game)$(if ($Hello.isHost) { ' (host)' })."
    Write-Info ''
    Write-Info 'Verbs:'
    Write-Info ''

    foreach ($verb in $Hello.verbs) {
        Write-Info "  $($verb.name)"
        Write-Info "      $($verb.description)"

        if ($verb.args) {
            foreach ($property in $verb.args.PSObject.Properties) {
                Write-Info "      --$($property.Name)  $($property.Value)"
            }
        }

        Write-Info ''
    }
}

# ---- main ---------------------------------------------------------------

$listener = $null
$socket = $null
$exitCode = 0

try {
    $timeoutMs = $TimeoutSeconds * 1000

    $listener = Start-Listener
    $socket = Wait-ForGame -Listener $listener -TimeoutMs $timeoutMs

    # the game announces its verb table the moment it connects
    $hello = Receive-Message -Socket $socket -TimeoutMs $timeoutMs | ConvertFrom-Json

    if (-not $Verb) {
        if ($Json) { $hello | ConvertTo-Json -Depth 10 } else { Show-Verbs -Hello $hello }
    }
    else {
        $request = @{
            id   = '1'
            verb = $Verb
            args = ConvertTo-ArgTable -Tokens $Rest
        }

        Send-Message -Socket $socket -Text ($request | ConvertTo-Json -Depth 10 -Compress)

        $reply = Receive-Message -Socket $socket -TimeoutMs $timeoutMs | ConvertFrom-Json

        if ($reply.ok) {
            $reply.result | ConvertTo-Json -Depth 10
        }
        else {
            if ($Json) { $reply | ConvertTo-Json -Depth 10 } else { Write-Error $reply.error -ErrorAction Continue }
            $exitCode = 1
        }
    }
}
catch {
    Write-Error $_.Exception.Message -ErrorAction Continue
    $exitCode = 1
}
finally {
    if ($socket) {
        try {
            $socket.CloseAsync(
                [System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure,
                'done',
                [Threading.CancellationToken]::None
            ).Wait(2000) | Out-Null
        }
        catch {}

        $socket.Dispose()
    }

    if ($listener) {
        try { $listener.Stop() } catch {}
        $listener.Close()
    }
}

exit $exitCode
