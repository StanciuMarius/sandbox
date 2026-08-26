<#
.SYNOPSIS
	Isolated s&box development environments, one per feature.

.DESCRIPTION
	An env is a git worktree, a branch and an s&box editor instance of its own. The
	worktree isolates the code. Rewriting the project's Ident isolates everything the
	engine keys off it - the asset cache, the data directory, the input config and the
	saved convars - and a per-env MCP port isolates the editor's tool server.

	Without the Ident rewrite two editors would share
	sbox/.source2/assets.marsz.sandboxmcp.cache and sbox/data/marsz/sandboxmcp#local,
	which is why setup rewrites it and why the modified sandbox.sbproj is hidden from
	git rather than committed.

.EXAMPLE
	./tools/agent-env/env.ps1 setup rope-slack
	./tools/agent-env/env.ps1 shot rope-slack -Name after-fix
	./tools/agent-env/env.ps1 teardown rope-slack
#>
[CmdletBinding()]
param(
	[Parameter(Mandatory, Position = 0)]
	[ValidateSet('setup', 'open', 'status', 'play', 'shot', 'mcp', 'teardown')]
	[string]$Command,

	# The env's name. Lowercase, digits and dashes - it becomes a branch, a directory
	# and a project Ident, all of which are fussier than a sentence.
	[Parameter(Position = 1)]
	[string]$Feature,

	# shot: file name under the run directory, without extension.
	[string]$Name = 'shot',

	# shot: a CameraComponent id or its game object's id. Empty means the scene's main camera.
	[string]$Camera = '',

	# shot: capture the editor viewport instead of a camera in the scene.
	[switch]$EditorView,

	[int]$Width = 1280,
	[int]$Height = 720,

	# mcp: the tool to invoke, and a json object of its arguments.
	[string]$Tool,
	[string]$Arguments = '{}',

	# play: stop rather than start.
	[switch]$Stop,

	# setup: leave the editor in edit mode rather than pressing play.
	[switch]$NoPlay,

	# setup: skip seeding compiled and cloud assets from the main checkout. Saves the
	# disk, costs a full recompile and re-download on first launch.
	[switch]$NoSeed,

	# teardown: also delete the branch, and the engine state this env's Ident created.
	[switch]$DeleteBranch,
	[switch]$Purge,

	# setup: how long to wait for the editor to answer. Seeded, that's seconds; with
	# -NoSeed the engine compiles and downloads everything first, which is minutes.
	[int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# MCP ports we hand out to envs. 7269 is the editor default, and therefore whichever
# editor the user launched by hand, so we start above it and never allocate it.
$script:PortFirst = 7270
$script:PortLast = 7299
$script:DefaultPort = 7269

# Bridge ports, where an env's sbx CLI listens for its own game to dial in. This list is
# the engine's and cannot be widened: WebSocket.Connect gates on Http.IsAllowedAsync,
# which waives the loopback port check only for editor, standalone and dedicated-server
# code - never for game code, not even in editor play mode. Measured, not assumed: a game
# pointed at 8443 connects, the same game pointed at 9451 never dials.
#
# 80 and 443 are left out because binding them needs elevation on Windows. So two envs
# can hold a bridge at once, and the user's own session competes for the same two. Envs
# past that still work - everything except the sbx verbs goes over MCP, which has no cap.
#
# 8443 before 8080 on purpose. An unconfigured game scans 8080 first, so leaving it free
# is what keeps the user's own sbx calls landing in the user's own game. It narrows the
# overlap rather than removing it: a scanning game that finds 8080 quiet moves on to 8443
# and can still answer an env's call. Pinning both sides is the only complete fix, and
# the user's session is theirs to pin - see the skill.
$script:BridgePorts = @(8443, 8080)


# ---------------------------------------------------------------- paths

function Write-Utf8NoBom
{
	# s&box and Claude Code both parse these files with readers that treat a BOM as
	# content, so Set-Content -Encoding utf8 (which writes one on 5.1) is not usable here.
	param([string]$Path, [string]$Text)

	[System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding $false))
}

function Invoke-Git
{
	<#
		Run git and return its exit code with its output.

		git reports ordinary progress on stderr - "Preparing worktree" and friends. Under
		$ErrorActionPreference = 'Stop', Windows PowerShell turns each of those lines into
		a NativeCommandError and kills the script on a command that actually succeeded, so
		the exit code is the only signal worth reading.

		Arguments come in as one explicit array, never as remaining arguments. An advanced
		function gets the common parameters, and a git flag that unambiguously prefixes one
		of them is bound there instead of being passed on: `-D` is swallowed by -Debug, so
		`branch -D x` silently ran as `branch x` and created the branch it was told to
		delete. `-v`, `-e` and `-o` collide the same way.
	#>
	param([Parameter(Mandatory)][string[]]$GitArgs)

	$previous = $ErrorActionPreference
	$ErrorActionPreference = 'Continue'

	try { $output = & git @GitArgs 2>&1 }
	finally { $ErrorActionPreference = $previous }

	return [pscustomobject]@{
		ExitCode = $LASTEXITCODE
		Output   = (@($output) | ForEach-Object { "$_" }) -join "`n"
	}
}

function Assert-Git
{
	<# Run git, and throw with its output if it actually failed. #>
	param([Parameter(Mandatory)][string]$What, [Parameter(Mandatory)][string[]]$GitArgs)

	$result = Invoke-Git -GitArgs $GitArgs
	if ( $result.ExitCode -ne 0 ) { throw "$What failed (exit $($result.ExitCode)):`n$($result.Output)" }

	return $result.Output
}

function Get-RepoRoot
{
	# --git-common-dir points at the main checkout's .git from inside any worktree, so
	# .agent-runs stays in one place no matter which env this is run from.
	$result = Invoke-Git -GitArgs @('rev-parse', '--path-format=absolute', '--git-common-dir')
	if ( $result.ExitCode -ne 0 ) { throw "Not in a git repository." }

	return (Resolve-Path (Split-Path $result.Output.Trim() -Parent)).Path
}

function Get-EnginePath
{
	$candidates = @()
	if ( $env:SBOX_ROOT ) { $candidates += $env:SBOX_ROOT }
	$candidates += 'C:\Program Files (x86)\Steam\steamapps\common\sbox'

	foreach ( $candidate in $candidates )
	{
		if ( Test-Path (Join-Path $candidate 'sbox-dev.exe') ) { return (Resolve-Path $candidate).Path }
	}

	throw "Can't find sbox-dev.exe. Tried: $($candidates -join '; '). Set SBOX_ROOT to the s&box install."
}

function Get-Paths
{
	param([string]$Feature)

	$repo = Get-RepoRoot
	$paths = [ordered]@{
		Repo     = $repo
		Engine   = Get-EnginePath
		EnvsRoot = Join-Path (Split-Path $repo -Parent) 'sandbox-envs'
		RunsRoot = Join-Path $repo '.agent-runs'
	}

	if ( $Feature )
	{
		$paths.Worktree = Join-Path $paths.EnvsRoot $Feature
		$paths.RunDir = Join-Path $paths.RunsRoot $Feature
		$paths.EnvFile = Join-Path (Join-Path $paths.RunsRoot $Feature) 'env.json'
		$paths.Branch = "agent/$Feature"
		$paths.Ident = "sandboxmcp-$Feature"
	}

	return [pscustomobject]$paths
}

function Assert-FeatureName
{
	param([string]$Feature)

	if ( -not $Feature ) { throw "This command needs a feature name: env.ps1 $Command <feature>" }

	if ( $Feature -notmatch '^[a-z0-9][a-z0-9-]{0,30}$' )
	{
		throw "Feature name '$Feature' is not usable. Lowercase letters, digits and dashes, starting with a letter or digit, up to 31 characters - it becomes a branch, a directory and a project Ident."
	}
}


# ---------------------------------------------------------------- mcp client

function Test-Port
{
	param([int]$Port, [int]$TimeoutMs = 250)

	$client = New-Object System.Net.Sockets.TcpClient
	try
	{
		$async = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
		if ( -not $async.AsyncWaitHandle.WaitOne($TimeoutMs) ) { return $false }
		$client.EndConnect($async)
		return $true
	}
	catch { return $false }
	finally { $client.Close() }
}

function Invoke-Mcp
{
	<#
		One JSON-RPC call against an editor's MCP server. The transport is stateless -
		no initialize, no session header - so a call is a single POST.

		Tool failures come back as a result carrying isError rather than as a protocol
		error, which is why both are turned into exceptions here.
	#>
	param(
		[int]$Port,
		[string]$Tool,
		$Arguments = @{},
		[int]$TimeoutSec = 180
	)

	$body = @{
		jsonrpc = '2.0'
		id      = 1
		method  = 'tools/call'
		params  = @{ name = $Tool; arguments = $Arguments }
	} | ConvertTo-Json -Depth 32 -Compress

	$response = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/mcp" -Method Post `
		-ContentType 'application/json' -Body $body -TimeoutSec $TimeoutSec

	if ( $response.PSObject.Properties['error'] )
	{
		throw "MCP error from port ${Port}: $($response.error.message)"
	}

	$result = $response.result

	if ( $result.PSObject.Properties['isError'] -and $result.isError )
	{
		$text = ($result.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join "`n"
		throw "Tool '$Tool' failed: $text"
	}

	return $result
}

function Get-EditorStatus
{
	<# Editor status on a port, or $null if nothing usable is listening there. #>
	param([int]$Port)

	if ( -not (Test-Port -Port $Port) ) { return $null }

	try
	{
		$result = Invoke-Mcp -Port $Port -Tool 'editor_status' -TimeoutSec 20
		return $result.structuredContent
	}
	catch { return $null }
}

function Find-EditorPort
{
	<#
		The port serving a given project root, or 0. Discovery rather than trust: the
		port cookie is editor-wide and a launch can lose the race for it, so the only
		proof that we're talking to this env's editor is that it reports this env's path.
	#>
	param([string]$ProjectRoot)

	if ( -not (Test-Path $ProjectRoot) ) { return 0 }
	$want = (Resolve-Path $ProjectRoot).Path.TrimEnd('\')

	foreach ( $port in @($script:DefaultPort) + ($script:PortFirst..$script:PortLast) )
	{
		$status = Get-EditorStatus -Port $port
		if ( $null -eq $status ) { continue }

		$root = $status.Paths.ProjectRoot
		if ( $root -and $root.TrimEnd('\') -eq $want ) { return $port }
	}

	return 0
}

function Get-ClaimedPorts
{
	<#
		Ports held by live envs, under the given field of env.json.

		Records outlive their envs on purpose - a conclusion is worth more than the env
		it came from - so the record alone doesn't mean a port is taken. The worktree is
		what settles it: once teardown has removed that, the ports go back in the pool.
		Without this check a handful of teardowns would permanently exhaust the two
		bridge ports.
	#>
	param([object]$Paths, [string]$Field, [string]$ExceptFeature = '')

	if ( -not (Test-Path $Paths.RunsRoot) ) { return @() }

	return @(Get-ChildItem $Paths.RunsRoot -Filter 'env.json' -Recurse -ErrorAction SilentlyContinue |
		ForEach-Object {
			$record = Get-Content $_.FullName -Raw | ConvertFrom-Json
			# Reopening an env shouldn't lose to its own claim from last time.
			if ( $ExceptFeature -and $record.Feature -eq $ExceptFeature ) { return }
			if ( -not $record.PSObject.Properties['Worktree'] ) { return }
			if ( -not (Test-Path $record.Worktree) ) { return }
			if ( $record.PSObject.Properties[$Field] ) { $record.$Field }
		})
}

function Get-FreePort
{
	<#
		First port in a range that nothing is listening on and no other env has claimed.
		Prefer names the port to try first, so a reopened env keeps the number the run
		record and the worktree's .mcp.json already advertise.
	#>
	param([object]$Paths, [int]$First, [int]$Last, [string]$Field, [string]$What,
		[string]$ExceptFeature = '', [int]$Prefer = 0)

	$claimed = Get-ClaimedPorts -Paths $Paths -Field $Field -ExceptFeature $ExceptFeature

	$candidates = @($First..$Last)
	if ( $Prefer -ge $First -and $Prefer -le $Last ) { $candidates = @($Prefer) + $candidates }

	foreach ( $port in $candidates )
	{
		if ( $claimed -contains $port ) { continue }
		if ( Test-Port -Port $port -TimeoutMs 100 ) { continue }
		return $port
	}

	throw "No free $What port between $First and $Last. Tear down an env first."
}

function Get-FreeBridgePort
{
	<#
		A bridge port for this env, or 0 if both are spoken for. Not having one is a
		degraded env rather than a broken one, so this reports and carries on: MCP
		covers everything except driving the game through sbx verbs.
	#>
	param([object]$Paths, [string]$ExceptFeature = '', [int]$Prefer = 0)

	$claimed = Get-ClaimedPorts -Paths $Paths -Field 'BridgePort' -ExceptFeature $ExceptFeature

	$candidates = @($script:BridgePorts)
	if ( $script:BridgePorts -contains $Prefer ) { $candidates = @($Prefer) + $candidates }

	foreach ( $port in $candidates )
	{
		if ( $claimed -contains $port ) { continue }
		return $port
	}

	Write-Host "note: both bridge ports ($($script:BridgePorts -join ', ')) are claimed by other envs, so this one gets no sbx bridge." -ForegroundColor DarkYellow
	Write-Host "      The engine only allows 8080/8443/80/443 on localhost and the last two need elevation." -ForegroundColor DarkYellow
	Write-Host "      Everything except sbx verbs still works over MCP." -ForegroundColor DarkYellow

	return 0
}


# ---------------------------------------------------------------- env state

function Start-Play
{
	<#
		Press play, and keep pressing until it takes.

		One request is not enough on an env's first launch. The editor answers MCP as soon
		as its C# has compiled, but the engine is still compiling assets behind that for
		minutes, and a play_start issued in that window is quietly dropped - it returns
		without error and nothing starts. Re-issuing costs nothing once it is playing,
		so retry rather than trying to guess when the engine is ready.

		Polling editor_status is what settles it either way: play_start's own reply comes
		back before the map has loaded.
	#>
	param([int]$Port, [int]$WaitSeconds = 300, [int]$RetrySeconds = 30)

	$status = Get-EditorStatus -Port $Port
	if ( $null -ne $status -and $status.IsPlaying )
	{
		Write-Host "already playing"
		return
	}

	Write-Host "starting play mode"

	$deadline = (Get-Date).AddSeconds($WaitSeconds)
	$lastAttempt = [DateTime]::MinValue
	$attempts = 0

	while ( (Get-Date) -lt $deadline )
	{
		if ( ((Get-Date) - $lastAttempt).TotalSeconds -ge $RetrySeconds )
		{
			$lastAttempt = Get-Date
			$attempts++
			if ( $attempts -gt 1 ) { Write-Host "  still not playing - asking again (attempt $attempts)" }

			try { [void](Invoke-Mcp -Port $Port -Tool 'play_start' -TimeoutSec 60) }
			catch { Write-Host "  play_start errored: $($_.Exception.Message)" }
		}

		Start-Sleep -Seconds 3

		$status = Get-EditorStatus -Port $Port
		if ( $null -ne $status -and $status.IsPlaying )
		{
			Write-Host "  playing '$($status.ActiveScene)'"
			return
		}
	}

	Write-Host "warning: play mode didn't come up within ${WaitSeconds}s over $attempts attempts." -ForegroundColor Yellow
	Write-Host "         Check the console: env.ps1 mcp <feature> -Tool read_console" -ForegroundColor Yellow
}

function Set-BridgePin
{
	<#
		Pin this game's bridge to this env's port, so it stops scanning.

		**Call this after play has started, never before.** Entering play mode reloads
		ConVarFlags.Saved convars from the shared config/convar/game.json, which puts
		sb.bridge_url back to whatever is on disk and silently undoes an earlier pin.
		bridge_status tells the two apart: a pinned url prints bare, a scanned one prints
		"(auto)" after it.

		Pinning matters even though the port list is fixed at four. Left to scan, every
		game starts at 8080, so two of them will answer each other's calls - and a call
		answered by the wrong game looks exactly like one that worked.
	#>
	param([int]$Port, [int]$BridgePort)

	if ( $BridgePort -eq 0 )
	{
		# No port to give it - better off than scanning onto someone else's listener.
		[void](Invoke-Mcp -Port $Port -Tool 'console_command' -Arguments @{ command = 'sb.bridge false' })
		return
	}

	[void](Invoke-Mcp -Port $Port -Tool 'console_command' -Arguments @{ command = "sb.bridge_url ws://localhost:$BridgePort/" })
	[void](Invoke-Mcp -Port $Port -Tool 'console_command' -Arguments @{ command = 'sb.bridge true' })
}

function Read-EnvRecord
{
	param([object]$Paths)

	if ( -not (Test-Path $Paths.EnvFile) )
	{
		throw "No env called '$Feature'. Run: env.ps1 setup $Feature"
	}

	return Get-Content $Paths.EnvFile -Raw | ConvertFrom-Json
}

function Resolve-LivePort
{
	<#
		The port this env's editor is actually on. The recorded port is a hint; the
		match on ProjectRoot is the proof. Anything else risks driving the user's editor
		by mistake, which is the one failure this whole design exists to prevent.
	#>
	param([object]$Record)

	$port = Find-EditorPort -ProjectRoot $Record.Worktree
	if ( $port -ne 0 ) { return $port }

	throw "No editor is serving $($Record.Worktree). It was on port $($Record.Port). Relaunch with: env.ps1 setup $($Record.Feature)"
}


# ---------------------------------------------------------------- editor config

function Set-Cookie
{
	<#
		Write one entry into an s&box cookie jar. Both jars share this shape: a flat
		object of { Value, Timeout, DeleteAt }, where Value is the setting serialised
		to a string. Creates the file if it isn't there yet.
	#>
	param([string]$File, [string]$Key, [string]$Value)

	$cookies = [pscustomobject]@{}

	if ( Test-Path $File )
	{
		Copy-Item $File "$File.agent-env.bak" -Force
		$cookies = Get-Content $File -Raw | ConvertFrom-Json
	}
	else
	{
		New-Item -ItemType Directory -Force -Path (Split-Path $File -Parent) | Out-Null
	}

	$entry = [pscustomobject]@{
		Value    = $Value
		Timeout  = [DateTimeOffset]::UtcNow.AddYears(1).ToUnixTimeSeconds()
		DeleteAt = 0
	}

	# The indexer rather than a -contains over .Name: an empty jar has no .Name at all
	# under StrictMode, and a fresh worktree always starts with an empty one.
	if ( $cookies.PSObject.Properties[$Key] ) { $cookies.$Key = $entry }
	else { $cookies | Add-Member -NotePropertyName $Key -NotePropertyValue $entry }

	Write-Utf8NoBom -Path $File -Text ($cookies | ConvertTo-Json -Depth 10)
}

function Set-McpPortCookie
{
	<#
		McpServerPort is an editor preference, so it lives in the editor-wide cookie jar
		and is read once at startup. Editor-wide means launches have to be serialised:
		write the port, launch, wait for the bind, then write the next one.

		A running editor rewrites this file when it quits, so what's in here is a
		starting instruction rather than a record of anything.
	#>
	param([object]$Paths, [int]$Port)

	$file = Join-Path $Paths.Engine 'config\tools.json'
	if ( -not (Test-Path $file) ) { throw "Editor cookie file not found: $file" }

	Set-Cookie -File $file -Key 'McpServerPort' -Value "$Port"
}

function Clear-BridgeConvar
{
	<#
		Put sb.bridge_url back to empty in the shared game convar store, so the next
		session scans instead of dialling a port that belonged to a torn-down env.
		Only touches that one key; every other saved convar is left alone.
	#>
	param([object]$Paths)

	$file = Join-Path $Paths.Engine 'config\convar\game.json'
	if ( -not (Test-Path $file) ) { return }

	$cookies = Get-Content $file -Raw | ConvertFrom-Json
	$key = 'convar.sb.bridge_url'

	if ( -not $cookies.PSObject.Properties[$key] ) { return }
	if ( -not $cookies.$key.Value ) { return }

	Write-Host "clearing $key (was $($cookies.$key.Value))"
	Set-Cookie -File $file -Key $key -Value ''
}

function Copy-BuildArtifacts
{
	<#
		Seed the worktree with the main checkout's compiled assets and cloud downloads.

		Both are gitignored and both live in the project directory, so a fresh worktree
		gets neither: `*.*_c` keeps 116MB of compiled output out of the tree, and
		`.sbox/*` keeps out the ~1.2GB of cloud assets the engine downloads per project.
		Without them an env spends minutes recompiling and re-downloading, and until it
		finishes it renders untextured - which is worse than slow, because a screenshot
		taken then looks like a rendering bug rather than a half-built cache.

		Copies rather than hardlinks. The engine rewrites `_c` files when a source
		changes, and a hardlink would put that write straight into the user's own
		checkout. Disk is cheaper than that.

		Deliberately not copied: .source2/assets.<org>.<ident>.cache. It carries the
		owning ident in its first bytes, so it can't just be renamed onto a new one, and
		the engine rebuilds that index cheaply once the compiled files are present.
	#>
	param([object]$Paths)

	$jobs = @(
		@{ What = 'compiled assets'; From = (Join-Path $Paths.Repo 'Assets'); To = (Join-Path $Paths.Worktree 'Assets'); Files = @('*_c', '*.generated.*') }
		@{ What = 'cloud assets'; From = (Join-Path $Paths.Repo '.sbox\cloud'); To = (Join-Path $Paths.Worktree '.sbox\cloud'); Files = @() }
	)

	foreach ( $job in $jobs )
	{
		if ( -not (Test-Path $job.From) )
		{
			Write-Host "  no $($job.What) to seed - the first launch will build them"
			continue
		}

		# robocopy reports what it did in its exit code: under 8 is success of some kind,
		# 8 and up is a real failure. Anything else would read a copy as a crash.
		$arguments = @($job.From, $job.To) + $job.Files + @('/S', '/MT:16', '/R:1', '/W:1', '/NJH', '/NJS', '/NP', '/NDL', '/NFL')
		$null = & robocopy @arguments

		if ( $LASTEXITCODE -ge 8 ) { throw "Seeding $($job.What) failed (robocopy exit $LASTEXITCODE)." }

		# robocopy leaves a non-zero code behind on success - 1 means "files were copied".
		# Nothing after this runs a native command, so that stale value would become the
		# script's own exit status and read as a failed setup.
		$null = & cmd.exe /c exit 0
	}

	$cloudDb = Join-Path $Paths.Repo '.sbox\cloud.db'
	if ( Test-Path $cloudDb )
	{
		New-Item -ItemType Directory -Force -Path (Join-Path $Paths.Worktree '.sbox') | Out-Null
		Copy-Item $cloudDb (Join-Path $Paths.Worktree '.sbox\cloud.db') -Force
	}

	$seeded = (Get-ChildItem $Paths.Worktree -Recurse -File -ErrorAction SilentlyContinue |
		Measure-Object -Property Length -Sum)

	Write-Host ("  seeded {0:N0} files, {1:N1} GB" -f $seeded.Count, ($seeded.Sum / 1GB))
}

function Initialize-SceneCookie
{
	<#
		Seed the worktree's project cookies so the editor opens the startup scene on
		launch. The MCP registry has no way to open a scene, and play_start plays
		whichever one is current, so this is what makes an env land somewhere playable
		instead of on an empty tab.
	#>
	param([object]$Paths)

	$sbproj = Get-Content (Join-Path $Paths.Worktree 'sandbox.sbproj') -Raw | ConvertFrom-Json
	$scene = $sbproj.Metadata.StartupScene

	if ( -not $scene )
	{
		Write-Host "note: no StartupScene in sandbox.sbproj - the editor will open whatever it defaults to." -ForegroundColor DarkYellow
		return ''
	}

	$file = Join-Path (Join-Path $Paths.Worktree '.sbox') 'project.json'
	Set-Cookie -File $file -Key 'editor.openscenes' -Value (ConvertTo-Json @($scene) -Compress)

	return $scene
}

function Set-WorktreeIdent
{
	<#
		Rewrite Ident so the engine treats this worktree as a different project, then
		hide the change from git. Surgical string edits rather than a json round trip,
		so the file an agent reads still looks like the one in the repo.
	#>
	param([object]$Paths, [string]$Feature, [string]$KnownOriginalIdent = '')

	$file = Join-Path $Paths.Worktree 'sandbox.sbproj'
	$text = Get-Content $file -Raw

	$identMatch = [regex]::Match($text, '"Ident":\s*"([^"]+)"')
	if ( -not $identMatch.Success ) { throw "No Ident in $file - has the project format changed?" }
	$currentIdent = $identMatch.Groups[1].Value

	$orgMatch = [regex]::Match($text, '"Org":\s*"([^"]+)"')
	if ( -not $orgMatch.Success ) { throw "No Org in $file - has the project format changed?" }
	$org = $orgMatch.Groups[1].Value

	# Reopening an env whose worktree survived means this file is already rewritten.
	# Running the edit again would take the isolated Ident to be the original - which
	# teardown -Purge relies on to tell an env's engine state from the real project's -
	# and would append the title suffix a second time.
	if ( $currentIdent -eq $Paths.Ident )
	{
		$originalIdent = $KnownOriginalIdent
		if ( -not $originalIdent )
		{
			throw "$file is already set to $currentIdent but the run record doesn't say what it started as. Remove the worktree and reopen to rebuild it from the branch."
		}

		return [pscustomobject]@{ Org = $org; OriginalIdent = $originalIdent }
	}

	$originalIdent = $currentIdent

	$text = $text -replace '("Ident":\s*")[^"]+(")', "`${1}$($Paths.Ident)`${2}"
	$text = $text -replace '("Title":\s*")([^"]*)(")', "`${1}`${2} [$Feature]`${3}"
	Write-Utf8NoBom -Path $file -Text $text

	[void](Assert-Git -What 'Hiding sandbox.sbproj from git (the Ident rewrite must never be committable)' `
		-GitArgs @('-C', $Paths.Worktree, 'update-index', '--skip-worktree', 'sandbox.sbproj'))

	return [pscustomobject]@{ Org = $org; OriginalIdent = $originalIdent }
}

function Set-WorktreeMcpConfig
{
	<#
		Point the worktree's .mcp.json at this env's port, so a Claude Code session
		started in the worktree reaches this editor rather than whatever holds the
		default port.
	#>
	param([object]$Paths, [int]$Port)

	$file = Join-Path $Paths.Worktree '.mcp.json'
	$config = Get-Content $file -Raw | ConvertFrom-Json
	$config.mcpServers.sbox.url = "http://127.0.0.1:$Port/mcp"

	Write-Utf8NoBom -Path $file -Text ($config | ConvertTo-Json -Depth 10)

	[void](Assert-Git -What 'Hiding .mcp.json from git' `
		-GitArgs @('-C', $Paths.Worktree, 'update-index', '--skip-worktree', '.mcp.json'))
}


# ---------------------------------------------------------------- commands

function Invoke-Setup
{
	Assert-FeatureName $Feature
	$paths = Get-Paths $Feature

	if ( Test-Path $paths.Worktree )
	{
		throw "$($paths.Worktree) already exists. Tear it down first: env.ps1 teardown $Feature"
	}

	$running = @(Get-Process -Name 'sbox-dev' -ErrorAction SilentlyContinue)
	if ( $running.Count -gt 0 )
	{
		Write-Host "note: $($running.Count) editor(s) already running. Leaving them alone." -ForegroundColor DarkYellow
	}

	New-Item -ItemType Directory -Force -Path $paths.EnvsRoot | Out-Null
	New-Item -ItemType Directory -Force -Path $paths.RunDir | Out-Null

	# --- worktree
	$baseCommit = (Assert-Git -What 'Reading HEAD' -GitArgs @('-C', $paths.Repo, 'rev-parse', 'HEAD')).Trim()

	$branchExists = (Invoke-Git -GitArgs @('-C', $paths.Repo, 'show-ref', '--verify', '--quiet', "refs/heads/$($paths.Branch)")).ExitCode -eq 0

	if ( $branchExists )
	{
		Write-Host "reusing existing branch $($paths.Branch)"
		[void](Assert-Git -What 'Adding the worktree' `
			-GitArgs @('-C', $paths.Repo, 'worktree', 'add', $paths.Worktree, $paths.Branch))
	}
	else
	{
		[void](Assert-Git -What 'Adding the worktree' `
			-GitArgs @('-C', $paths.Repo, 'worktree', 'add', '-b', $paths.Branch, $paths.Worktree, 'HEAD'))
	}

	Start-Env -Paths $paths -Feature $Feature -BaseCommit $baseCommit
}

function Start-Env
{
	<#
		Isolate a worktree, launch its editor and record what came up. Shared by setup and
		open: the difference between a new env and a reopened one is only where the
		worktree came from, so everything after that lives here.

		Created and BaseCommit are carried through rather than regenerated, so reopening a
		run doesn't rewrite the history of when it started or what it branched from.
	#>
	param(
		[object]$Paths,
		[string]$Feature,
		[string]$BaseCommit,
		[string]$Created = '',
		[int]$PreferPort = 0,
		[int]$PreferBridgePort = 0,
		[string]$KnownOriginalIdent = ''
	)

	# --- isolate
	$project = Set-WorktreeIdent -Paths $Paths -Feature $Feature -KnownOriginalIdent $KnownOriginalIdent
	$port = Get-FreePort -Paths $Paths -First $script:PortFirst -Last $script:PortLast -Field 'Port' -What 'MCP' `
		-ExceptFeature $Feature -Prefer $PreferPort
	$bridgePort = Get-FreeBridgePort -Paths $Paths -ExceptFeature $Feature -Prefer $PreferBridgePort

	Set-WorktreeMcpConfig -Paths $paths -Port $port
	Set-McpPortCookie -Paths $paths -Port $port

	if ( -not $NoSeed )
	{
		Write-Host "seeding compiled and cloud assets from the main checkout"
		Copy-BuildArtifacts -Paths $paths
	}

	$scene = Initialize-SceneCookie -Paths $paths

	# --- launch
	$exe = Join-Path $paths.Engine 'sbox-dev.exe'
	$sbproj = Join-Path $paths.Worktree 'sandbox.sbproj'

	Write-Host ""
	Write-Host "launching editor for $($paths.Ident) on port $port"

	if ( $NoSeed )
	{
		Write-Host "unseeded, so the engine compiles and downloads every asset from scratch - expect minutes."
	}

	$process = Start-Process -FilePath $exe -ArgumentList @('-project', $sbproj) -PassThru

	# --- wait for it to answer, and prove it's the right one
	$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
	$livePort = 0
	$lastReport = Get-Date

	while ( (Get-Date) -lt $deadline )
	{
		if ( $process.HasExited )
		{
			throw "The editor exited before its MCP server came up (exit code $($process.ExitCode)). Check $($paths.Engine)\logs."
		}

		$livePort = Find-EditorPort -ProjectRoot $paths.Worktree
		if ( $livePort -ne 0 ) { break }

		if ( ((Get-Date) - $lastReport).TotalSeconds -ge 30 )
		{
			$waited = [int]((Get-Date) - $process.StartTime).TotalSeconds
			Write-Host "  still compiling / starting up (${waited}s)"
			$lastReport = Get-Date
		}

		Start-Sleep -Seconds 5
	}

	if ( $livePort -eq 0 )
	{
		$occupant = Get-EditorStatus -Port $port
		$detail = "nothing is listening on $port"
		if ( $null -ne $occupant ) { $detail = "port $port is serving $($occupant.Paths.ProjectRoot) instead" }

		throw "No editor served $($paths.Worktree) within ${TimeoutSeconds}s - $detail. If the editor is up but has no MCP server, it lost the race for the port; tear down and set up again."
	}

	if ( $livePort -ne $port )
	{
		Write-Host "note: editor came up on $livePort, not the requested $port." -ForegroundColor DarkYellow
	}

	# --- start playing, so there's a game to measure
	if ( -not $NoPlay )
	{
		Start-Play -Port $livePort
	}

	# --- pin this game's bridge, after play, never before
	Set-BridgePin -Port $livePort -BridgePort $bridgePort

	# --- record
	$createdAt = $Created
	if ( -not $createdAt ) { $createdAt = (Get-Date).ToString('o') }

	$record = [ordered]@{
		Feature       = $Feature
		Branch        = $paths.Branch
		Worktree      = $paths.Worktree
		RunDir        = $paths.RunDir
		Org           = $project.Org
		Ident         = $paths.Ident
		OriginalIdent = $project.OriginalIdent
		Port          = $livePort
		BridgePort    = $bridgePort
		Scene         = $scene
		Pid           = $process.Id
		BaseCommit    = $baseCommit
		Created       = $createdAt
		LastOpened    = (Get-Date).ToString('o')
	}

	Write-Utf8NoBom -Path $paths.EnvFile -Text ([pscustomobject]$record | ConvertTo-Json -Depth 10)

	Write-Host ""
	Write-Host "env '$Feature' is up." -ForegroundColor Green
	Write-Host "  worktree  $($paths.Worktree)"
	Write-Host "  branch    $($paths.Branch)  (from $($baseCommit.Substring(0,7)))"
	$bridgeState = 'none - both ports taken, use MCP'
	if ( $bridgePort -ne 0 ) { $bridgeState = "port $bridgePort" }

	Write-Host "  editor    port $livePort, ident $($paths.Ident)"
	Write-Host "  bridge    $bridgeState"
	Write-Host "  run dir   $($paths.RunDir)"
	Write-Host ""
	Write-Host "Work in the worktree. Its .mcp.json points at port $livePort, so a session"
	Write-Host "started there reaches this editor. From anywhere else, drive it with:"
	Write-Host "  env.ps1 mcp $Feature -Tool editor_status"

	if ( $bridgePort -ne 0 )
	{
		Write-Host ""
		Write-Host "Drive the game through this env's bridge with:"
		Write-Host "  `$env:SBX_PORT = '$bridgePort'"
		Write-Host "  & '$($paths.Engine)\data\$($project.Org)\$($paths.Ident)#local\agent\sbx.ps1' <verb>"
	}
}

function Invoke-Open
{
	<#
		Bring a recorded run back up, by the name of its folder under .agent-runs.

		Teardown keeps the branch and the artifacts but removes the worktree, so most
		reopens have to rebuild the worktree from the branch. The branch is the work; the
		worktree is just a checkout of it, and the run record says which one.
	#>
	Assert-FeatureName $Feature
	$paths = Get-Paths $Feature
	$record = Read-EnvRecord -Paths $paths

	# Already up? Say where, and don't launch a second editor onto the same worktree.
	$live = Find-EditorPort -ProjectRoot $record.Worktree
	if ( $live -ne 0 )
	{
		$status = Get-EditorStatus -Port $live
		$playing = 'in edit mode'
		if ( $null -ne $status -and $status.IsPlaying ) { $playing = "playing '$($status.ActiveScene)'" }

		Write-Host "env '$Feature' is already up on port $live, $playing." -ForegroundColor Green
		Write-Host "  worktree  $($record.Worktree)"
		return
	}

	# --- the branch is what has to exist; the worktree can be rebuilt from it
	$branchExists = (Invoke-Git -GitArgs @('-C', $paths.Repo, 'show-ref', '--verify', '--quiet', "refs/heads/$($record.Branch)")).ExitCode -eq 0

	if ( -not (Test-Path $record.Worktree) )
	{
		if ( -not $branchExists )
		{
			throw "Branch $($record.Branch) is gone, and so is $($record.Worktree) - there's nothing left to open. The artifacts in $($record.RunDir) are all that survives this run."
		}

		Write-Host "worktree is gone - rebuilding it from $($record.Branch)"
		New-Item -ItemType Directory -Force -Path $paths.EnvsRoot | Out-Null

		[void](Assert-Git -What 'Adding the worktree' `
			-GitArgs @('-C', $paths.Repo, 'worktree', 'add', $record.Worktree, $record.Branch))
	}

	$baseCommit = $record.BaseCommit
	if ( -not $baseCommit ) { $baseCommit = (Assert-Git -What 'Reading HEAD' -GitArgs @('-C', $paths.Repo, 'rev-parse', 'HEAD')).Trim() }

	$created = ''
	if ( $record.PSObject.Properties['Created'] ) { $created = $record.Created }

	$preferBridge = 0
	if ( $record.PSObject.Properties['BridgePort'] ) { $preferBridge = $record.BridgePort }

	$knownOriginal = ''
	if ( $record.PSObject.Properties['OriginalIdent'] ) { $knownOriginal = $record.OriginalIdent }

	Start-Env -Paths $paths -Feature $Feature -BaseCommit $baseCommit -Created $created `
		-PreferPort $record.Port -PreferBridgePort $preferBridge -KnownOriginalIdent $knownOriginal
}

function Invoke-Status
{
	$paths = Get-Paths ''

	if ( -not (Test-Path $paths.RunsRoot) )
	{
		Write-Host "No envs."
		return
	}

	$rows = @()

	foreach ( $file in Get-ChildItem $paths.RunsRoot -Filter 'env.json' -Recurse -ErrorAction SilentlyContinue )
	{
		$record = Get-Content $file.FullName -Raw | ConvertFrom-Json
		$live = Find-EditorPort -ProjectRoot $record.Worktree

		$state = 'down'
		if ( -not (Test-Path $record.Worktree) ) { $state = 'torn down (artifacts only)' }

		if ( $live -ne 0 )
		{
			$state = "up on $live"
			$status = Get-EditorStatus -Port $live
			if ( $null -ne $status -and $status.IsPlaying ) { $state += ', playing' }
		}

		$rows += [pscustomobject]@{
			Feature  = $record.Feature
			Editor   = $state
			Bridge   = $record.BridgePort
			Branch   = $record.Branch
			Worktree = $record.Worktree
		}
	}

	if ( $rows.Count -eq 0 ) { Write-Host "No envs." }
	else { $rows | Format-Table -AutoSize }
}

function Invoke-Play
{
	Assert-FeatureName $Feature
	$paths = Get-Paths $Feature
	$record = Read-EnvRecord -Paths $paths
	$port = Resolve-LivePort -Record $record

	if ( $Stop )
	{
		[void](Invoke-Mcp -Port $port -Tool 'play_stop')
		Write-Host "stopped"
		return
	}

	Start-Play -Port $port

	# Play reloads the saved convars, so the pin has to be reapplied every time.
	Set-BridgePin -Port $port -BridgePort $record.BridgePort
}

function Invoke-Shot
{
	Assert-FeatureName $Feature
	$paths = Get-Paths $Feature
	$record = Read-EnvRecord -Paths $paths
	$port = Resolve-LivePort -Record $record

	$toolName = 'camera_screenshot'
	$toolArgs = @{ width = $Width; height = $Height }

	if ( $EditorView )
	{
		$toolName = 'editor_camera_screenshot'
	}
	elseif ( $Camera )
	{
		$toolArgs.camera = $Camera
	}

	$result = Invoke-Mcp -Port $port -Tool $toolName -Arguments $toolArgs
	$image = $result.content | Where-Object { $_.type -eq 'image' } | Select-Object -First 1

	if ( $null -eq $image ) { throw "$toolName returned no image. Is there a camera in the scene?" }

	New-Item -ItemType Directory -Force -Path $record.RunDir | Out-Null
	$out = Join-Path $record.RunDir "$Name.png"
	[System.IO.File]::WriteAllBytes($out, [Convert]::FromBase64String($image.data))

	Write-Host $out
}

function Invoke-McpCommand
{
	Assert-FeatureName $Feature
	if ( -not $Tool ) { throw "This command needs -Tool. Try: env.ps1 mcp $Feature -Tool search_tools -Arguments '{`"query`":`"`"}'" }

	$paths = Get-Paths $Feature
	$record = Read-EnvRecord -Paths $paths
	$port = Resolve-LivePort -Record $record

	$parsed = $Arguments | ConvertFrom-Json
	$result = Invoke-Mcp -Port $port -Tool $Tool -Arguments $parsed

	if ( $result.PSObject.Properties['structuredContent'] )
	{
		$result.structuredContent | ConvertTo-Json -Depth 32
	}
	else
	{
		($result.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join "`n"
	}
}

function Invoke-Teardown
{
	Assert-FeatureName $Feature
	$paths = Get-Paths $Feature
	$record = Read-EnvRecord -Paths $paths

	# --- close the editor, and only this env's editor
	$port = Find-EditorPort -ProjectRoot $record.Worktree
	if ( $port -ne 0 )
	{
		$process = Get-Process -Id $record.Pid -ErrorAction SilentlyContinue

		if ( $null -ne $process -and $process.Name -eq 'sbox-dev' )
		{
			Write-Host "closing editor (pid $($record.Pid))"
			[void]$process.CloseMainWindow()
			if ( -not $process.WaitForExit(30000) )
			{
				Write-Host "  it didn't close on its own, killing it"
				Stop-Process -Id $record.Pid -Force
			}
		}
		else
		{
			Write-Host "warning: an editor is serving this worktree on port $port but pid $($record.Pid) is gone. Close it by hand." -ForegroundColor Yellow
		}
	}

	# --- unpin the shared bridge convar
	#
	# sb.bridge_url is ConVarFlags.Saved and every project writes it to the same
	# config/convar/game.json, so an env's pin outlives the env and would send the user's
	# next session to a port nothing owns. Clearing it in the running editor doesn't
	# settle it - the convar save races the shutdown, and the pinned value can still win.
	# Repairing the file after the process is gone is the only ordering that holds.
	Clear-BridgeConvar -Paths $paths

	# --- worktree and branch
	if ( Test-Path $record.Worktree )
	{
		[void](Assert-Git -What 'Removing the worktree (the editor may still have files open)' `
			-GitArgs @('-C', $paths.Repo, 'worktree', 'remove', '--force', $record.Worktree))
	}

	[void](Invoke-Git -GitArgs @('-C', $paths.Repo, 'worktree', 'prune'))

	if ( $DeleteBranch )
	{
		[void](Assert-Git -What 'Deleting the branch' -GitArgs @('-C', $paths.Repo, 'branch', '-D', $record.Branch))
	}
	else
	{
		Write-Host "keeping branch $($record.Branch) - delete it with -DeleteBranch once merged."
	}

	# --- engine state this Ident created
	if ( $Purge )
	{
		if ( $record.Ident -eq $record.OriginalIdent -or $record.Ident -notmatch '-' )
		{
			throw "Refusing to purge '$($record.Ident)' - it doesn't look like an env-specific Ident."
		}

		$data = Join-Path (Join-Path $paths.Engine 'data') $record.Org
		$inputDir = Join-Path $paths.Engine 'config\input'

		$targets = @(
			(Join-Path $data $record.Ident)
			(Join-Path $data "$($record.Ident)#local")
			(Join-Path (Join-Path $paths.Engine '.source2') "assets.$($record.Org).$($record.Ident).cache")
			(Join-Path $inputDir "$($record.Org).$($record.Ident).json")
			(Join-Path $inputDir "$($record.Org).$($record.Ident)#local.json")
		)

		foreach ( $target in $targets )
		{
			if ( Test-Path $target )
			{
				Write-Host "removing $target"
				Remove-Item $target -Recurse -Force
			}
		}
	}

	Write-Host ""
	Write-Host "env '$Feature' torn down. Run artifacts kept at $($record.RunDir)." -ForegroundColor Green
}


switch ( $Command )
{
	'setup' { Invoke-Setup }
	'open' { Invoke-Open }
	'status' { Invoke-Status }
	'play' { Invoke-Play }
	'shot' { Invoke-Shot }
	'mcp' { Invoke-McpCommand }
	'teardown' { Invoke-Teardown }
}
