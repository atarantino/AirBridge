param(
    [Parameter(Mandatory = $true)][string] $PipeName,
    [Parameter(Mandatory = $true)][string] $Method,
    [string] $Parameters = '{}',
    [ValidateRange(100, 30000)][int] $TimeoutMs = 20000,
    [switch] $AllowError
)

$ErrorActionPreference = 'Stop'
$parametersObject = ConvertFrom-Json -InputObject $Parameters
if ($null -eq $parametersObject -or $parametersObject -is [array] -or $parametersObject -is [string]) {
    throw 'Parameters must be a JSON object.'
}
$requestId = [Guid]::NewGuid().ToString('N')
$request = @{ id = $requestId; method = $Method; params = $parametersObject } | ConvertTo-Json -Depth 20 -Compress
if ($request.Length -gt 32768) { throw 'Debug requests must be at most 32768 characters.' }
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName,
    [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
$reader = $null
try {
    $pipe.Connect($TimeoutMs)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($request + "`n")
    $pipe.Write($bytes, 0, $bytes.Length)
    $pipe.Flush()
    $reader = [System.IO.StreamReader]::new($pipe, [System.Text.Encoding]::UTF8, $false, 1024, $true)
    $read = $reader.ReadLineAsync()
    if (-not $read.Wait($TimeoutMs)) { throw "Debug request '$Method' exceeded $TimeoutMs ms." }
    $line = $read.GetAwaiter().GetResult()
    if ([string]::IsNullOrWhiteSpace($line)) { throw 'The app closed the debug connection without a response.' }
    $response = ConvertFrom-Json -InputObject $line
    if ($response.id -ne $requestId) { throw 'The debug response request ID did not match.' }
    if (-not $response.ok -and -not $AllowError) {
        throw "Debug '$Method' failed [$($response.error.code)]: $($response.error.message)"
    }
    return $response
}
finally {
    if ($null -ne $reader) { $reader.Dispose() }
    $pipe.Dispose()
}
