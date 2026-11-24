# Sends one Control API request to a running XrShift (started with --api) and prints the reply.
#   .\api.ps1 scene.list
#   .\api.ps1 layout.save "Build day"
#   .\api.ps1 layout.load "Sales day"
param([Parameter(Mandatory)][string]$Method, [string]$Name)

$request = @{ jsonrpc = '2.0'; id = 1; method = $Method }
if ($Name) { $request.params = @{ name = $Name } }

$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'ArWorkspace.Api', 'InOut')
try {
    $pipe.Connect(3000)
    $writer = [System.IO.StreamWriter]::new($pipe); $writer.AutoFlush = $true
    $reader = [System.IO.StreamReader]::new($pipe)
    $writer.WriteLine(($request | ConvertTo-Json -Compress))
    $reader.ReadLine() | ConvertFrom-Json | ConvertTo-Json -Depth 6
}
finally { $pipe.Dispose() }
